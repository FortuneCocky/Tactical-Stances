using System.Reflection;
using EFT;
using EFT.Animations;
using EFT.AnimatedInteractionsSubsystem;
using HarmonyLib;
using UnityEngine;

namespace TacticalStances;

/// <summary>
/// Stance manager for low, mid (active aim), and high stances.
///
/// LAYERED APPROACH: TacticalStances slaps over TarkovRL.
///
/// TarkovRL applies procedural motion (deadzone, sway, tracking) to
/// WeaponRoot — this is the base layer.
///
/// TacticalStances applies stance position + rotation directly to
/// WeaponRootAnim (child of WeaponRoot) in Player.LateUpdate postfix.
/// This runs after all game animation and after TarkovRL's patches.
/// Because WeaponRootAnim is a child of WeaponRoot, the stance offset
/// layers cleanly on top of TarkovRL's procedural motion.
///
/// Both position and rotation are applied directly to the transform —
/// no spring injection, no fighting with the animation system. The
/// game resets WeaponRootAnim via SetPositionAndRotation each frame
/// in ApplyComplexRotation, so we apply fresh every frame.
/// </summary>
public static class StanceManager
{
    private static readonly FieldInfo _fcField;
    private static readonly FieldInfo _playerField;

    // Cached references
    private static ProceduralWeaponAnimation _pwa;
    private static object _lastFirearmController;

    // Transition state — spring-smoothed offsets for natural transitions
    private static Vector3 _targetPosOffset;
    private static Vector3 _targetRotOffset;
    private static Vector3 _currentPosOffset;
    private static Vector3 _currentRotOffset;
    private static Vector3 _posVel;
    private static Vector3 _rotVel;
    private static EStance _activeStance = EStance.None;

    // Base pose — captured fresh every frame in the IkProcess prefix.
    // The springs set WeaponRootAnim to this position/rotation, then we
    // add our stance offset on top. Stored so LateUpdate postfix can
    // re-apply from the known base if LateTransformations overwrote it.
    private static Vector3 _baseLocalPos = Vector3.zero;
    private static Quaternion _baseLocalRot = Quaternion.identity;

    // Shoulder swap mirroring
    private static float _mirrorBlend;
    private static float _mirrorBlendVel;
    private static bool _lastIsLeftStance;

    static StanceManager()
    {
        _fcField = AccessTools.Field(typeof(ProceduralWeaponAnimation), "_firearmController");
        _playerField = AccessTools.Field(typeof(Player.FirearmController), "_player");
    }

    public static void CacheSprings(Spring handsPos, Spring handsRot, ProceduralWeaponAnimation pwa)
    {
        _pwa = pwa;
        ResetState();
    }

    public static void Tick(Player player, float dt)
    {
        if (!TacticalStancesPlugin.EnableStances.Value || player == null || !player.IsYourPlayer)
        {
            ResetOffsets();
            return;
        }

        if (player.HealthController == null || !player.HealthController.IsAlive)
        {
            ResetOffsets();
            return;
        }

        if (player.MovementContext == null || player.MovementContext.CurrentState == null)
        {
            ResetOffsets();
            return;
        }

        // Detect weapon swap
        object fc = (_pwa != null) ? _fcField?.GetValue(_pwa) : null;
        if (fc != _lastFirearmController)
        {
            _lastFirearmController = fc;
            ResetState();
        }

        // Check blocked states
        bool isInteractionPlaying = false;
        try
        {
            isInteractionPlaying = player.MovementContext?.PlayerAnimator?.AnimatedInteractions?.IsInteractionPlaying ?? false;
        }
        catch { }

        bool handsBusy = IsHandsBusy(player);
        bool isBlockedState = (player.IsInventoryOpened || isInteractionPlaying || handsBusy) ||
            player.MovementContext.CurrentState.Name == EPlayerState.Sprint ||
            (_pwa != null && (_pwa.IsGrenadeLauncher || _pwa.IsMountedState));

        bool isAiming = _pwa != null && _pwa.IsAiming;

        EStance desiredStance = TacticalStancesPlugin.DesiredStance;
        bool shouldShowStance = !isBlockedState && !isAiming && desiredStance != EStance.None;

        // Determine target stance and raw offsets
        EStance targetStance = shouldShowStance ? desiredStance : EStance.None;
        _activeStance = targetStance;

        GetStanceOffset(targetStance, out var rawPos, out var rawRot);

        // Shoulder swap mirroring
        bool isLeftStance = false;
        try
        {
            isLeftStance = player.MovementContext?.LeftStanceController?.LeftStance ?? false;
        }
        catch { }

        float target = (TacticalStancesPlugin.EnableShoulderSwap.Value && isLeftStance) ? 1f : 0f;
        if (isLeftStance != _lastIsLeftStance) _lastIsLeftStance = isLeftStance;

        float mirrorFreq = Mathf.Max(2f, TacticalStancesPlugin.TransitionSpeed.Value * 1.5f);
        SpringMirror(ref _mirrorBlend, ref _mirrorBlendVel, target, mirrorFreq, dt);

        rawPos.x = Mathf.Lerp(rawPos.x, -rawPos.x, _mirrorBlend);
        rawRot.y = Mathf.Lerp(rawRot.y, -rawRot.y, _mirrorBlend);

        // Spring-smooth the offsets toward the target.
        // This gives natural transitions between stances — when scrolling
        // from LowReady to ActiveAim, the weapon smoothly moves from one
        // offset to the other instead of snapping.
        _targetPosOffset = rawPos;
        _targetRotOffset = rawRot;

        float springFreq = Mathf.Max(2f, TacticalStancesPlugin.TransitionSpeed.Value * 2f);
        SpringVec(ref _currentPosOffset, ref _posVel, _targetPosOffset, springFreq, dt);
        SpringVec(ref _currentRotOffset, ref _rotVel, _targetRotOffset, springFreq, dt);

        // Rotation is applied in ApplyStanceOffsetForIK / ApplyStanceOffsetForVisual.

        UpdateStamina(player, dt);
    }

