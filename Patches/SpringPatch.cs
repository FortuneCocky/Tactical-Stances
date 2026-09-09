using System.Reflection;
using EFT;
using EFT.Animations;
using HarmonyLib;
using UnityEngine;

namespace TacticalStances;

/// <summary>
/// LAYERED APPROACH: TacticalStances slaps over TarkovRL.
///
/// IK FIX: The stance offset is applied in Player.IkProcess PREFIX,
/// before the IK system reads the left hand marker. This way the IK
/// solver sees the stance-offset weapon position and places the left
/// hand correctly on the gun.
///
/// VISUAL: The offset is re-applied in Player.LateUpdate POSTFIX in
/// case LateTransformations overwrote WeaponRootAnim. The base position
/// captured in the IkProcess prefix is reused so we re-apply from the
/// same known base.
///
/// Both position and rotation are applied directly to WeaponRootAnim
/// (child of WeaponRoot). TarkovRL's procedural motion on the parent
/// WeaponRoot layers underneath.
/// </summary>

/// <summary>
/// Apply stance offset BEFORE IkProcess reads the left hand marker.
/// This is the critical IK fix — the marker (child of WeaponRootAnim)
/// moves with our offset, so the IK solver places the left hand at the
/// correct stance-offset position.
/// </summary>
[HarmonyPatch(typeof(Player), "IkProcess")]
[HarmonyPriority(Priority.High)]
public static class StanceIkProcessPatch
{
    [HarmonyPrefix]
    private static void Prefix(Player __instance)
    {
        if (!TacticalStancesPlugin.EnableStances.Value)
            return;
        if (__instance == null || !__instance.IsYourPlayer)
            return;
        if (__instance.ProceduralWeaponAnimation?.HandsContainer?.WeaponRootAnim == null)
            return;

        StanceManager.ApplyStanceOffsetForIK(__instance.ProceduralWeaponAnimation.HandsContainer.WeaponRootAnim);
    }
}

/// <summary>
/// Re-apply stance offset for visual rendering after all game animation.
/// LateTransformations may have overwritten WeaponRootAnim, so we reset
/// to the captured base and re-apply.
/// </summary>
[HarmonyPatch(typeof(Player), "LateUpdate")]
[HarmonyPriority(Priority.Low)]
public static class StanceLateUpdatePostfixPatch
{
    [HarmonyPostfix]
    private static void Postfix(Player __instance)
    {
        if (!TacticalStancesPlugin.EnableStances.Value)
            return;
        if (__instance == null || !__instance.IsYourPlayer)
            return;
        if (__instance.ProceduralWeaponAnimation?.HandsContainer?.WeaponRootAnim == null)
            return;

        StanceManager.ApplyStanceOffsetForVisual(__instance.ProceduralWeaponAnimation.HandsContainer.WeaponRootAnim);
    }
}

/// <summary>
/// Cache PWA reference when weapon variables are updated.
/// </summary>
[HarmonyPatch(typeof(ProceduralWeaponAnimation), nameof(ProceduralWeaponAnimation.UpdateWeaponVariables))]
[HarmonyPriority(1)]
public static class StanceWeaponVariablesPatch
{
    private static readonly FieldInfo _fcField = AccessTools.Field(typeof(ProceduralWeaponAnimation), "_firearmController");
    private static readonly FieldInfo _playerField = AccessTools.Field(typeof(Player.FirearmController), "_player");

    [HarmonyPostfix]
    private static void Postfix(ProceduralWeaponAnimation __instance)
    {
        if (__instance?.HandsContainer == null)
            return;

        object fc = _fcField?.GetValue(__instance);
        if (fc == null)
            return;

        Player player = _playerField?.GetValue(fc) as Player;
        if (player == null || !player.IsYourPlayer)
            return;

        StanceManager.CacheSprings(null, null, __instance);
    }
}
