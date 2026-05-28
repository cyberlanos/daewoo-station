// Ported from ColonialMarinesUniverse Content.Shared/_CMU14/ZLevels/Core/EntitySystems/CMUZLevelShootingSystem.cs.
// Adapted for lanos:
//   * Subscribes to per-projectile PlayerShotProjectileEvent (lanos's prediction-correct hook
//     point) instead of CMU's post-Shoot list iteration. SharedGunSystem.Shoot raises this event
//     per projectile in the same prediction tick, so we can attach the visual offset without
//     needing Shoot() to return a list.
//   * Inline LookUp-disable since lanos's viewer system doesn't expose TryDisableLookUp publicly.

using System.Numerics;
using Content.Shared._Pirate.Input;
using Content.Shared._Pirate.Projectiles;
using Content.Shared._Pirate.ZLevels.Core.Components;
using Content.Shared._Pirate.ZLevels.Core.EntitySystems;
using Content.Shared.Popups;
using SharedCCVars = Content.Shared.CCVar.CCVars;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Content.Shared.Wieldable;
using Content.Shared.Wieldable.Components;
using Robust.Shared.Configuration;
using Robust.Shared.Input.Binding;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;

namespace Content.Shared._Pirate.ZLevels.Shooting;

public sealed partial class CEZLevelShootingSystem : EntitySystem
{
    /// <summary>Nudge the projectile this far from the opening center back toward the shooter so it spawns
    /// just inside the hole on the target layer, not deep in space.</summary>
    private const float CrossZOpeningSourceNudge = 0.30f;

    [Dependency] private readonly CESharedZLevelsSystem _zLevels = default!;
    [Dependency] private readonly SharedGunSystem _gun = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IConfigurationManager _config = default!;

    private bool _debug;
    private float _crossZShotRange = 4f;
    private float _crossZOpeningSourceEdgeRangeTiles = 2f;

    /// <summary>
    /// Per-call visual-offset slot. Set by <see cref="BeginShotOffset"/> before <c>SharedGunSystem.Shoot</c>,
    /// consumed by the <see cref="PlayerShotProjectileEvent"/> subscriber that lanos's Shoot raises
    /// per projectile, cleared by <see cref="EndShotOffset"/> after.
    /// </summary>
    private Vector2 _pendingVisualOffset;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<GunComponent, ItemUnwieldedEvent>(OnGunUnwielded);
        SubscribeLocalEvent<PlayerShotProjectileEvent>(OnPlayerShotProjectile);

        Subs.CVar(_config, SharedCCVars.CEZLevelsShootingDebug, v => _debug = v, true);
        Subs.CVar(_config, SharedCCVars.CEZShootingRange, v => _crossZShotRange = v, true);
        Subs.CVar(_config, SharedCCVars.CEZShootingOpeningTileRange, v => _crossZOpeningSourceEdgeRangeTiles = v, true);

