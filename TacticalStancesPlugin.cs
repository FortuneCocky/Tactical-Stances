using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace TacticalStances;

[BepInPlugin("com.devin.tacticalstances", "Tactical Stances", "1.1.0")]
public class TacticalStancesPlugin : BaseUnityPlugin
{
    public static ManualLogSource Log;

    public static ConfigEntry<bool> EnableStances;
    public static ConfigEntry<float> TransitionSpeed;
    public static ConfigEntry<float> SprintBlendIn;
    public static ConfigEntry<float> SprintBlendOut;
    public static ConfigEntry<bool> AltScrollCycle;
    public static ConfigEntry<bool> InvertScroll;
    public static ConfigEntry<EStance> SavedStance;
    public static ConfigEntry<bool> AutoNormalOnFire;
    public static ConfigEntry<float> FireReturnDelay;
    public static ConfigEntry<EStance> FireReturnStance;
    public static ConfigEntry<bool> BlockFireLowHigh;

    // Only one stamina config exposed — global multiplier
    public static ConfigEntry<float> StaminaDrainMultiplier;

    // Weapon-weight stamina scaling
    public static ConfigEntry<bool> EnableWeightStamina;
    public static ConfigEntry<float> WeightReferenceKg;
    public static ConfigEntry<float> WeightMinMul;
    public static ConfigEntry<float> WeightMaxMul;
    public static ConfigEntry<float> SwapTime;

    public static ConfigEntry<KeyboardShortcut> KeyLowReady;
    public static ConfigEntry<KeyboardShortcut> KeyHighReady;
    public static ConfigEntry<KeyboardShortcut> KeyActiveAim;
    public static ConfigEntry<KeyboardShortcut> KeyCantedAim;
    public static ConfigEntry<bool> CantedAimToggle;

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

    public static ConfigEntry<float> CantedAimPosX;
    public static ConfigEntry<float> CantedAimPosY;
    public static ConfigEntry<float> CantedAimPosZ;
    public static ConfigEntry<float> CantedAimRotX;
    public static ConfigEntry<float> CantedAimRotY;
    public static ConfigEntry<float> CantedAimRotZ;

    // Pistol-specific stances — separate from long guns (no canted for pistols)
    public static ConfigEntry<float> PistolLowPosX;
    public static ConfigEntry<float> PistolLowPosY;
    public static ConfigEntry<float> PistolLowPosZ;
    public static ConfigEntry<float> PistolLowRotX;
    public static ConfigEntry<float> PistolLowRotY;
    public static ConfigEntry<float> PistolLowRotZ;

    public static ConfigEntry<float> PistolHighPosX;
    public static ConfigEntry<float> PistolHighPosY;
    public static ConfigEntry<float> PistolHighPosZ;
    public static ConfigEntry<float> PistolHighRotX;
    public static ConfigEntry<float> PistolHighRotY;
    public static ConfigEntry<float> PistolHighRotZ;

    public static ConfigEntry<float> PistolActivePosX;
    public static ConfigEntry<float> PistolActivePosY;
    public static ConfigEntry<float> PistolActivePosZ;
    public static ConfigEntry<float> PistolActiveRotX;
    public static ConfigEntry<float> PistolActiveRotY;
    public static ConfigEntry<float> PistolActiveRotZ;

    public static ConfigEntry<bool> EnableLeanShoulderSwap;
    public static ConfigEntry<float> LeanSwapPosX;
    public static ConfigEntry<float> LeanSwapPosY;
    public static ConfigEntry<float> LeanSwapPosZ;
    public static ConfigEntry<float> LeanSwapRotX;
    public static ConfigEntry<float> LeanSwapRotY;
    public static ConfigEntry<float> LeanSwapRotZ;

    public static ConfigEntry<bool> EnableTacSprint;

    // Elbow IK — moves the arm bend hints with the stance offset so the
    // elbow drops with the gun instead of sticking out sideways.


