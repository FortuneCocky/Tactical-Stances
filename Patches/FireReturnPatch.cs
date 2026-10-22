using System.Reflection;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace TacticalStances;

[HarmonyPatch(typeof(Player.FirearmController), "InitiateShot")]
public static class FireReturnPatch
{
    private static readonly FieldInfo _playerField = AccessTools.Field(typeof(Player.FirearmController), "_player");

    // Prefix (not postfix): the recoil impulse (AddRecoilForce) runs inside
    // InitiateShot, so the fire override must already be active when the
    // shot's recoil is computed.
    [HarmonyPrefix]
    private static void Prefix(Player.FirearmController __instance)
    {
        if (!TacticalStancesPlugin.AutoNormalOnFire.Value)
            return;

        try
        {
            if ((_playerField?.GetValue(__instance) as Player)?.IsYourPlayer != true)
                return;
            TacticalStancesPlugin.NotifyShotFired();
        }
        catch { }
    }
}

// Holds the trigger press while "return to normal on fire" is still raising
// the weapon to the fire pose. The press is swallowed (the game never sees
// it) and re-delivered via SetTriggerPressed once the offsets converge —
// so the first bullet exits along the raised barrel instead of the lowered
// stance, and nothing is silently consumed. Releasing the trigger mid-raise
// cancels the pending press.
[HarmonyPatch(typeof(Player.FirearmController), "SetTriggerPressed")]
public static class TriggerHoldPatch
{
    private static readonly FieldInfo _playerField = AccessTools.Field(typeof(Player.FirearmController), "_player");

    private static Player.FirearmController _pendingController;
    private static float _pendingSince = -1f;
    private const float MaxHoldSeconds = 1.5f;

    [HarmonyPrefix]
    private static void Prefix(Player.FirearmController __instance, ref bool pressed)
    {
        if (!TacticalStancesPlugin.AutoNormalOnFire.Value)
            return;

        try
        {
            if ((_playerField?.GetValue(__instance) as Player)?.IsYourPlayer != true)
                return;

            if (!pressed)
            {
                _pendingController = null;
                return;
            }

            // No stance active, already converged, or canted — fire normally.
            if (TacticalStancesPlugin.DesiredStance == EStance.None ||
                TacticalStancesPlugin.CantedActive ||
                !StanceManager.OffsetBeyondThreshold)
            {
                _pendingController = null;
                return;
            }

            // Start the raise now so the weapon moves to the fire pose
            // before the shot is delivered.
            TacticalStancesPlugin.NotifyShotFired();
            _pendingController = __instance;
            _pendingSince = Time.time;
            pressed = false;
        }
        catch { }
    }

    /// <summary>Called every frame from the plugin Update. Delivers the held
    /// press once the weapon has converged on the fire pose.</summary>
    internal static void DeliverPending()
    {
        if (_pendingController == null)
            return;

        if (Time.time - _pendingSince > MaxHoldSeconds)
        {
            _pendingController = null;
            return;
        }

        if (!StanceManager.OffsetBeyondThreshold)
        {
            Player.FirearmController fc = _pendingController;
            _pendingController = null;
            try { fc.SetTriggerPressed(true); }
            catch { }
        }
    }

    internal static void Clear()
    {
        _pendingController = null;
    }
}
