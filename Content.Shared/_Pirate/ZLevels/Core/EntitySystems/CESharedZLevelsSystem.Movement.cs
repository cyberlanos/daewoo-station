/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using System.Numerics;
using Content.Shared._Pirate.ZLevels.Core.Components;
using Content.Shared.Chasm;
using Content.Shared.Inventory;
using Content.Shared.Movement.Components;
using Content.Shared.Throwing;
using JetBrains.Annotations;
using Robust.Shared.Audio;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;

namespace Content.Shared._Pirate.ZLevels.Core.EntitySystems;

public abstract partial class CESharedZLevelsSystem
{
    public const int MaxZLevelsBelowRendering = 3;

    private const float ZGravityForce = 9.8f;
    private const float ZVelocityLimit = 20.0f;
    private const float StairUpTransferHeightThreshold = 1f;
    private const float StairUpLandingForwardNudge = 0.5f;
    // Target t-offset from the high end of the stair where the entity lands after descending.
    // Must be > 0.5 so the entity lands past the high zone of curve [1.05, 1.0, 0.1] (h<1.0 at t>0.5).
    private const float StairDownLandingForwardNudge = 0.6f;
    private const float StairDirectionMinimumSpeed = 0.01f;

    /// <summary>
    /// The minimum speed required to trigger LandEvent events.
    /// </summary>
    private const float ImpactVelocityLimit = 3f;

    private EntityQuery<CEZLevelHighGroundComponent> _highgroundQuery;

    private void InitMovement()
    {
        _highgroundQuery = GetEntityQuery<CEZLevelHighGroundComponent>();

        SubscribeLocalEvent<CEZPhysicsComponent, CEGetZVelocityEvent>(OnGetVelocity);
        SubscribeLocalEvent<CEZPhysicsComponent, CEZLevelMapMoveEvent>(OnZLevelMapMove);
        SubscribeLocalEvent<CEActiveZPhysicsComponent, ComponentInit>(OnActiveInit);

        SubscribeLocalEvent<CEZPhysicsComponent, MoveEvent>(OnMoveEvent);
        SubscribeLocalEvent<CEZLevelMapComponent, TileChangedEvent>(OnTileChanged);
    }

    private void OnActiveInit(Entity<CEActiveZPhysicsComponent> ent, ref ComponentInit args)
    {
        if (!ZPhyzQuery.TryComp(ent, out var zComp))
            return;
        CacheMovement((ent, zComp));
    }

    private void OnTileChanged(Entity<CEZLevelMapComponent> ent, ref TileChangedEvent args)
    {
        if (!TryComp<MapGridComponent>(args.Entity, out var grid))
            return;

        // For each changed tile compute its world AABB and query all entities intersecting it
        foreach (var change in args.Changes)
        {
            var mapCoords = _map.GridTileToWorld(args.Entity, grid, change.GridIndices);

            var half = grid.TileSizeHalfVector;
            var min = mapCoords.Position - half;
            var max = mapCoords.Position + half;
            var aabb = new Box2(min, max);

            var ents = _lookup.GetEntitiesIntersecting(mapCoords.MapId, aabb);
            foreach (var uid in ents)
            {
                if (!ZPhyzQuery.TryComp(uid, out var zComp))
                    continue;

                CacheMovement((uid, zComp));
            }
        }
    }

    private void CacheMovement(Entity<CEZPhysicsComponent> ent)
    {
        var oldGroundHeight = ent.Comp.CurrentGroundHeight;
        var oldSticky = ent.Comp.CurrentStickyGround;
        var oldFromBelow = ent.Comp.CurrentGroundFromBelowLevel;
        var oldSupportBelow = ent.Comp.CurrentHasSupportBelow;
        var oldHighGroundBelow = ent.Comp.CurrentHighGroundBelow;

        ent.Comp.CurrentGroundHeight = ComputeGroundHeightInternal((ent, ent), out var sticky, out var fromBelow);
        ent.Comp.CurrentStickyGround = sticky;
        ent.Comp.CurrentGroundFromBelowLevel = fromBelow;
        ent.Comp.CurrentHasSupportBelow = ComputeHasSupportBelow(ent, Transform(ent), out var isHighGround);
        ent.Comp.CurrentHighGroundBelow = isHighGround;

        if (ZDebugEnabled &&
            (MathF.Abs(oldGroundHeight - ent.Comp.CurrentGroundHeight) > 0.01f ||
             oldSticky != ent.Comp.CurrentStickyGround ||
             oldFromBelow != ent.Comp.CurrentGroundFromBelowLevel ||
             oldSupportBelow != ent.Comp.CurrentHasSupportBelow ||
             oldHighGroundBelow != ent.Comp.CurrentHighGroundBelow))
        {
            DebugZ(ent,
                $"movement cache updated at tile={_transform.GetGridOrMapTilePosition(ent)} world={_transform.GetWorldPosition(ent)} " +
                $"ground {oldGroundHeight:0.00}->{ent.Comp.CurrentGroundHeight:0.00} sticky {oldSticky}->{ent.Comp.CurrentStickyGround} " +
                $"fromBelow {oldFromBelow}->{ent.Comp.CurrentGroundFromBelowLevel} " +
                $"supportBelow {oldSupportBelow}->{ent.Comp.CurrentHasSupportBelow} highGroundBelow {oldHighGroundBelow}->{ent.Comp.CurrentHighGroundBelow}");
        }
    }