    // Stance gameplay bonuses
    public static ConfigEntry<float> LowReadySprintAccel;
    public static ConfigEntry<float> HighReadyMoveSpeed;
    public static ConfigEntry<float> ActiveAimAdsSpeed;
    public static ConfigEntry<float> ActiveAimRecoil;
    public static ConfigEntry<float> CantedAimRecoil;
    public static ConfigEntry<bool> CantedAutoSideSight;

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
    private static bool _cantedToggled;
    private static bool _cantedWanted;
    private static bool _cantedBlocked;
    private static bool _prevIsAiming;
    private static float _cachedWeaponSize = 1f;
    private static int _preCantedAimIndex = -1;
    private static Item _cantedSwapWeapon;
    private static bool _cantedAutoAimed;

    /// <summary>True while the camera is aimed through a real side-mounted
    /// sight — the custom canted pose offsets are suppressed in that case.</summary>
    public static bool CantedSightSwapped { get; private set; }

    public static EStance DesiredStance { get; set; } = EStance.None;

    /// <summary>True while the canted-aim hotkey is held (or toggled on).
    /// Canted aim is a momentary overlay — it never changes DesiredStance,
    /// so releasing the key returns to whatever stance was selected.</summary>
    public static bool CantedActive { get; private set; }

    private static float _fireOverrideUntil;

    /// <summary>True while a shot has temporarily returned the weapon to the fire-return pose.</summary>
    public static bool FireOverrideActive => AutoNormalOnFire != null && AutoNormalOnFire.Value && Time.time < _fireOverrideUntil;

    /// <summary>The pose the weapon is actually in — canted aim while its key
    /// is held, the fire-return stance during the fire override window,
    /// otherwise the selected stance.</summary>
    public static EStance EffectiveStance =>
        CantedActive
            ? EStance.CantedAim
            : FireOverrideActive && DesiredStance != EStance.None && FireReturnStance != null
                ? FireReturnStance.Value
                : DesiredStance;

    public static void NotifyShotFired()
    {
        _fireOverrideUntil = Time.time + FireReturnDelay.Value;
    }

