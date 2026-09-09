using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace TacticalStances;

[BepInPlugin("com.devin.tacticalstances", "Tactical Stances", "1.0.0")]
public class TacticalStancesPlugin : BaseUnityPlugin
{
    public static ManualLogSource Log;

    public static ConfigEntry<bool> EnableStances;
    public static ConfigEntry<float> TransitionSpeed;
    public static ConfigEntry<bool> AltScrollCycle;
    public static ConfigEntry<bool> InvertScroll;
    public static ConfigEntry<EStance> SavedStance;

    // Only one stamina config exposed — global multiplier
    public static ConfigEntry<float> StaminaDrainMultiplier;

    public static ConfigEntry<KeyboardShortcut> KeyLowReady;
    public static ConfigEntry<KeyboardShortcut> KeyHighReady;
    public static ConfigEntry<KeyboardShortcut> KeyActiveAim;

    // Stance visual offsets (still configurable)
    public static ConfigEntry<float> LowReadyPosX;
    public static ConfigEntry<float> LowReadyPosY;
    public static ConfigEntry<float> LowReadyPosZ;
    public static ConfigEntry<float> LowReadyRotX;
    public static ConfigEntry<float> LowReadyRotY;
    public static ConfigEntry<float> LowReadyRotZ;

    public static ConfigEntry<float> HighReadyPosX;
    public static ConfigEntry<float> HighReadyPosY;
    public static ConfigEntry<float> HighReadyPosZ;
    public static ConfigEntry<float> HighReadyRotX;
    public static ConfigEntry<float> HighReadyRotY;
    public static ConfigEntry<float> HighReadyRotZ;

    public static ConfigEntry<float> ActiveAimPosX;
    public static ConfigEntry<float> ActiveAimPosY;
    public static ConfigEntry<float> ActiveAimPosZ;
    public static ConfigEntry<float> ActiveAimRotX;
    public static ConfigEntry<float> ActiveAimRotY;
    public static ConfigEntry<float> ActiveAimRotZ;

    public static ConfigEntry<bool> EnableTacSprint;
    public static ConfigEntry<bool> EnableShoulderSwap;
    public static ConfigEntry<bool> EnableShoulderSwapMirroring;

    // === Internal balanced stamina constants (not exposed in config) ===
    // Low Ready: regen stamina 25% faster than vanilla (extra 5%)
    public const float LowReadyRegenRate = 1.25f;
    // Active Aim: drain at 5% of capacity per second (non-sprint)
    public const float ActiveAimDrainFrac = 0.05f;
    // Active Aim while sprinting: drain at 10% — 5% more than the default
    // 5% non-sprint rate, reflecting the extra effort while running.
    public const float ActiveAimSprintDrainFrac = 0.10f;
    // High Ready while sprinting: drain at 10% of capacity per second.
    // No drain when not sprinting — regen instead (resting the weapon).
    public const float HighReadySprintDrainFrac = 0.10f;
    // High Ready: gentle regen when not sprinting (resting the weapon)
    public const float HighReadyRegenRate = 0.5f;

    private static float _lastScrollTime;
    private static bool _wasEnabled;
    private static bool _wasTacSprinting;
    private static float _cachedWeaponSize = 1f;

    public static EStance DesiredStance { get; set; } = EStance.None;

