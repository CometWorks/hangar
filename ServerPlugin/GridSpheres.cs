using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PluginSdk.Clustering;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRageMath;

namespace ServerPlugin;

// The bounding spheres of the grids around a point, for a load that must stay out of them. A cluster
// node knows only the grids of its own partitions, so there every node answers.
internal static class GridSpheres
{
    private const string Topic = "grid-spheres";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static IDisposable handler;
    private static long nextRegistrationTick;

    // Game thread: registers the handler once the cluster's plugin services exist
    public static void Update(long tick, string pluginId)
    {
        if (handler != null || !PluginCluster.IsClusterProcess || tick < nextRegistrationTick)
            return;

        nextRegistrationTick = tick + 60;
        if (PluginCluster.Current != null)
            handler = PluginCluster.ForPlugin(pluginId).RegisterHandler(Topic, OnAsked);
    }

    public static void Dispose()
    {
        handler?.Dispose();
        handler = null;
    }

    // Calls back on the game thread, with null when a node could not be asked
    public static void Around(
        string pluginId,
        Vector3D center,
        double reach,
        Action<List<BoundingSphereD>> done
    )
    {
        if (!PluginCluster.IsClusterProcess)
        {
            done(Local(center, reach));
            return;
        }

        var question = new MemoryStream();
        using (var writer = new BinaryWriter(question))
        {
            writer.Write(center.X);
            writer.Write(center.Y);
            writer.Write(center.Z);
            writer.Write(reach);
        }

        PluginCluster
            .ForPlugin(pluginId)
            .BroadcastAsync(
                Topic,
                question.ToArray(),
                Guid.NewGuid(),
                Timeout,
                includeWorldAuthority: false
            )
            .ContinueWith(task =>
            {
                var result = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
                var spheres =
                    result?.Code == PluginResultCode.Success
                        ? result.Nodes.Values.SelectMany(node => Decode(node.Payload)).ToList()
                        : null;
                MyAPIGateway.Utilities.InvokeOnGameThread(() => done(spheres));
            });
    }

    private static Task<byte[]> OnAsked(PluginMessage message)
    {
        using var reader = new BinaryReader(new MemoryStream(message.Payload));
        var center = new Vector3D(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
        var answer = new MemoryStream();
        using (var writer = new BinaryWriter(answer))
        {
            foreach (var sphere in Local(center, reader.ReadDouble()))
            {
                writer.Write(sphere.Center.X);
                writer.Write(sphere.Center.Y);
                writer.Write(sphere.Center.Z);
                writer.Write(sphere.Radius);
            }
        }

        return Task.FromResult(answer.ToArray());
    }

    private static IEnumerable<BoundingSphereD> Decode(byte[] answer)
    {
        using var reader = new BinaryReader(new MemoryStream(answer ?? Array.Empty<byte>()));
        for (var read = 0; read < reader.BaseStream.Length; read += 4 * sizeof(double))
            yield return new BoundingSphereD(
                new Vector3D(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble()),
                reader.ReadDouble()
            );
    }

    // ponytail: every entity once per load, which only a player's command asks for. A pruning query
    // would miss a grid whose sphere reaches further than its box.
    private static List<BoundingSphereD> Local(Vector3D center, double reach) =>
        MyEntities
            .GetEntities()
            .OfType<MyCubeGrid>()
            .Where(grid => !grid.Closed && !grid.MarkedForClose && !grid.IsPreview)
            .Select(grid => grid.PositionComp.WorldVolume)
            .Where(sphere => Vector3D.Distance(sphere.Center, center) - sphere.Radius < reach)
            .ToList();

    // The spots a sphere can go to, nearest first: along evenly spread directions, how far it has to
    // go from at least start to clear all the spheres. Each comes from the rays through the spheres,
    // so a big station costs no more steps than a small one.
    public static IEnumerable<Vector3D> ClearSpots(
        BoundingSphereD ship,
        IReadOnlyList<BoundingSphereD> others,
        double start
    ) =>
        Directions
            .Select(direction =>
                (direction, distance: ClearDistance(ship, direction, others, start))
            )
            .Where(spot => !double.IsInfinity(spot.distance))
            .OrderBy(spot => spot.distance)
            .Select(spot => ship.Center + spot.direction * spot.distance);

    private const double Margin = 1;

    // Past the exact edge of a sphere, where rounding at world coordinates would still be inside
    private const double Slack = 0.001;

    // A ray leaves each sphere once, so it takes one pass per sphere and one to see it is clear.
    // Infinity when it isn't by then, which only broken numbers can do.
    private static double ClearDistance(
        BoundingSphereD ship,
        Vector3D direction,
        IReadOnlyList<BoundingSphereD> others,
        double start
    )
    {
        var distance = start;
        for (var pass = 0; pass <= others.Count; pass++)
        {
            var blocked = false;
            var at = ship.Center + direction * distance;
            foreach (var other in others)
            {
                var clear = other.Radius + ship.Radius + Margin;
                var toOther = other.Center - at;
                var squared = toOther.LengthSquared();
                if (!(squared < clear * clear))
                    continue;

                var along = Vector3D.Dot(toOther, direction);
                distance += along + Math.Sqrt(clear * clear - squared + along * along) + Slack;
                blocked = true;
                break;
            }

            if (!blocked)
                return distance;
        }

        return double.PositiveInfinity;
    }

    // A Fibonacci sphere
    private static readonly Vector3D[] Directions = Enumerable
        .Range(0, 64)
        .Select(i =>
        {
            var y = 1 - (2 * i + 1) / 64.0;
            var ring = Math.Sqrt(1 - y * y);
            var angle = i * Math.PI * (3 - Math.Sqrt(5));
            return new Vector3D(Math.Cos(angle) * ring, y, Math.Sin(angle) * ring);
        })
        .ToArray();
}
