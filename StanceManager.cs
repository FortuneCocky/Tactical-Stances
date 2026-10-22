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
    private static readonly FieldInfo _markersField = AccessTools.Field(typeof(Player), "_markers");

    // Stance pivot: the firing-hand grip marker expressed in WeaponRootAnim's
    // LOCAL space. It's a rigid child of the weapon, so this is effectively
    // constant per weapon — and being local, it's immune to anything that
    // moves the rig between IkProcess and LateUpdate. Rotating around it
    // means stance angles read identically regardless of attachments.
    private static Vector3 _gripLocal;
    private static bool _gripLocalValid;

    // Bend rotation in WeaponRootAnim's PARENT space, computed once per frame
    // in the IK pass and reused verbatim by the visual pass.
    private static Quaternion _bendLocal = Quaternion.identity;

    // Last pose we wrote — if the game didn't rewrite WeaponRootAnim before
    // the next IK pass, we'd otherwise capture our own output as the base.
    private static Vector3 _lastWrittenPos;
    private static Quaternion _lastWrittenRot = Quaternion.identity;
    private static bool _hasWritten;

    // Lazy-physics bend: how fast the camera is swinging (deg/s, smoothed)
    // and the bend angle currently applied to the muzzle end of the gun.
    private static Vector2 _lastCamEuler;
    private static Vector2 _camAngVel;      // x = pitch rate, y = yaw rate
    private static Vector2 _bendAngle;      // x = pitch deg, y = yaw deg
    private static Transform _camTransform;
    private static bool _sprinting;
    /// <summary>True while the left lean shoulder-swap is driving the weapon.</summary>
    internal static bool LeanSwapActive { get; private set; }

    // Sprint suppression: 1 = stance offsets fully hidden under the sprint
    // animation, 0 = stance fully applied. Fades in fast on sprint entry,
    // out slowly on exit — swapping stances mid-sprint lands smoothly because
    // the smoothed offsets have already converged to the new stance.
    private static float _suppress;

    /// <summary>
    /// How far the live offsets sit from their targets — used to hold the
    /// first bullet until the weapon has actually raised to the fire pose.
    /// Degrees for rotation, meters for position.
    /// </summary>
    internal static bool OffsetBeyondThreshold
        => (_currentRotOffset - _targetRotOffset).sqrMagnitude > 0.25f ||   // ~0.5 deg
           (_currentLeanSwapRot - _targetLeanSwapRot).sqrMagnitude > 0.25f ||
           (_currentPosOffset - _targetPosOffset).sqrMagnitude > 2.5e-5f || // ~5mm
           (_currentLeanSwapPos - _targetLeanSwapPos).sqrMagnitude > 2.5e-5f;


    // Cached references
    private static ProceduralWeaponAnimation _pwa;
    private static object _lastFirearmController;

    // Transition state — exponentially smoothed offsets for stable natural transitions
    private static Vector3 _targetPosOffset;
    private static Vector3 _targetRotOffset;
    private static Vector3 _currentPosOffset;
    private static Vector3 _currentRotOffset;
    private static Vector3 _posVel;
    private static Vector3 _rotVel;
    private static Vector3 _targetLeanSwapPos;
    private static Vector3 _targetLeanSwapRot;
    private static Vector3 _currentLeanSwapPos;
    private static Vector3 _currentLeanSwapRot;
    private static Vector3 _leanSwapPosVel;
    private static Vector3 _leanSwapRotVel;
    private static EStance _activeStance = EStance.None;

    // Pistols use their own stance offset set — separate sliders from
    // long guns so a pose tuned for a rifle doesn't break pistol grips.
    private static bool _isPistol;

    // Base pose — captured fresh every frame in the IkProcess prefix.
    // The springs set WeaponRootAnim to this position/rotation, then we
    // add our stance offset on top. Stored so LateUpdate postfix can
    // re-apply from the known base if LateTransformations overwrote it.
    private static Vector3 _baseLocalPos = Vector3.zero;
    private static Quaternion _baseLocalRot = Quaternion.identity;
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
        LeanSwapActive = false;
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

        // Firearms only — grenades, TNT, melee and meds also carry a
        // WeaponRootAnim; treat them as "no stance" so offsets decay to
        // neutral instead of contorting the hands.
        if (player.HandsController is not Player.FirearmController)
        {
            _isPistol = false;
            _activeStance = EStance.None;
            _targetPosOffset = Vector3.zero;
            _targetRotOffset = Vector3.zero;
            _targetLeanSwapPos = Vector3.zero;
            _targetLeanSwapRot = Vector3.zero;
            SmoothVec(ref _currentPosOffset, ref _posVel, Vector3.zero, 35f, dt);
            SmoothVec(ref _currentRotOffset, ref _rotVel, Vector3.zero, 35f, dt);
            SmoothVec(ref _currentLeanSwapPos, ref _leanSwapPosVel, Vector3.zero, 35f, dt);
            SmoothVec(ref _currentLeanSwapRot, ref _leanSwapRotVel, Vector3.zero, 35f, dt);
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
        _isPistol = IsPistol(player);

        bool isInteractionPlaying = false;
        try
        {
            isInteractionPlaying = player.MovementContext?.PlayerAnimator?.AnimatedInteractions?.IsInteractionPlaying ?? false;
        }
        catch { }

        bool handsBusy = IsHandsBusy(player);
        EPlayerState stateName = player.MovementContext.CurrentState.Name;
        bool isProne = stateName == EPlayerState.ProneIdle || stateName == EPlayerState.ProneMove ||
            stateName == EPlayerState.Transit2Prone || stateName == EPlayerState.Prone2Stand;

        // Sprint suppression: fades the stance out/in against the sprint anim
        // instead of hard-zeroing targets (which snapped on sprint exit).
        bool sprintRequested = false;
        try { sprintRequested = player.MovementContext.IsSprintEnabled; } catch { }
        bool sprintNow = stateName == EPlayerState.Sprint ||
            (_isPistol && sprintRequested);   // pistols suppress on request too
        float supTarget = sprintNow ? 1f : 0f;
        float supRate = supTarget > _suppress
            ? TacticalStancesPlugin.SprintBlendIn.Value
            : TacticalStancesPlugin.SprintBlendOut.Value;
        _suppress = Mathf.MoveTowards(_suppress, supTarget, supRate * dt);

        bool isBlockedState = (player.IsInventoryOpened || isInteractionPlaying || handsBusy) || isProne ||
            (_pwa != null && (_pwa.IsGrenadeLauncher || _pwa.IsMountedState));

        if (isProne)
        {
            _targetPosOffset = Vector3.zero;
            _targetRotOffset = Vector3.zero;
            _currentPosOffset = Vector3.zero;
            _currentRotOffset = Vector3.zero;
            _posVel = Vector3.zero;
            _rotVel = Vector3.zero;
            _targetLeanSwapPos = Vector3.zero;
            _targetLeanSwapRot = Vector3.zero;
            _currentLeanSwapPos = Vector3.zero;
            _currentLeanSwapRot = Vector3.zero;
            _leanSwapPosVel = Vector3.zero;
            _leanSwapRotVel = Vector3.zero;
            _activeStance = EStance.None;
            UpdateStamina(player, dt);
            return;
        }

        bool leanSwapActive = TacticalStancesPlugin.EnableLeanShoulderSwap.Value &&
            !isBlockedState && (_pwa == null || !_pwa.IsAiming) &&
            player.MovementContext.Tilt < -0.1f;
        LeanSwapActive = leanSwapActive;
        _targetLeanSwapPos = leanSwapActive
            ? new Vector3(TacticalStancesPlugin.LeanSwapPosX.Value, TacticalStancesPlugin.LeanSwapPosY.Value, TacticalStancesPlugin.LeanSwapPosZ.Value)
            : Vector3.zero;
        _targetLeanSwapRot = leanSwapActive
            ? new Vector3(TacticalStancesPlugin.LeanSwapRotX.Value, TacticalStancesPlugin.LeanSwapRotY.Value, TacticalStancesPlugin.LeanSwapRotZ.Value)
            : Vector3.zero;

        bool isAiming = _pwa != null && _pwa.IsAiming;
        if (isAiming)
        {
            _targetPosOffset = Vector3.zero;
            _targetRotOffset = Vector3.zero;
            _currentPosOffset = Vector3.zero;
            _currentRotOffset = Vector3.zero;
            _posVel = Vector3.zero;
            _rotVel = Vector3.zero;
        }

        bool fireOverride = TacticalStancesPlugin.FireOverrideActive;
        bool shouldShowStance = !isBlockedState && !isAiming;

        // Determine target stance and raw offsets — during the fire override
        // window the weapon transitions to the configured fire-return stance
        // (Active Aim by default) instead of collapsing to the normal pose.
        EStance targetStance = shouldShowStance ? TacticalStancesPlugin.EffectiveStance : EStance.None;
        _activeStance = targetStance;

        GetStanceOffset(targetStance, out var rawPos, out var rawRot);

        // Smooth the offsets toward the target without overshoot.
        // This gives natural transitions between stances — when scrolling
        // from LowReady to ActiveAim, the weapon smoothly moves from one
        // offset to the other instead of snapping.
        _targetPosOffset = rawPos;
        _targetRotOffset = rawRot;

        float transitionSpeed = Mathf.Max(2f, TacticalStancesPlugin.TransitionSpeed.Value * 2f);
        if (fireOverride && shouldShowStance) transitionSpeed *= 2.5f;

        SmoothVec(ref _currentPosOffset, ref _posVel, _targetPosOffset, transitionSpeed, dt);
        SmoothVec(ref _currentRotOffset, ref _rotVel, _targetRotOffset, transitionSpeed, dt);
        SmoothVec(ref _currentLeanSwapPos, ref _leanSwapPosVel, _targetLeanSwapPos, transitionSpeed, dt);
        SmoothVec(ref _currentLeanSwapRot, ref _leanSwapRotVel, _targetLeanSwapRot, transitionSpeed, dt);

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
    public static void ApplyStanceOffsetForIK(Transform weaponRootAnim, Player player)
    {
        // Capture the base pose — unless what's there is our own output from
        // last frame (nobody rewrote it), in which case keep the old base.
        Vector3 curPos = weaponRootAnim.localPosition;
        Quaternion curRot = weaponRootAnim.localRotation;
        bool ownOutput = _hasWritten
            && (curPos - _lastWrittenPos).sqrMagnitude < 1e-12f
            && Quaternion.Dot(curRot, _lastWrittenRot) > 0.9999999f;
        if (!ownOutput)
        {
            _baseLocalPos = curPos;
            _baseLocalRot = curRot;
        }

        _sprinting = false;
        try { _sprinting = player != null && player.MovementContext.IsSprintEnabled; } catch { }

        // Grip marker in weapon-local space. Rigid child of the weapon, so
        // the sample is stable; light smoothing filters any bone noise.
        // Pistols are skipped — their R IK marker is unreliable/absent and
        // a bad anchor would drag the gun; for a one-handed weapon the
        // weapon centre is already essentially the grip.
        bool pistol = player != null && IsPistol(player);
        if (player != null && !pistol
            && _markersField?.GetValue(player) is Transform[] markers
            && markers.Length > 1 && markers[1] != null)
        {
            Vector3 g = weaponRootAnim.InverseTransformPoint(markers[1].position);
            if (g.sqrMagnitude < 1f)   // reject garbage (>1m from root)
            {
                if (!_gripLocalValid)
                {
                    _gripLocal = g;
                    _gripLocalValid = true;
                }
                else
                {
                    _gripLocal = Vector3.Lerp(_gripLocal, g, 1f - Mathf.Exp(-6f * Time.deltaTime));
                }
            }
        }
        else
        {
            // Pistol, or the markers array is missing for this weapon —
            // never let a stale grip from a previous weapon leak through.
            _gripLocalValid = false;
        }

        // Camera for the lazy-physics bend; resolve the bend into parent
        // space now so both passes apply the exact same rotation.
        _camTransform = null;
        try { _camTransform = player?.ProceduralWeaponAnimation?.HandsContainer?.CameraTransform; } catch { }
        if (_camTransform == null && Camera.main != null)
            _camTransform = Camera.main.transform;
        UpdateBend(Time.deltaTime);

        _bendLocal = Quaternion.identity;
        Transform parent = weaponRootAnim.parent;
        if (_camTransform != null && parent != null && _bendAngle.sqrMagnitude > 0.0001f)
        {
            Vector3 up = parent.InverseTransformDirection(_camTransform.up);
            Vector3 right = parent.InverseTransformDirection(_camTransform.right);
            _bendLocal = Quaternion.AngleAxis(_bendAngle.y, up) * Quaternion.AngleAxis(-_bendAngle.x, right);
        }

        WritePose(weaponRootAnim);
    }

    /// <summary>
    /// Single source of truth for the weapon pose. Pure function of the
    /// captured base, stance offsets, grip point and bend — written as an
    /// absolute local pose, so running it in both the IK and visual passes
    /// is idempotent and the drawn gun always matches what the IK solved to.
    ///
    ///   pos = B + T + Q·S·(g − R·g)     // stance rotation pivots on grip
    ///   rot = Q·R
    ///   then bend about the grip point in parent space.
    ///
    /// With no stance and no bend this returns exactly B/Q — vanilla pose.
    /// </summary>
    private static void WritePose(Transform weaponRootAnim)
    {
        // Sprint suppression scales the stance offsets — a partial fade writes
        // a pose part-way to the stance, so sprint->stance crossfades smoothly.
        float vis = 1f - _suppress;
        Vector3 totalPos = (_currentPosOffset + _currentLeanSwapPos) * vis;
        Vector3 totalRot = (_currentRotOffset + _currentLeanSwapRot) * vis;
        bool hasOffset = totalPos != Vector3.zero || totalRot != Vector3.zero;
        bool usePivot = _gripLocalValid && !_sprinting;
        bool bendActive = !_sprinting && _bendLocal != Quaternion.identity;

        if (!hasOffset && !bendActive)
        {
            // Nothing to add — leave the game's pose untouched.
            _hasWritten = false;
            return;
        }

        Quaternion R = Quaternion.Euler(totalRot);
        Vector3 S = weaponRootAnim.localScale;
        Vector3 pos = _baseLocalPos + totalPos;
        Quaternion rot = _baseLocalRot * R;

        // Pivot around the grip when a valid marker exists (long guns); for
        // pistols the weapon centre doubles as the grip — pivot there. Bend
        // still applies either way, keyed off the same point.
        Vector3 pivot = usePivot ? _gripLocal : Vector3.zero;
        Vector3 pivotCorr = _baseLocalRot * Vector3.Scale(S, pivot - R * pivot);
        // Hard cap — a bad grip sample can never teleport the weapon.
        pos += Vector3.ClampMagnitude(pivotCorr, 0.4f);

        bool hasBend = !_sprinting && _bendLocal != Quaternion.identity;
        if (hasBend)
        {
            Vector3 p = pos + rot * Vector3.Scale(S, pivot);
            pos = p + _bendLocal * (pos - p);
            rot = _bendLocal * rot;
        }

        weaponRootAnim.localPosition = pos;
        weaponRootAnim.localRotation = rot;
        _lastWrittenPos = pos;
        _lastWrittenRot = rot;
        _hasWritten = true;
    }

    /// <summary>
    /// Lazy-physics bend: while the camera swings, the muzzle end of the gun
    /// rotates ahead of the pivot — looking up whips the front up before the
    /// rear follows, same for left/right/down. Produces a flex/bend feel.
    /// Tracks camera angular velocity, smoothed and clamped.
    /// </summary>
    private static void UpdateBend(float dt)
    {
        if (_camTransform == null || dt <= 0f)
        {
            _bendAngle = Vector2.Lerp(_bendAngle, Vector2.zero, 0.3f);
            return;
        }

        // Primary signal: raw mouse deltas — direct input, unaffected by
        // animation/camera-system euler noise. Converted to an equivalent
        // angular velocity; camera-euler deltas fill in if input is
        // unavailable (controller, edge cases).
        Vector3 e = _camTransform.eulerAngles;
        Vector2 cur = new Vector2(e.x > 180f ? e.x - 360f : e.x,
                                  e.y > 180f ? e.y - 360f : e.y);
        Vector2 eulerRate = (cur - _lastCamEuler) / dt;
        _lastCamEuler = cur;

        Vector2 raw;
        try
        {
            Vector2 mouse = new Vector2(UnityEngine.Input.GetAxisRaw("Mouse X"),
                                        UnityEngine.Input.GetAxisRaw("Mouse Y"));
            if (mouse.sqrMagnitude > 0.000001f)
            {
                // Mouse units -> degrees: camera yaw ≈ mouse * sens(deg/unit).
                // Empirically ~3 deg/unit at default aim sensitivity; the
                // bend scale below is tuned against that.
                raw = new Vector2(mouse.y * (3f / dt), mouse.x * (3f / dt));
            }
            else
            {
                raw = eulerRate;
            }
        }
        catch { raw = eulerRate; }

        // Reject wild frames (teleports, tab-out hitches)
        if (raw.sqrMagnitude > 4f * 10e5f)
            raw = Vector2.zero;

        // Deadband on raw velocity — idle noise shouldn't hold a residual bend.
        if (raw.sqrMagnitude < 0.25f)   // < 0.5 deg/s
            raw = Vector2.zero;

        // Asymmetric smoothing: the lead attacks fast (muzzle whips with the
        // flick), release is slower so the butt visibly catches up after the
        // swing stops.
        _camAngVel = Vector2.Lerp(_camAngVel, raw, 1f - Mathf.Exp(-22f * dt));
        if (_camAngVel.sqrMagnitude < 0.25f)
            _camAngVel = Vector2.zero;

        // Degrees of bend: a bigger fraction of swing speed — flick ≈ 12°.
        Vector2 target = Vector2.ClampMagnitude(_camAngVel * 0.02f, 12f);
        float track = target.sqrMagnitude > _bendAngle.sqrMagnitude ? 18f : 7f;
        _bendAngle = Vector2.Lerp(_bendAngle, target, 1f - Mathf.Exp(-track * dt));
        if (_bendAngle.sqrMagnitude < 0.0004f)   // < ~0.02 deg
            _bendAngle = Vector2.zero;
    }

    /// <summary>
    /// Re-apply the pose for visual rendering (Player.LateUpdate postfix).
    /// LateTransformations may have overwritten WeaponRootAnim — WritePose
    /// rebuilds the identical absolute pose from the IK-time inputs, so the
    /// drawn weapon matches where the IK solver put the hands.
    /// </summary>
    public static void ApplyStanceOffsetForVisual(Transform weaponRootAnim)
    {
        WritePose(weaponRootAnim);
    }

    private static void SmoothVec(ref Vector3 current, ref Vector3 velocity, Vector3 target, float speed, float dt)
    {
        float t = 1f - Mathf.Exp(-speed * Mathf.Min(dt, 0.2f));
        current = Vector3.Lerp(current, target, t);
        velocity = Vector3.zero;

        if ((target - current).sqrMagnitude < 0.00000001f)
            current = target;
    }

    public static bool IsPistol(Player player)
    {
        try
        {
            return player.HandsController?.Item is EFT.InventoryLogic.Weapon weapon && weapon.WeapClass == "pistol";
        }
        catch { }
        return false;
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
        _targetLeanSwapPos = Vector3.zero;
        _targetLeanSwapRot = Vector3.zero;
        _currentLeanSwapPos = Vector3.zero;
        _currentLeanSwapRot = Vector3.zero;
        _leanSwapPosVel = Vector3.zero;
        _leanSwapRotVel = Vector3.zero;
        _baseLocalPos = Vector3.zero;
        _baseLocalRot = Quaternion.identity;
        _gripLocalValid = false;
        _hasWritten = false;
        _bendLocal = Quaternion.identity;
        TriggerHoldPatch.Clear();
    }

    private static void ResetOffsets()
    {
        _targetPosOffset = Vector3.zero;
        _targetRotOffset = Vector3.zero;
        _currentPosOffset = Vector3.zero;
        _currentRotOffset = Vector3.zero;
        _posVel = Vector3.zero;
        _rotVel = Vector3.zero;
        _targetLeanSwapPos = Vector3.zero;
        _targetLeanSwapRot = Vector3.zero;
        _currentLeanSwapPos = Vector3.zero;
        _currentLeanSwapRot = Vector3.zero;
        _leanSwapPosVel = Vector3.zero;
        _leanSwapRotVel = Vector3.zero;
        _activeStance = EStance.None;
    }

    private static EFT.InventoryLogic.Item _weightItem;
    private static float _drainWeightMul = 1f;
    private static float _prevHandsCurrent;

    /// <summary>
    /// Weight-based stamina scaling. Weapon total weight (attachments included)
    /// relative to a ~4kg reference rifle scales drain directly and regen
    /// inversely — a 1kg pistol barely taxes the arms, a 9kg LMG obviously does.
    /// </summary>
    private static void UpdateWeightStaminaMultipliers(Player player)
    {
        if (!TacticalStancesPlugin.EnableWeightStamina.Value)
        {
            _weightItem = null;
            _drainWeightMul = 1f;
            return;
        }

        EFT.InventoryLogic.Item item = null;
        try { item = player?.HandsController?.Item; } catch { }
        if (item == _weightItem) return;

        _weightItem = item;
        float kg = 1f;
        try { if (item != null) kg = Mathf.Max(0.3f, item.Weight); } catch { }

        float refKg = Mathf.Max(0.5f, TacticalStancesPlugin.WeightReferenceKg.Value);
        _drainWeightMul = Mathf.Clamp(kg / refKg,
            TacticalStancesPlugin.WeightMinMul.Value,
            TacticalStancesPlugin.WeightMaxMul.Value);
    }

    private static void UpdateStamina(Player player, float dt)
    {
        if (player?.Physical?.HandsStamina == null) return;

        EStance stance = TacticalStancesPlugin.EffectiveStance;
        if (stance == EStance.None) { _prevHandsCurrent = -1f; return; }

        // Skip stamina drain/regen while hands are busy (reloading, interactions, etc.)
        if (IsHandsBusy(player)) { _prevHandsCurrent = -1f; return; }

        UpdateWeightStaminaMultipliers(player);

        Stamina handsStamina = player.Physical.HandsStamina;
        float totalCapacity = handsStamina.TotalCapacity.Value;
        float current = handsStamina.Current;

        bool isSprinting = false;
        try
        {
            isSprinting = player.MovementContext?.CurrentState?.Name == EPlayerState.Sprint;
        }
        catch { }

        // Neutralize EFT's native hand-stamina regen during drain stances:
        // when the game regenerated since our last write, roll back to our
        // baseline so the weight-scaled drain below is the real net rate.
        // (EFT regens hands stamina while not aiming, which otherwise fully
        // cancels a modest drain and makes the weight scaling invisible.)
        bool drainingStance = stance == EStance.ActiveAim || stance == EStance.CantedAim
            || (stance == EStance.HighReady && isSprinting);
        if (drainingStance && current > _prevHandsCurrent && _prevHandsCurrent > 0f)
            current = _prevHandsCurrent;

        float drainMul = TacticalStancesPlugin.StaminaDrainMultiplier.Value;

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
            case EStance.CantedAim:
            {
                // Active Aim / Canted Aim drains arm stamina, scaled by weapon
                // weight — a pistol barely taxes the arms, an LMG obviously does.
                // While sprinting it drains 5% more than the default rate.
                float drainFrac = isSprinting
                    ? TacticalStancesPlugin.ActiveAimSprintDrainFrac
                    : TacticalStancesPlugin.ActiveAimDrainFrac;
                float drain = drainFrac * totalCapacity * drainMul * _drainWeightMul;
                handsStamina.Current = Mathf.Max(0f, current - drain * dt);
                break;
            }
        }

        _prevHandsCurrent = handsStamina.Current;
    }

    private static void GetStanceOffset(EStance stance, out Vector3 pos, out Vector3 rot)
    {
        if (_isPistol)
        {
            switch (stance)
            {
                case EStance.LowReady:
                    pos = new Vector3(TacticalStancesPlugin.PistolLowPosX.Value, TacticalStancesPlugin.PistolLowPosY.Value, TacticalStancesPlugin.PistolLowPosZ.Value);
                    rot = new Vector3(TacticalStancesPlugin.PistolLowRotX.Value, TacticalStancesPlugin.PistolLowRotY.Value, TacticalStancesPlugin.PistolLowRotZ.Value);
                    break;
                case EStance.HighReady:
                    pos = new Vector3(TacticalStancesPlugin.PistolHighPosX.Value, TacticalStancesPlugin.PistolHighPosY.Value, TacticalStancesPlugin.PistolHighPosZ.Value);
                    rot = new Vector3(TacticalStancesPlugin.PistolHighRotX.Value, TacticalStancesPlugin.PistolHighRotY.Value, TacticalStancesPlugin.PistolHighRotZ.Value);
                    break;
                case EStance.ActiveAim:
                    pos = new Vector3(TacticalStancesPlugin.PistolActivePosX.Value, TacticalStancesPlugin.PistolActivePosY.Value, TacticalStancesPlugin.PistolActivePosZ.Value);
                    rot = new Vector3(TacticalStancesPlugin.PistolActiveRotX.Value, TacticalStancesPlugin.PistolActiveRotY.Value, TacticalStancesPlugin.PistolActiveRotZ.Value);
                    break;
                default:
                    // Pistols have no canted stance — fall through to normal.
                    pos = Vector3.zero;
                    rot = Vector3.zero;
                    break;
            }
            return;
        }

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
            case EStance.CantedAim:
                // When auto side-sight swapped the camera onto a real rotated
                // mount, that sight does the alignment — skip the custom pose.
                if (TacticalStancesPlugin.CantedSightSwapped)
                {
                    pos = Vector3.zero;
                    rot = Vector3.zero;
                    break;
                }
                pos = new Vector3(TacticalStancesPlugin.CantedAimPosX.Value, TacticalStancesPlugin.CantedAimPosY.Value, TacticalStancesPlugin.CantedAimPosZ.Value);
                rot = new Vector3(TacticalStancesPlugin.CantedAimRotX.Value, TacticalStancesPlugin.CantedAimRotY.Value, TacticalStancesPlugin.CantedAimRotZ.Value);
                break;
            default:
                pos = Vector3.zero;
                rot = Vector3.zero;
                break;
        }
    }
}