    private void Awake()
    {
        Log = Logger;

        EnableStances = Config.Bind("a - General", "Enable Stances", true, "Toggle the stance system on/off");
        TransitionSpeed = Config.Bind("a - General", "Transition Speed", 7f, new ConfigDescription("How quickly the weapon moves to the stance", new AcceptableValueRange<float>(1f, 10f), Array.Empty<object>()));
        AltScrollCycle = Config.Bind("a - General", "Alt + Scroll to Cycle", true, "Hold LeftAlt and scroll to cycle stances");
        InvertScroll = Config.Bind("a - General", "Invert Scroll", false, "Reverse the scroll-wheel direction for cycling");
        SavedStance = Config.Bind("a - General", "Saved Stance", EStance.LowReady, "Last selected stance, persists across raids");

        // Only stamina config exposed — controls stance ARM stamina drain only.
        // Does NOT affect regen or sprint stamina.
        StaminaDrainMultiplier = Config.Bind("a - General", "Stamina Drain Multiplier", 0.6f, new ConfigDescription("Controls arm stamina DRAIN from stances only. Does not affect regen or sprint stamina. 1.0 = base drain, 0.0 = no arm stamina drain from stances, 2.0 = double drain.", new AcceptableValueRange<float>(0f, 2f), Array.Empty<object>()));

        EnableTacSprint = Config.Bind("a - General", "Enable Tactical Sprint Animation", true, "When in High Ready and sprinting, plays the tactical sprint animation (weapon held up while running)");
        EnableShoulderSwap = Config.Bind("a - General", "Enable Shoulder Swap on Lean", false, "When enabled, leaning left (Q) swaps weapon to left shoulder and leaning right (E) swaps back. Stance offsets mirror accordingly. When disabled, vanilla Alt+left/right shoulder swap only.");
        EnableShoulderSwapMirroring = Config.Bind("a - General", "Enable Shoulder Swap Mirroring", false, "Mirror stance offsets when on left shoulder.");

        KeyLowReady = Config.Bind("b - Hotkeys", "Low Ready Key", new KeyboardShortcut(KeyCode.None), "Key to switch to Low Ready");
        KeyHighReady = Config.Bind("b - Hotkeys", "High Ready Key", new KeyboardShortcut(KeyCode.None), "Key to switch to High Ready");
        KeyActiveAim = Config.Bind("b - Hotkeys", "Active Aim Key", new KeyboardShortcut(KeyCode.None), "Key to switch to Active Aim");

        string[] sections = { "c - Low Ready", "d - High Ready", "e - Active Aim" };
        BindStanceOffsets(sections[0], out LowReadyPosX, out LowReadyPosY, out LowReadyPosZ, out LowReadyRotX, out LowReadyRotY, out LowReadyRotZ, new Vector3(0.103662f, 0f, 0.06309859f), new Vector3(18.7324f, -7.098592f, -21.50235f));
        BindStanceOffsets(sections[1], out HighReadyPosX, out HighReadyPosY, out HighReadyPosZ, out HighReadyRotX, out HighReadyRotY, out HighReadyRotZ, new Vector3(0.01971831f, 0.03647888f, -0.2129578f), new Vector3(-30.91549f, 0f, 1.197183f));
        BindStanceOffsets(sections[2], out ActiveAimPosX, out ActiveAimPosY, out ActiveAimPosZ, out ActiveAimRotX, out ActiveAimRotY, out ActiveAimRotZ, new Vector3(0f, 0.06478873f, 0f), new Vector3(0f, 0f, 0f));

        DesiredStance = SavedStance.Value;
        if (EnableStances.Value)
        {
            PatchAll();
        }
        _wasEnabled = EnableStances.Value;
        Log.LogInfo("Tactical Stances v1.0.0 loaded");
    }

    private void Update()
    {
        if (!_wasEnabled && EnableStances.Value)
        {
            PatchAll();
        }
        _wasEnabled = EnableStances.Value;
        if (!EnableStances.Value) return;

        // Block stance changes while aiming down sights
        Player localPlayer = GetLocalPlayer();
        bool isAiming = false;
        if (localPlayer != null)
        {
            try
            {
                isAiming = localPlayer.HandsController is Player.FirearmController fc && fc.IsAiming;
            }
            catch { }
        }

        if (!isAiming)
        {
            if (IsShortcutPressed(KeyLowReady)) SetStance(EStance.LowReady);
            if (IsShortcutPressed(KeyHighReady)) SetStance(EStance.HighReady);
            if (IsShortcutPressed(KeyActiveAim)) SetStance(EStance.ActiveAim);

            if (AltScrollCycle.Value && Input.GetKey(KeyCode.LeftAlt))
            {
                float scroll = Input.mouseScrollDelta.y;
                if (InvertScroll.Value) scroll = -scroll;
                if (scroll != 0f && Time.time - _lastScrollTime > 0.15f)
                {
                    _lastScrollTime = Time.time;
                    if (DesiredStance == EStance.None)
                    {
                        SetStance(EStance.ActiveAim);
                        return;
                    }
                    int idx = DesiredStance switch
                    {
                        EStance.LowReady => 0,
                        EStance.ActiveAim => 1,
                        EStance.HighReady => 2,
                        _ => 1,
                    };
                    if (scroll > 0f && idx < 2) idx++;
                    else if (scroll < 0f && idx > 0) idx--;
                    SetStance(idx switch
                    {
                        0 => EStance.LowReady,
                        1 => EStance.ActiveAim,
                        2 => EStance.HighReady,
                        _ => EStance.ActiveAim,
                    });
                }
            }
        }

        HandleTacSprint();

        // Tick the stance system — updates transition progress and offsets.
        // The offsets are applied via Spring.Get postfix patches, not here.
        if (localPlayer != null)
        {
            StanceManager.Tick(localPlayer, Mathf.Min(Time.unscaledDeltaTime, 0.05f));
        }
    }

