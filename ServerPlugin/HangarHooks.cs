using System;

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
}
