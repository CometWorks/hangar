using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Havok;
using PluginSdk.Clustering;
using PluginSdk.Storage;
using Sandbox;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRageMath;

namespace ServerPlugin;

public sealed class HangarStorage
{
    private const string BlobDirectoryName = "GridBlobs";
    private const string PluginId = "17C928CE-4645-4E1B-9365-C3C46BFE061D";

    /// <summary>Number of leading id characters the list and the save reply show</summary>
    public const int ShownIdLength = 8;

    private readonly Plugin plugin;
    private readonly ClusterHangarIndex clusterIndex;

    public HangarStorage(Plugin plugin)
    {
        this.plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        if (PluginCluster.IsClusterProcess)
        {
            if (Config.HangarEntries?.Count > 0 || Config.Cooldowns?.Count > 0)
                throw new InvalidDataException(
                    "Legacy Hangar entries and cooldowns must be migrated with their grid blobs before clustered use."
                );
            clusterIndex = new ClusterHangarIndex(PluginStorage.GetSharedDirectory(PluginId));
        }
    }

    private PluginConfig Config => plugin.PluginConfig;

    public string StorageRoot =>
        clusterIndex?.Root
        ?? (
            string.IsNullOrWhiteSpace(Config.StorageRoot)
                ? Plugin.DefaultStorageRoot
                : Config.StorageRoot
        );

    public (int Entries, int Cooldowns) Counts =>
        clusterIndex?.Counts() ?? (Config.HangarEntries.Count, Config.Cooldowns.Count);

    public void EnsureStorage()
    {
        Directory.CreateDirectory(StorageRoot);
        Directory.CreateDirectory(Path.Combine(StorageRoot, BlobDirectoryName));
    }

