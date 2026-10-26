using System;
using System.Linq;
using HarmonyLib;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using VRage.Network;

namespace ServerPlugin;

[HarmonyPatch(typeof(MyMultiplayerBase), "OnChatMessageReceived_Server")]
[HarmonyPatch([typeof(ChatMsg)])]
public static class VanillaHangarBlockPatch
{
    [HarmonyPriority(Priority.Last)]
    public static bool Prefix(ChatMsg msg)
    {
        try
        {
            var config = Plugin.Instance?.PluginConfig;
            if (config is not { Enabled: true, BlockVanillaHangarCommands: true })
                return true;

            if (msg.Channel != (byte)ChatChannel.Global)
                return true;

            if (!TryGetRoot(msg.Text, out var root))
                return true;

            if (
                !config.BlockedHangarCommandRoots.Any(blocked =>
                    string.Equals(blocked?.Trim(), root, StringComparison.OrdinalIgnoreCase)
                )
            )
                return true;

            RespondBlocked(msg);
            return false;
        }
        catch (Exception exception)
        {
            Plugin.Instance?.Log.Warning(exception, "Vanilla hangar command blocker failed.");
            return true;
        }
    }

    private static bool TryGetRoot(string text, out string root)
    {
        root = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = text.TrimStart();
        if (text.Length < 2 || (text[0] != '!' && text[0] != '/'))
            return false;

        var command = text.Substring(1).TrimStart();
        if (command.Length == 0)
            return false;

        var separator = command.IndexOfAny([' ', '\t', '\r', '\n']);
        root = separator < 0 ? command : command.Substring(0, separator);
        return root.Length > 0;
    }

    private static void RespondBlocked(ChatMsg msg)
    {
        var sender = MyEventContext.Current.Sender.Value;
        if (sender == 0)
            return;

        var identityId = MySession.Static?.Players?.TryGetIdentityId(sender) ?? 0;
        if (identityId == 0)
            return;

        MyVisualScriptLogicProvider.SendChatMessage(
            "Vanilla hangar commands are disabled. Use !hangar.",
            Plugin.PluginName,
            identityId
        );
    }
}
