using System.Collections.Generic;
using System.Reflection;
using EFT;
using HarmonyLib;

namespace TacticalStances;

/// <summary>
/// Blocks firing while Low Ready or High Ready is selected by swallowing the
/// trigger press before the firing pipeline starts. ADS and Active Aim fire
/// normally; trigger releases always pass through.
/// </summary>
[HarmonyPatch]
public static class FireBlockPatch
{
    private static readonly FieldInfo _playerField = AccessTools.Field(typeof(Player.FirearmController), "_player");

    // Every weapon type has its own FirearmController subclass that overrides
    // SetTriggerPressed — patch each declared override plus the base method.
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var baseType = typeof(Player.FirearmController);
        foreach (var t in baseType.Assembly.GetTypes())
        {
            if (!baseType.IsAssignableFrom(t)) continue;
            var m = AccessTools.DeclaredMethod(t, "SetTriggerPressed");
            if (m != null) yield return m;
        }
    }

    [HarmonyPrefix]
    private static void Prefix(Player.FirearmController __instance, ref bool pressed)
    {
        if (!pressed)
            return;
        if (!TacticalStancesPlugin.EnableStances.Value || !TacticalStancesPlugin.BlockFireLowHigh.Value)
            return;

        var stance = TacticalStancesPlugin.DesiredStance;
        if (stance != EStance.LowReady && stance != EStance.HighReady)
            return;

        // ADS fires regularly regardless of stance.
        if (__instance.IsAiming)
            return;

        if ((_playerField?.GetValue(__instance) as Player)?.IsYourPlayer != true)
            return;

        pressed = false;
    }
}