    /// <summary>
    /// Checks whether the Z-level directly below has support at this entity's XY position.
    /// <paramref name="isHighGround"/> is true when that support is a CEZLevelHighGround
    /// entity (stairs/ladder) rather than a plain floor tile.
    /// </summary>
    private bool ComputeHasSupportBelow(EntityUid ent, TransformComponent xform, out bool isHighGround)
    {
        isHighGround = false;

        if (xform.MapUid is not { } mapUid || !_zMapQuery.TryComp(mapUid, out var zMapComp))
            return false;

        if (!TryMapOffset((mapUid, zMapComp), -1, out _))
            return false; // No Z-level below at all

        if (!TryResolveGridForMapOffset(ent, xform, -1, out var belowGridUid, out var belowGrid))
            return false;

        var worldPos = _transform.GetWorldPosition(ent);
        var tileIndices = _map.WorldToTile(belowGridUid, belowGrid, worldPos);

        var anchoredQuery = _map.GetAnchoredEntitiesEnumerator(belowGridUid, belowGrid, tileIndices);
        while (anchoredQuery.MoveNext(out var uid))
        {
            if (_highgroundQuery.HasComp(uid.Value))
            {
                isHighGround = true;
                return true;
            }
        }

        return _map.TryGetTileRef(belowGridUid, belowGrid, worldPos, out var tileRef) && !tileRef.Tile.IsEmpty;
    }

    /// <summary>
    /// Returns true when the entity is allowed to automatically descend to the Z-level below.
    /// Rules:
    /// - Sticky surface (currently on a ladder) → always allow.
    /// - High-ground (stairs/ladder) directly below → always allow; stair traversal works
    ///   even in weightless areas so the player can walk down from a space-walk to an airlock deck.
    /// - Regular floor below + gravity active → allow (falling through a deck hole).
    /// - Anything else → block (entity floats on current level).
    /// </summary>
    private bool CanAutoDescend(EntityUid uid, CEZPhysicsComponent zPhys, TransformComponent xform, PhysicsComponent physics)
    {
        // Currently on a sticky surface (e.g. already mid-ladder)
        if (zPhys.CurrentStickyGround)
            return true;

        // No floor at this XY on the level below — never descend
        if (!zPhys.CurrentHasSupportBelow)
            return false;

        // Stairs or ladder below — allow descent regardless of gravity
        if (zPhys.CurrentHighGroundBelow)
            return true;

        // Plain floor below — only fall if under gravity (prevents drifting into lower deck in vacuum)
        return !_gravity.IsWeightless(uid, physics, xform);
    }

    private void OnMoveEvent(Entity<CEZPhysicsComponent> ent, ref MoveEvent args)
    {
        CacheMovement(ent);
    }

    private void OnZLevelMapMove(Entity<CEZPhysicsComponent> ent, ref CEZLevelMapMoveEvent args)
    {
        ent.Comp.CurrentZLevel = args.CurrentZLevel;
        DirtyField(ent, ent.Comp, nameof(CEZPhysicsComponent.CurrentZLevel));
        // Update cached ground height when entity moves between Z-level maps
        CacheMovement(ent);
    }