    /// <summary>
    /// Apply stance offset BEFORE IkProcess reads the left hand marker.
    /// Called from Player.IkProcess prefix.
    ///
    /// At this point in the frame, ProcessEffectors has already SET
    /// WeaponRootAnim.localPosition/Rotation from the spring system.
    /// We capture that as the base, then add our stance offset on top.
    /// The marker (child of WeaponRootAnim) moves with it, so when
    /// IkProcess reads _markers[0].position, it's at the stance-offset
    /// position. The IK solver then places the left hand correctly.
    /// </summary>
    public static void ApplyStanceOffsetForIK(Transform weaponRootAnim)
    {
        if (_currentPosOffset == Vector3.zero && _currentRotOffset == Vector3.zero)
        {
            // Still capture the base so LateUpdate can use it
            _baseLocalPos = weaponRootAnim.localPosition;
            _baseLocalRot = weaponRootAnim.localRotation;
            return;
        }

        // Capture the spring output as the base
        _baseLocalPos = weaponRootAnim.localPosition;
        _baseLocalRot = weaponRootAnim.localRotation;

        // Apply stance offset on top
        weaponRootAnim.localPosition = _baseLocalPos + _currentPosOffset;
        weaponRootAnim.localRotation = _baseLocalRot * Quaternion.Euler(_currentRotOffset);
    }

    /// <summary>
    /// Re-apply stance offset for visual rendering.
    /// Called from Player.LateUpdate postfix.
    ///
    /// LateTransformations (at the end of VisualPass) may have overwritten
    /// WeaponRootAnim. We reset to the captured base and re-apply the
    /// stance offset so the visual matches what the IK solved to.
    /// </summary>
    public static void ApplyStanceOffsetForVisual(Transform weaponRootAnim)
    {
        if (_currentPosOffset == Vector3.zero && _currentRotOffset == Vector3.zero)
            return;

        // Reset to base and re-apply offset
        weaponRootAnim.localPosition = _baseLocalPos + _currentPosOffset;
        weaponRootAnim.localRotation = _baseLocalRot * Quaternion.Euler(_currentRotOffset);
    }

    private static void SpringVec(ref Vector3 current, ref Vector3 velocity, Vector3 target, float freq, float dt)
    {
        float k = freq * freq;
        float d = 1.4f * freq;
        velocity += ((target - current) * k - velocity * d) * dt;
        current += velocity * dt;
    }

    private static bool IsHandsBusy(Player player)
    {
        try
        {
            if (!player.HasFirearmInHands()) return true;
            MovementContext mc = player.MovementContext;
            if (mc?.PlayerAnimator != null)
            {
                IAnimatedInteractions ai = mc.PlayerAnimator.AnimatedInteractions;
                if (ai != null && ai.IsInteractionPlaying) return true;
            }
        }
        catch { }
        return false;
    }

    private static void ResetState()
    {
        _activeStance = EStance.None;
        _targetPosOffset = Vector3.zero;
        _targetRotOffset = Vector3.zero;
        _currentPosOffset = Vector3.zero;
        _currentRotOffset = Vector3.zero;
        _posVel = Vector3.zero;
        _rotVel = Vector3.zero;
        _mirrorBlend = 0f;
        _mirrorBlendVel = 0f;
        _lastIsLeftStance = false;
        _baseLocalPos = Vector3.zero;
        _baseLocalRot = Quaternion.identity;
    }

