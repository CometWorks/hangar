using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game;
using Sandbox.Game.EntityComponents.Interfaces;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using SpaceEngineers.Game.EntityComponents.Blocks;
using SpaceEngineers.Game.SessionComponents;
using VRage.Network;

namespace ServerPlugin;

internal static class VanillaGridStorageBlock
{
    public const string Message = "Vanilla grid storage is disabled. Use !hangar.";

    public static bool Enabled =>
        Plugin.Instance?.PluginConfig is { Enabled: true, BlockVanillaGridStorage: true };

    public static void NotifyCaller()
    {
        var sender = MyEventContext.Current.Sender.Value;
        if (sender != 0)
            NotifyCaller(sender);
    }

    public static void NotifyCaller(ulong endpointId)
    {
        if (endpointId == 0)
            return;

        var identityId = MySession.Static?.Players?.TryGetIdentityId(endpointId) ?? 0;
        if (identityId == 0)
            return;

        MyVisualScriptLogicProvider.SendChatMessage(Message, Plugin.PluginName, identityId);
    }

    public static ulong? CurrentSenderEndpoint()
    {
        var sender = MyEventContext.Current.Sender.Value;
        return sender == 0 ? null : sender;
    }
}

[HarmonyPatch(typeof(MyGridsStorageEntityComponent))]
public static class VanillaGridStorageRpcBlockPatch
{
    private static readonly string[] BlockedServerEndpoints =
    [
        "GetStoredGridsEndpoint",
        "GetGridsInDesignatedArea",
        "ValidateRetrievalEndpoint",
        "ValidateGridEndpoint",
        "SetOwnershipKindEndpoint",
        "DeleteGridEndpoint",
        "GetAutoDeployStateEndpoint",
        "SetAutoDeploySwitchEndpoint",
        "GetOrdersEndpoint",
        "CancelCurrentClientOrderEndpoint",
        "ConfirmDeployEndpoint",
        "ExpediteCurrentClientOrderEndpoint",
        "StoreGridTrusted",
        "RetrieveGridTrusted",
    ];

    public static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var methodName in BlockedServerEndpoints)
        {
            var method = AccessTools.Method(typeof(MyGridsStorageEntityComponent), methodName);
            if (method != null)
                yield return method;
        }
    }

    public static bool Prefix(MyGridsStorageEntityComponent __instance, MethodBase __originalMethod)
    {
        if (!VanillaGridStorageBlock.Enabled)
            return true;

        var endpoint = VanillaGridStorageBlock.CurrentSenderEndpoint();
        VanillaGridStorageBlock.NotifyCaller(endpoint ?? 0);

        switch (__originalMethod.Name)
        {
            case "StoreGridTrusted":
                __instance.OnGridStorageDepositRequestFinished(
                    MyGridStorageRequestResult.UnspecifiedStorageIssue,
                    endpoint
                );
                break;
            case "RetrieveGridTrusted":
                __instance.OnGridStorageRetrievalRequestFinished(
                    MyGridStorageRequestResult.UnspecifiedStorageIssue,
                    endpoint
                );
                break;
        }

        return false;
    }
}

[HarmonyPatch(
    typeof(MyGridsStorageSessionComponent),
    nameof(MyGridsStorageSessionComponent.StoreGrid)
)]
public static class VanillaGridStorageSessionStoreBlockPatch
{
    public static bool Prefix(
        IMyGridStorageProxy callerEntity,
        ulong? callerEndpointId,
        Action<MyGridStorageRequestResult> callback
    )
    {
        if (!VanillaGridStorageBlock.Enabled)
            return true;

        if (callerEndpointId.HasValue)
            VanillaGridStorageBlock.NotifyCaller(callerEndpointId.Value);

        callback?.Invoke(MyGridStorageRequestResult.UnspecifiedStorageIssue);
        callerEntity?.OnGridStorageDepositRequestFinished(
            MyGridStorageRequestResult.UnspecifiedStorageIssue,
            callerEndpointId
        );
        return false;
    }
}

[HarmonyPatch(
    typeof(MyGridsStorageSessionComponent),
    nameof(MyGridsStorageSessionComponent.RetrieveGrid)
)]
public static class VanillaGridStorageSessionRetrieveBlockPatch
{
    public static bool Prefix(
        IMyGridStorageProxy callerEntity,
        ulong? callerEndpointId,
        Action<MyGridStorageRequestResult> callback
    )
    {
        if (!VanillaGridStorageBlock.Enabled)
            return true;

        if (callerEndpointId.HasValue)
            VanillaGridStorageBlock.NotifyCaller(callerEndpointId.Value);

        callback?.Invoke(MyGridStorageRequestResult.UnspecifiedStorageIssue);
        callerEntity?.OnGridStorageRetrievalRequestFinished(
            MyGridStorageRequestResult.UnspecifiedStorageIssue,
            callerEndpointId
        );
        return false;
    }
}

[HarmonyPatch(
    typeof(MyGridsStorageSessionComponent),
    nameof(MyGridsStorageSessionComponent.DeleteGrid)
)]
public static class VanillaGridStorageSessionDeleteBlockPatch
{
    public static bool Prefix(ref bool __result)
    {
        if (!VanillaGridStorageBlock.Enabled)
            return true;

        __result = false;
        return false;
    }
}

[HarmonyPatch(
    typeof(MyGridsStorageSessionComponent),
    nameof(MyGridsStorageSessionComponent.EnqueueOrder)
)]
public static class VanillaGridStorageSessionOrderBlockPatch
{
    public static bool Prefix()
    {
        return !VanillaGridStorageBlock.Enabled;
    }
}

[HarmonyPatch(
    typeof(MyGridsStorageSessionComponent),
    nameof(MyGridsStorageSessionComponent.ConfirmDeploy)
)]
public static class VanillaGridStorageSessionDeployBlockPatch
{
    public static bool Prefix(ref bool __result)
    {
        if (!VanillaGridStorageBlock.Enabled)
            return true;

        __result = false;
        return false;
    }
}