    private void OnGetVelocity(Entity<CEZPhysicsComponent> ent, ref CEGetZVelocityEvent args)
    {
        args.VelocityDelta -= ZGravityForce * ent.Comp.GravityMultiplier;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<CEZPhysicsComponent, CEActiveZPhysicsComponent, TransformComponent, PhysicsComponent>();
        while (query.MoveNext(out var uid, out var zPhys, out _, out var xform, out var physics))
        {
            if (!_zMapQuery.HasComp(xform.MapUid))
                continue;

            var oldVelocity = zPhys.Velocity;
            var oldHeight = zPhys.LocalPosition;

            // Apply Z-gravity unless the entity is resting on an actual floor of the current level.
            // Entities parented to map (no grid) are always BodyStatus.InAir in SS14 physics, so
            // we cannot use physics.BodyStatus to detect ground rest.
            var restingOnGround = !zPhys.CurrentGroundFromBelowLevel
                                  && (zPhys.LocalPosition - zPhys.CurrentGroundHeight) <= 0.001f
                                  && zPhys.Velocity <= 0f;
            if (!restingOnGround)
            {
                var velocityEv = new CEGetZVelocityEvent((uid, zPhys));
                RaiseLocalEvent(uid, velocityEv);
                zPhys.Velocity += velocityEv.VelocityDelta * frameTime;
            }

            //Movement application
            zPhys.LocalPosition += zPhys.Velocity * frameTime;

            var distanceToGround = zPhys.LocalPosition - zPhys.CurrentGroundHeight;
            var snappedUpToGround = false;
            var snappedDownToStickyGround = false;

            // AutoStep: lift entity up if floor is higher.
            // Skip when ground came from the level below — a stair peak poking above this level's
            // floor plane should not trap the entity; let gravity pull it through to the lower level.
            if (zPhys.AutoStep && distanceToGround < 0 && !zPhys.CurrentGroundFromBelowLevel)
            {
                zPhys.LocalPosition = zPhys.CurrentGroundHeight;
                distanceToGround = 0f;
                snappedUpToGround = true;
                if (ZDebugEnabled &&
                    (zPhys.CurrentGroundHeight > 0.01f || zPhys.CurrentStickyGround || zPhys.LocalPosition > 0.01f))
                    DebugZVerbose(uid, $"autostep snapped entity to ground at local={zPhys.LocalPosition:0.00}");
            }

            // Sticky ground: only pull down when slowly falling on sticky surfaces (ladders)
            if (zPhys.CurrentStickyGround && distanceToGround > 0)
            {
                zPhys.LocalPosition = zPhys.CurrentGroundHeight;
                distanceToGround = 0f;
                snappedDownToStickyGround = true;
                if (ZDebugEnabled)
                    DebugZVerbose(uid, $"sticky ground snapped entity to local={zPhys.LocalPosition:0.00}");
            }

            if (zPhys.Velocity < 0) //Falling down
            {
                if (distanceToGround <= 0.05f && !zPhys.CurrentGroundFromBelowLevel) //There`s a ground
                {
                    var suppressBounce = snappedDownToStickyGround ||
                                         snappedUpToGround && zPhys.CurrentGroundHeight > 0.01f;

                    if (suppressBounce)
                    {
                        if (ZDebugEnabled)
                            DebugZVerbose(uid, $"suppressed bounce on stair contact at ground={zPhys.CurrentGroundHeight:0.00}");

                        zPhys.Velocity = 0f;
                    }
                    else
                    {
                        if (MathF.Abs(zPhys.Velocity) >= ImpactVelocityLimit)
                        {
                            var ev = new CEZLevelHitEvent(-zPhys.Velocity);
                            RaiseLocalEvent(uid, ref ev);
                            var land = new LandEvent(null, true);
                            RaiseLocalEvent(uid, ref land);
                        }

                        zPhys.Velocity = -zPhys.Velocity * zPhys.Bounciness;
                    }
                }
            }

            if (zPhys.LocalPosition < 0) // Need to descend to Z-level below
            {
                if (CanAutoDescend(uid, zPhys, xform, physics))
                {
                    if (ZDebugEnabled)
                        DebugZ(uid, "local position dropped below 0, gravity+support present, attempting move down");

                    if (TryMoveDown(uid))
                    {
                        zPhys.LocalPosition += 1;

                        if (!zPhys.CurrentStickyGround)
                        {
                            var fallEv = new CEZLevelFallMapEvent();
                            RaiseLocalEvent(uid, ref fallEv);
                        }
                    }
                    else
                    {
                        // Level below exists but transfer failed — stop cleanly
                        if (ZDebugEnabled)
                            DebugZ(uid, "move down failed despite support below, clamping to 0");
                        zPhys.LocalPosition = 0f;
                        if (zPhys.Velocity < 0f) zPhys.Velocity = 0f;
                    }
                }
                else
                {
                    // Weightless or no floor below — clamp and float on current level
                    if (ZDebugEnabled)
                        DebugZ(uid, $"descent blocked (weightless or no floor below), clamping to 0 — supportBelow={zPhys.CurrentHasSupportBelow}");
                    zPhys.LocalPosition = 0f;
                    if (zPhys.Velocity < 0f) zPhys.Velocity = 0f;
                }
            }

            var upwardTransferThreshold = StairUpTransferHeightThreshold;
            if (zPhys.LocalPosition >= upwardTransferThreshold) //Need teleport to ZLevel up
            {
                var hasTileAbove = HasTileAbove(uid);
                if (hasTileAbove) //Hit roof
                {
                    if (ZDebugEnabled)
                        DebugZ(uid, "upward move blocked by tile above");
                    if (MathF.Abs(zPhys.Velocity) >= ImpactVelocityLimit)
                    {
                        var ev = new CEZLevelHitEvent(zPhys.Velocity);
                        RaiseLocalEvent(uid, ref ev);
                        var land = new LandEvent(null, true);
                        RaiseLocalEvent(uid, ref land);
                    }

                    zPhys.LocalPosition = upwardTransferThreshold;
                    zPhys.Velocity = -zPhys.Velocity * zPhys.Bounciness;
                }
                else //Move up
                {
                    var movedUp = TryMoveUp(uid);
                    if (ZDebugEnabled)
                        DebugZ(uid, $"upward transfer attempted, success={movedUp}");
                    if (movedUp)
                        zPhys.LocalPosition -= 1;
                }
            }

            if (Math.Abs(zPhys.Velocity) > ZVelocityLimit)
                zPhys.Velocity = MathF.Sign(zPhys.Velocity) * ZVelocityLimit;

            if (ZDebugEnabled && ShouldLogMovementTick(zPhys, oldHeight))
            {
                var finalDistanceToGround = zPhys.LocalPosition - zPhys.CurrentGroundHeight;
                DebugZVerbose(uid,
                    $"tick frame={frameTime:0.000} " +
                    $"pos {oldHeight:0.00}->{zPhys.LocalPosition:0.00} vel {oldVelocity:0.00}->{zPhys.Velocity:0.00} " +
                    $"dist={finalDistanceToGround:0.00} ground={zPhys.CurrentGroundHeight:0.00} " +
                    $"fromBelow={zPhys.CurrentGroundFromBelowLevel} resting={restingOnGround}");
            }

            if (Math.Abs(oldVelocity - zPhys.Velocity) > 0.01f)
                DirtyField(uid, zPhys, nameof(CEZPhysicsComponent.Velocity));

            if (Math.Abs(oldHeight - zPhys.LocalPosition) > 0.01f)
                DirtyField(uid, zPhys, nameof(CEZPhysicsComponent.LocalPosition));
        }
    }

    /// <summary>
    /// Returns the last cached distance to the floor.
    /// </summary>
    /// <param name="target">The entity, the distance to the floor which we calculate</param>
    /// <returns></returns>
    public float DistanceToGround(Entity<CEZPhysicsComponent?> target)
    {
        if (!Resolve(target, ref target.Comp, false))
            return 0;

        return target.Comp.LocalPosition - target.Comp.CurrentGroundHeight;
    }

    private bool TryResolveAnyGridOnMap(EntityUid mapUid, out EntityUid gridUid, out MapGridComponent gridComp)
    {
        var gridQuery = EntityQueryEnumerator<MapGridComponent, TransformComponent>();
        while (gridQuery.MoveNext(out var uid, out var grid, out var xform))
        {
            if (xform.MapUid != mapUid)
                continue;

            gridUid = uid;
            gridComp = grid;
            return true;
        }

        if (_gridQuery.TryComp(mapUid, out var mapAsGrid))
        {
            gridUid = mapUid;
            gridComp = mapAsGrid;
            return true;
        }

        gridUid = EntityUid.Invalid;
        gridComp = default!;
        return false;
    }

