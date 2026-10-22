using System;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace TacticalStances;

/// <summary>
/// Weapon-weight swap speed. Spawn() drives the draw/equip animation via
/// FirearmsAnimator.SetAnimationSpeed — scaling its animationSpeed argument
/// makes a pistol snap out fast and an LMG come up heavy. Speed multiplier =
/// ReferenceWeight / weapon weight, clamped to the configured range.
/// </summary>
internal static class WeaponSwapSpeed
{
    public static float GetMul(Player player, EFT.InventoryLogic.Item item)
    {
        if (player == null || !player.IsYourPlayer || item == null) return 1f;

        float kg;
        try { kg = Mathf.Max(0.3f, item.Weight); }
        catch { return 1f; }

        // SwapTime is normalized: 1.0 = natural draw for a ~4kg rifle.
        // Weapon weight scales it percent-wise on top: 9kg LMG draws 2.25x
        // slower than the set speed, a 1kg pistol 4x faster — no caps.
        // (Internally the natural rate maps to a 3x duration factor.)
        float swapTime = Mathf.Max(0.1f, TacticalStancesPlugin.SwapTime.Value);
        return 1f / (swapTime * 3f * (kg / 4f));
    }
}

[HarmonyPatch(typeof(Player.FirearmController), "Spawn")]
public static class FirearmSwapSpeedPatch
{
    [HarmonyPrefix]
    private static void Prefix(Player.FirearmController __instance, ref float animationSpeed, ref Action callback)
    {
        try
        {
            animationSpeed *= WeaponSwapSpeed.GetMul(__instance._player, __instance.Item);

            // SetAnimationSpeed writes the persistent Animator.speed — without
            // resetting it, the swap multiplier leaks into reloads and every
            // later animation. Restore 1x once the draw completes.
            var fc = __instance;
            Action orig = callback;
            callback = () =>
            {
                try { orig?.Invoke(); }
                finally
                {
                    try { fc.FirearmsAnimator?.SetAnimationSpeed(1f); } catch { }
                }
            };
        }
        catch { }
    }
}