    private void Awake()
    {
        Log = Logger;

        EnableStances = Config.Bind("a - General", "Enable Stances", true, "Toggle the stance system on/off");
        TransitionSpeed = Config.Bind("a - General", "Transition Speed", 1.8f, new ConfigDescription("How quickly the weapon moves to the stance", new AcceptableValueRange<float>(1f, 10f), Array.Empty<object>()));
        SprintBlendIn = Config.Bind("a - General", "Sprint Blend-In Speed", 12f, new ConfigDescription("How fast stance offsets fade out as the sprint animation takes over", new AcceptableValueRange<float>(1f, 30f), Array.Empty<object>()));
        SprintBlendOut = Config.Bind("a - General", "Sprint Blend-Out Speed", 4.5f, new ConfigDescription("How fast the stance fades back in after sprint ends — lower = smoother/slower", new AcceptableValueRange<float>(1f, 30f), Array.Empty<object>()));
        AltScrollCycle = Config.Bind("a - General", "Alt + Scroll to Cycle", true, "Hold LeftAlt and scroll to cycle stances");
        InvertScroll = Config.Bind("a - General", "Invert Scroll", false, "Reverse the scroll-wheel direction for cycling");
        SavedStance = Config.Bind("a - General", "Saved Stance", EStance.LowReady, "Last selected stance, persists across raids");

        AutoNormalOnFire = Config.Bind("a - General", "Return To Normal On Fire", true, "When enabled, shooting from any stance moves the weapon to the Fire Return Stance pose, then returns to your selected stance after the delay below.");
        FireReturnDelay = Config.Bind("a - General", "Fire Return Delay (s)", 1.4f, new ConfigDescription("Seconds the weapon stays in the fire-return pose after the last shot before returning to the selected stance.", new AcceptableValueRange<float>(1f, 20f), Array.Empty<object>()));
        FireReturnStance = Config.Bind("a - General", "Fire Return Stance", EStance.ActiveAim, "Pose the weapon transitions to while firing. ActiveAim = shoulders the weapon (recommended — keeps the deadzone feel). None = vanilla normal pose (old behavior).");
        BlockFireLowHigh = Config.Bind("a - General", "Block Firing In Low/High Ready", false, "When enabled, the trigger does nothing while Low Ready or High Ready is selected. Active Aim and ADS fire normally.");

        // Only stamina config exposed — controls stance ARM stamina drain only.
        // Does NOT affect regen or sprint stamina.
        StaminaDrainMultiplier = Config.Bind("a - General", "Stamina Drain Multiplier", 0.65f, new ConfigDescription("Controls arm stamina DRAIN from stances only. Does not affect regen or sprint stamina. 1.0 = base drain, 0.0 = no arm stamina drain from stances, 2.0 = double drain.", new AcceptableValueRange<float>(0f, 2f), Array.Empty<object>()));

        const string ws = "m - Weight & Stamina";
        EnableWeightStamina = Config.Bind(ws, "Enable Weight Scaling", true, "Scales arm-stamina drain/regen by weapon weight (with attachments). Light pistols/SMGs barely drain; heavy LMGs drain noticeably.");
        WeightReferenceKg = Config.Bind(ws, "Reference Weight (kg)", 4f, new ConfigDescription("Weapon weight that maps to 1.0x drain — roughly a typical rifle.", new AcceptableValueRange<float>(1f, 10f), Array.Empty<object>()));
        WeightMinMul = Config.Bind(ws, "Min Weight Multiplier", 0.3f, new ConfigDescription("Drain multiplier floor for the lightest weapons.", new AcceptableValueRange<float>(0.05f, 1f), Array.Empty<object>()));
        WeightMaxMul = Config.Bind(ws, "Max Weight Multiplier", 2f, new ConfigDescription("Drain multiplier cap for the heaviest weapons (LMGs).", new AcceptableValueRange<float>(1f, 4f), Array.Empty<object>()));
        SwapTime = Config.Bind(ws, "Swap Time", 1f, new ConfigDescription("Weapon draw/holster speed. 1.0 = natural rate for a ~4kg rifle; lower = faster, higher = slower. Weapon weight scales it on top — a 9kg LMG takes ~2.25x your set time, a 1kg pistol ~0.25x.", new AcceptableValueRange<float>(0.2f, 3f), Array.Empty<object>()));



        EnableTacSprint = Config.Bind("a - General", "Enable Tactical Sprint Animation", true, "When in High Ready and sprinting, plays the tactical sprint animation (weapon held up while running)");

        LowReadySprintAccel = Config.Bind("f - Stance Bonuses", "Low Ready Sprint Acceleration", 1.55f, new ConfigDescription("Sprint acceleration multiplier while Low Ready is selected. 1 = vanilla, 1.35 = ~35% faster sprint spool-up.", new AcceptableValueRange<float>(1f, 2f), Array.Empty<object>()));
        HighReadyMoveSpeed = Config.Bind("f - Stance Bonuses", "High Ready Move Speed", 1.35f, new ConfigDescription("Movement speed multiplier while High Ready is selected. 1 = vanilla.", new AcceptableValueRange<float>(1f, 1.5f), Array.Empty<object>()));
        ActiveAimAdsSpeed = Config.Bind("f - Stance Bonuses", "Active Aim ADS Move Speed", 1f, new ConfigDescription("ADS movement speed multiplier while Active Aim is selected. 1 = vanilla.", new AcceptableValueRange<float>(1f, 2f), Array.Empty<object>()));
        ActiveAimRecoil = Config.Bind("f - Stance Bonuses", "Active Aim Recoil", 0.75f, new ConfigDescription("Recoil multiplier while firing from Active Aim. 1 = vanilla hipfire, lower = weaker recoil.", new AcceptableValueRange<float>(0.3f, 1f), Array.Empty<object>()));
        CantedAimRecoil = Config.Bind("f - Stance Bonuses", "Canted Aim Recoil", 0.75f, new ConfigDescription("Recoil multiplier while firing from Canted Aim. 1 = vanilla hipfire, lower = weaker recoil.", new AcceptableValueRange<float>(0.3f, 1f), Array.Empty<object>()));
        CantedAutoSideSight = Config.Bind("g - Canted Aim", "Auto-Select Side Sight", true, "When canted aim activates, automatically switches the camera to a side-mounted (rotated) sight if the weapon has one, and restores your previous sight when you release. Same as pressing the 'change sight' key.");

        KeyLowReady = Config.Bind("b - Hotkeys", "Low Ready Key", new KeyboardShortcut(KeyCode.None), "Key to switch to Low Ready");
        KeyHighReady = Config.Bind("b - Hotkeys", "High Ready Key", new KeyboardShortcut(KeyCode.None), "Key to switch to High Ready");
        KeyActiveAim = Config.Bind("b - Hotkeys", "Active Aim Key", new KeyboardShortcut(KeyCode.None), "Key to switch to Active Aim");
        KeyCantedAim = Config.Bind("b - Hotkeys", "Canted Aim Key", new KeyboardShortcut(KeyCode.Mouse4), "Key for canted aim (Mouse3 = mouse button 4). Behavior depends on the toggle setting below.");
        CantedAimToggle = Config.Bind("b - Hotkeys", "Canted Aim Toggle Mode", false, "Off = hold the key to cant the weapon, release to return to your stance. On = press once to cant, press again to release.");

        string[] sections = { "c - Low Ready", "d - High Ready", "e - Active Aim", "g - Canted Aim" };
        BindStanceOffsets(sections[0], out LowReadyPosX, out LowReadyPosY, out LowReadyPosZ, out LowReadyRotX, out LowReadyRotY, out LowReadyRotZ, new Vector3(0.03323947f, 0f, 0.06309859f), new Vector3(16.47888f, -5.971831f, -20.93897f));
        BindStanceOffsets(sections[1], out HighReadyPosX, out HighReadyPosY, out HighReadyPosZ, out HighReadyRotX, out HighReadyRotY, out HighReadyRotZ, new Vector3(0.01971831f, 0.05056338f, -0.1481691f), new Vector3(-22.4648f, -12.39437f, 11.33803f));
        BindStanceOffsets(sections[2], out ActiveAimPosX, out ActiveAimPosY, out ActiveAimPosZ, out ActiveAimRotX, out ActiveAimRotY, out ActiveAimRotZ, new Vector3(0.005633803f, 0.05352112f, 0f), new Vector3(0f, 0f, 0f));
        BindStanceOffsets(sections[3], out CantedAimPosX, out CantedAimPosY, out CantedAimPosZ, out CantedAimRotX, out CantedAimRotY, out CantedAimRotZ, new Vector3(-0.0384f, 0.03126f, 0.0742f), new Vector3(0.56338f, -38.169f, 0f));

        // Pistol stances — own sections. Defaults mirror the long-gun
        // stances so they start at the current feel and can be tuned
        // independently. Pistols get no canted stance.
        BindStanceOffsets("h - Pistol Low Ready", out PistolLowPosX, out PistolLowPosY, out PistolLowPosZ, out PistolLowRotX, out PistolLowRotY, out PistolLowRotZ, new Vector3(0.05295778f, 0.08169018f, 0.1785915f), new Vector3(16.47888f, -12.16901f, -16.43193f));
        BindStanceOffsets("i - Pistol High Ready", out PistolHighPosX, out PistolHighPosY, out PistolHighPosZ, out PistolHighRotX, out PistolHighRotY, out PistolHighRotZ, new Vector3(0.04225352f, 0.01394366f, -0.2690141f), new Vector3(-42.1831f, -5.633798f, 1.760563f));
        BindStanceOffsets("j - Pistol Active Aim", out PistolActivePosX, out PistolActivePosY, out PistolActivePosZ, out PistolActiveRotX, out PistolActiveRotY, out PistolActiveRotZ, new Vector3(0f, 0.06478873f, 0f), new Vector3(0f, 0f, 0f));

        EnableLeanShoulderSwap = Config.Bind("k - Lean Shoulder Swap", "Enable Lean Shoulder Swap", true, "Moves the weapon to the left shoulder while leaning left without changing EFT's native shoulder stance. This preserves TarkovRL deadzone behavior.");
        BindStanceOffsets("k - Lean Shoulder Swap", out LeanSwapPosX, out LeanSwapPosY, out LeanSwapPosZ, out LeanSwapRotX, out LeanSwapRotY, out LeanSwapRotZ, new Vector3(-0.16f, -0.002816901f, -0.002816901f), new Vector3(0f, -1.126761f, 0f));



        DesiredStance = SavedStance.Value;
        if (EnableStances.Value)
        {
            PatchAll();
        }
        _wasEnabled = EnableStances.Value;
        Log.LogInfo("Tactical Stances v1.1.0 loaded");
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

        // Canted aim — momentary overlay. Hold mode (default): active while
        // the key is held. Toggle mode: press flips it on/off. Suppressed
        // while aiming; entering ADS releases the toggle state too.
        // Pistols have no canted stance.
        bool isPistol = localPlayer != null && StanceManager.IsPistol(localPlayer);
        if (isPistol) _cantedToggled = false;
        bool cantedHeld = !isPistol && IsShortcutHeld(KeyCantedAim);

        // _cantedWanted tracks the hotkey alone — it must NOT drop when the
        // player aims, otherwise the auto side-sight restores mid-ADS and
        // hijacks right-click. Only the visual pose (CantedActive) is
        // suppressed while aiming.
        if (_cantedBlocked && !cantedHeld && !_cantedToggled)
            _cantedBlocked = false;
        bool wasWanted = _cantedWanted;
        if (CantedAimToggle.Value)
        {
            if (!isPistol && IsShortcutPressed(KeyCantedAim))
            {
                _cantedToggled = !_cantedToggled;
                _cantedBlocked = false;
            }
            _cantedWanted = _cantedToggled && !_cantedBlocked;
        }
        else
        {
            _cantedWanted = cantedHeld && !_cantedBlocked;
        }
        if (_cantedWanted != wasWanted)
            HandleCantedSightSwap(localPlayer, _cantedWanted);

        // Manually un-aiming (right-click out of ADS) ends the canted session:
        // restore the previous sight and stance instead of dropping into a
        // still-swapped mid-state that aims back into canted on next RMB.
        // Only on a real aim->un-aim transition — isAiming is read before the
        // swap's SetAiming(true), so a stale false would cancel same-frame.
        if (_cantedWanted && CantedSightSwapped && !isAiming && _prevIsAiming)
        {
            _cantedBlocked = true;   // don't re-arm until the hotkey is re-pressed
            _cantedToggled = false;
            _cantedWanted = false;
            HandleCantedSightSwap(localPlayer, false);
        }
        _prevIsAiming = isAiming;

        CantedActive = _cantedWanted && !isAiming;

        // Left lean-swap owns the weapon's orientation — canted aim can't
        // coexist; release it (and any latched toggle) while swapped.
        if (StanceManager.LeanSwapActive)
        {
            _cantedToggled = false;
            CantedActive = false;
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

        // Deliver a deferred trigger press once the fire-return raise has
        // converged on the fire pose.
        TriggerHoldPatch.DeliverPending();

        // Tick the stance system — updates transition progress and offsets.
        // The offsets are applied via Spring.Get postfix patches, not here.
        if (localPlayer != null)
        {
            StanceManager.Tick(localPlayer, Mathf.Min(Time.unscaledDeltaTime, 0.2f));
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

    /// <summary>
    /// On canted hotkey press: switch the aiming sight to a rotated (45°/side)
    /// mount if the weapon has one — the same path as the vanilla
    /// "change aiming mode" key, so the camera aligns to the side sight.
    /// Stays latched through ADS; restores the previous sight only when the
    /// canted hotkey is released/untoggled. While swapped, the custom canted
    /// pose offsets are disabled — the real sight does the alignment.
    /// </summary>
    private static void HandleCantedSightSwap(Player player, bool entering)
    {
        CantedSightSwapped = false;
        if (!CantedAutoSideSight.Value || player == null) { _preCantedAimIndex = -1; _cantedSwapWeapon = null; return; }
        try
        {
            if (!(player.HandsController is Player.FirearmController fc) || fc.Item == null)
                { _preCantedAimIndex = -1; _cantedSwapWeapon = null; return; }
            var pwa = player.ProceduralWeaponAnimation;
            var scopes = pwa?.ScopeAimTransforms;
            if (scopes == null || scopes.Count < 2) { _preCantedAimIndex = -1; _cantedSwapWeapon = null; return; }

            int current = Mathf.Clamp(fc.Item.AimIndex.Value, 0, scopes.Count - 1);
            if (entering)
            {
                float threshold = EFTHardSettings.Instance.SCOPE_ROTATION_THRESHOLD;
                int canted = -1;
                float best = 0f;
                for (int i = 0; i < scopes.Count; i++)
                {
                    float r = Mathf.Abs(scopes[i].Rotation);
                    if (r >= threshold && r > best) { best = r; canted = i; }
                }
                if (canted < 0 || canted == current) { _preCantedAimIndex = -1; _cantedSwapWeapon = null; return; }
                _preCantedAimIndex = current;
                _cantedSwapWeapon = fc.Item;
                fc.ChangeAimingMode(canted);
                CantedSightSwapped = true;

                // From hip: aim through the side sight immediately instead of
                // waiting for right-click. SetAiming goes through EFT's own
                // checks (sprint, blindfire, obstruction) so it fails safe.
                _cantedAutoAimed = false;
                if (!fc.IsAiming)
                {
                    try
                    {
                        fc.CurrentOperation?.SetAiming(true);
                        _cantedAutoAimed = fc.IsAiming;
                    }
                    catch { }
                }
            }
            else
            {
                if (_cantedAutoAimed && fc.IsAiming)
                {
                    try { fc.CurrentOperation?.SetAiming(false); } catch { }
                }
                _cantedAutoAimed = false;

                // Restore only if the same weapon is still in hand.
                if (_preCantedAimIndex >= 0 && _preCantedAimIndex < scopes.Count
                    && _preCantedAimIndex != current && fc.Item == _cantedSwapWeapon)
                {
                    fc.ChangeAimingMode(_preCantedAimIndex);
                }
                _preCantedAimIndex = -1;
                _cantedSwapWeapon = null;
            }
        }
        catch { _preCantedAimIndex = -1; _cantedSwapWeapon = null; _cantedAutoAimed = false; }
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
            _fireOverrideUntil = 0f;
            DesiredStance = stance;
            SavedStance.Value = stance;
            Log.LogInfo("Tactical stance changed to: " + stance);
        }
    }

    private void PatchAll()
    {
        // Patch each class individually so one bad target can't kill the rest.
        var harmony = new Harmony("com.devin.tacticalstances");
        int patched = 0;
        foreach (var type in typeof(TacticalStancesPlugin).Assembly.GetTypes())
        {
            if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0) continue;
            try
            {
                harmony.PatchAll(type);
                patched++;
            }
            catch (System.Exception e)
            {
                Log.LogError($"Failed to patch {type.Name}: {e.Message}");
            }
        }
        Log.LogInfo($"Tactical Stances patched ({patched} patches)");
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

    private static bool IsShortcutHeld(ConfigEntry<KeyboardShortcut> entry)
    {
        KeyboardShortcut shortcut = entry.Value;
        if (shortcut.MainKey == KeyCode.None) return false;
        foreach (KeyCode mod in shortcut.Modifiers)
        {
            if (!Input.GetKey(mod)) return false;
        }
        return Input.GetKey(shortcut.MainKey);
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