    private static void ResetOffsets()
    {
        _targetPosOffset = Vector3.zero;
        _targetRotOffset = Vector3.zero;
        _currentPosOffset = Vector3.zero;
        _currentRotOffset = Vector3.zero;
        _posVel = Vector3.zero;
        _rotVel = Vector3.zero;
        _activeStance = EStance.None;
        _mirrorBlend = 0f;
        _mirrorBlendVel = 0f;
        _lastIsLeftStance = false;
    }

    private static void SpringMirror(ref float current, ref float velocity, float target, float freq, float dt)
    {
        float k = freq * freq;
        float d = 1.4f * freq;
        velocity += ((target - current) * k - velocity * d) * dt;
        current += velocity * dt;
        current = Mathf.Clamp01(current);
    }

    private static void UpdateStamina(Player player, float dt)
    {
        if (player?.Physical?.HandsStamina == null) return;
        if (Mathf.Abs(player.MovementContext.Tilt) > 0.1f) return;

        EStance stance = TacticalStancesPlugin.DesiredStance;
        if (stance == EStance.None) return;

        // Skip stamina drain/regen while hands are busy (reloading, interactions, etc.)
        if (IsHandsBusy(player)) return;

        Stamina handsStamina = player.Physical.HandsStamina;
        float totalCapacity = handsStamina.TotalCapacity.Value;
        float current = handsStamina.Current;

        float drainMul = TacticalStancesPlugin.StaminaDrainMultiplier.Value;

        bool isSprinting = false;
        try
        {
            isSprinting = player.MovementContext?.CurrentState?.Name == EPlayerState.Sprint;
        }
        catch { }

        switch (stance)
        {
            case EStance.LowReady:
            {
                float regen = TacticalStancesPlugin.LowReadyRegenRate;
                handsStamina.Current = Mathf.Min(totalCapacity, current + regen * dt);
                break;
            }
            case EStance.HighReady:
            {
                if (isSprinting)
                {
                    float drain = TacticalStancesPlugin.HighReadySprintDrainFrac * totalCapacity * drainMul;
                    handsStamina.Current = Mathf.Max(0f, current - drain * dt);
                }
                else
                {
                    float regen = TacticalStancesPlugin.HighReadyRegenRate;
                    handsStamina.Current = Mathf.Min(totalCapacity, current + regen * dt);
                }
                break;
            }
            case EStance.ActiveAim:
            {
                // Active Aim drains arm stamina. While sprinting it drains
                // 5% more than the default rate.
                float drainFrac = isSprinting
                    ? TacticalStancesPlugin.ActiveAimSprintDrainFrac
                    : TacticalStancesPlugin.ActiveAimDrainFrac;
                float drain = drainFrac * totalCapacity * drainMul;
                handsStamina.Current = Mathf.Max(0f, current - drain * dt);
                break;
            }
        }
    }

    private static void GetStanceOffset(EStance stance, out Vector3 pos, out Vector3 rot)
    {
        switch (stance)
        {
            case EStance.LowReady:
                pos = new Vector3(TacticalStancesPlugin.LowReadyPosX.Value, TacticalStancesPlugin.LowReadyPosY.Value, TacticalStancesPlugin.LowReadyPosZ.Value);
                rot = new Vector3(TacticalStancesPlugin.LowReadyRotX.Value, TacticalStancesPlugin.LowReadyRotY.Value, TacticalStancesPlugin.LowReadyRotZ.Value);
                break;
            case EStance.HighReady:
                pos = new Vector3(TacticalStancesPlugin.HighReadyPosX.Value, TacticalStancesPlugin.HighReadyPosY.Value, TacticalStancesPlugin.HighReadyPosZ.Value);
                rot = new Vector3(TacticalStancesPlugin.HighReadyRotX.Value, TacticalStancesPlugin.HighReadyRotY.Value, TacticalStancesPlugin.HighReadyRotZ.Value);
                break;
            case EStance.ActiveAim:
                pos = new Vector3(TacticalStancesPlugin.ActiveAimPosX.Value, TacticalStancesPlugin.ActiveAimPosY.Value, TacticalStancesPlugin.ActiveAimPosZ.Value);
                rot = new Vector3(TacticalStancesPlugin.ActiveAimRotX.Value, TacticalStancesPlugin.ActiveAimRotY.Value, TacticalStancesPlugin.ActiveAimRotZ.Value);
                break;
            default:
                pos = Vector3.zero;
                rot = Vector3.zero;
                break;
        }
    }
}
