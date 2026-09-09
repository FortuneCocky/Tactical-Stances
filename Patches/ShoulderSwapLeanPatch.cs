using EFT;
using HarmonyLib;
using UnityEngine;

namespace TacticalStances;

[HarmonyPatch(typeof(Player), "ToggleLean")]
public static class ShoulderSwapLeanPatch
{
    [HarmonyPostfix]
    private static void Postfix(Player __instance, float dir)
    {
        if (!TacticalStancesPlugin.EnableShoulderSwap.Value || __instance == null || !__instance.IsYourPlayer)
            return;

        MovementContext mc = __instance.MovementContext;
        if (mc == null || !mc.LeftStanceEnabled)
            return;

        bool shouldSwap;
        if (dir > 0f)
        {
            shouldSwap = !mc.LeftStanceController.LeftStance;
        }
        else if (dir < 0f)
        {
            shouldSwap = mc.LeftStanceController.LeftStance;
        }
        else
        {
            return;
        }

        if (shouldSwap)
        {
            Player.AbstractHandsController handsController = __instance.HandsController;
            if (handsController is Player.FirearmController fc)
            {
                fc.ChangeLeftStance();
            }
        }
    }
}