    private bool TryResolveGridForMapOffset(EntityUid ent, TransformComponent xform, int offset, out EntityUid gridUid, out MapGridComponent gridComp)
    {
        if (offset == 0)
        {
            if (xform.GridUid is { } currentGridUid &&
                _gridQuery.TryComp(currentGridUid, out var currentGrid))
            {
                gridUid = currentGridUid;
                gridComp = currentGrid;
                return true;
            }

            if (xform.MapUid is { } currentMapUid &&
                TryResolveAnyGridOnMap(currentMapUid, out gridUid, out gridComp))
            {
                return true;
            }

            gridUid = EntityUid.Invalid;
            gridComp = default!;
            return false;
        }

        if (xform.GridUid is { } sourceGridUid &&
            TryComp<CEZLinkedGridComponent>(sourceGridUid, out var linked))
        {
            var targetDepth = linked.Depth + offset;
            if (linked.PeerGrids.TryGetValue(targetDepth, out var peerGridUid) &&
                _gridQuery.TryComp(peerGridUid, out var peerGrid))
            {
                DebugZVerbose(ent, $"resolved grid for offset={offset} via linked peer grid {ToPrettyString(peerGridUid)}");

                gridUid = peerGridUid;
                gridComp = peerGrid;
                return true;
            }

            DebugZVerbose(ent, $"no linked peer grid found for offset={offset} targetDepth={targetDepth}");
        }

        if (xform.MapUid is { } sourceMapUid &&
            TryMapOffset(sourceMapUid, offset, out var targetMap) &&
            TryResolveAnyGridOnMap(targetMap.Value.Owner, out gridUid, out gridComp))
        {
            if (offset != 0)
                DebugZVerbose(ent, $"resolved grid for offset={offset} via z-level map {targetMap.Value.Owner} using grid {ToPrettyString(gridUid)}");

            return true;
        }

        gridUid = EntityUid.Invalid;
        gridComp = default!;
        return false;
    }

    private bool TryGetForwardLandingPosition(EntityUid ent, int offset, Vector2 baseTargetWorldPos, EntityUid? targetGridUid, MapId targetMapId, out Vector2 landingWorldPos)
    {
        landingWorldPos = baseTargetWorldPos;

        if (!TryComp<CEZPhysicsComponent>(ent, out var zPhys))
            return false;

        var resolvedByTransferHint = TryGetStairTransferDirection(ent, offset, out var forwardDir);
        TryGetTileLocalPositionForTarget(baseTargetWorldPos, targetGridUid, targetMapId, out var local);

        // Only apply the forward step-off behavior for staircase/slope transitions.
        if (offset > 0)
        {
            if (zPhys.CurrentGroundHeight <= 0.01f)
            {
                DebugZVerbose(ent, $"stair exit nudge skipped for upward move: current ground {zPhys.CurrentGroundHeight:0.00} is not elevated");
                return false;
            }

            var distanceToEdge = GetDirectionalDistanceToNextTileEdge(local, forwardDir);
            landingWorldPos += forwardDir.ToVec() * (distanceToEdge + StairUpLandingForwardNudge);
        }
        else if (offset < 0)
        {
            if (!resolvedByTransferHint)
            {
                DebugZVerbose(ent, "stair exit nudge skipped for downward move: no stair or movement direction was resolved");
                return false;
            }

            // Compute how far the entity already is from the stair's high end.
            // forwardDir points from high end toward low end, so the component of
            // tileLocal along forwardDir equals t (distance from the high end).
            var localAlongForward = forwardDir switch
            {
                Direction.North => local.Y,
                Direction.South => 1f - local.Y,
                Direction.East => local.X,
                Direction.West => 1f - local.X,
                _ => 0.5f,
            };

            // Only nudge the amount needed to reach the target t; never overshoot.
            var neededClearance = StairDownLandingForwardNudge - localAlongForward;
            if (neededClearance > 0)
                landingWorldPos += forwardDir.ToVec() * neededClearance;
        }
        else
        {
            return false;
        }

        DebugZVerbose(ent, $"computed stair exit nudge offset={offset} dir={forwardDir} landing={landingWorldPos}");
        if (!resolvedByTransferHint)
            DebugZVerbose(ent, $"stair exit nudge fell back to facing direction {forwardDir}");

        return true;
    }

    private static Vector2 GetTileLocalPosition(Vector2 localPos)
    {
        return new Vector2((localPos.X % 1 + 1) % 1, (localPos.Y % 1 + 1) % 1);
    }

    private bool TryGetTileLocalPositionForTarget(Vector2 worldPos, EntityUid? targetGridUid, MapId targetMapId, out Vector2 tileLocal)
    {
        if (targetGridUid is { } gridUid &&
            _gridQuery.HasComp(gridUid))
        {
            tileLocal = GetTileLocalPosition(Vector2.Transform(worldPos, _transform.GetInvWorldMatrix(gridUid)));
            return true;
        }

        if (_map.TryGetMap(targetMapId, out var mapUid) &&
            TryResolveAnyGridOnMap(mapUid.Value, out var resolvedGridUid, out _))
        {
            tileLocal = GetTileLocalPosition(Vector2.Transform(worldPos, _transform.GetInvWorldMatrix(resolvedGridUid)));
            return true;
        }

        tileLocal = GetTileLocalPosition(worldPos);
        return false;
    }

    private static float GetDirectionalDistanceToNextTileEdge(Vector2 local, Direction dir)
    {
        return dir switch
        {
            Direction.East => 1f - local.X,
            Direction.West => local.X,
            Direction.North => 1f - local.Y,
            Direction.South => local.Y,
            _ => 0.5f,
        };
    }

    private readonly struct GroundSupportSample
    {
        public readonly EntityUid GridUid;
        public readonly int FloorOffset;
        public readonly EntityUid SupportUid;
        public readonly Direction SurfaceDirection;
        public readonly Vector2 TileLocal;
        public readonly float Sample;
        public readonly float GroundHeight;
        public readonly bool Sticky;
        public readonly bool IsHighGround;

        public GroundSupportSample(
            EntityUid gridUid,
            int floorOffset,
            EntityUid supportUid,
            Direction surfaceDirection,
            Vector2 tileLocal,
            float sample,
            float groundHeight,
            bool sticky,
            bool isHighGround)
        {
            GridUid = gridUid;
            FloorOffset = floorOffset;
            SupportUid = supportUid;
            SurfaceDirection = surfaceDirection;
            TileLocal = tileLocal;
            Sample = sample;
            GroundHeight = groundHeight;
            Sticky = sticky;
            IsHighGround = isHighGround;
        }
    }

