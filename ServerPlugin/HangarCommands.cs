using System;
using System.Collections.Generic;
using System.Linq;
using PluginSdk.Commands;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRage.Game.ModAPI;

namespace ServerPlugin;

public abstract class HangarCommandModule : CommandModule
{
    protected IEnumerable<string> ListEntries()
    {
        if (!TryGetPlugin(out var plugin, out var error))
            return new[] { error };
        if (!TryGetPlayerCaller(out var steamId, out _, out _, out error))
            return new[] { error };

        var entries = plugin.Storage.ListPlayerEntries(steamId);
        if (entries.Count == 0)
            return new[] { "Your hangar is empty." };

        return entries.Select((entry, index) => plugin.Storage.Describe(entry, index + 1)).ToList();
    }

    protected IEnumerable<string> EntryInfo(string selector)
    {
        if (!TryGetPlugin(out var plugin, out var error))
            return new[] { error };
        if (!TryGetPlayerCaller(out var steamId, out _, out _, out error))
            return new[] { error };

        return plugin.Storage.TryResolvePlayerEntry(steamId, selector, out var entry, out error)
            ? plugin.Storage.DescribeDetails(entry).ToList()
            : new[] { error };
    }

    protected string SaveEntry(string gridNameOrEntityId)
    {
        if (!TryGetPlugin(out var plugin, out var error))
            return error;

        if (!TryGetCharacter(out var steamId, out var identityId, out var callerName, out var character, out error))
            return error;

        return plugin.Storage.TrySavePlayerGrid(
            steamId,
            identityId,
            callerName,
            character,
            gridNameOrEntityId,
            out var entry,
            out error)
            ? $"Saved {entry.GridName} to hangar as {entry.Id.Substring(0, 8)}."
            : error;
    }

    protected string LoadEntry(string selector, bool nearPlayer)
    {
        if (!TryGetPlugin(out var plugin, out var error))
            return error;
        if (!TryGetPlayerCaller(out var steamId, out var identityId, out _, out error))
            return error;

        MyCharacter character = null;
        if (nearPlayer || plugin.PluginConfig.DefaultLoadMode == HangarLoadMode.NearPlayer)
        {
            if (!TryGetCharacter(out _, out _, out _, out character, out error))
                return error;
        }

        return plugin.Storage.TryLoadPlayerGrid(
            steamId,
            identityId,
            character,
            selector,
            nearPlayer,
            out var entry,
            out error)
            ? $"Loaded {entry.GridName} from hangar."
            : error;
    }

    protected string RemoveEntry(string selector)
    {
        if (!TryGetPlugin(out var plugin, out var error))
            return error;
        if (!TryGetPlayerCaller(out var steamId, out _, out _, out error))
            return error;

        return plugin.Storage.TryRemovePlayerEntry(steamId, selector, out var entry, out error)
            ? $"Removed {entry.GridName} from hangar."
            : error;
    }

    protected IEnumerable<string> StatusLines()
    {
        if (!TryGetPlugin(out var plugin, out var error))
            return new[] { error };

        var config = plugin.PluginConfig;
        var counts = plugin.Storage.Counts;
        return new[]
        {
            $"enabled: {config.Enabled}",
            $"storage_root: {plugin.Storage.StorageRoot}",
            $"entries: {counts.Entries}",
            $"cooldowns: {counts.Cooldowns}",
            $"include_connected_grids: {config.IncludeConnectedGrids}",
            $"remove_original_on_save: {config.RemoveOriginalOnSave}",
            $"default_load_mode: {config.DefaultLoadMode}",
            $"block_vanilla_grid_storage: {config.BlockVanillaGridStorage}",
            $"block_vanilla_hangar_commands: {config.BlockVanillaHangarCommands}",
            $"blocked_hangar_roots: {string.Join(", ", config.BlockedHangarCommandRoots)}",
        };
    }

    protected string EnablePlugin()
    {
        if (!TryGetPlugin(out var plugin, out var error))
            return error;

        plugin.SetEnabled(true);
        return "Hangar enabled.";
    }

    protected string DisablePlugin()
    {
        if (!TryGetPlugin(out var plugin, out var error))
            return error;

        plugin.SetEnabled(false);
        return "Hangar disabled.";
    }

    private bool TryGetPlugin(out Plugin plugin, out string error)
    {
        plugin = Plugin.Instance;
        error = null;
        if (plugin == null || plugin.Storage == null)
        {
            error = "Hangar is not loaded.";
            return false;
        }

        return true;
    }