        CommandBinds.Builder
            .Bind(CEKeyFunctions.CEToggleShootDownZLevel,
                InputCmdHandler.FromDelegate(session =>
                    {
                        if (_debug) Log.Info($"[crossz-shoot] keybind fired, session={session?.Name ?? "null"} attached={(session?.AttachedEntity is { } a ? ToPrettyString(a) : "null")}");
                        if (session?.AttachedEntity is { } user)
                            ToggleShootDown(user);
                    },
                    handle: false))
            .Register<CEZLevelShootingSystem>();
    }

    public override void Shutdown()
    {
        base.Shutdown();
        CommandBinds.Unregister<CEZLevelShootingSystem>();
    }

    private void OnGunUnwielded(Entity<GunComponent> ent, ref ItemUnwieldedEvent args)
    {
        if (TryDisableShootDown(args.User) && !args.Force)
            PopupSelf(args.User, "ce-zlevel-shoot-down-disabled-unwield");
    }

    /// <summary>
    /// Called per-projectile by SharedGunSystem.Shoot. We apply the pending visual offset (set
    /// just before the Shoot call by the SharedGunSystem hook) to the spawned projectile.
    /// </summary>
    private void OnPlayerShotProjectile(ref PlayerShotProjectileEvent args)
    {
        if (_pendingVisualOffset.LengthSquared() <= 0.001f)
            return;

        ApplyProjectileVisualOffset(args.Projectile, _pendingVisualOffset);
    }

    /// <summary>Reserve a visual offset to be applied to every projectile fired during the next Shoot call.</summary>
    public void BeginShotOffset(Vector2 offset) => _pendingVisualOffset = offset;

    public void EndShotOffset() => _pendingVisualOffset = Vector2.Zero;

    // --- ShootDown toggle -------------------------------------------------------------------

    private void ToggleShootDown(EntityUid user)
    {
        if (_debug) Log.Info($"[crossz-shoot] ToggleShootDown user={ToPrettyString(user)}");

        if (!TryGetReadyGun(user, "ce-zlevel-shoot-down-no-gun", "ce-zlevel-shoot-down-requires-wield"))
        {
            if (_debug) Log.Info($"[crossz-shoot] ToggleShootDown bail: no ready gun");
            return;
        }

        var shootDown = !IsShootDownEnabled(user);
        SetShootDown(user, shootDown);

        PopupSelf(user, shootDown ? "ce-zlevel-shoot-down-enabled" : "ce-zlevel-shoot-down-disabled");
    }

    private bool TryGetReadyGun(EntityUid user, string noGunMessage, string requiresWieldMessage)
    {
        if (!_gun.TryGetGun(user, out var gunUid, out _))
        {
            PopupSelf(user, noGunMessage);
            return false;
        }

        if (!IsReadyGun(gunUid))
        {
            PopupSelf(user, requiresWieldMessage);
            return false;
        }

        return true;
    }

    private bool HasReadyGun(EntityUid user) =>
        _gun.TryGetGun(user, out var gunUid, out _) && IsReadyGun(gunUid);

    private bool IsReadyGun(EntityUid gunUid) =>
        !TryComp<WieldableComponent>(gunUid, out var wieldable) || wieldable.Wielded;

    private bool TryDisableShootDown(EntityUid user)
    {
        if (!IsShootDownEnabled(user))
            return false;

        SetShootDown(user, false);
        return true;
    }

    public bool IsShootDownEnabled(EntityUid user) =>
        TryComp<CEZLevelShooterComponent>(user, out var shooter) && shooter.ShootDown;

    public void SetShootDown(EntityUid user, bool enabled)
    {
        CEZLevelShooterComponent shooter;
        if (TryComp<CEZLevelShooterComponent>(user, out var existing))
        {
            shooter = existing;
        }
        else
        {
            if (!enabled)
                return;

            shooter = EnsureComp<CEZLevelShooterComponent>(user);
        }

        if (shooter.ShootDown == enabled)
            return;

        shooter.ShootDown = enabled;
        DirtyField(user, shooter, nameof(CEZLevelShooterComponent.ShootDown));

        // ShootDown + LookUp are mutually exclusive — both modify the +/-1 routing.
        if (enabled)
            _zLevels.TryDisableLookUp(user);
    }

    // --- Shot redirection (called by SharedGunSystem) ---------------------------------------

    /// <summary>
    /// If the shooter has a Z-offset active (ShootDown=-1 or LookUp+gun=+1), rewrites
    /// <paramref name="fromCoordinates"/>/<paramref name="toCoordinates"/> to point at the
    /// target Z layer through a floor opening. Returns false (and shows a popup) if there's no
    /// adjacent layer or no opening along the shot line — caller should abort the shot.
    /// </summary>
    public bool TryAdjustShotCoordinates(
        EntityUid shooter,
        EntityCoordinates fromCoordinates,
        EntityCoordinates toCoordinates,
        out EntityCoordinates adjustedFromCoordinates,
        out EntityCoordinates adjustedToCoordinates,
        bool requireReadyGunForLookUp = true)
    {
        adjustedFromCoordinates = fromCoordinates;
        adjustedToCoordinates = toCoordinates;

        var offset = GetRequestedShotOffset(shooter, requireReadyGunForLookUp);
        if (offset == 0)
            return true;

        var shooterMap = Transform(shooter).MapUid;
        if (shooterMap == null ||
            !_zLevels.TryMapOffset((shooterMap.Value, null), offset, out var targetMap) ||
            !TryComp<MapComponent>(targetMap.Value.Owner, out var targetMapComp))
        {
            PopupSelf(shooter, offset > 0
                ? "ce-zlevel-shoot-up-no-level"
                : "ce-zlevel-shoot-down-no-level");
            return false;
        }

        var fromMap = _transform.ToMapCoordinates(fromCoordinates);
        var toMap = _transform.ToMapCoordinates(toCoordinates);
        var clampedTo = ClampCrossZShotTarget(fromMap.Position, toMap.Position);
        if (_debug)
        {
            Log.Info($"[crossz-shoot] redirect offset={offset} from={fromMap.Position} to={toMap.Position} clampedTo={clampedTo} shooterMap={ToPrettyString(shooterMap.Value)} targetMap={ToPrettyString(targetMap.Value.Owner)}");

            // Dump opening-tile status for shooter / click tiles on whichever map carries the floor.
            var openingMapForDebug = offset < 0 ? shooterMap.Value : targetMap.Value.Owner;
            var shooterIsOpen = _zLevels.IsOpeningAt(openingMapForDebug, fromMap.Position);
            var clickIsOpen = _zLevels.IsOpeningAt(openingMapForDebug, clampedTo);
            Log.Info($"[crossz-shoot]   openingMap={ToPrettyString(openingMapForDebug)} shooterTileOpen={shooterIsOpen} clickTileOpen={clickIsOpen}");
        }
        if (!_zLevels.TryFindZShotOpening(
                shooterMap.Value,
                targetMap.Value.Owner,
                offset,
                fromMap.Position,
                clampedTo,
                out var opening,
                preferOpeningAwayFromSource: true,
                maxSourceDistanceFromOpeningEdgeTiles: _crossZOpeningSourceEdgeRangeTiles,
                debug: _debug))
        {
            if (_debug) Log.Info($"[crossz-shoot] no opening found along line within {_crossZOpeningSourceEdgeRangeTiles + 0.5f} tiles");
            PopupSelf(shooter, offset > 0
                ? "ce-zlevel-shoot-up-blocked-floor"
                : "ce-zlevel-shoot-down-blocked-floor");
            return false;
        }
        // Opening is grid-anchored EntityCoordinates — lift to world only for the path math.
        // Re-derives world from the (possibly moving) grid's current transform.
        var openingWorld = _transform.ToMapCoordinates(opening).Position;
        if (_debug) Log.Info($"[crossz-shoot] opening anchor={ToPrettyString(opening.EntityId)} localPos={opening.Position} world={openingWorld}");

        GetCrossZProjectilePath(
            fromMap.Position,
            toMap.Position,
            clampedTo,
            openingWorld,
            offset,
            out var projectileFrom,
            out var projectileTo);

        var targetFrom = new MapCoordinates(projectileFrom, targetMapComp.MapId);
        var targetTo = new MapCoordinates(projectileTo, targetMapComp.MapId);

        adjustedFromCoordinates = _transform.ToCoordinates(targetFrom);
        adjustedToCoordinates = _transform.ToCoordinates(targetTo);
        return true;
    }

    /// <summary>
    /// Returns the cosmetic offset that should be applied to spawned projectiles' sprites so
    /// the muzzle flash renders at the gun barrel on the source layer, not at the opening on the
    /// target layer.
    /// </summary>
    public bool TryGetProjectileVisualOffset(
        EntityUid shooter,
        EntityCoordinates sourceFromCoordinates,
        EntityCoordinates projectileFromCoordinates,
        out Vector2 visualOffset,
        bool requireReadyGunForLookUp = true)
    {
        visualOffset = default;

        var offset = GetRequestedShotOffset(shooter, requireReadyGunForLookUp);
        if (offset == 0)
            return false;

        var sourceFromMap = _transform.ToMapCoordinates(sourceFromCoordinates);
        var projectileFromMap = _transform.ToMapCoordinates(projectileFromCoordinates);
        if (sourceFromMap.MapId == MapId.Nullspace || projectileFromMap.MapId == MapId.Nullspace)
            return false;

        // Adjacent-layer rendering: the renderer's eye for the target-Z pass is shifted by
        // (0, ZLevelOffset * depth) so contents appear vertically displaced (upper-layer down,
        // lower-layer up). To make the sprite land at the *source-layer* barrel B given the
        // projectile lives at P on the target map, solve P + spriteOffset - eyeShift = B →
        // spriteOffset = B - P + eyeShift. GetCrossZRenderOffset(+1) = (0, +0.7), (-1) = (0, -0.7).
        // See Content.Client/_Pirate/ZLevels/Core/ScalingViewport.CEZLevels.cs:300 for the eye shift.
        visualOffset = sourceFromMap.Position + GetCrossZRenderOffset(offset) - projectileFromMap.Position;
        return visualOffset.LengthSquared() > 0.001f;
    }

    // --- Visual-offset attachment ------------------------------------------------------------

    public void ApplyProjectileVisualOffset(EntityUid projectile, Vector2 visualOffset)
    {
        if (visualOffset.LengthSquared() <= 0.001f)
            return;

        // During client prediction, don't dirty server-owned entities. Use a predicted-only
        // component instead; the server will add the synced variant when it confirms the shot.
        if (_timing.InPrediction && !IsClientSide(projectile))
        {
            if (!TryComp<CEZLevelPredictedProjectileVisualOffsetComponent>(projectile, out var predicted))
            {
                predicted = new CEZLevelPredictedProjectileVisualOffsetComponent { Offset = visualOffset };
                AddComp(projectile, predicted);
                return;
            }

            predicted.Offset = visualOffset;
            return;
        }

        if (!TryComp<CEZLevelProjectileVisualOffsetComponent>(projectile, out var visual))
        {
            visual = new CEZLevelProjectileVisualOffsetComponent { Offset = visualOffset };
            AddComp(projectile, visual);
            Dirty(projectile, visual);
            return;
        }

        visual.Offset = visualOffset;
        Dirty(projectile, visual);
    }

    // --- Math helpers (pure, from CMU verbatim) ---------------------------------------------

    private static void GetCrossZProjectilePath(
        Vector2 from,
        Vector2 to,
        Vector2 clampedTo,
        Vector2 opening,
        int offset,
        out Vector2 projectileFrom,
        out Vector2 projectileTo)
    {
        projectileFrom = NudgeOpeningTowardSource(opening, from);
        var direction = to - from;
        if (direction.LengthSquared() <= 0.001f)
            direction = clampedTo - projectileFrom;

        if (direction.LengthSquared() <= 0.001f)
        {
            projectileTo = clampedTo;
            return;
        }

        var distance = Math.Max(1f, Vector2.Distance(projectileFrom, clampedTo));
        projectileTo = projectileFrom + Vector2.Normalize(direction) * distance;
    }

    private static Vector2 GetCrossZRenderOffset(int offset) =>
        new(0f, CESharedZLevelsSystem.ZLevelVisualOffset * offset);

    private static Vector2 NudgeOpeningTowardSource(Vector2 opening, Vector2 source)
    {
        var sourceDirection = source - opening;
        if (sourceDirection.LengthSquared() <= 0.001f)
            return opening;

        return opening + Vector2.Normalize(sourceDirection) * CrossZOpeningSourceNudge;
    }

    private Vector2 ClampCrossZShotTarget(Vector2 from, Vector2 to)
    {
        var delta = to - from;
        var distance = delta.Length();

        if (distance <= _crossZShotRange || distance <= 0.001f)
            return to;

        return from + delta / distance * _crossZShotRange;
    }

    private void PopupSelf(EntityUid user, string message) =>
        _popup.PopupClient(Loc.GetString(message), user, user, PopupType.SmallCaution);

    /// <summary>Resolves the shooter's current Z-offset request: -1 down, +1 up, 0 normal.</summary>
    private int GetRequestedShotOffset(EntityUid shooter, bool requireReadyGunForLookUp = false)
    {
        if (TryComp<CEZLevelShooterComponent>(shooter, out var shooterComp) && shooterComp.ShootDown)
            return -1;

        if (TryComp<CEZLevelViewerComponent>(shooter, out var viewer) &&
            viewer.LookUp &&
            (!requireReadyGunForLookUp || HasReadyGun(shooter)))
        {
            return 1;
        }

        return 0;
    }
}