    private bool TrySampleHighGround(
        Entity<CEZPhysicsComponent?> target,
        EntityUid checkingGridUid,
        int floor,
        Vector2 tileLocal,
        EntityUid supportUid,
        CEZLevelHighGroundComponent heightComp,
        out GroundSupportSample sample,
        bool logProbe = false)
    {
        sample = default;

        var dir = _transform.GetWorldRotation(supportUid).GetCardinalDir();
        var t = dir switch
        {
            Direction.East => heightComp.Corner ? (tileLocal.X + 1f - tileLocal.Y) / 2f : tileLocal.X,
            Direction.West => heightComp.Corner ? (1f - tileLocal.X + tileLocal.Y) / 2f : 1f - tileLocal.X,
            Direction.North => heightComp.Corner ? (tileLocal.X + tileLocal.Y) / 2f : tileLocal.Y,
            Direction.South => heightComp.Corner ? (1f - tileLocal.X + 1f - tileLocal.Y) / 2f : 1f - tileLocal.Y,
            _ => 0.5f,
        };

        t = Math.Clamp(t, 0f, 1f);

        var curve = heightComp.HeightCurve;
        if (curve.Count == 0)
            return false;

        var groundY = curve.Count == 1
            ? curve[0]
            : InterpolateHeightCurve(curve, t);

        var sticky = target.Comp != null &&
                     target.Comp.Velocity < 0 &&
                     target.Comp.Velocity > -2f &&
                     heightComp.Stick;

        var groundHeight = -floor + groundY;
        sample = new GroundSupportSample(
            checkingGridUid,
            floor,
            supportUid,
            dir,
            tileLocal,
            t,
            groundHeight,
            sticky,
            true);

        if (logProbe)
        {
            DebugZVerbose(target.Owner,
                $"ground probe hit highground {ToPrettyString(supportUid)} floorOffset=-{floor} dir={dir} " +
                $"local=({tileLocal.X:0.00}, {tileLocal.Y:0.00}) sample={t:0.00} result={groundHeight:0.00} sticky={sticky} curvePts={curve.Count}");
        }

        return true;
    }

    private static float InterpolateHeightCurve(List<float> curve, float t)
    {
        var step = 1f / (curve.Count - 1);
        var index = (int) (t / step);
        var frac = (t - index * step) / step;

        var y0 = curve[Math.Clamp(index, 0, curve.Count - 1)];
        var y1 = curve[Math.Clamp(index + 1, 0, curve.Count - 1)];

        return MathHelper.Lerp(y0, y1, frac);
    }

    private bool TryGetGroundSupportSample(Entity<CEZPhysicsComponent?> target, out GroundSupportSample support, int maxFloors = 1, bool logProbe = false)
    {
        support = default;

        if (!Resolve(target, ref target.Comp, false))
            return false;

        var xform = Transform(target);
        if (xform.MapUid is not { } currentMapUid ||
            !_zMapQuery.TryComp(currentMapUid, out var zMapComp))
        {
            if (logProbe)
                DebugZVerbose(target.Owner, "ground probe failed: entity is not on a z-level map");
            return false;
        }

        if (!TryResolveGridForMapOffset(target.Owner, xform, 0, out var mapGridUid, out var mapGrid))
        {
            if (logProbe)
                DebugZVerbose(target.Owner, "ground probe failed: could not resolve current grid");
            return false;
        }

        var worldPos = _transform.GetWorldPosition(target);

        var checkingGridUid = mapGridUid;
        var checkingGrid = mapGrid;

        for (var floor = 0; floor <= maxFloors; floor++)
        {
            if (floor != 0)
            {
                if (!TryMapOffset((currentMapUid, zMapComp), -floor, out var tempCheckingMap))
                {
                    if (logProbe)
                        DebugZVerbose(target.Owner, $"ground probe skipped floor={floor}: no z-level map below");
                    continue;
                }

                if (!TryResolveGridForMapOffset(target.Owner, xform, -floor, out var tempCheckingGridUid, out var tempCheckingGrid))
                {
                    if (logProbe)
                        DebugZVerbose(target.Owner, $"ground probe skipped floor={floor}: could not resolve grid below");
                    continue;
                }

                checkingGridUid = tempCheckingGridUid;
                checkingGrid = tempCheckingGrid;
            }

            var tileLocal = GetTileLocalPosition(Vector2.Transform(worldPos, _transform.GetInvWorldMatrix(checkingGridUid)));
            var tileIndices = _map.WorldToTile(checkingGridUid, checkingGrid, worldPos);

            var foundHighGround = false;
            var bestHighGround = default(GroundSupportSample);
            var query = _map.GetAnchoredEntitiesEnumerator(checkingGridUid, checkingGrid, tileIndices);
            while (query.MoveNext(out var uid))
            {
                if (!_highgroundQuery.TryComp(uid, out var heightComp))
                    continue;

                if (!TrySampleHighGround(target, checkingGridUid, floor, tileLocal, uid.Value, heightComp, out var candidate, logProbe))
                    continue;

                if (!foundHighGround || candidate.GroundHeight > bestHighGround.GroundHeight)
                {
                    bestHighGround = candidate;
                    foundHighGround = true;
                }
            }

            if (foundHighGround)
            {
                support = bestHighGround;
                return true;
            }

            if (_map.TryGetTileRef(checkingGridUid, checkingGrid, worldPos, out var tileRef) &&
                !tileRef.Tile.IsEmpty)
            {
                support = new GroundSupportSample(
                    checkingGridUid,
                    floor,
                    EntityUid.Invalid,
                    Direction.Invalid,
                    tileLocal,
                    0f,
                    -floor,
                    false,
                    false);

                if (logProbe)
                {
                    DebugZVerbose(target.Owner,
                        $"ground probe hit tile floorOffset=-{floor} grid={ToPrettyString(checkingGridUid)} tile={tileIndices} result={-floor:0.00}");
                }

                return true;
            }
        }

        if (logProbe)
            DebugZVerbose(target.Owner, $"ground probe found no support within {maxFloors} floor(s), returning {-maxFloors:0.00}");

        return false;
    }

