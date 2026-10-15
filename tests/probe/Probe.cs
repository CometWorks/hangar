using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using HarmonyLib;
using PluginSdk.Commands;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders;
using VRage.Plugins;
using VRageMath;

namespace HangarProbe;

// Test hooks of the Hangar suite: grids, ownership and inventories on the server,
// the plugin config and stored cooldowns changed at runtime. Reaches Hangar by
// reflection, so it needs no build dependency on it.
public class Probe : IPlugin
{
    public void Init(object gameInstance) =>
        ServerCommands.Register(Assembly.GetExecutingAssembly(), typeof(ProbeCommands));

    public void Update() { }

    public void Dispose() { }
}

[CommandRoot("hgprobe", "HangarProbe", "Hangar test hooks")]
public sealed class ProbeCommands : CommandModule
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    // The operational state in the config, which `config` leaves alone
    private static readonly string[] State = { "HangarEntries", "Cooldowns", "StorageRoot" };

    private static object Plugin =>
        AppDomain
            .CurrentDomain.GetAssemblies()
            .Select(a =>
                a.GetType("ServerPlugin.HangarStorage") != null
                    ? a.GetType("ServerPlugin.Plugin")
                    : null
            )
            .First(t => t != null)
            .GetProperty("Instance")
            .GetValue(null);

    private static object Get(object target, string name) =>
        target.GetType().GetProperty(name, Any)?.GetValue(target)
        ?? target.GetType().GetField(name, Any)?.GetValue(target);

    private static object Config => Get(Plugin, "PluginConfig");

    private static object Storage => Get(Plugin, "Storage");

    private static bool Clustered => Get(Storage, "clusterIndex") != null;

    private static string StorageRoot => (string)Get(Storage, "StorageRoot");

    private static void SaveConfig() =>
        Plugin.GetType().GetMethod("SaveConfig").Invoke(Plugin, null);

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string V(Vector3D v) => $"{F(v.X)},{F(v.Y)},{F(v.Z)}";

    private static List<MyCubeGrid> Named(string name) =>
        MyEntities
            .GetEntities()
            .OfType<MyCubeGrid>()
            .Where(g => g.DisplayName == name && !g.MarkedForClose)
            .OrderByDescending(g => g.BlocksCount)
            .ToList();

    private static List<MyCubeGrid> Group(MyCubeGrid grid) =>
        MyCubeGridGroups.Static.Physical.GetGroup(grid)?.Nodes.Select(n => n.NodeData).ToList()
        ?? new List<MyCubeGrid> { grid };

    [Command(
        "config",
        "Puts the Hangar options back to their defaults, then sets Name=value pairs."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string SetConfig(params string[] options)
    {
        object config = Config;
        Type type = config.GetType();
        object defaults = Activator.CreateInstance(type);
        var values = options
            .Select(o => o.Split(new[] { '=' }, 2))
            .ToDictionary(p => p[0], p => p[1]);
        foreach (string unknown in values.Keys.Where(k => type.GetProperty(k) == null))
            return $"Probe config: no option {unknown}";

        foreach (
            PropertyInfo property in type.GetProperties(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
            )
        )
        {
            if (!property.CanWrite || State.Contains(property.Name))
                continue;
            object value = values.TryGetValue(property.Name, out string text)
                ? Parse(property.PropertyType, text)
                : property.GetValue(defaults);
            property.SetValue(config, value);
        }

        return "Probe config: done";
    }

    private static object Parse(Type type, string text)
    {
        if (type.IsEnum)
            return Enum.Parse(type, text);
        if (type == typeof(List<string>))
            return text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        return Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
    }

    [Command("wipe", "Deletes every stored entry, cooldown and grid blob.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Wipe()
    {
        if (Clustered)
        {
            foreach (string file in Directory.GetFiles(Path.Combine(StorageRoot, "Players")))
                File.Delete(file);
        }
        else
        {
            object config = Config;
            foreach (string name in new[] { "HangarEntries", "Cooldowns" })
            {
                PropertyInfo property = config.GetType().GetProperty(name);
                property.SetValue(config, Activator.CreateInstance(property.PropertyType));
            }
            SaveConfig();
        }

        string blobs = Path.Combine(StorageRoot, "GridBlobs");
        if (Directory.Exists(blobs))
            Directory.Delete(blobs, true);
        Directory.CreateDirectory(blobs);
        return "Probe wipe: done";
    }

    [Command("cooldown", "Moves a player's last save into the past by this many minutes.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Cooldown(ulong steamId, double minutesAgo)
    {
        string stamp = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo).ToString("O");
        string id = steamId.ToString(CultureInfo.InvariantCulture);
        if (Clustered)
        {
            string path = Path.Combine(StorageRoot, "Players", id + ".xml");
            if (!File.Exists(path))
                return "Probe cooldown: no state";
            XDocument document = XDocument.Load(path);
            document.Root.Element("LastSaveUtc").Value = stamp;
            document.Save(path);
            return "Probe cooldown: done";
        }

        var cooldowns = (IList)Get(Config, "Cooldowns");
        for (int i = 0; i < cooldowns.Count; i++)
        {
            if ((string)Get(cooldowns[i], "SteamId") != id)
                continue;
            object cooldown = cooldowns[i];
            cooldown.GetType().GetProperty("LastSaveUtc").SetValue(cooldown, stamp);
            cooldowns[i] = cooldown;
            SaveConfig();
            return "Probe cooldown: done";
        }

        return "Probe cooldown: no state";
    }

    [Command("grid", "The grids of this name on the server; details of the biggest.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Grid(string name)
    {
        List<MyCubeGrid> grids = Named(name);
        if (grids.Count == 0)
            return "Probe grid: count=0";

        MyCubeGrid grid = grids[0];
        List<MyCubeGrid> group = Group(grid);
        IEnumerable<MyCubeBlock> fat = group.SelectMany(g => g.GetFatBlocks());
        string owners = string.Join(
            ",",
            fat.Where(b => b.IDModule != null)
                .GroupBy(b => b.OwnerId)
                .OrderBy(g => g.Key)
                .Select(g => $"{g.Key}:{g.Count()}")
        );
        int pilots = fat.OfType<MyShipController>().Count(c => c.Pilot != null);
        return $"Probe grid: count={grids.Count} id={grid.EntityId} blocks={grid.BlocksCount} "
            + $"static={grid.IsStatic} pos={V(grid.PositionComp.GetPosition())} "
            + $"group={group.Count} groupblocks={group.Sum(g => g.BlocksCount)} owners={owners} pilots={pilots}";
    }

    [Command("items", "The items in every inventory of a grid and the grids connected to it.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Items(string name)
    {
        List<MyCubeGrid> grids = Named(name);
        if (grids.Count == 0)
            return "Probe items: no grid";

        IEnumerable<string> items = Group(grids[0])
            .SelectMany(g => g.GetFatBlocks())
            .SelectMany(b => Enumerable.Range(0, b.InventoryCount).Select(b.GetInventory))
            .SelectMany(i => i.GetItems())
            .GroupBy(i => i.Content.SubtypeName)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key}={F((double)g.Sum(i => (double)i.Amount))}");
        return "Probe items: " + string.Join(" ", items);
    }

    [Command("fill", "Puts components into the first cargo container of a grid.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Fill(string name, string subtype, int amount)
    {
        MyCargoContainer cargo = Named(name)
            .FirstOrDefault()
            ?.GetFatBlocks()
            .OfType<MyCargoContainer>()
            .FirstOrDefault();
        if (cargo == null)
            return "Probe fill: no cargo container";

        var id = new MyDefinitionId(typeof(MyObjectBuilder_Component), subtype);
        var item = (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializer.CreateNewObject(id);
        cargo.GetInventory().AddItems(amount, item);
        return $"Probe fill: {amount} {subtype}";
    }

    [Command("own", "Gives the first terminal blocks of a grid to an identity.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Own(string name, long identityId, int count)
    {
        MyCubeGrid grid = Named(name).FirstOrDefault();
        if (grid == null)
            return "Probe own: no grid";

        List<MyCubeBlock> blocks = grid.GetFatBlocks()
            .Where(b => b.IDModule != null)
            .OrderBy(b => b.Position.X)
            .ThenBy(b => b.Position.Y)
            .ThenBy(b => b.Position.Z)
            .Take(count)
            .ToList();
        foreach (MyCubeBlock block in blocks)
            block.ChangeOwner(identityId, MyOwnershipShareModeEnum.None);
        return $"Probe own: {blocks.Count}";
    }

    [Command("attach", "Builds the rotor head of the first rotor base of a grid.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Attach(string name)
    {
        MyMechanicalConnectionBlockBase stator = Named(name)
            .FirstOrDefault()
            ?.GetFatBlocks()
            .OfType<MyMechanicalConnectionBlockBase>()
            .FirstOrDefault();
        if (stator == null)
            return "Probe attach: none";

        if (stator.TopGrid == null)
            typeof(MyMechanicalConnectionBlockBase)
                .GetMethod("RecreateTop", Any)
                .Invoke(
                    stator,
                    new object[]
                    {
                        stator.OwnerId,
                        MyMechanicalConnectionBlockBase.MyTopBlockSize.Normal,
                        true,
                    }
                );
        return $"Probe attach: top={stator.TopGrid?.EntityId ?? 0}";
    }

    [Command("connect", "Locks the first connector of a grid to the one in front of it.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Connect(string name)
    {
        var connector = Named(name)
            .FirstOrDefault()
            ?.GetFatBlocks()
            .OfType<Sandbox.ModAPI.Ingame.IMyShipConnector>()
            .FirstOrDefault();
        if (connector == null)
            return "Probe connect: none";

        connector.Connect();
        return $"Probe connect: status={connector.Status}";
    }

    [Command("move", "Moves a grid by this many meters, without its physics in the way.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Move(string name, double dx, double dy, double dz)
    {
        MyCubeGrid grid = Named(name).FirstOrDefault();
        if (grid == null)
            return "Probe move: no grid";

        MatrixD matrix = grid.WorldMatrix;
        matrix.Translation += new Vector3D(dx, dy, dz);
        grid.Teleport(matrix);
        return $"Probe move: pos={V(grid.PositionComp.GetPosition())}";
    }

    [Command("seat", "Seats a player in the first cockpit of a grid, the way the mod API does.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Seat(string name, long identityId)
    {
        var cockpit = Named(name)
            .FirstOrDefault()
            ?.GetFatBlocks()
            .OfType<MyCockpit>()
            .FirstOrDefault();
        var character = MySession.Static.Players.TryGetIdentity(identityId)?.Character;
        if (cockpit == null || character == null)
            return "Probe seat: no cockpit or character";

        ((Sandbox.ModAPI.IMyCockpit)cockpit).AttachPilot(character);
        return "Probe seat: requested";
    }

    [Command(
        "teleport",
        "Teleports a player the way the admin teleport does, for players without the right."
    )]
    [Permission(MyPromoteLevel.Admin)]
    public string Teleport(ulong steamId, double x, double y, double z)
    {
        typeof(MyMultiplayer)
            .GetMethod("OnTeleport", Any)
            .Invoke(null, new object[] { steamId, new Vector3D(x, y, z) });
        return "Probe teleport: done";
    }

    [Command("patched", "The game methods Hangar's Harmony patches are on.")]
    [Permission(MyPromoteLevel.Admin)]
    public string Patched() =>
        "Probe patched: "
        + string.Join(
            " ",
            Harmony
                .GetAllPatchedMethods()
                .Where(m => Harmony.GetPatchInfo(m).Owners.Contains("Hangar"))
                .Select(m => m.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
        );
}