    private bool TryGetPlayerCaller(out ulong steamId, out long identityId, out string callerName, out string error)
    {
        steamId = 0;
        identityId = 0;
        callerName = "";
        error = null;
        var caller = Context.Caller;
        if (caller.IsConsole || caller.SteamId == 0)
        {
            error = "This command can only be used by a player.";
            return false;
        }

        steamId = caller.SteamId;
        identityId = caller.IdentityId;
        callerName = caller.Name;
        return true;
    }

    private bool TryGetCharacter(out ulong steamId, out long identityId, out string callerName, out MyCharacter character, out string error)
    {
        character = null;
        if (!TryGetPlayerCaller(out steamId, out identityId, out callerName, out error))
            return false;

        var caller = Context.Caller;
        if (MySession.Static?.Players == null ||
            !MySession.Static.Players.TryGetPlayerBySteamId(caller.SteamId, out var player) ||
            player?.Character == null)
        {
            error = "Player character not found.";
            return false;
        }

        character = player.Character as MyCharacter;
        if (character == null)
        {
            error = "Player character is not ready.";
            return false;
        }

        return true;
    }
}

[CommandRoot("hangar", "Hangar", "Save and load player grids from Quasar-managed storage")]
public sealed class HangarCommands : HangarCommandModule
{
    [Command("", "Lists stored grids")]
    [Permission(MyPromoteLevel.None)]
    public IEnumerable<string> ListDefault() => ListEntries();

    [Command("list", "Lists stored grids")]
    [Permission(MyPromoteLevel.None)]
    public IEnumerable<string> List() => ListEntries();

    [Command("info", "Shows one stored grid")]
    [Permission(MyPromoteLevel.None)]
    public IEnumerable<string> Info(string selector = "") => EntryInfo(selector);

    [Command("save", "Saves the looked-at grid or a named grid")]
    [Permission(MyPromoteLevel.None)]
    public string Save(params string[] gridNameOrEntityId) => SaveEntry(string.Join(" ", gridNameOrEntityId ?? Array.Empty<string>()));

    [Command("load", "Loads one stored grid. Add true to force near-player placement")]
    [Permission(MyPromoteLevel.None)]
    public string Load(string selector = "", bool nearPlayer = false) => LoadEntry(selector, nearPlayer);

    [Command("remove", "Deletes one stored grid")]
    [Permission(MyPromoteLevel.None)]
    public string Remove(string selector) => RemoveEntry(selector);

    [Command("delete", "Deletes one stored grid")]
    [Permission(MyPromoteLevel.None)]
    public string Delete(string selector) => RemoveEntry(selector);

    [Command("status", "Shows Hangar runtime state")]
    [Permission(MyPromoteLevel.Admin)]
    public IEnumerable<string> Status() => StatusLines();

    [Command("enable", "Enables Hangar")]
    [Permission(MyPromoteLevel.Admin)]
    public string Enable() => EnablePlugin();

    [Command("disable", "Disables Hangar")]
    [Permission(MyPromoteLevel.Admin)]
    public string Disable() => DisablePlugin();
}

[CommandRoot("h", "Hangar", "Short alias for Hangar")]
public sealed class HangarShortCommands : HangarCommandModule
{
    [Command("", "Lists stored grids")]
    [Permission(MyPromoteLevel.None)]
    public IEnumerable<string> ListDefault() => ListEntries();

    [Command("list", "Lists stored grids")]
    [Permission(MyPromoteLevel.None)]
    public IEnumerable<string> List() => ListEntries();

    [Command("info", "Shows one stored grid")]
    [Permission(MyPromoteLevel.None)]
    public IEnumerable<string> Info(string selector = "") => EntryInfo(selector);

    [Command("save", "Saves the looked-at grid or a named grid")]
    [Permission(MyPromoteLevel.None)]
    public string Save(params string[] gridNameOrEntityId) => SaveEntry(string.Join(" ", gridNameOrEntityId ?? Array.Empty<string>()));

    [Command("load", "Loads one stored grid. Add true to force near-player placement")]
    [Permission(MyPromoteLevel.None)]
    public string Load(string selector = "", bool nearPlayer = false) => LoadEntry(selector, nearPlayer);

    [Command("remove", "Deletes one stored grid")]
    [Permission(MyPromoteLevel.None)]
    public string Remove(string selector) => RemoveEntry(selector);

    [Command("delete", "Deletes one stored grid")]
    [Permission(MyPromoteLevel.None)]
    public string Delete(string selector) => RemoveEntry(selector);
}