    private bool TryGetMovementDirection(EntityUid ent, out Direction direction)
    {
        if (TryComp<PhysicsComponent>(ent, out var physics) &&
            physics.LinearVelocity.LengthSquared() > StairDirectionMinimumSpeed * StairDirectionMinimumSpeed)
        {
            direction = physics.LinearVelocity.ToWorldAngle().GetCardinalDir();
            return true;
        }

        if (TryComp<InputMoverComponent>(ent, out var mover) &&
            mover.WishDir.LengthSquared() > StairDirectionMinimumSpeed * StairDirectionMinimumSpeed)
        {
            direction = mover.WishDir.ToWorldAngle().GetCardinalDir();
            return true;
        }

        direction = _transform.GetWorldRotation(ent).GetCardinalDir();
        return false;
    }

    private bool TryGetStairTransferDirection(EntityUid ent, int offset, out Direction direction)
    {
        if (ZPhyzQuery.TryComp(ent, out var zComp) &&
            TryGetGroundSupportSample((ent, zComp), out var support, Math.Abs(offset), false) &&
            support.IsHighGround)
        {
            direction = offset > 0
                ? support.SurfaceDirection.GetOpposite()
                : support.SurfaceDirection;
            return true;
        }

        return TryGetMovementDirection(ent, out direction);
    }

    /// <summary>
    /// Computes the "ground height" relative to the entity's current Z-level.
    /// Returns values where 0 means ground on the same level, -1 means ground one level below,
    /// and intermediate values are possible for high ground entities (stairs).
    /// <paramref name="fromBelowLevel"/> is true when the nearest support was found on the
    /// Z-level below rather than on the current one (FloorOffset > 0). When true, AutoStep
    /// and Bounce are suppressed so a stair peak that pokes above the current-level floor plane
    /// does not trap the entity instead of letting it fall through.
    /// </summary>
    private float ComputeGroundHeightInternal(Entity<CEZPhysicsComponent?> target, out bool stickyGround, out bool fromBelowLevel, int maxFloors = 1)
    {
        stickyGround = false;
        fromBelowLevel = false;
        if (!TryGetGroundSupportSample(target, out var support, maxFloors, true))
            return -maxFloors;

        stickyGround = support.Sticky;
        fromBelowLevel = support.FloorOffset > 0;
        return support.GroundHeight;
    }

    /// <summary>
    /// Checks whether there is a ceiling above the specified entity (tiles on the layer above).
    /// If there are no Z-levels above, false will be returned.
    /// </summary>
    [PublicAPI]
    public bool HasTileAbove(EntityUid ent, Entity<CEZLevelMapComponent?>? currentMapUid = null)
    {
        currentMapUid ??= Transform(ent).MapUid;

        if (currentMapUid is null)
        {
            DebugZVerbose(ent, "roof check failed: entity has no current map");
            return false;
        }

        if (!TryMapUp(currentMapUid.Value, out var mapAboveUid))
        {
            DebugZVerbose(ent, "roof check: no z-level map above");
            return false;
        }

        var xform = Transform(ent);
        if (!TryResolveGridForMapOffset(ent, xform, 1, out var mapAboveGridUid, out var mapAboveGrid))
        {
            DebugZVerbose(ent, "roof check failed: could not resolve grid above");
            return false;
        }

        var hasTileAbove =
            _map.TryGetTileRef(mapAboveGridUid, mapAboveGrid, _transform.GetWorldPosition(ent), out var tileRef) &&
            !tileRef.Tile.IsEmpty;

        DebugZVerbose(ent, $"roof check on map {mapAboveUid.Value.Owner} grid {ToPrettyString(mapAboveGridUid)} result={hasTileAbove}");

        return hasTileAbove;
    }

    /// <summary>
    /// Checks whether there is a ceiling above the specified entity (tiles on the layer above).
    /// If there are no Z-levels above, false will be returned.
    /// </summary>
    [PublicAPI]
    public bool HasTileAbove(Vector2i indices, Entity<CEZLevelMapComponent?> map)
    {
        if (!Resolve(map, ref map.Comp, false))
            return false;

        if (!TryMapUp(map, out var mapAboveUid))
            return false;

        if (!TryResolveAnyGridOnMap(mapAboveUid.Value.Owner, out var mapAboveGridUid, out var mapAboveGrid))
            return false;

        if (_map.TryGetTileRef(mapAboveGridUid, mapAboveGrid, indices, out var tileRef) &&
            !tileRef.Tile.IsEmpty)
            return true;

        return false;
    }

    [PublicAPI]
    public void SetZPosition(Entity<CEZPhysicsComponent?> ent, float newPosition)
    {
        if (!Resolve(ent.Owner, ref ent.Comp))
            return;

        ent.Comp.LocalPosition = newPosition;
        DirtyField(ent, ent.Comp, nameof(CEZPhysicsComponent.LocalPosition));
    }

    [PublicAPI]
    public void UpdateGravityState(Entity<CEZPhysicsComponent?> ent)
    {
        if (!Resolve(ent.Owner, ref ent.Comp))
            return;

        var ev = new CECheckGravityEvent();
        RaiseLocalEvent(ent.Owner, ev);

        SetZGravity(ent, ev.Gravity);
    }

    private void SetZGravity(Entity<CEZPhysicsComponent?> ent, float newGravityMultiplier)
    {
        if (!Resolve(ent.Owner, ref ent.Comp))
            return;

        ent.Comp.GravityMultiplier = newGravityMultiplier;
        DirtyField(ent, ent.Comp, nameof(CEZPhysicsComponent.GravityMultiplier));
    }

    /// <summary>
    /// Sets the vertical velocity for the entity. Positive values make the entity fly upward. Negative values make it fly downward.
    /// </summary>
    [PublicAPI]
    public void SetZVelocity(Entity<CEZPhysicsComponent?> ent, float newVelocity)
    {
        if (!Resolve(ent.Owner, ref ent.Comp))
            return;

        ent.Comp.Velocity = newVelocity;
        DirtyField(ent, ent.Comp, nameof(CEZPhysicsComponent.Velocity));
    }

