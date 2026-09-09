using Comfort.Common;
using EFT;
using EFT.CameraControl;
using HarmonyLib;
using UnityEngine;

namespace TacticalStances;

[HarmonyPatch(typeof(PlayerCameraController), "LateUpdate")]
public static class ShoulderSwapResetPatch
{
    [HarmonyPrefix]
    private static void Prefix(PlayerCameraController __instance)
    {
        if (!TacticalStancesPlugin.EnableShoulderSwap.Value)
            return;

        try
        {
            GameWorld gw = Singleton<GameWorld>.Instance;
            if (gw?.MainPlayer == null || !gw.MainPlayer.IsYourPlayer)
                return;

            Player mainPlayer = gw.MainPlayer;
            MovementContext mc = mainPlayer.MovementContext;
            if (mc != null && mc.LeftStanceEnabled && Mathf.Abs(mc.Tilt) < 0.01f && mc.LeftStanceController.LeftStance)
            {
                Player.AbstractHandsController handsController = mainPlayer.HandsController;
                if (handsController is Player.FirearmController fc)
                {
                    fc.ChangeLeftStance();
                }
            }
        }
        catch { }
    }
}