    private static void HandleTacSprint()
    {
        if (!EnableTacSprint.Value)
        {
            if (_wasTacSprinting) RestoreWeaponSize();
            return;
        }
        Player localPlayer = GetLocalPlayer();
        if (localPlayer == null || localPlayer.HealthController == null || !localPlayer.HealthController.IsAlive)
        {
            if (_wasTacSprinting) RestoreWeaponSize();
            return;
        }
        bool isSprinting = false;
        try
        {
            MovementContext mc = localPlayer.MovementContext;
            isSprinting = mc != null && mc.IsSprintEnabled;
        }
        catch { }

        bool shouldTacSprint = (DesiredStance == EStance.HighReady) && isSprinting;
        if (shouldTacSprint && !_wasTacSprinting)
        {
            try { _cachedWeaponSize = localPlayer.BodyAnimatorCommon.GetFloat(PlayerAnimator.WEAPON_SIZE_MODIFIER_PARAM_HASH); }
            catch { _cachedWeaponSize = 1f; }
            localPlayer.BodyAnimatorCommon.SetFloat(PlayerAnimator.WEAPON_SIZE_MODIFIER_PARAM_HASH, 2f);
            _wasTacSprinting = true;
        }
        else if (shouldTacSprint && _wasTacSprinting)
        {
            localPlayer.BodyAnimatorCommon.SetFloat(PlayerAnimator.WEAPON_SIZE_MODIFIER_PARAM_HASH, 2f);
        }
        else if (!shouldTacSprint && _wasTacSprinting)
        {
            RestoreWeaponSize();
        }
    }

    private static void RestoreWeaponSize()
    {
        Player localPlayer = GetLocalPlayer();
        if (localPlayer != null)
        {
            try { localPlayer.BodyAnimatorCommon.SetFloat(PlayerAnimator.WEAPON_SIZE_MODIFIER_PARAM_HASH, _cachedWeaponSize); }
            catch { }
        }
        _wasTacSprinting = false;
    }

    private void SetStance(EStance stance)
    {
        if (DesiredStance != stance)
        {
            DesiredStance = stance;
            SavedStance.Value = stance;
            Log.LogInfo("Tactical stance changed to: " + stance);
        }
    }

    private void PatchAll()
    {
        new Harmony("com.devin.tacticalstances").PatchAll();
        Log.LogInfo("Tactical Stances patched");
    }

    private void BindStanceOffsets(string section, out ConfigEntry<float> posX, out ConfigEntry<float> posY, out ConfigEntry<float> posZ, out ConfigEntry<float> rotX, out ConfigEntry<float> rotY, out ConfigEntry<float> rotZ, Vector3 defaultPos, Vector3 defaultRot)
    {
        posX = Config.Bind(section, "Position X", defaultPos.x, new ConfigDescription("Left/Right position offset (meters). Positions the default weapon pose in XYZ. Layers on top of TarkovRL procedural motion.", new AcceptableValueRange<float>(-0.3f, 0.3f), Array.Empty<object>()));
        posY = Config.Bind(section, "Position Y", defaultPos.y, new ConfigDescription("Up/Down position offset (meters). Positions the default weapon pose in XYZ. Layers on top of TarkovRL procedural motion.", new AcceptableValueRange<float>(-0.3f, 0.3f), Array.Empty<object>()));
        posZ = Config.Bind(section, "Position Z", defaultPos.z, new ConfigDescription("Forward/Back position offset (meters). Positions the default weapon pose in XYZ. Layers on top of TarkovRL procedural motion.", new AcceptableValueRange<float>(-0.3f, 0.3f), Array.Empty<object>()));
        rotX = Config.Bind(section, "Rotation X", defaultRot.x, new ConfigDescription("Pitch rotation offset (degrees). Rotates the weapon pose. Layers on top of TarkovRL procedural motion.", new AcceptableValueRange<float>(-60f, 60f), Array.Empty<object>()));
        rotY = Config.Bind(section, "Rotation Y", defaultRot.y, new ConfigDescription("Yaw rotation offset (degrees). Rotates the weapon pose. Layers on top of TarkovRL procedural motion.", new AcceptableValueRange<float>(-60f, 60f), Array.Empty<object>()));
        rotZ = Config.Bind(section, "Rotation Z", defaultRot.z, new ConfigDescription("Roll rotation offset (degrees). Rotates the weapon pose. Layers on top of TarkovRL procedural motion.", new AcceptableValueRange<float>(-60f, 60f), Array.Empty<object>()));
    }

    private static bool IsShortcutPressed(ConfigEntry<KeyboardShortcut> entry)
    {
        KeyboardShortcut shortcut = entry.Value;
        if (shortcut.MainKey == KeyCode.None) return false;
        if (Input.GetKeyDown(shortcut.MainKey))
        {
            foreach (KeyCode mod in shortcut.Modifiers)
            {
                if (!Input.GetKey(mod)) return false;
            }
            return true;
        }
        return false;
    }

    private static Player GetLocalPlayer()
    {
        try
        {
            GameWorld gw = Singleton<GameWorld>.Instance;
            if (gw?.MainPlayer != null && gw.MainPlayer.IsYourPlayer)
                return gw.MainPlayer;
        }
        catch { }
        return null;
    }
}