    /// <summary>
    /// Add the vertical velocity for the entity. Positive values make the entity fly upward. Negative values make it fly downward.
    /// </summary>
    [PublicAPI]
    public void AddZVelocity(Entity<CEZPhysicsComponent?> ent, float newVelocity)
    {
        if (!Resolve(ent.Owner, ref ent.Comp, false))
            return;

        ent.Comp.Velocity += newVelocity;
        DirtyField(ent, ent.Comp, nameof(CEZPhysicsComponent.Velocity));
    }

    /// <summary>
    /// Resolves a vertical move target by preferring linked shuttle peer grids.
    /// This keeps ghost and manual z-moves working while multiz shuttles are temporarily sitting on FTL maps.
    /// </summary>
    private bool TryResolveLinkedMoveTarget(EntityUid ent, int offset, out MapId targetMapId, out int targetZLevel, out EntityUid? peerGridUid)
    {
        targetMapId = default;
        targetZLevel = default;
        peerGridUid = null;

        var xform = Transform(ent);
        if (xform.GridUid is not { } currentGridUid ||
            !TryComp<CEZLinkedGridComponent>(currentGridUid, out var linked))
        {
            return false;
        }

        targetZLevel = linked.Depth + offset;
        if (!linked.PeerGrids.TryGetValue(targetZLevel, out var targetPeerGridUid))
        {
            DebugZVerbose(ent, $"linked move target missing for offset={offset} targetZ={targetZLevel}");
            return false;
        }

        if (Transform(targetPeerGridUid).MapUid is not { } targetMapUid ||
            !_mapQuery.TryComp(targetMapUid, out var targetMapComp))
        {
            DebugZVerbose(ent, $"linked move target grid {ToPrettyString(targetPeerGridUid)} has no valid target map");
            return false;
        }

        peerGridUid = targetPeerGridUid;
        targetMapId = targetMapComp.MapId;
        return true;
    }

    /// <summary>
    /// Preserves the mover's local position inside the multiz structure when transitioning between linked grids.
    /// </summary>
    private Vector2 GetLinkedMoveTargetPosition(EntityUid ent, EntityUid peerGridUid, Vector2 fallbackWorldPosition)
    {
        var xform = Transform(ent);
        if (xform.GridUid is not { } currentGridUid)
            return fallbackWorldPosition;

        var currentGridMatrix = _transform.GetWorldMatrix(currentGridUid);
        var peerGridMatrix = _transform.GetWorldMatrix(peerGridUid);

        if (!Matrix3x2.Invert(currentGridMatrix, out var inverseCurrentGrid))
            return fallbackWorldPosition;

        var localToCurrentGrid = Vector2.Transform(fallbackWorldPosition, inverseCurrentGrid);
        return Vector2.Transform(localToCurrentGrid, peerGridMatrix);
    }

    [PublicAPI]
    public bool TryMove(EntityUid ent, int offset, Entity<CEZLevelMapComponent?>? map = null)
    {
        MapId targetMapId;
        int targetZLevel;
        EntityUid? peerGridUid;
        var worldPos = _transform.GetWorldPosition(ent);
        var worldRot = _transform.GetWorldRotation(ent);

        if (!TryResolveLinkedMoveTarget(ent, offset, out targetMapId, out targetZLevel, out peerGridUid))
        {
            map ??= Transform(ent).MapUid;

            if (map is null)
            {
                if (ZDebugEnabled)
                    DebugZ(ent, $"move failed: no current map for offset={offset}");
                return false;
            }

            if (!TryMapOffset(map.Value, offset, out var targetMap))
            {
                if (ZDebugEnabled)
                    DebugZ(ent, $"move failed: no target map at offset={offset}");
                return false;
            }

            if (!_mapQuery.TryComp(targetMap, out var targetMapComp))
            {
                if (ZDebugEnabled)
                    DebugZ(ent, $"move failed: target map {targetMap.Value.Owner} has no map component");
                return false;
            }

            targetMapId = targetMapComp.MapId;
            targetZLevel = targetMap.Value.Comp.Depth;
        }

        if (ZDebugEnabled)
            DebugZ(ent, $"attempting move offset={offset} targetMapId={targetMapId} targetZ={targetZLevel} peerGrid={peerGridUid} sourceWorld={worldPos}");

        var beforeEv = new CEZLevelBeforeMapMoveEvent(offset, targetZLevel);
        RaiseLocalEvent(ent, ref beforeEv);

        var targetWorldPos = peerGridUid != null
            ? GetLinkedMoveTargetPosition(ent, peerGridUid.Value, worldPos)
            : worldPos;

        if (TryGetForwardLandingPosition(ent, offset, targetWorldPos, peerGridUid, targetMapId, out var forwardLandingWorldPos))
        {
            DebugZVerbose(ent, $"using stair exit nudge landing at {forwardLandingWorldPos} for offset={offset}");

            targetWorldPos = forwardLandingWorldPos;
        }

        // Save mover eye rotation state before the move.
        // OnInputParentChange resets RelativeRotation on map change, causing an eye snap.
        // We compensate for the change in relative entity to keep eye orientation seamless.
        Angle savedRelativeRot = default;
        Angle savedTargetRelativeRot = default;
        Angle savedEyeWorldRot = worldRot;
        Angle savedTargetEyeWorldRot = worldRot;
        var hasMover = TryComp<InputMoverComponent>(ent, out var mover);
        if (hasMover)
        {
            savedRelativeRot = mover!.RelativeRotation;
            savedTargetRelativeRot = mover.TargetRelativeRotation;
            if (mover.RelativeEntity is { } oldRelativeEntity)
            {
                var oldRelativeWorldRot = _transform.GetWorldRotation(oldRelativeEntity);
                savedEyeWorldRot = oldRelativeWorldRot + savedRelativeRot;
                savedTargetEyeWorldRot = oldRelativeWorldRot + savedTargetRelativeRot;
            }
        }

        // SetMapCoordinates doesn't preserve rotation when reparenting across maps.
        // We save world rotation and restore it after the move.
        if (peerGridUid is { } targetPeerGridUid)
        {
            var peerGridCoordinates = new EntityCoordinates(
                targetPeerGridUid,
                Vector2.Transform(targetWorldPos, _transform.GetInvWorldMatrix(targetPeerGridUid)));
            _transform.SetCoordinates(ent, peerGridCoordinates);
        }
        else
        {
            _transform.SetMapCoordinates(ent, new MapCoordinates(targetWorldPos, targetMapId));
        }
        // Force set both local rotation and world rotation to ensure consistency.
        var xform = Transform(ent);
        var parentRot = _transform.GetWorldRotation(xform.ParentUid);
        _transform.SetLocalRotation(ent, worldRot - parentRot);

        // Restore mover eye rotation to preserve visual orientation across Z-level transition.
        // Preserve the current eye world rotation directly instead of diffing parent rotations,
        // which is more stable when linked-grid transitions trigger multiple parent changes.
        if (hasMover && TryComp<InputMoverComponent>(ent, out var moverAfter))
        {
            var newRelative = xform.GridUid ?? xform.MapUid;
            var newRelRot = newRelative != null
                ? _transform.GetWorldRotation(newRelative.Value)
                : Angle.Zero;

            moverAfter.RelativeEntity = newRelative;
            moverAfter.RelativeRotation = savedEyeWorldRot - newRelRot;
            moverAfter.TargetRelativeRotation = savedTargetEyeWorldRot - newRelRot;
            Dirty(ent, moverAfter);
        }

        var ev = new CEZLevelMapMoveEvent(offset, targetZLevel);
        RaiseLocalEvent(ent, ref ev);

        if (ZDebugEnabled)
            DebugZ(ent, $"move succeeded offset={offset} newZ={targetZLevel} landing={targetWorldPos}");

        return true;
    }

