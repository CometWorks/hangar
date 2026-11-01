using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using VRageMath;

namespace ServerPlugin;

// For other server plugins, which find it by reflection: plugin types share the template's
// namespace, so they can't reference Hangar's directly.
public static class HangarHooks
{
    // Called when a stored grid is about to come back, once its spot is found, with the player's
    // identity id, the admin safe zone the grid was saved in, the one it comes back in (names,
    // empty for none) and its PCU. Returns null to go on, or why it may not, which the player
    // is told. A plugin charging for the load charges here.
    public static Func<long, string, string, int, string> BeforeLoad;

    // Stores grids for a player who may be elsewhere or offline, and removes them from the world,
    // with no cooldown, ownership or safe zone rule, and without starting their cooldown. The entry
    // records the admin safe zone at zoneAt. Full: the player has no free slot, nothing changed.
    public static (bool Stored, bool Full, string Error) Store(
        ulong steamId,
        string ownerName,
        IReadOnlyList<MyCubeGrid> grids,
        Vector3D zoneAt
    )
    {
        var storage = Plugin.Instance?.Storage;
        if (storage == null)
            return (false, false, "Hangar is not loaded.");
        var stored = storage.TryStoreFor(
            steamId,
            ownerName,
            grids,
            zoneAt,
            out var full,
            out var error
        );
        return (stored, full, error);
    }
}
