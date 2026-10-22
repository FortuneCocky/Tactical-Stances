using System.Reflection;
using EFT;
using EFT.Animations.NewRecoil;
using HarmonyLib;
using UnityEngine;

namespace TacticalStances;

/// <summary>
/// Stance gameplay bonuses. All three key off the SELECTED stance
/// (DesiredStance), not the visual pose — the pose is suppressed during
/// sprint/ADS, but the stance's gameplay benefit should still apply.
///
/// Low Ready: faster sprint acceleration (weapon held low, free to run).
/// High Ready: faster movement speed (aggressive stance).
/// Active Aim: faster ADS movement speed + weaker recoil than hipfire.
/// </summary>

[HarmonyPatch(typeof(MovementContext), "SprintAcceleration")]
public static class StanceSprintAccelPatch
{
    private static readonly FieldInfo _playerField = AccessTools.Field(typeof(MovementContext), "_player");

    // Runs after vanilla adds this frame's acceleration. We add the bonus
    // portion on top, clamped to the same sprint speed limit vanilla used.
    [HarmonyPostfix]
    private static void Postfix(MovementContext __instance, float deltaTime)
    {
        if (!TacticalStancesPlugin.EnableStances.Value)
            return;
        if (TacticalStancesPlugin.DesiredStance != EStance.LowReady)
            return;
        if (!(_playerField?.GetValue(__instance) is Player player) || !player.IsYourPlayer || player.Physical == null)
            return;

        float limit = (player.Physical.SprintSpeed * __instance.SprintingSpeed + 1f) * __instance.StateSprintSpeedLimit;
        float extra = player.Physical.SprintAcceleration * (TacticalStancesPlugin.LowReadySprintAccel.Value - 1f) * deltaTime;
        __instance.SprintSpeed = Mathf.Clamp(
            __instance.SprintSpeed + extra * Mathf.Sign(limit - __instance.SprintSpeed),
            0.01f, limit);
    }
}

[HarmonyPatch(typeof(MovementContext), "ClampSpeed")]
public static class StanceMoveSpeedPatch
{
    private static readonly FieldInfo _playerField = AccessTools.Field(typeof(MovementContext), "_player");

    // Vanilla caps speed at StateSpeedLimit; we raise the cap while High
    // Ready is selected so all movement (walk, strafe, crouch) is faster.
    [HarmonyPostfix]
    private static void Postfix(MovementContext __instance, float speed, ref float __result)
    {
        if (!TacticalStancesPlugin.EnableStances.Value)
            return;
        if (TacticalStancesPlugin.DesiredStance != EStance.HighReady)
            return;
        if (!(_playerField?.GetValue(__instance) is Player player) || !player.IsYourPlayer)
            return;

        __result = Mathf.Clamp(speed, 0f, __instance.StateSpeedLimit * TacticalStancesPlugin.HighReadyMoveSpeed.Value);
    }
}

[HarmonyPatch(typeof(MovementContext), "SetAimingSlowdown")]
public static class StanceAdsSpeedPatch
{
    private static readonly FieldInfo _playerField = AccessTools.Field(typeof(MovementContext), "_player");

    // Vanilla just wrote the Aiming entry into _speedLimits. AddStateSpeedLimit
    // only applies to keys that don't exist yet, so we scale the stored limit
    // directly and mark it dirty so StateSpeedLimit is recomputed.
    [HarmonyPostfix]
    private static void Postfix(MovementContext __instance, bool isAiming)
    {
        if (!isAiming || !TacticalStancesPlugin.EnableStances.Value)
            return;
        if (TacticalStancesPlugin.DesiredStance != EStance.ActiveAim)
            return;
        if (!(_playerField?.GetValue(__instance) is Player player) || !player.IsYourPlayer)
            return;

        if (__instance._speedLimits.TryGetValue(Player.ESpeedLimit.Aiming, out float limit))
        {
            __instance._speedLimits[Player.ESpeedLimit.Aiming] =
                Mathf.Clamp(limit * TacticalStancesPlugin.ActiveAimAdsSpeed.Value, 0f, 2f);
            __instance.method_5();
        }
    }
}

[HarmonyPatch(typeof(NewRecoilShotEffect), "AddRecoilForce")]
public static class StanceRecoilPatch
{
    private static readonly FieldInfo _playerField = AccessTools.Field(typeof(Player.FirearmController), "_player");

    // incomingForce feeds both rotation and position recoil power — scaling
    // it weakens the whole recoil impulse while Active Aim is selected.
    [HarmonyPrefix]
    private static void Prefix(NewRecoilShotEffect __instance, ref float incomingForce)
    {
        if (!TacticalStancesPlugin.EnableStances.Value)
            return;
        // EffectiveStance counts the fire-return pose: shots fired from
        // Low/High Ready while the weapon transitions to Active Aim get
        // the Active Aim recoil cut, not vanilla hipfire recoil.
        EStance stance = TacticalStancesPlugin.EffectiveStance;
        if (stance != EStance.ActiveAim && stance != EStance.CantedAim)
            return;
        if (__instance?._firearmController == null)
            return;

        // Only weaken while firing from the stance — ADS keeps vanilla recoil.
        if (__instance._firearmController.IsAiming)
            return;
        if (!(_playerField?.GetValue(__instance._firearmController) is Player player) || !player.IsYourPlayer)
            return;

        incomingForce *= stance == EStance.CantedAim
            ? TacticalStancesPlugin.CantedAimRecoil.Value
            : TacticalStancesPlugin.ActiveAimRecoil.Value;
    }
}