    public IReadOnlyList<HangarEntry> ListPlayerEntries(ulong steamId)
    {
        return (clusterIndex?.Read(steamId).Entries ?? Config.HangarEntries)
            .Where(entry =>
                entry.Scope == HangarEntryScope.Player
                && string.Equals(
                    entry.OwnerId,
                    steamId.ToString(CultureInfo.InvariantCulture),
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .OrderBy(entry => ParseDate(entry.SavedUtc))
            .ToList();
    }

    public bool TrySavePlayerGrid(
        ulong steamId,
        long identityId,
        string ownerName,
        MyCharacter character,
        string gridNameOrEntityId,
        out HangarEntry entry,
        out string error
    )
    {
        entry = default;
        error = null;

        if (!Config.Enabled)
        {
            error = "Hangar is disabled.";
            return false;
        }

        if (character == null)
        {
            error = "Stand near a grid or look at the grid you want to save.";
            return false;
        }

        using var lease = clusterIndex?.Acquire(steamId);
        var playerEntries = ListPlayerEntries(steamId);
        if (Config.MaxEntriesPerPlayer > 0 && playerEntries.Count >= Config.MaxEntriesPerPlayer)
        {
            error = $"Hangar is full. Limit: {Config.MaxEntriesPerPlayer}.";
            return false;
        }

        if (!CheckCooldown(steamId, lease?.State, out error))
            return false;

        if (
            !GridFinder.TryFind(
                gridNameOrEntityId,
                character,
                Config.IncludeConnectedGrids,
                out var grids
            )
        )
        {
            error = string.IsNullOrWhiteSpace(gridNameOrEntityId)
                ? "No grid found in your line of sight."
                : $"Grid not found: {gridNameOrEntityId}";
            return false;
        }

        var mainGrid = GridFinder.BiggestGrid(grids);
        if (Config.RequireMajorityOwnership && !HasMajorityOwnership(mainGrid, identityId))
        {
            error = "You must own the majority of functional blocks on the main grid.";
            return false;
        }

        var bounds = BoundingBoxD.CreateInvalid();
        foreach (var grid in grids)
            bounds.Include(grid.PositionComp.WorldAABB);
        var zone = AdminZoneAt(bounds.Center);
        if (Config.AdminSafeZonesOnly && zone == null)
        {
            error = "You can only save grids inside a station's safe zone.";
            return false;
        }

        var blocks = grids.Sum(grid => grid.BlocksCount);
        var pcu = grids.Sum(grid => grid.BlocksPCU);
        if (Config.MaxBlocksPerEntry > 0 && blocks > Config.MaxBlocksPerEntry)
        {
            error = $"Grid is too large: {blocks} blocks, limit {Config.MaxBlocksPerEntry}.";
            return false;
        }

        if (Config.MaxPcuPerEntry > 0 && pcu > Config.MaxPcuPerEntry)
        {
            error = $"Grid is too expensive: {pcu} PCU, limit {Config.MaxPcuPerEntry}.";
            return false;
        }

        EnsureStorage();
        var id = Guid.NewGuid().ToString("N");
        var relativePath = string.Join(
            "/",
            BlobDirectoryName,
            steamId.ToString(CultureInfo.InvariantCulture),
            id + ".sbc"
        );
        var absolutePath = Path.Combine(StorageRoot, relativePath);
        var temporary = lease is null
            ? absolutePath
            : absolutePath + ".tmp-" + Guid.NewGuid().ToString("N");
        bool saved;
        try
        {
            saved = GridSerializer.Save(temporary, mainGrid.DisplayName, grids);
            if (saved && lease is not null)
            {
                using (
                    var file = new FileStream(
                        temporary,
                        FileMode.Open,
                        FileAccess.ReadWrite,
                        FileShare.ReadWrite
                    )
                )
                    file.Flush(true);
                File.Move(temporary, absolutePath);
            }
        }
        finally
        {
            if (lease is not null)
                TryDelete(temporary);
        }
        if (!saved)
        {
            error = "Failed to write grid data.";
            return false;
        }

        var position = mainGrid.PositionComp.GetPosition();
        entry = new HangarEntry
        {
            Id = id,
            Scope = HangarEntryScope.Player,
            OwnerId = steamId.ToString(CultureInfo.InvariantCulture),
            OwnerName = ownerName ?? "",
            GridName = mainGrid.DisplayName ?? id,
            BlobPath = relativePath,
            SavedUtc = DateTimeOffset.UtcNow.ToString("O"),
            GridKind = GetKind(grids),
            Pcu = pcu,
            Blocks = blocks,
            GridCount = grids.Count,
            StaticGridCount = grids.Count(grid => grid.IsStatic),
            LargeGridCount = grids.Count(grid =>
                grid.GridSizeEnum == MyCubeSize.Large && !grid.IsStatic
            ),
            SmallGridCount = grids.Count(grid => grid.GridSizeEnum == MyCubeSize.Small),
            OriginalX = position.X,
            OriginalY = position.Y,
            OriginalZ = position.Z,
            Zone = ZoneName(zone),
            Description = "",
        };

        if (lease is not null)
        {
            lease.State.Entries.Add(entry);
            lease.State.LastSaveUtc = entry.SavedUtc;
            lease.Save();
        }
        else
        {
            Config.HangarEntries.Add(entry);
            Config.NotifyChanged(nameof(Config.HangarEntries));
            SetCooldown(steamId);
            plugin.SaveConfig();
        }

        if (Config.RemoveOriginalOnSave)
        {
            foreach (var grid in grids)
                grid.Close();
        }

        return true;
    }

    public bool TryLoadPlayerGrid(
        ulong steamId,
        long identityId,
        MyCharacter character,
        string selector,
        bool nearPlayer,
        out HangarEntry entry,
        out string error
    )
    {
        entry = default;
        error = null;

        if (!Config.Enabled)
        {
            error = "Hangar is disabled.";
            return false;
        }

        MySafeZone zone = null;
        if (Config.AdminSafeZonesOnly)
        {
            zone = character == null ? null : AdminZoneAt(character.PositionComp.GetPosition());
            if (zone == null)
            {
                error = "You can only load grids inside a station's safe zone.";
                return false;
            }
        }

        using var lease = clusterIndex?.Acquire(steamId);
        if (!TryResolvePlayerEntry(steamId, selector, out entry, out error))
            return false;

        var absolutePath = GetAbsoluteBlobPath(entry);
        if (!GridSerializer.Load(absolutePath, out var grids))
        {
            error = "Stored grid data is missing or unreadable.";
            return false;
        }

        if (Config.TransferOwnershipOnLoad)
            GridSerializer.TransferOwnership(grids, identityId);

        // In a zone the grid goes back where it was when it was saved there, else near the player
        var near =
            zone != null
                ? nearPlayer || entry.Zone != ZoneName(zone)
                : nearPlayer || Config.DefaultLoadMode == HangarLoadMode.NearPlayer;
        if (near)
        {
            if (character == null)
            {
                error = "A player character is required for near-player load.";
                return false;
            }

            MoveNearCharacter(grids, character);
        }

        if (zone != null)
            MoveInside(zone, grids);

        if (!TryMoveToFreeSpace(grids, zone != null))
        {
            error = $"No free space to load {entry.GridName}. Clear the spot or load it elsewhere.";
            return false;
        }

        var bounds = Bounds(grids);
        if (zone != null && zone.Containment(ref bounds) != ContainmentType.Contains)
        {
            error =
                $"No free space for {entry.GridName} inside {ZoneName(zone)}. Clear some room or load it elsewhere.";
            return false;
        }

        if (!AllowLoad(identityId, entry, ZoneName(zone ?? AdminZoneAt(bounds.Center)), out error))
            return false;

        MyEntities.RemapObjectBuilderCollection(grids);
        foreach (var grid in grids)
        {
            grid.PersistentFlags |= MyPersistentEntityFlags2.InScene;
            MyEntities.CreateFromObjectBuilderAndAdd(grid, true);
        }

        if (lease is not null)
        {
            var id = entry.Id;
            lease.State.Entries.RemoveAll(e => e.Id == id);
            lease.Save();
        }
        else
            RemoveEntry(entry);
        TryDelete(absolutePath);
        if (lease is null)
            plugin.SaveConfig();
        return true;
    }

    public bool TryRemovePlayerEntry(
        ulong steamId,
        string selector,
        out HangarEntry entry,
        out string error
    )
    {
        entry = default;
        error = null;

        using var lease = clusterIndex?.Acquire(steamId);
        if (!TryResolvePlayerEntry(steamId, selector, out entry, out error))
            return false;

        if (lease is not null)
        {
            var id = entry.Id;
            lease.State.Entries.RemoveAll(e => e.Id == id);
            lease.Save();
        }
        else
            RemoveEntry(entry);
        TryDelete(GetAbsoluteBlobPath(entry));
        if (lease is null)
            plugin.SaveConfig();
        return true;
    }

    public string Describe(HangarEntry entry, int index)
    {
        return $"{index}. {entry.GridName} [{entry.Id.Substring(0, Math.Min(ShownIdLength, entry.Id.Length))}] "
            + $"{entry.GridCount} grids, {entry.Blocks} blocks, {entry.Pcu} PCU, saved {FormatDate(entry.SavedUtc)}"
            + (string.IsNullOrEmpty(entry.Zone) ? "" : $" in {entry.Zone}");
    }

    public IEnumerable<string> DescribeDetails(HangarEntry entry)
    {
        yield return $"id: {entry.Id}";
        yield return $"name: {entry.GridName}";
        yield return $"owner: {entry.OwnerName} ({entry.OwnerId})";
        yield return $"saved_utc: {entry.SavedUtc}";
        yield return $"kind: {entry.GridKind}";
        yield return $"grids: {entry.GridCount} total, {entry.StaticGridCount} static, {entry.LargeGridCount} large, {entry.SmallGridCount} small";
        yield return $"blocks: {entry.Blocks}";
        yield return $"pcu: {entry.Pcu}";
        yield return $"blob: {entry.BlobPath}";
        yield return $"original_position: {entry.OriginalX:0.##}, {entry.OriginalY:0.##}, {entry.OriginalZ:0.##}";
        yield return $"zone: {entry.Zone}";
    }

    public bool TryResolvePlayerEntry(
        ulong steamId,
        string selector,
        out HangarEntry entry,
        out string error
    )
    {
        entry = default;
        error = null;

        var entries = ListPlayerEntries(steamId);
        if (entries.Count == 0)
        {
            error = "Your hangar is empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(selector))
        {
            if (entries.Count == 1)
            {
                entry = entries[0];
                return true;
            }

            error = "Select an entry by number, name, or id.";
            return false;
        }

        var isNumber = int.TryParse(
            selector,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var number
        );
        if (isNumber && number >= 1 && number <= entries.Count)
        {
            entry = entries[number - 1];
            return true;
        }

        // Any other number is an all-digit id as the list shows it, or a grid named exactly that.
        // Looser matching would let a mistyped list number pick, or remove, an unrelated entry.
        var matches = entries
            .Where(e =>
                isNumber
                    ? (
                        selector.Length >= ShownIdLength
                        && e.Id.StartsWith(selector, StringComparison.OrdinalIgnoreCase)
                    ) || string.Equals(e.GridName, selector, StringComparison.OrdinalIgnoreCase)
                    : e.Id.StartsWith(selector, StringComparison.OrdinalIgnoreCase)
                        || e.GridName.IndexOf(selector, StringComparison.OrdinalIgnoreCase) >= 0
            )
            .ToList();
        if (matches.Count == 1)
        {
            entry = matches[0];
            return true;
        }

        error =
            matches.Count > 1 ? $"Multiple hangar entries match '{selector}'. Use number or id."
            : isNumber ? $"No hangar entry #{number}."
            : $"No hangar entry matches '{selector}'.";
        return false;
    }

    private bool CheckCooldown(ulong steamId, HangarPlayerState state, out string error)
    {
        error = null;
        if (Config.SaveCooldownMinutes <= 0)
            return true;

        var timestamp = state is null
            ? Config
                .Cooldowns.FirstOrDefault(c =>
                    c.SteamId == steamId.ToString(CultureInfo.InvariantCulture)
                )
                .LastSaveUtc
            : state.LastSaveUtc;
        if (
            string.IsNullOrWhiteSpace(timestamp)
            || !DateTimeOffset.TryParse(
                timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var lastSave
            )
        )
            return true;

        var availableAt = lastSave.AddMinutes(Config.SaveCooldownMinutes);
        if (availableAt <= DateTimeOffset.UtcNow)
            return true;

        var remaining = availableAt - DateTimeOffset.UtcNow;
        error = $"Save cooldown active for {Math.Ceiling(remaining.TotalMinutes)} more minutes.";
        return false;
    }

    private void SetCooldown(ulong steamId)
    {
        var id = steamId.ToString(CultureInfo.InvariantCulture);
        Config.Cooldowns.RemoveAll(c => c.SteamId == id);
        Config.Cooldowns.Add(
            new HangarCooldown { SteamId = id, LastSaveUtc = DateTimeOffset.UtcNow.ToString("O") }
        );
        Config.NotifyChanged(nameof(Config.Cooldowns));
    }

    private void RemoveEntry(HangarEntry entry)
    {
        Config.HangarEntries.RemoveAll(e => e.Id == entry.Id);
        Config.NotifyChanged(nameof(Config.HangarEntries));
    }

    private string GetAbsoluteBlobPath(HangarEntry entry)
    {
        if (clusterIndex is not null)
        {
            var parts = entry.BlobPath?.Split('/');
            if (
                parts?.Length != 3
                || parts[0] != BlobDirectoryName
                || !ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _)
                || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(parts[2]), "N", out _)
                || Path.GetExtension(parts[2]) != ".sbc"
            )
                throw new InvalidDataException("Stored Hangar blob path is invalid.");
            return Path.Combine(StorageRoot, parts[0], parts[1], parts[2]);
        }
        return Path.IsPathRooted(entry.BlobPath)
            ? entry.BlobPath
            : Path.Combine(
                new[] { StorageRoot }
                    .Concat(
                        entry.BlobPath.Split(
                            new[] { '/', '\\' },
                            StringSplitOptions.RemoveEmptyEntries
                        )
                    )
                    .ToArray()
            );
    }

    private void MoveNearCharacter(
        IReadOnlyList<VRage.Game.MyObjectBuilder_CubeGrid> grids,
        MyCharacter character
    )
    {
        if (grids.Count == 0)
            return;

        var head = character.GetHeadMatrix(true);
        var target = head.Translation + head.Forward * Config.LoadOffsetMeters;
        var firstMatrix = grids[0].PositionAndOrientation?.GetMatrix() ?? MatrixD.Identity;

        foreach (var grid in grids)
        {
            var current = grid.PositionAndOrientation?.GetMatrix() ?? firstMatrix;
            var offset = current.Translation - firstMatrix.Translation;
            current.Translation = target + offset;
            grid.PositionAndOrientation = new MyPositionAndOrientation(current);
        }
    }

    // Leaves the grids where they are when nothing is in the way, otherwise moves them all to a
    // free spot nearby that the game's spawn search finds. False when it finds none. That search
    // skips safe zones unless inZone allows them.
    private static bool TryMoveToFreeSpace(
        IReadOnlyList<MyObjectBuilder_CubeGrid> grids,
        bool inZone
    )
    {
        if (!grids.Any(IsObstructed))
            return true;

        var sphere = BoundingSphereD.CreateFromBoundingBox(Bounds(grids));
        var free = MyEntities.FindFreePlaceCustom(
            sphere.Center,
            (float)sphere.Radius,
            maxTestCount: 50,
            testsPerDistance: 10,
            allowSafezones: inZone
        );
        if (free == null)
            return false;

        Shift(grids, free.Value - sphere.Center);
        return true;
    }

    // Pulls the grids toward the center of a sphere zone until their bounds fit in it, if they can
    private static void MoveInside(MySafeZone zone, IReadOnlyList<MyObjectBuilder_CubeGrid> grids)
    {
        if (zone.Shape != MySafeZoneShape.Sphere)
            return;

        var sphere = BoundingSphereD.CreateFromBoundingBox(Bounds(grids));
        var offset = sphere.Center - zone.PositionComp.GetPosition();
        var room = zone.Radius - sphere.Radius;
        // A grid's width of slack, so that a shift to free space stays inside
        if (room > 2 * sphere.Radius)
            room -= 2 * sphere.Radius;
        var distance = offset.Length();
        if (room > 0 && distance > room)
            Shift(grids, offset * (room / distance - 1));
    }

    private static void Shift(IReadOnlyList<MyObjectBuilder_CubeGrid> grids, Vector3D delta)
    {
        foreach (var grid in grids)
        {
            var matrix = WorldMatrix(grid);
            matrix.Translation += delta;
            grid.PositionAndOrientation = new MyPositionAndOrientation(matrix);
        }
    }

    private static BoundingBoxD Bounds(IReadOnlyList<MyObjectBuilder_CubeGrid> grids)
    {
        var bounds = BoundingBoxD.CreateInvalid();
        foreach (var grid in grids)
            bounds.Include(
                ((BoundingBoxD)grid.CalculateBoundingBox()).TransformFast(WorldMatrix(grid))
            );
        return bounds;
    }

    // An admin safe zone is one an admin or the world made, not a safe zone block
    private static MySafeZone AdminZoneAt(Vector3D point) =>
        MySessionComponentSafeZones.SafeZones.FirstOrDefault(zone =>
            zone.Enabled
            && !zone.MarkedForClose
            && zone.SafeZoneBlockId == 0
            && zone.Contains(point)
        );

    private static string ZoneName(MySafeZone zone) =>
        zone == null ? ""
        : string.IsNullOrWhiteSpace(zone.DisplayName)
            ? zone.EntityId.ToString(CultureInfo.InvariantCulture)
        : zone.DisplayName;

    private bool AllowLoad(long identityId, HangarEntry entry, string zone, out string error)
    {
        error = null;
        var hook = HangarHooks.BeforeLoad;
        if (hook == null)
            return true;

        try
        {
            error = hook(identityId, entry.Zone ?? "", zone, entry.Pcu);
        }
        catch (Exception exception)
        {
            plugin.Log.Error(exception, "Hangar's BeforeLoad hook failed");
            error = "Hangar can't load grids right now.";
        }

        return error == null;
    }

    // ponytail: the grid's bounding box against the physics of everything else, so an L-shaped grid
    // can be refused a spot it would fit in; test per block if that bites. Terrain doesn't count:
    // a grid saved on the ground goes back into it. The box is shrunk a little so a grid that was
    // resting on another one, like a ship on a landing pad, still fits.
    private static bool IsObstructed(MyObjectBuilder_CubeGrid grid)
    {
        var matrix = WorldMatrix(grid);
        var box = grid.CalculateBoundingBox();
        var center = Vector3D.Transform((Vector3D)box.Center, matrix);
        var rotation = Quaternion.CreateFromRotationMatrix(in matrix);
        HkShape shape = new HkBoxShape(Vector3.Max(box.HalfExtents - 0.25f, new Vector3(0.05f)));
        try
        {
            return MyEntities.IsShapePenetrating(
                shape,
                ref center,
                ref rotation,
                entity => entity is MyVoxelBase || entity is MyFloatingObject,
                allowSafezones: true
            );
        }
        finally
        {
            shape.RemoveReference();
        }
    }

    private static MatrixD WorldMatrix(MyObjectBuilder_CubeGrid grid) =>
        grid.PositionAndOrientation?.GetMatrix() ?? MatrixD.Identity;

    private static bool HasMajorityOwnership(MyCubeGrid grid, long identityId)
    {
        if (grid == null)
            return false;

        var totalOwnedBlocks = 0;
        var playerOwnedBlocks = 0;
        foreach (var block in grid.GetFatBlocks())
        {
            if (block?.IDModule == null)
                continue;

            totalOwnedBlocks++;
            if (block.OwnerId == identityId)
                playerOwnedBlocks++;
        }

        if (totalOwnedBlocks == 0)
            return grid.BigOwners.Contains(identityId);

        return playerOwnedBlocks > totalOwnedBlocks / 2;
    }

    private static HangarGridKind GetKind(IReadOnlyCollection<MyCubeGrid> grids)
    {
        var staticCount = grids.Count(grid => grid.IsStatic);
        var largeCount = grids.Count(grid =>
            grid.GridSizeEnum == MyCubeSize.Large && !grid.IsStatic
        );
        var smallCount = grids.Count(grid => grid.GridSizeEnum == MyCubeSize.Small);
        if (staticCount == grids.Count)
            return HangarGridKind.Static;
        if (largeCount == grids.Count)
            return HangarGridKind.Large;
        if (smallCount == grids.Count)
            return HangarGridKind.Small;
        return HangarGridKind.Mixed;
    }

    private static DateTimeOffset ParseDate(string value)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var result
        )
            ? result
            : DateTimeOffset.MinValue;
    }

    private static string FormatDate(string value)
    {
        var parsed = ParseDate(value);
        return parsed == DateTimeOffset.MinValue ? "unknown" : parsed.ToString("u");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Keeping the index correct is more important than failing a player command over stale blobs.
        }
    }
}