    [PublicAPI]
    public bool TryMoveUp(EntityUid ent)
    {
        return TryMove(ent, 1);
    }

    [PublicAPI]
    public bool TryMoveDown(EntityUid ent)
    {
        return TryMove(ent, -1);
    }

    [PublicAPI]
    public bool TryMoveDownOrChasm(EntityUid ent)
    {
        if (TryMoveDown(ent))
        {
            if (ZDebugEnabled)
                DebugZ(ent, "downward transfer completed");
            return true;
        }

        //welp, that default Chasm behavior. Not really good, but ok for now.
        if (HasComp<ChasmFallingComponent>(ent))
        {
            if (ZDebugEnabled)
                DebugZ(ent, "downward transfer failed and entity is already in chasm fall");
            return false; //Already falling
        }

        var attempt = new CEZLevelChasmAttempt(ent);
        RaiseLocalEvent(ent, attempt);

        if (attempt.Cancelled)
        {
            if (ZDebugEnabled)
                DebugZ(ent, "downward transfer failed and chasm fallback was cancelled");
            return false;
        }

        var audio = new SoundPathSpecifier("/Audio/Effects/falling.ogg");
        _audio.PlayPredicted(audio, Transform(ent).Coordinates, ent);
        var falling = AddComp<ChasmFallingComponent>(ent);
        falling.NextDeletionTime = _timing.CurTime + falling.DeletionTime;
        _blocker.UpdateCanMove(ent);

        if (ZDebugEnabled)
            DebugZ(ent, "downward transfer failed; entity entered chasm fall");

        return false;
    }
}

/// <summary>
/// Is called on an entity right before it moves between z-levels.
/// </summary>
/// <param name="offset">How many levels were crossed. If negative, it means there was a downward movement. If positive, it means an upward movement.</param>
[ByRefEvent]
public struct CEZLevelBeforeMapMoveEvent(int offset, int level)
{
    /// <summary>
    /// How many levels were crossed. If negative, it means there was a downward movement. If positive, it means an upward movement.
    /// </summary>
    public int Offset = offset;

    public int CurrentZLevel = level;
}

/// <summary>
/// Is called on an entity when it moves between z-levels.
/// </summary>
/// <param name="offset">How many levels were crossed. If negative, it means there was a downward movement. If positive, it means an upward movement.</param>
[ByRefEvent]
public struct CEZLevelMapMoveEvent(int offset, int level)
{
    /// <summary>
    /// How many levels were crossed. If negative, it means there was a downward movement. If positive, it means an upward movement.
    /// </summary>
    public int Offset = offset;

    public int CurrentZLevel = level;
}

/// <summary>
///Called upon the essence before attempting to fall into the abyss
/// </summary>
public sealed class CEZLevelChasmAttempt(EntityUid falled) : CancellableEntityEventArgs, IInventoryRelayEvent
{
    public EntityUid Falled = falled;
    public SlotFlags TargetSlots => SlotFlags.All;
}

/// <summary>
/// Is triggered when an entity falls to the lower z-levels under the force of gravity
/// </summary>
[ByRefEvent]
public struct CEZLevelFallMapEvent;

/// <summary>
/// It is called on an entity when it hits the floor or ceiling with force.
/// </summary>
/// <param name="impactPower">The speed at the moment of impact. Always positive</param>
[ByRefEvent]
public struct CEZLevelHitEvent(float impactPower)
{
    /// <summary>
    /// The speed at the moment of impact. Always positive
    /// </summary>
    public float ImpactPower = impactPower;
}

/// <summary>
/// Is called every frame to calculate the current vertical velocity of the object with CEActiveZPhysicsComponent.
/// </summary>
public sealed class CEGetZVelocityEvent(Entity<CEZPhysicsComponent> target) : EntityEventArgs
{
    public Entity<CEZPhysicsComponent> Target = target;
    public float VelocityDelta = 0;
}

/// <summary>
/// Called when UpdateGravityState is used to update the current strength of the active z-level gravity. Various systems can subscribe to this to disable gravity.
/// </summary>
public sealed class CECheckGravityEvent : EntityEventArgs
{
    public float Gravity = 1f;
}
