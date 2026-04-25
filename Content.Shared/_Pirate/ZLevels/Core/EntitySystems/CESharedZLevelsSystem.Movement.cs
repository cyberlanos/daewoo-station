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
    // Transfer up when the mover's sampled center reaches late stair 3.
    // In practice the player's center never reaches the geometric 0.3125 cutoff because the
    // stair collision stops the body slightly earlier, so we use a center-sample threshold that
    // matches the reachable point seen in live traces while still leaving enough separation from
    // the downward landing sample to avoid reintroducing up/down stair loops.
    // Keep a tiny tolerance here because the same stair path can stabilize around ~0.381-0.383
    // after a full up/down cycle due to landing and collision quantization.
    private const float StairUpTransferSampleThreshold = 0.39f;
    // Place the mover just inside the next tile after climbing so it will not immediately re-trigger descent.
    private const float StairUpLandingForwardNudge = 0.05f;
    // Place the mover at the start of stair 3 after descending so it does not instantly climb back up.
    private const float StairDownLandingSample = 0.64f;
    private const float StairDirectionMinimumSpeed = 0.01f;
    private const float StairTransferGraceSeconds = 0.2f;
    private const float StairTransferMovingGridGraceScale = 0.01f;
    private const float StairTransferMovingGridGraceMaxSeconds = 0.8f;
    private const float FlatGroundSettleVelocityThreshold = 1.0f;
    private static readonly float[] StairUpLandingSearchSamples = [0.05f, 0.15f, 0.25f, 0.35f, 0.45f];

    /// <summary>
    /// The minimum speed required to trigger LandEvent events.
    /// </summary>
    private const float ImpactVelocityLimit = 3f;

    private readonly Dictionary<EntityUid, DeferredClientMovingStairDescent> _deferredClientMovingStairDescents = new();
    private EntityQuery<CEZLevelHighGroundComponent> _highgroundQuery;

    private struct DeferredClientMovingStairDescent
    {
        public TimeSpan ExpiresAt;
        public EntityUid SourceMapUid;
        public EntityUid SupportGridUid;
    }

    private enum AutoDescendMode
    {
        None,
        ControlledStep,
        FreeFall
    }

    private void InitMovement()
    {
        _highgroundQuery = GetEntityQuery<CEZLevelHighGroundComponent>();

        SubscribeLocalEvent<CEZPhysicsComponent, CEGetZVelocityEvent>(OnGetVelocity);
        SubscribeLocalEvent<CEZPhysicsComponent, CEZLevelMapMoveEvent>(OnZLevelMapMove);
        SubscribeLocalEvent<CEActiveZPhysicsComponent, ComponentInit>(OnActiveInit);

        SubscribeLocalEvent<CEZPhysicsComponent, MoveEvent>(OnMoveEvent);
        SubscribeLocalEvent<MapGridComponent, EntParentChangedMessage>(OnGridParentChanged);
        SubscribeLocalEvent<MapGridComponent, MapUidChangedEvent>(OnGridMapUidChanged);
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
    /// Resolves whether there is actual support directly below this entity on the next z-level.
    /// Returns the supporting grid uid so callers can inspect the lower deck's gravity state live,
    /// which is important for moving linked shuttle grids.
    /// </summary>
    private bool TryGetSupportBelow(EntityUid ent, TransformComponent xform, out EntityUid belowGridUid, out bool isHighGround)
    {
        belowGridUid = EntityUid.Invalid;
        isHighGround = false;

        if (!TryResolveGridForMapOffset(ent, xform, -1, out belowGridUid, out var belowGrid))
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
    /// Returns true when the tile or high-ground directly below this entity belongs to a grid/map that currently has gravity.
    /// This is evaluated live from world position instead of using cached movement state, so it stays correct for moving shuttles.
    /// </summary>
    [PublicAPI]
    public bool HasEffectiveGravityFromBelow(EntityUid ent, TransformComponent? xform = null)
    {
        if (!Resolve(ent, ref xform, false))
            return false;

        if (!TryGetSupportBelow(ent, xform, out var belowGridUid, out _))
            return false;

        return _gravity.EntityGridOrMapHaveGravity((belowGridUid, Transform(belowGridUid)));
    }

    private bool TryFindSupportedLevelBelow(EntityUid ent, TransformComponent xform, out int supportOffset, out EntityUid supportGridUid, out bool isHighGround)
    {
        supportOffset = 0;
        supportGridUid = EntityUid.Invalid;
        isHighGround = false;

        var worldPos = _transform.GetWorldPosition(ent);

        for (var offset = 1; ; offset++)
        {
            if (!TryResolveGridForMapOffset(ent, xform, -offset, out var belowGridUid, out var belowGrid))
                break;

            var tileIndices = _map.WorldToTile(belowGridUid, belowGrid, worldPos);
            var anchoredQuery = _map.GetAnchoredEntitiesEnumerator(belowGridUid, belowGrid, tileIndices);
            while (anchoredQuery.MoveNext(out var uid))
            {
                if (!_highgroundQuery.HasComp(uid.Value))
                    continue;

                supportOffset = offset;
                supportGridUid = belowGridUid;
                isHighGround = true;
                return true;
            }

            if (!_map.TryGetTileRef(belowGridUid, belowGrid, worldPos, out var tileRef) || tileRef.Tile.IsEmpty)
                continue;

            supportOffset = offset;
            supportGridUid = belowGridUid;
            return true;
        }

        return false;
    }

    private AutoDescendMode GetAutoDescendMode(EntityUid uid,
        CEZPhysicsComponent zPhys,
        TransformComponent xform,
        out int supportOffset,
        out EntityUid supportGridUid,
        out bool supportIsHighGround,
        out bool effectiveGravityBelow)
    {
        supportOffset = 0;
        supportGridUid = EntityUid.Invalid;
        supportIsHighGround = false;
        effectiveGravityBelow = false;

        if (zPhys.CurrentStickyGround)
        {
            if (TryResolveGridForMapOffset(uid, xform, -1, out supportGridUid, out _))
                return AutoDescendMode.ControlledStep;

            return AutoDescendMode.None;
        }

        // When the mover has already slipped off the current deck and the cached probe says the
        // immediate level below is a stair, prefer that snapshot over a second live lookup.
        // On moving shuttles the live probe can miss the stair for one tick while map-parented,
        // which incorrectly downgrades a stair descent into a free-fall drop.
        if (zPhys.CurrentGroundFromBelowLevel &&
            zPhys.CurrentHighGroundBelow &&
            TryResolveGridForMapOffset(uid, xform, -1, out supportGridUid, out _))
        {
            supportOffset = 1;
            supportIsHighGround = true;
            effectiveGravityBelow = _gravity.EntityGridOrMapHaveGravity((supportGridUid, Transform(supportGridUid)));
            return AutoDescendMode.ControlledStep;
        }

        if (!TryFindSupportedLevelBelow(uid, xform, out supportOffset, out supportGridUid, out supportIsHighGround))
            return AutoDescendMode.None;

        effectiveGravityBelow = _gravity.EntityGridOrMapHaveGravity((supportGridUid, Transform(supportGridUid)));

        if (supportOffset == 1 && supportIsHighGround)
            return AutoDescendMode.ControlledStep;

        if (effectiveGravityBelow)
            return AutoDescendMode.FreeFall;

        return AutoDescendMode.None;
    }

    /// <summary>
    /// Returns true when the entity is allowed to automatically descend to the Z-level below.
    /// Rules:
    /// - Sticky surface (currently on a ladder) → always allow.
    /// - High-ground (stairs/ladder) directly below → always allow; stair traversal works
    ///   even in weightless areas so the player can walk down from a space-walk to an airlock deck.
    /// - Regular floor below + effective gravity from that lower support → allow.
    /// - Anything else → block (entity floats on current level).
    /// </summary>
    private bool CanAutoDescend(EntityUid uid, CEZPhysicsComponent zPhys, TransformComponent xform)
    {
        // Currently on a sticky surface (e.g. already mid-ladder)
        if (zPhys.CurrentStickyGround)
            return true;

        // No floor at this XY on the level below — never descend
        if (!TryGetSupportBelow(uid, xform, out _, out var isHighGround))
            return false;

        // Stairs or ladder below — allow descent regardless of gravity
        if (isHighGround)
            return true;

        // Plain floor below — only fall if that lower support actually has gravity
        return HasEffectiveGravityFromBelow(uid, xform);
    }

    private void OnMoveEvent(Entity<CEZPhysicsComponent> ent, ref MoveEvent args)
    {
        PruneDeferredClientMovingStairDescent(ent, Transform(ent));

        if (args.ParentChanged &&
            (_net.IsServer ||
             _net.IsClient && !_timing.ApplyingState))
        {
            if (args.OldPosition.EntityId != EntityUid.Invalid &&
                TryComp<CEZLinkedGridComponent>(args.OldPosition.EntityId, out _) &&
                args.NewPosition.EntityId == Transform(ent).MapUid)
            {
                ent.Comp.DetachedCarrierGridUid = args.OldPosition.EntityId;
                ent.Comp.DetachedCarrierLocalPosition = args.OldPosition.Position;
                DebugZStairCsv(ent,
                    "detached_cache_capture",
                    $"carrier_grid={ToPrettyString(args.OldPosition.EntityId)},cached_local={StairCsvVec2(args.OldPosition.Position)},carrier_world={GetEntityWorldPositionCsv(args.OldPosition.EntityId)},carrier_rot={StairCsvFloat((float) _transform.GetWorldRotation(args.OldPosition.EntityId).Degrees)},carrier_vel={GetEntityVelocityCsv(args.OldPosition.EntityId)},target_map={args.NewPosition.EntityId}",
                    $"{ToPrettyString(args.OldPosition.EntityId)}|{args.NewPosition.EntityId}|{StairCsvDedupeVec2(args.OldPosition.Position, 2)}");
            }
            else
            {
                ent.Comp.DetachedCarrierGridUid = EntityUid.Invalid;
                ent.Comp.DetachedCarrierLocalPosition = Vector2.Zero;
            }
        }

        CacheMovement(ent);
    }

    private void OnGridParentChanged(Entity<MapGridComponent> ent, ref EntParentChangedMessage args)
    {
        RefreshAttachedZPhysics(ent.Owner);
    }

    private void OnGridMapUidChanged(Entity<MapGridComponent> ent, ref MapUidChangedEvent args)
    {
        RefreshAttachedZPhysics(ent.Owner);
    }

    private bool ShouldStayAttachedToCarrierGrid(CEZPhysicsComponent zPhys)
    {
        return zPhys.CurrentStickyGround ||
               zPhys.CurrentHasSupportBelow ||
               zPhys.CurrentHighGroundBelow ||
               zPhys.CurrentGroundFromBelowLevel ||
               zPhys.CurrentGroundHeight > 0.01f ||
               zPhys.LocalPosition < 0f;
    }

    private void ClearDetachedCarrierReference(CEZPhysicsComponent zPhys)
    {
        zPhys.DetachedCarrierGridUid = EntityUid.Invalid;
        zPhys.DetachedCarrierLocalPosition = Vector2.Zero;
    }

    private bool TryGetDetachedCarrierLocalReference(CEZPhysicsComponent zPhys, out EntityUid sourceGridUid, out Vector2 carrierLocal)
    {
        sourceGridUid = EntityUid.Invalid;
        carrierLocal = Vector2.Zero;

        if (zPhys.DetachedCarrierGridUid == EntityUid.Invalid ||
            !zPhys.CurrentGroundFromBelowLevel)
        {
            return false;
        }

        sourceGridUid = zPhys.DetachedCarrierGridUid;
        carrierLocal = zPhys.DetachedCarrierLocalPosition;
        return true;
    }

    private bool ShouldDeferClientPredictedMovingStairDescent(EntityUid uid, CEZPhysicsComponent zPhys, TransformComponent xform, EntityUid supportGridUid, bool supportIsHighGround)
    {
        if (!_net.IsClient ||
            _timing.ApplyingState ||
            xform.GridUid != null ||
            !zPhys.CurrentGroundFromBelowLevel ||
            !zPhys.CurrentHighGroundBelow ||
            !supportIsHighGround ||
            supportGridUid == EntityUid.Invalid ||
            !TryGetLinearVelocity(supportGridUid, out var supportVelocity))
        {
            return false;
        }

        // Once the mover has detached from the upper linked grid, the cached carrier-local
        // coordinate lets the client predict the same peer-grid landing as the server.
        if (TryGetDetachedCarrierLocalReference(zPhys, out _, out _))
            return false;

        return supportVelocity.LengthSquared() > 0.01f * 0.01f;
    }

    private void RefreshDeferredClientMovingStairDescent(EntityUid uid, EntityUid sourceMapUid, EntityUid supportGridUid)
    {
        if (!_net.IsClient ||
            sourceMapUid == EntityUid.Invalid ||
            supportGridUid == EntityUid.Invalid)
        {
            return;
        }

        _deferredClientMovingStairDescents[uid] = new DeferredClientMovingStairDescent
        {
            ExpiresAt = _timing.CurTime + TimeSpan.FromSeconds(StairTransferMovingGridGraceMaxSeconds),
            SourceMapUid = sourceMapUid,
            SupportGridUid = supportGridUid
        };
    }

    private void PruneDeferredClientMovingStairDescent(EntityUid uid, TransformComponent xform)
    {
        if (!_net.IsClient ||
            !_deferredClientMovingStairDescents.TryGetValue(uid, out var deferred))
        {
            return;
        }

        if (_timing.CurTime > deferred.ExpiresAt ||
            xform.MapUid != deferred.SourceMapUid ||
            xform.GridUid == deferred.SupportGridUid)
        {
            _deferredClientMovingStairDescents.Remove(uid);
        }
    }

    private bool ShouldBlockClientCarrierReattach(EntityUid uid, TransformComponent xform)
    {
        if (!_net.IsClient)
            return false;

        PruneDeferredClientMovingStairDescent(uid, xform);

        return xform.GridUid == null &&
               _deferredClientMovingStairDescents.TryGetValue(uid, out var deferred) &&
               xform.MapUid == deferred.SourceMapUid;
    }

    private bool TryResolveCurrentLinkedGrid(EntityUid ent, TransformComponent xform, out EntityUid gridUid, out CEZLinkedGridComponent linked, out string resolutionSource)
    {
        gridUid = EntityUid.Invalid;
        linked = default!;
        resolutionSource = "none";

        if (xform.GridUid is { } currentGridUid &&
            TryComp<CEZLinkedGridComponent>(currentGridUid, out var currentLinked))
        {
            gridUid = currentGridUid;
            linked = currentLinked;
            resolutionSource = "parent_grid";
            return true;
        }

        var worldPos = _transform.GetWorldPosition(ent);
        if (xform.MapUid is { } currentMapUid &&
            TryResolveGridAtWorldPositionOnMap(currentMapUid, worldPos, out var worldGridUid, out _) &&
            TryComp<CEZLinkedGridComponent>(worldGridUid, out var worldLinked))
        {
            gridUid = worldGridUid;
            linked = worldLinked;
            resolutionSource = "world_grid";
            return true;
        }

        if (xform.MapUid is not { } fallbackMapUid ||
            !TryGetTraversalDepth(xform, out var depth))
        {
            return false;
        }

        var found = false;
        var bestDistance = float.MaxValue;
        var query = EntityQueryEnumerator<CEZLinkedGridComponent, TransformComponent>();
        while (query.MoveNext(out var candidateUid, out var candidateLinked, out var candidateXform))
        {
            if (candidateLinked.Depth != depth ||
                candidateXform.MapUid != fallbackMapUid)
            {
                continue;
            }

            var candidateDistance = Vector2.DistanceSquared(_transform.GetWorldPosition(candidateUid), worldPos);
            if (found && candidateDistance >= bestDistance)
                continue;

            found = true;
            bestDistance = candidateDistance;
            gridUid = candidateUid;
            linked = candidateLinked;
        }

        if (found)
        {
            resolutionSource = "nearest_linked";
            return true;
        }

        return false;
    }

    private bool TryAttachToCarrierGrid(EntityUid ent, CEZPhysicsComponent zPhys, ref TransformComponent xform)
    {
        if (ShouldBlockClientCarrierReattach(ent, xform))
            return false;

        var shouldAttach = ShouldStayAttachedToCarrierGrid(zPhys);
        var resolvedCarrier = TryResolveCurrentLinkedGrid(ent, xform, out var carrierGridUid, out _, out var carrierResolveSource);
        var descentCandidate = zPhys.CurrentGroundFromBelowLevel || zPhys.LocalPosition < 0f;

        if (_net.IsServer && xform.GridUid == null)
        {
            DebugZStairCsv(ent,
                "carrier_check",
                $"has_grid=0,should_attach={StairCsvBool(shouldAttach)},descent_candidate={StairCsvBool(descentCandidate)},resolved={StairCsvBool(resolvedCarrier)},resolve_source={carrierResolveSource},carrier={(resolvedCarrier ? ToPrettyString(carrierGridUid) : "null")},world={StairCsvVec2(_transform.GetWorldPosition(ent))}",
                $"0|{StairCsvBool(shouldAttach)}|{StairCsvBool(descentCandidate)}|{StairCsvBool(resolvedCarrier)}|{carrierResolveSource}|{(resolvedCarrier ? ToPrettyString(carrierGridUid) : "null")}");
        }

        if (_net.IsServer &&
            xform.GridUid == null &&
            descentCandidate &&
            shouldAttach &&
            resolvedCarrier)
        {
            DebugZStairCsv(ent,
                "carrier_skip",
                $"reason=descent_candidate,carrier={ToPrettyString(carrierGridUid)},resolve_source={carrierResolveSource},local={StairCsvFloat(zPhys.LocalPosition)},from_below={StairCsvBool(zPhys.CurrentGroundFromBelowLevel)}",
                $"descent_candidate|{ToPrettyString(carrierGridUid)}|{carrierResolveSource}|{StairCsvBool(zPhys.CurrentGroundFromBelowLevel)}|{StairCsvFloat(zPhys.LocalPosition)}");
        }

        if (xform.GridUid != null ||
            descentCandidate ||
            !shouldAttach ||
            !resolvedCarrier)
        {
            return false;
        }

        var worldPos = _transform.GetWorldPosition(ent);
        var carrierLocal = Vector2.Transform(worldPos, _transform.GetInvWorldMatrix(carrierGridUid));
        _transform.SetCoordinates(ent, new EntityCoordinates(carrierGridUid, carrierLocal));
        ClearDetachedCarrierReference(zPhys);
        xform = Transform(ent);
        CacheMovement((ent, zPhys));

        if (_net.IsServer && ZDebugStairsEnabled)
        {
            var carrierVelocity = TryGetLinearVelocity(carrierGridUid, out var velocity)
                ? StairCsvVec2(velocity)
                : "na";
            DebugZStairCsv(ent,
                "carrier_attach",
                $"carrier={ToPrettyString(carrierGridUid)},resolve_source={carrierResolveSource},world={StairCsvVec2(worldPos)},local={StairCsvVec2(carrierLocal)},carrier_vel={carrierVelocity}",
                $"{ToPrettyString(carrierGridUid)}|{carrierResolveSource}|{StairCsvVec2(carrierLocal)}|{carrierVelocity}");
        }

        if (ZDebugEnabled)
            DebugZVerbose(ent, $"reattached mover to carrier grid {ToPrettyString(carrierGridUid)} while preserving world position {worldPos}");

        return true;
    }

    private void RefreshAttachedZPhysics(EntityUid rootUid)
    {
        var stack = new Stack<EntityUid>();
        stack.Push(rootUid);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            var xform = Transform(current);
            using var children = xform.ChildEnumerator;
            while (children.MoveNext(out var child))
            {
                stack.Push(child);

                if (!ZPhyzQuery.TryComp(child, out var zPhys))
                    continue;

                CacheMovement((child, zPhys));

                if (TryGetTraversalDepth(Transform(child), out var depth) &&
                    zPhys.CurrentZLevel != depth)
                {
                    zPhys.CurrentZLevel = depth;
                    DirtyField(child, zPhys, nameof(CEZPhysicsComponent.CurrentZLevel));
                }

                zPhys.StartupSuppressedUntil = _timing.CurTime + StartupActivationDelay;
            }
        }
    }

    private void OnZLevelMapMove(Entity<CEZPhysicsComponent> ent, ref CEZLevelMapMoveEvent args)
    {
        if (ent.Comp.CurrentZLevel != args.CurrentZLevel)
        {
            ent.Comp.CurrentZLevel = args.CurrentZLevel;
            DirtyField(ent, ent.Comp, nameof(CEZPhysicsComponent.CurrentZLevel));
        }

        DebugZStairCsv(ent,
            "zlevel_event",
            $"offset={args.Offset},current_z={args.CurrentZLevel}");
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
            if (!HasTraversalContext(xform))
                continue;

            TryAttachToCarrierGrid(uid, zPhys, ref xform);

            var oldVelocity = zPhys.Velocity;
            var oldHeight = zPhys.LocalPosition;
            var startedOnElevatedGround = zPhys.CurrentStickyGround || zPhys.CurrentGroundHeight > 0.01f;

            if (_timing.CurTime < zPhys.StartupSuppressedUntil)
            {
                CacheMovement((uid, zPhys));

                if (!zPhys.CurrentGroundFromBelowLevel && zPhys.LocalPosition < zPhys.CurrentGroundHeight)
                    zPhys.LocalPosition = zPhys.CurrentGroundHeight;

                if (zPhys.Velocity != 0f)
                    zPhys.Velocity = 0f;

                if (Math.Abs(oldVelocity - zPhys.Velocity) > 0.01f)
                    DirtyField(uid, zPhys, nameof(CEZPhysicsComponent.Velocity));

                if (Math.Abs(oldHeight - zPhys.LocalPosition) > 0.01f)
                    DirtyField(uid, zPhys, nameof(CEZPhysicsComponent.LocalPosition));

                continue;
            }

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
            var distanceToGroundBeforeSnap = distanceToGround;
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
                DebugZStairCsv(uid,
                    "snap_up",
                    $"dist_before={StairCsvFloat(distanceToGroundBeforeSnap)},from_below={StairCsvBool(zPhys.CurrentGroundFromBelowLevel)}",
                    $"{StairCsvFloat(MathF.Round(zPhys.CurrentGroundHeight, 2))}|{StairCsvFloat(MathF.Round(distanceToGroundBeforeSnap, 2))}|{StairCsvBool(zPhys.CurrentGroundFromBelowLevel)}");
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
                DebugZStairCsv(uid,
                    "snap_sticky",
                    $"dist_before={StairCsvFloat(distanceToGroundBeforeSnap)},from_below={StairCsvBool(zPhys.CurrentGroundFromBelowLevel)}",
                    $"{StairCsvFloat(MathF.Round(zPhys.CurrentGroundHeight, 2))}|{StairCsvFloat(MathF.Round(distanceToGroundBeforeSnap, 2))}|{StairCsvBool(zPhys.CurrentGroundFromBelowLevel)}");
                if (ZDebugEnabled)
                    DebugZVerbose(uid, $"sticky ground snapped entity to local={zPhys.LocalPosition:0.00}");
            }

            if (zPhys.Velocity < 0) //Falling down
            {
                if (distanceToGround <= 0.05f && !zPhys.CurrentGroundFromBelowLevel) //There`s a ground
                {
                    var impactPower = MathF.Abs(zPhys.Velocity);
                    var suppressBounce = snappedDownToStickyGround ||
                                         snappedUpToGround &&
                                         (zPhys.CurrentGroundHeight > 0.01f ||
                                          impactPower <= FlatGroundSettleVelocityThreshold ||
                                          startedOnElevatedGround);

                    DebugZStairCsv(uid,
                        "ground_contact",
                        $"dist={StairCsvFloat(distanceToGround)},suppress={StairCsvBool(suppressBounce)},impact={StairCsvFloat(impactPower)},snap_up={StairCsvBool(snappedUpToGround)},snap_sticky={StairCsvBool(snappedDownToStickyGround)},from_below={StairCsvBool(zPhys.CurrentGroundFromBelowLevel)}",
                        $"{StairCsvBool(suppressBounce)}|{StairCsvFloat(MathF.Round(impactPower, 2))}|{StairCsvFloat(MathF.Round(zPhys.CurrentGroundHeight, 2))}|{StairCsvBool(snappedUpToGround)}|{StairCsvBool(snappedDownToStickyGround)}");

                    if (suppressBounce)
                    {
                        DebugZStairCsv(uid,
                            "ground_settle",
                            $"impact={StairCsvFloat(impactPower)},mode={(snappedDownToStickyGround ? "sticky" : "stair")},ground={StairCsvFloat(zPhys.CurrentGroundHeight)}",
                            $"{(snappedDownToStickyGround ? "sticky" : "stair")}|{StairCsvFloat(MathF.Round(impactPower, 2))}|{StairCsvFloat(MathF.Round(zPhys.CurrentGroundHeight, 2))}");
                        if (ZDebugEnabled)
                            DebugZVerbose(uid, $"suppressed bounce on stair contact at ground={zPhys.CurrentGroundHeight:0.00}");

                        zPhys.Velocity = 0f;
                    }
                    else
                    {
                        var preBounceVelocity = zPhys.Velocity;

                        if (impactPower >= ImpactVelocityLimit)
                        {
                            DebugZStairCsv(uid,
                                "impact_hit",
                                $"impact={StairCsvFloat(impactPower)},threshold={StairCsvFloat(ImpactVelocityLimit)},ground={StairCsvFloat(zPhys.CurrentGroundHeight)},bounciness={StairCsvFloat(zPhys.Bounciness)}",
                                $"{StairCsvFloat(MathF.Round(impactPower, 2))}|{StairCsvFloat(MathF.Round(zPhys.CurrentGroundHeight, 2))}|{StairCsvFloat(MathF.Round(zPhys.Bounciness, 2))}");
                            var ev = new CEZLevelHitEvent(-zPhys.Velocity);
                            RaiseLocalEvent(uid, ref ev);
                            var land = new LandEvent(null, true);
                            RaiseLocalEvent(uid, ref land);
                        }

                        zPhys.Velocity = -preBounceVelocity * zPhys.Bounciness;
                        DebugZStairCsv(uid,
                            "impact_bounce",
                            $"old_vel={StairCsvFloat(preBounceVelocity)},new_vel={StairCsvFloat(zPhys.Velocity)},ground={StairCsvFloat(zPhys.CurrentGroundHeight)},bounciness={StairCsvFloat(zPhys.Bounciness)}",
                            $"{StairCsvFloat(MathF.Round(preBounceVelocity, 2))}|{StairCsvFloat(MathF.Round(zPhys.Velocity, 2))}|{StairCsvFloat(MathF.Round(zPhys.CurrentGroundHeight, 2))}");
                    }
                }
            }

            if (zPhys.LocalPosition < 0) // Need to descend to Z-level below
            {
                var isWeightless = _gravity.IsWeightless(uid, physics, xform);
                var downBlocked = _timing.CurTime < zPhys.AutoDownBlockedUntil;
                var supportOffset = 0;
                var supportGridUid = EntityUid.Invalid;
                var supportIsHighGround = false;
                var effectiveGravityBelow = false;
                var descendMode = downBlocked
                    ? AutoDescendMode.None
                    : GetAutoDescendMode(uid, zPhys, xform, out supportOffset, out supportGridUid, out supportIsHighGround, out effectiveGravityBelow);
                var canAutoDescend = descendMode != AutoDescendMode.None;
                var gridVelocity = xform.GridUid != null && TryGetLinearVelocity(xform.GridUid.Value, out var currentGridVelocity)
                    ? StairCsvVec2(currentGridVelocity)
                    : "na";
                var supportGridVelocity = supportGridUid != EntityUid.Invalid && TryGetLinearVelocity(supportGridUid, out var belowGridVelocity)
                    ? StairCsvVec2(belowGridVelocity)
                    : "na";

                DebugZStairCsv(uid,
                    "down_check",
                    $"allow={StairCsvBool(canAutoDescend)},blocked={StairCsvBool(downBlocked)},mode={descendMode},support_below={StairCsvBool(zPhys.CurrentHasSupportBelow)},highground_below={StairCsvBool(zPhys.CurrentHighGroundBelow)},support_offset={supportOffset},support_grid={(supportGridUid == EntityUid.Invalid ? "null" : ToPrettyString(supportGridUid))},support_grid_vel={supportGridVelocity},support_highground={StairCsvBool(supportIsHighGround)},weightless={StairCsvBool(isWeightless)},effective_gravity_below={StairCsvBool(effectiveGravityBelow)},grid_vel={gridVelocity}",
                    $"{StairCsvBool(canAutoDescend)}|{StairCsvBool(downBlocked)}|{descendMode}|{StairCsvBool(zPhys.CurrentHasSupportBelow)}|{StairCsvBool(zPhys.CurrentHighGroundBelow)}|{supportOffset}|{(supportGridUid == EntityUid.Invalid ? "null" : ToPrettyString(supportGridUid))}|{supportGridVelocity}|{StairCsvBool(supportIsHighGround)}|{StairCsvBool(isWeightless)}|{StairCsvBool(effectiveGravityBelow)}|{gridVelocity}");

                if (canAutoDescend)
                {
                    if (ShouldDeferClientPredictedMovingStairDescent(uid, zPhys, xform, supportGridUid, supportIsHighGround))
                    {
                        if (xform.MapUid is { } sourceMapUid)
                            RefreshDeferredClientMovingStairDescent(uid, sourceMapUid, supportGridUid);

                        DebugZStairCsv(uid,
                            "down_defer",
                            $"reason=client_moving_stair,mode={descendMode},support_grid={ToPrettyString(supportGridUid)},support_grid_vel={supportGridVelocity},local_before={StairCsvFloat(zPhys.LocalPosition)},vel_before={StairCsvFloat(zPhys.Velocity)}",
                            $"client_moving_stair|{descendMode}|{ToPrettyString(supportGridUid)}|{supportGridVelocity}");

                        zPhys.LocalPosition = MathF.Min(zPhys.LocalPosition, -0.01f);
                        zPhys.Velocity = 0f;
                        continue;
                    }

                    if (ZDebugEnabled)
                        DebugZ(uid, $"local position dropped below 0, attempting move down in mode={descendMode}");

                    var movedDown = TryMoveDown(uid);
                    DebugZStairCsv(uid,
                        "down_result",
                        $"success={StairCsvBool(movedDown)},mode={descendMode},support_offset={supportOffset},support_grid={(supportGridUid == EntityUid.Invalid ? "null" : ToPrettyString(supportGridUid))},grid_vel={gridVelocity},support_grid_vel={supportGridVelocity}",
                        $"{StairCsvBool(movedDown)}|{descendMode}|{supportOffset}|{(supportGridUid == EntityUid.Invalid ? "null" : ToPrettyString(supportGridUid))}|{gridVelocity}|{supportGridVelocity}");

                    if (movedDown)
                    {
                        if (descendMode == AutoDescendMode.ControlledStep)
                        {
                            zPhys.LocalPosition = MathF.Max(0f, zPhys.CurrentGroundHeight);
                            zPhys.Velocity = 0f;

                            if (supportIsHighGround &&
                                physics.LinearVelocity.LengthSquared() > 0.0001f)
                            {
                                var oldLinear = physics.LinearVelocity;
                                _physics.SetLinearVelocity(uid, Vector2.Zero, body: physics);
                                DebugZStairCsv(uid,
                                    "down_velocity_reset",
                                    $"mode={descendMode},support_grid={(supportGridUid == EntityUid.Invalid ? "null" : ToPrettyString(supportGridUid))},old_linear={StairCsvVec2(oldLinear)}");
                            }

                            var upBlockSeconds = StairTransferGraceSeconds;
                            if (supportGridUid != EntityUid.Invalid &&
                                TryGetLinearVelocity(supportGridUid, out var supportVelocityForGrace))
                            {
                                upBlockSeconds = MathF.Min(
                                    StairTransferMovingGridGraceMaxSeconds,
                                    StairTransferGraceSeconds + supportVelocityForGrace.Length() * StairTransferMovingGridGraceScale);
                            }

                            zPhys.AutoUpBlockedUntil = _timing.CurTime + TimeSpan.FromSeconds(upBlockSeconds);
                        }
                        else
                        {
                            zPhys.LocalPosition += 1f;
                        }

                        if (descendMode == AutoDescendMode.FreeFall || !zPhys.CurrentStickyGround)
                        {
                            var fallEv = new CEZLevelFallMapEvent();
                            RaiseLocalEvent(uid, ref fallEv);
                        }
                    }
                    else
                    {
                        // Level below exists but transfer failed — stop cleanly
                        DebugZStairCsv(uid,
                            "down_clamp",
                            $"reason=move_failed,mode={descendMode},grid_vel={gridVelocity},support_grid_vel={supportGridVelocity}",
                            $"move_failed|{descendMode}|{gridVelocity}|{supportGridVelocity}");
                        if (ZDebugEnabled)
                            DebugZ(uid, $"move down failed in mode={descendMode}, clamping to 0");
                        zPhys.LocalPosition = 0f;
                        if (zPhys.Velocity < 0f) zPhys.Velocity = 0f;
                    }
                }
                else
                {
                    // Weightless or no floor below — clamp and float on current level
                    DebugZStairCsv(uid,
                        "down_clamp",
                        $"reason=blocked,blocked={StairCsvBool(downBlocked)},weightless={StairCsvBool(isWeightless)},support_below={StairCsvBool(zPhys.CurrentHasSupportBelow)},support_grid={(supportGridUid == EntityUid.Invalid ? "null" : ToPrettyString(supportGridUid))},grid_vel={gridVelocity}",
                        $"blocked|{StairCsvBool(downBlocked)}|{StairCsvBool(isWeightless)}|{StairCsvBool(zPhys.CurrentHasSupportBelow)}|{(supportGridUid == EntityUid.Invalid ? "null" : ToPrettyString(supportGridUid))}|{gridVelocity}");
                    if (ZDebugEnabled)
                        DebugZ(uid, $"descent blocked (blocked={downBlocked}, weightless={isWeightless}, supportBelow={zPhys.CurrentHasSupportBelow}), clamping to 0");
                    zPhys.LocalPosition = 0f;
                    if (zPhys.Velocity < 0f) zPhys.Velocity = 0f;
                }
            }

            var upwardTransferThreshold = StairUpTransferHeightThreshold;
            if (ShouldAttemptUpwardTransfer(uid, zPhys, upwardTransferThreshold)) //Need teleport to ZLevel up
            {
                var hasTileAbove = HasTileAbove(uid);
                if (hasTileAbove) //Hit roof
                {
                    DebugZStairCsv(uid,
                        "up_result",
                        "success=0,reason=tile_above",
                        "0|tile_above");
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
                    DebugZStairCsv(uid,
                        "up_result",
                        $"success={StairCsvBool(movedUp)},reason={(movedUp ? "ok" : "move_failed")}",
                        $"{StairCsvBool(movedUp)}|{(movedUp ? "ok" : "move_failed")}");
                    if (ZDebugEnabled)
                        DebugZ(uid, $"upward transfer attempted, success={movedUp}");
                    if (movedUp)
                    {
                        zPhys.LocalPosition = MathF.Max(0f, zPhys.CurrentGroundHeight);
                        zPhys.Velocity = 0f;
                        zPhys.AutoDownBlockedUntil = _timing.CurTime + TimeSpan.FromSeconds(StairTransferGraceSeconds);
                    }
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

    private bool TryResolveGridAtWorldPositionOnMap(EntityUid mapUid, Vector2 worldPos, out EntityUid gridUid, out MapGridComponent gridComp)
    {
        var bestNonEmptyGridUid = EntityUid.Invalid;
        MapGridComponent? bestNonEmptyGrid = null;
        var bestNonEmptyArea = float.MaxValue;

        var bestTileGridUid = EntityUid.Invalid;
        MapGridComponent? bestTileGrid = null;
        var bestTileArea = float.MaxValue;

        var bestBoundsGridUid = EntityUid.Invalid;
        MapGridComponent? bestBoundsGrid = null;
        var bestBoundsArea = float.MaxValue;

        var gridQuery = EntityQueryEnumerator<MapGridComponent, TransformComponent>();
        while (gridQuery.MoveNext(out var uid, out var grid, out var xform))
        {
            if (xform.MapUid != mapUid)
                continue;

            var gridWorldPos = _transform.GetWorldPosition(uid);
            var gridWorldRot = _transform.GetWorldRotation(uid);
            var worldAabb = new Box2Rotated(grid.LocalAABB.Translated(gridWorldPos), gridWorldRot, gridWorldPos).CalcBoundingBox();

            if (!worldAabb.Contains(worldPos))
                continue;

            var area = worldAabb.Size.X * worldAabb.Size.Y;

            if (_map.TryGetTileRef(uid, grid, worldPos, out var tileRef))
            {
                if (!tileRef.Tile.IsEmpty && area < bestNonEmptyArea)
                {
                    bestNonEmptyArea = area;
                    bestNonEmptyGridUid = uid;
                    bestNonEmptyGrid = grid;
                }
                else if (area < bestTileArea)
                {
                    bestTileArea = area;
                    bestTileGridUid = uid;
                    bestTileGrid = grid;
                }
            }

            if (area < bestBoundsArea)
            {
                bestBoundsArea = area;
                bestBoundsGridUid = uid;
                bestBoundsGrid = grid;
            }
        }

        if (bestNonEmptyGridUid != EntityUid.Invalid && bestNonEmptyGrid != null)
        {
            gridUid = bestNonEmptyGridUid;
            gridComp = bestNonEmptyGrid;
            return true;
        }

        if (bestTileGridUid != EntityUid.Invalid && bestTileGrid != null)
        {
            gridUid = bestTileGridUid;
            gridComp = bestTileGrid;
            return true;
        }

        if (bestBoundsGridUid != EntityUid.Invalid && bestBoundsGrid != null)
        {
            gridUid = bestBoundsGridUid;
            gridComp = bestBoundsGrid;
            return true;
        }

        gridUid = EntityUid.Invalid;
        gridComp = default!;
        return false;
    }

    private bool TryGetLinearVelocity(EntityUid uid, out Vector2 velocity)
    {
        if (TryComp<PhysicsComponent>(uid, out var physics))
        {
            velocity = physics.LinearVelocity;
            return true;
        }

        velocity = Vector2.Zero;
        return false;
    }

    private string GetEntityVelocityCsv(EntityUid? uid)
    {
        if (uid is not { } resolvedUid)
            return "na";

        return TryGetLinearVelocity(resolvedUid, out var velocity)
            ? StairCsvVec2(velocity)
            : "na";
    }

    private string GetEntityWorldPositionCsv(EntityUid? uid)
    {
        if (uid is not { } resolvedUid)
            return "na";

        return StairCsvVec2(_transform.GetWorldPosition(resolvedUid));
    }

    private bool TryResolveGridForMapOffset(EntityUid ent, TransformComponent xform, int offset, out EntityUid gridUid, out MapGridComponent gridComp)
    {
        var worldPos = _transform.GetWorldPosition(ent);

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
                (TryResolveGridAtWorldPositionOnMap(currentMapUid, worldPos, out gridUid, out gridComp) ||
                 TryResolveAnyGridOnMap(currentMapUid, out gridUid, out gridComp)))
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
            TryResolveTraversalMapOffset(sourceMapUid, offset, out var targetMapUid, out _) &&
            (TryResolveGridAtWorldPositionOnMap(targetMapUid, worldPos, out gridUid, out gridComp) ||
             TryResolveAnyGridOnMap(targetMapUid, out gridUid, out gridComp)))
        {
            if (offset != 0)
                DebugZVerbose(ent, $"resolved grid for offset={offset} via traversal map {targetMapUid} using grid {ToPrettyString(gridUid)}");

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
        TryGetTileLocalPositionForTarget(baseTargetWorldPos, targetGridUid, targetMapId, out var local, out var localGridUid, out var localGridSource);

        // Only apply the forward step-off behavior for staircase/slope transitions.
        if (offset > 0)
        {
            if (zPhys.CurrentGroundHeight <= 0.01f)
            {
                DebugZVerbose(ent, $"stair exit nudge skipped for upward move: current ground {zPhys.CurrentGroundHeight:0.00} is not elevated");
                return false;
            }

            if (!TryGetSupportedNextTileLandingPosition(forwardDir, local, baseTargetWorldPos, targetGridUid, targetMapId, out landingWorldPos))
            {
                DebugZVerbose(ent, "stair exit landing skipped for upward move: failed to resolve supported tile ahead");
                return false;
            }
        }
        else if (offset < 0)
        {
            if (!resolvedByTransferHint)
            {
                DebugZVerbose(ent, "stair exit nudge skipped for downward move: no stair or movement direction was resolved");
                return false;
            }

            var cachedBelowHighGround = zPhys.CurrentGroundFromBelowLevel && zPhys.CurrentHighGroundBelow;

            var supportResolved = false;
            GroundSupportSample support = default;
            var supportMatchesDirection = false;

            if (TryComp<CEZPhysicsComponent>(ent, out var sourceZPhys) &&
                TryGetGroundSupportSample((ent, sourceZPhys), out support, 1, false) &&
                support.IsHighGround)
            {
                supportResolved = true;
                supportMatchesDirection = support.SurfaceDirection == forwardDir;
            }

            if ((!supportResolved && !cachedBelowHighGround) ||
                (supportResolved && !supportMatchesDirection) ||
                !TryGetLocalDirectionForTarget(forwardDir, targetGridUid, targetMapId, baseTargetWorldPos, out var targetLocalDir, out var targetDirectionGridUid, out var targetDirectionGridSource) ||
                !TrySetTileLocalForStairSample(local, targetLocalDir, StairDownLandingSample, out var targetLocal))
            {
                DebugZVerbose(ent, "stair exit placement skipped for downward move: no straight stair sample could be resolved");
                return false;
            }

            if (!TrySetTileLocalWorldPosition(ent, targetLocal, baseTargetWorldPos, targetGridUid, targetMapId, out landingWorldPos, out var landingGridUid, out var landingGridSource))
            {
                DebugZVerbose(ent, "stair exit placement skipped for downward move: failed to convert target stair sample to world position");
                return false;
            }

            if (_net.IsServer)
            {
                var explicitGridPayload = string.Empty;
                if (landingGridSource == "explicit_grid" &&
                    landingGridUid != EntityUid.Invalid)
                {
                    explicitGridPayload =
                        $",tile_local_input={StairCsvVec2(targetLocal)},landing_grid_world={GetEntityWorldPositionCsv(landingGridUid)},landing_grid_rot={StairCsvFloat((float) _transform.GetWorldRotation(landingGridUid).Degrees)},landing_grid_vel={GetEntityVelocityCsv(landingGridUid)}";
                }

                DebugZStairCsv(ent,
                    "land_down_probe",
                    $"dir={forwardDir},base_local={StairCsvVec2(local)},base_local_grid={(localGridUid == EntityUid.Invalid ? "null" : ToPrettyString(localGridUid))},base_local_source={localGridSource},support_source={(supportResolved ? "live" : cachedBelowHighGround ? "cache" : "none")},support_floor={(supportResolved ? support.FloorOffset : 1)},support_grid={(supportResolved ? ToPrettyString(support.GridUid) : targetGridUid is { } explicitTargetGrid ? ToPrettyString(explicitTargetGrid) : "null")},support_uid={(supportResolved && support.SupportUid != EntityUid.Invalid ? ToPrettyString(support.SupportUid) : "null")},support_sample={(supportResolved ? StairCsvFloat(support.Sample) : "na")},support_ground={(supportResolved ? StairCsvFloat(support.GroundHeight) : StairCsvFloat(zPhys.CurrentGroundHeight))},target_dir_grid={(targetDirectionGridUid == EntityUid.Invalid ? "null" : ToPrettyString(targetDirectionGridUid))},target_dir_source={targetDirectionGridSource},target_local_dir={targetLocalDir},target_local={StairCsvVec2(targetLocal)},landing_grid={(landingGridUid == EntityUid.Invalid ? "null" : ToPrettyString(landingGridUid))},landing_grid_source={landingGridSource},landing_x={StairCsvFloat(landingWorldPos.X)},landing_y={StairCsvFloat(landingWorldPos.Y)}{explicitGridPayload}");
            }
        }
        else
        {
            return false;
        }

        DebugZVerbose(ent, $"computed stair exit nudge offset={offset} dir={forwardDir} landing={landingWorldPos}");
        if (!resolvedByTransferHint)
            DebugZVerbose(ent, $"stair exit nudge fell back to facing direction {forwardDir}");

        DebugZStairCsv(ent,
            offset > 0 ? "land_up" : "land_down",
            $"dir={forwardDir},local_x={StairCsvFloat(local.X)},local_y={StairCsvFloat(local.Y)},landing_x={StairCsvFloat(landingWorldPos.X)},landing_y={StairCsvFloat(landingWorldPos.Y)}");

        return true;
    }

    private static Vector2 GetTileLocalPosition(Vector2 localPos)
    {
        return new Vector2((localPos.X % 1 + 1) % 1, (localPos.Y % 1 + 1) % 1);
    }

    private bool TryGetTileLocalPositionForTarget(Vector2 worldPos, EntityUid? targetGridUid, MapId targetMapId, out Vector2 tileLocal, out EntityUid resolvedGridUid, out string resolutionSource)
    {
        if (targetGridUid is { } gridUid &&
            _gridQuery.HasComp(gridUid))
        {
            tileLocal = GetTileLocalPosition(Vector2.Transform(worldPos, _transform.GetInvWorldMatrix(gridUid)));
            resolvedGridUid = gridUid;
            resolutionSource = "explicit_grid";
            return true;
        }

        if (_map.TryGetMap(targetMapId, out var mapUid) &&
            TryResolveGridAtWorldPositionOnMap(mapUid.Value, worldPos, out resolvedGridUid, out _))
        {
            tileLocal = GetTileLocalPosition(Vector2.Transform(worldPos, _transform.GetInvWorldMatrix(resolvedGridUid)));
            resolutionSource = "map_world_grid";
            return true;
        }

        if (_map.TryGetMap(targetMapId, out var fallbackMapUid) &&
            TryResolveAnyGridOnMap(fallbackMapUid.Value, out resolvedGridUid, out _))
        {
            tileLocal = GetTileLocalPosition(Vector2.Transform(worldPos, _transform.GetInvWorldMatrix(resolvedGridUid)));
            resolutionSource = "map_any_grid";
            return true;
        }

        tileLocal = GetTileLocalPosition(worldPos);
        resolvedGridUid = EntityUid.Invalid;
        resolutionSource = "world_fallback";
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

    private Direction GetGridLocalDirection(EntityUid gridUid, Direction worldDir)
    {
        if (!_gridQuery.HasComp(gridUid))
            return worldDir;

        var worldVector = worldDir.ToVec();
        var inverseRotation = Matrix3Helpers.CreateRotation(-_transform.GetWorldRotation(gridUid));
        var localVector = Vector2.TransformNormal(worldVector, inverseRotation);
        return localVector.ToWorldAngle().GetCardinalDir();
    }

    private bool TryGetLocalDirectionForTarget(Direction worldDir, EntityUid? targetGridUid, MapId targetMapId, Vector2 fallbackWorldPos, out Direction localDir, out EntityUid resolvedGridUid, out string resolutionSource)
    {
        if (targetGridUid is { } gridUid &&
            _gridQuery.HasComp(gridUid))
        {
            localDir = GetGridLocalDirection(gridUid, worldDir);
            resolvedGridUid = gridUid;
            resolutionSource = "explicit_grid";
            return true;
        }

        if (_map.TryGetMap(targetMapId, out var mapUid) &&
            TryResolveGridAtWorldPositionOnMap(mapUid.Value, fallbackWorldPos, out resolvedGridUid, out _))
        {
            localDir = GetGridLocalDirection(resolvedGridUid, worldDir);
            resolutionSource = "map_world_grid";
            return true;
        }

        if (_map.TryGetMap(targetMapId, out var fallbackMapUid) &&
            TryResolveAnyGridOnMap(fallbackMapUid.Value, out resolvedGridUid, out _))
        {
            localDir = GetGridLocalDirection(resolvedGridUid, worldDir);
            resolutionSource = "map_any_grid";
            return true;
        }

        localDir = worldDir;
        resolvedGridUid = EntityUid.Invalid;
        resolutionSource = "world_fallback";
        return true;
    }

    private bool TryResolveLandingGrid(EntityUid? targetGridUid, MapId targetMapId, Vector2 fallbackWorldPos, out EntityUid resolvedGridUid, out MapGridComponent resolvedGrid)
    {
        if (targetGridUid is { } explicitGridUid &&
            _gridQuery.TryComp(explicitGridUid, out var explicitGrid))
        {
            resolvedGridUid = explicitGridUid;
            resolvedGrid = explicitGrid;
            return true;
        }

        if (_map.TryGetMap(targetMapId, out var mapUid) &&
            (TryResolveGridAtWorldPositionOnMap(mapUid.Value, fallbackWorldPos, out resolvedGridUid, out resolvedGrid) ||
             TryResolveAnyGridOnMap(mapUid.Value, out resolvedGridUid, out resolvedGrid)))
        {
            return true;
        }

        resolvedGridUid = EntityUid.Invalid;
        resolvedGrid = default!;
        return false;
    }

    private bool HasSupportAtWorldPositionOnGrid(EntityUid gridUid, MapGridComponent grid, Vector2 worldPos)
    {
        var tileIndices = _map.WorldToTile(gridUid, grid, worldPos);
        var anchoredQuery = _map.GetAnchoredEntitiesEnumerator(gridUid, grid, tileIndices);
        while (anchoredQuery.MoveNext(out var uid))
        {
            if (_highgroundQuery.HasComp(uid.Value))
                return true;
        }

        return _map.TryGetTileRef(gridUid, grid, worldPos, out var tileRef) && !tileRef.Tile.IsEmpty;
    }

    [PublicAPI]
    public bool HasSupportAtWorldPositionOnCurrentLevel(EntityUid ent, Vector2 worldPos, TransformComponent? xform = null)
    {
        if (!Resolve(ent, ref xform, false) ||
            xform.MapUid is not { } currentMapUid ||
            !TryResolveGridAtWorldPositionOnMap(currentMapUid, worldPos, out var gridUid, out var grid))
        {
            return false;
        }

        return HasSupportAtWorldPositionOnGrid(gridUid, grid, worldPos);
    }

    private bool TryGetSupportedNextTileLandingPosition(Direction forwardDir, Vector2 currentLocal, Vector2 fallbackWorldPos, EntityUid? targetGridUid, MapId targetMapId, out Vector2 landingWorldPos)
    {
        landingWorldPos = fallbackWorldPos;

        if (!TryResolveLandingGrid(targetGridUid, targetMapId, fallbackWorldPos, out var resolvedGridUid, out var resolvedGrid) ||
            !TryGetLocalDirectionForTarget(forwardDir, resolvedGridUid, targetMapId, fallbackWorldPos, out var localDir, out _, out _))
        {
            return false;
        }

        var localFallback = Vector2.Transform(fallbackWorldPos, _transform.GetInvWorldMatrix(resolvedGridUid));
        var tileOrigin = new Vector2(MathF.Floor(localFallback.X), MathF.Floor(localFallback.Y));
        var nextTileOrigin = tileOrigin + localDir.ToVec();
        var foundFallback = false;

        foreach (var sample in StairUpLandingSearchSamples)
        {
            if (!TrySetTileLocalForStairSample(currentLocal, localDir, sample, out var targetLocal))
                continue;

            var localPos = nextTileOrigin + targetLocal;
            var candidateWorldPos = Vector2.Transform(localPos, _transform.GetWorldMatrix(resolvedGridUid));

            if (!foundFallback)
            {
                landingWorldPos = candidateWorldPos;
                foundFallback = true;
            }

            if (HasSupportAtWorldPositionOnGrid(resolvedGridUid, resolvedGrid, candidateWorldPos))
            {
                landingWorldPos = candidateWorldPos;
                return true;
            }
        }

        return foundFallback;
    }

    private static bool TrySetTileLocalForStairSample(Vector2 currentLocal, Direction dir, float sample, out Vector2 targetLocal)
    {
        targetLocal = currentLocal;
        sample = Math.Clamp(sample, 0f, 1f);

        switch (dir)
        {
            case Direction.East:
                targetLocal.X = sample;
                return true;
            case Direction.West:
                targetLocal.X = 1f - sample;
                return true;
            case Direction.North:
                targetLocal.Y = sample;
                return true;
            case Direction.South:
                targetLocal.Y = 1f - sample;
                return true;
            default:
                return false;
        }
    }

    private bool TrySetTileLocalWorldPosition(EntityUid ent, Vector2 targetLocal, Vector2 fallbackWorldPos, EntityUid? targetGridUid, MapId targetMapId, out Vector2 landingWorldPos, out EntityUid resolvedGridUid, out string resolutionSource)
    {
        if (targetGridUid is { } gridUid &&
            _gridQuery.HasComp(gridUid))
        {
            var localFallback = Vector2.Transform(fallbackWorldPos, _transform.GetInvWorldMatrix(gridUid));
            var tileOrigin = new Vector2(MathF.Floor(localFallback.X), MathF.Floor(localFallback.Y));
            var localPos = tileOrigin + targetLocal;
            landingWorldPos = Vector2.Transform(localPos, _transform.GetWorldMatrix(gridUid));
            resolvedGridUid = gridUid;
            resolutionSource = "explicit_grid";
            return true;
        }

        if (_map.TryGetMap(targetMapId, out var mapUid) &&
            TryResolveGridAtWorldPositionOnMap(mapUid.Value, fallbackWorldPos, out resolvedGridUid, out _))
        {
            var localFallback = Vector2.Transform(fallbackWorldPos, _transform.GetInvWorldMatrix(resolvedGridUid));
            var tileOrigin = new Vector2(MathF.Floor(localFallback.X), MathF.Floor(localFallback.Y));
            var localPos = tileOrigin + targetLocal;
            landingWorldPos = Vector2.Transform(localPos, _transform.GetWorldMatrix(resolvedGridUid));
            resolutionSource = "map_world_grid";
            return true;
        }

        if (_map.TryGetMap(targetMapId, out var fallbackMapUid) &&
            TryResolveAnyGridOnMap(fallbackMapUid.Value, out resolvedGridUid, out _))
        {
            var localFallback = Vector2.Transform(fallbackWorldPos, _transform.GetInvWorldMatrix(resolvedGridUid));
            var tileOrigin = new Vector2(MathF.Floor(localFallback.X), MathF.Floor(localFallback.Y));
            var localPos = tileOrigin + targetLocal;
            landingWorldPos = Vector2.Transform(localPos, _transform.GetWorldMatrix(resolvedGridUid));
            resolutionSource = "map_any_grid";
            return true;
        }

        var worldTileOrigin = new Vector2(MathF.Floor(fallbackWorldPos.X), MathF.Floor(fallbackWorldPos.Y));
        landingWorldPos = worldTileOrigin + targetLocal;
        resolvedGridUid = EntityUid.Invalid;
        resolutionSource = "world_tile_origin";
        DebugZVerbose(ent, $"tile-local world landing fell back to world origin for target {targetLocal} at {fallbackWorldPos}");
        return true;
    }

    private bool TryGetMovementIntentVector(EntityUid ent, out Vector2 direction)
    {
        if (TryComp<InputMoverComponent>(ent, out var mover) &&
            mover.WishDir.LengthSquared() > StairDirectionMinimumSpeed * StairDirectionMinimumSpeed)
        {
            direction = mover.WishDir;
            return true;
        }

        if (TryComp<PhysicsComponent>(ent, out var physics) &&
            physics.LinearVelocity.LengthSquared() > StairDirectionMinimumSpeed * StairDirectionMinimumSpeed)
        {
            direction = physics.LinearVelocity;
            return true;
        }

        direction = Vector2.Zero;
        return false;
    }

    private bool ShouldAttemptUpwardTransfer(EntityUid ent, CEZPhysicsComponent zPhys, float upwardTransferThreshold)
    {
        if (zPhys.LocalPosition < upwardTransferThreshold)
            return false;

        if (_timing.CurTime < zPhys.AutoUpBlockedUntil)
        {
            DebugZStairCsv(ent,
                "up_check",
                $"allow=0,reason=blocked,sample=na,sample_thr={StairCsvFloat(StairUpTransferSampleThreshold)},support_dir={Direction.Invalid},up_dir={Direction.Invalid},move_dot=na,move_intent=na",
                "blocked");
            return false;
        }

        var reason = "non_highground";
        var allow = false;
        var sample = 0f;
        var moveDot = 0f;
        var moveIntentFound = false;
        var supportDirection = Direction.Invalid;
        var upwardDirection = Direction.Invalid;

        if (!TryGetGroundSupportSample((ent, zPhys), out var support, 0, false) ||
            !support.IsHighGround)
        {
            reason = "no_highground";
            DebugZStairCsv(ent,
                "up_check",
                $"allow={StairCsvBool(allow)},reason={reason},sample=na,sample_thr={StairCsvFloat(StairUpTransferSampleThreshold)},support_dir={supportDirection},up_dir={upwardDirection},move_dot=na,move_intent=na");
            return false;
        }

        sample = support.Sample;
        supportDirection = support.SurfaceDirection;

        if (!TryGetStairTransferDirection(ent, 1, out upwardDirection))
        {
            allow = false;
            reason = "no_up_dir";
            DebugZStairCsv(ent,
                "up_check",
                $"allow={StairCsvBool(allow)},reason={reason},sample={StairCsvFloat(sample)},sample_thr={StairCsvFloat(StairUpTransferSampleThreshold)},support_dir={supportDirection},up_dir={upwardDirection},move_dot=na,move_intent=na",
                $"{reason}|{StairCsvFloat(MathF.Round(sample, 2))}|{supportDirection}|{upwardDirection}");
            return false;
        }

        // Only trigger when the mover is actually progressing toward the stair's high end.
        moveIntentFound = TryGetMovementIntentVector(ent, out var movementIntent);
        if (moveIntentFound)
            moveDot = Vector2.Dot(Vector2.Normalize(movementIntent), upwardDirection.ToVec());

        if (!moveIntentFound)
        {
            allow = false;
            reason = "no_move_intent";
            DebugZStairCsv(ent,
                "up_check",
                $"allow={StairCsvBool(allow)},reason={reason},sample={StairCsvFloat(sample)},sample_thr={StairCsvFloat(StairUpTransferSampleThreshold)},support_dir={supportDirection},up_dir={upwardDirection},move_dot=na,move_intent={StairCsvBool(moveIntentFound)}",
                $"{reason}|{StairCsvFloat(MathF.Round(sample, 2))}|{supportDirection}|{upwardDirection}");
            return false;
        }

        allow = moveDot > 0.25f && support.Sample <= StairUpTransferSampleThreshold;
        reason = moveDot <= 0.25f
            ? "move_dir_gate"
            : allow
                ? "sample_pass"
                : "sample_gate";

        DebugZStairCsv(ent,
            "up_check",
            $"allow={StairCsvBool(allow)},reason={reason},sample={StairCsvFloat(sample)},sample_thr={StairCsvFloat(StairUpTransferSampleThreshold)},support_dir={supportDirection},up_dir={upwardDirection},move_dot={StairCsvFloat(moveDot)},move_intent={StairCsvBool(moveIntentFound)}",
            $"{reason}|{StairCsvFloat(MathF.Round(sample, 2))}|{StairCsvFloat(MathF.Round(moveDot, 2))}|{supportDirection}|{upwardDirection}|{StairCsvBool(moveIntentFound)}");

        return allow;
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

        var worldDir = _transform.GetWorldRotation(supportUid).GetCardinalDir();
        var sampleDir = GetGridLocalDirection(checkingGridUid, worldDir);
        var t = sampleDir switch
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

        var sticky = floor == 0 && heightComp.Stick;

        var groundHeight = -floor + groundY;
        sample = new GroundSupportSample(
            checkingGridUid,
            floor,
            supportUid,
            worldDir,
            tileLocal,
            t,
            groundHeight,
            sticky,
            true);

        if (logProbe)
        {
            DebugZVerbose(target.Owner,
                $"ground probe hit highground {ToPrettyString(supportUid)} floorOffset=-{floor} dir={worldDir} sample_dir={sampleDir} " +
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
        if (!HasTraversalContext(xform))
        {
            if (logProbe)
                DebugZVerbose(target.Owner, "ground probe failed: entity has no traversal context");
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
        var xform = Transform(ent);
        if (!TryResolveGridForMapOffset(ent, xform, 1, out var mapAboveGridUid, out var mapAboveGrid))
        {
            DebugZVerbose(ent, "roof check failed: could not resolve grid above");
            return false;
        }

        var hasTileAbove =
            _map.TryGetTileRef(mapAboveGridUid, mapAboveGrid, _transform.GetWorldPosition(ent), out var tileRef) &&
            !tileRef.Tile.IsEmpty;

        DebugZVerbose(ent, $"roof check on grid {ToPrettyString(mapAboveGridUid)} result={hasTileAbove}");

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
        if (!TryResolveCurrentLinkedGrid(ent, xform, out var currentGridUid, out var linked, out var resolutionSource))
        {
            DebugZStairCsv(ent,
                "move_target",
                $"success=0,reason=no_current_linked,offset={offset},source_grid=null,source_resolve={resolutionSource}",
                $"0|no_current_linked|{offset}|{resolutionSource}");
            return false;
        }

        targetZLevel = linked.Depth + offset;
        if (!linked.PeerGrids.TryGetValue(targetZLevel, out var targetPeerGridUid))
        {
            DebugZVerbose(ent, $"linked move target missing for offset={offset} targetZ={targetZLevel}");
            DebugZStairCsv(ent,
                "move_target",
                $"success=0,reason=missing_peer,offset={offset},source_grid={ToPrettyString(currentGridUid)},source_resolve={resolutionSource},target_z={targetZLevel},z_network={linked.ZNetwork}",
                $"0|missing_peer|{offset}|{ToPrettyString(currentGridUid)}|{resolutionSource}|{targetZLevel}");
            return false;
        }

        if (Transform(targetPeerGridUid).MapUid is not { } targetMapUid ||
            !_mapQuery.TryComp(targetMapUid, out var targetMapComp))
        {
            DebugZVerbose(ent, $"linked move target grid {ToPrettyString(targetPeerGridUid)} has no valid target map");
            DebugZStairCsv(ent,
                "move_target",
                $"success=0,reason=invalid_target_map,offset={offset},source_grid={ToPrettyString(currentGridUid)},source_resolve={resolutionSource},target_z={targetZLevel},peer_grid={ToPrettyString(targetPeerGridUid)}",
                $"0|invalid_target_map|{offset}|{ToPrettyString(currentGridUid)}|{resolutionSource}|{ToPrettyString(targetPeerGridUid)}");
            return false;
        }

        peerGridUid = targetPeerGridUid;
        targetMapId = targetMapComp.MapId;
        DebugZStairCsv(ent,
            "move_target",
            $"success=1,offset={offset},source_grid={ToPrettyString(currentGridUid)},source_resolve={resolutionSource},target_z={targetZLevel},peer_grid={ToPrettyString(targetPeerGridUid)},target_map={targetMapUid},z_network={linked.ZNetwork}",
            $"1|{offset}|{ToPrettyString(currentGridUid)}|{resolutionSource}|{targetZLevel}|{ToPrettyString(targetPeerGridUid)}|{targetMapUid}");
        return true;
    }

    /// <summary>
    /// Preserves the mover's local position inside the multiz structure when transitioning between linked grids.
    /// </summary>
    private Vector2 GetLinkedMoveTargetPosition(EntityUid ent, EntityUid peerGridUid, Vector2 fallbackWorldPosition)
    {
        EntityUid currentGridUid;
        string resolutionSource;

        if (ZPhyzQuery.TryComp(ent, out var zPhys) &&
            TryGetDetachedCarrierLocalReference(zPhys, out var detachedCarrierGridUid, out var detachedCarrierLocal))
        {
            currentGridUid = detachedCarrierGridUid;
            resolutionSource = "detached_grid";

            var detachedPeerGridMatrix = _transform.GetWorldMatrix(peerGridUid);
            var detachedTargetWorldPosition = Vector2.Transform(detachedCarrierLocal, detachedPeerGridMatrix);
            if (ZDebugStairsEnabled)
            {
                var sourceGridWorldPosition = _transform.GetWorldPosition(currentGridUid);
                var sourceGridRotation = (float) _transform.GetWorldRotation(currentGridUid).Degrees;
                var sourceGridVelocityResolved = TryGetLinearVelocity(currentGridUid, out var sourceGridVelocityVector);
                var sourceGridVelocity = sourceGridVelocityResolved
                    ? StairCsvVec2(sourceGridVelocityVector)
                    : "na";
                var sourceGridVelocityKey = sourceGridVelocityResolved
                    ? StairCsvDedupeVec2(sourceGridVelocityVector, 3)
                    : "na";
                var peerGridWorldPosition = _transform.GetWorldPosition(peerGridUid);
                var peerGridRotation = (float) _transform.GetWorldRotation(peerGridUid).Degrees;
                var peerGridVelocityResolved = TryGetLinearVelocity(peerGridUid, out var peerGridVelocityVector);
                var peerGridVelocity = peerGridVelocityResolved
                    ? StairCsvVec2(peerGridVelocityVector)
                    : "na";
                var peerGridVelocityKey = peerGridVelocityResolved
                    ? StairCsvDedupeVec2(peerGridVelocityVector, 3)
                    : "na";
                var targetDelta = detachedTargetWorldPosition - fallbackWorldPosition;
                var dedupeKey =
                    $"{ToPrettyString(currentGridUid)}|{StairCsvDedupeVec2(sourceGridWorldPosition, 2)}|{StairCsvDedupeFloat(sourceGridRotation, 3)}|{sourceGridVelocityKey}|" +
                    $"{ToPrettyString(peerGridUid)}|{StairCsvDedupeVec2(peerGridWorldPosition, 2)}|{StairCsvDedupeFloat(peerGridRotation, 3)}|{peerGridVelocityKey}|" +
                    $"{StairCsvDedupeVec2(detachedCarrierLocal, 2)}|{StairCsvDedupeVec2(detachedTargetWorldPosition, 2)}";

                if (DebugZStairCsv(ent,
                    "move_target_transform",
                    $"source_grid={ToPrettyString(currentGridUid)},source_resolve={resolutionSource},source_local_source=detached_cache,peer_grid={ToPrettyString(peerGridUid)},source_world={StairCsvVec2(fallbackWorldPosition)},source_local={StairCsvVec2(detachedCarrierLocal)},target_world={StairCsvVec2(detachedTargetWorldPosition)},source_grid_world={StairCsvVec2(sourceGridWorldPosition)},source_grid_rot={StairCsvFloat(sourceGridRotation)},source_grid_vel={sourceGridVelocity},peer_grid_world={StairCsvVec2(peerGridWorldPosition)},peer_grid_rot={StairCsvFloat(peerGridRotation)},peer_grid_vel={peerGridVelocity},target_delta={StairCsvVec2(targetDelta)}",
                    dedupeKey))
                {
                    WatchGridSyncPair(currentGridUid, peerGridUid);
                }
            }

            return detachedTargetWorldPosition;
        }

        var xform = Transform(ent);
        if (!TryResolveCurrentLinkedGrid(ent, xform, out currentGridUid, out _, out resolutionSource))
            return fallbackWorldPosition;

        var currentGridMatrix = _transform.GetWorldMatrix(currentGridUid);
        var peerGridMatrix = _transform.GetWorldMatrix(peerGridUid);

        if (!Matrix3x2.Invert(currentGridMatrix, out var inverseCurrentGrid))
            return fallbackWorldPosition;

        var localToCurrentGrid = Vector2.Transform(fallbackWorldPosition, inverseCurrentGrid);
        var targetWorldPosition = Vector2.Transform(localToCurrentGrid, peerGridMatrix);
        if (ZDebugStairsEnabled)
        {
            DebugZStairCsv(ent,
                "move_target_transform",
                $"source_grid={ToPrettyString(currentGridUid)},source_resolve={resolutionSource},source_local_source=live_world,peer_grid={ToPrettyString(peerGridUid)},source_world={StairCsvVec2(fallbackWorldPosition)},source_local={StairCsvVec2(localToCurrentGrid)},target_world={StairCsvVec2(targetWorldPosition)}");
        }
        return targetWorldPosition;
    }

    [PublicAPI]
    public bool TryMove(EntityUid ent, int offset, Entity<CEZLevelMapComponent?>? map = null, Vector2? targetWorldPositionOverride = null, bool allowStairExitLanding = true)
    {
        MapId targetMapId;
        int targetZLevel;
        EntityUid? peerGridUid;
        var worldPos = _transform.GetWorldPosition(ent);
        var worldRot = _transform.GetWorldRotation(ent);
        var xform = Transform(ent);
        var sourceGridVelocity = xform.GridUid != null && TryGetLinearVelocity(xform.GridUid.Value, out var sourceGridVel)
            ? StairCsvVec2(sourceGridVel)
            : "na";
        var sourceLocal = xform.GridUid != null
            ? StairCsvVec2(Vector2.Transform(worldPos, _transform.GetInvWorldMatrix(xform.GridUid.Value)))
            : "na";
        var sourceParentUid = xform.ParentUid;
        var sourceParentWorld = GetEntityWorldPositionCsv(sourceParentUid);
        var sourceParentVelocity = GetEntityVelocityCsv(sourceParentUid);

        if (!TryResolveLinkedMoveTarget(ent, offset, out targetMapId, out targetZLevel, out peerGridUid))
        {
            var currentMapUid = map?.Owner ?? xform.MapUid;

            if (currentMapUid is null)
            {
                DebugZStairCsv(ent,
                    "move_fail",
                    $"offset={offset},reason=no_current_map,source_world={StairCsvVec2(worldPos)},source_grid={(xform.GridUid == null ? "null" : ToPrettyString(xform.GridUid.Value))},source_grid_vel={sourceGridVelocity}",
                    $"{offset}|no_current_map|{(xform.GridUid == null ? "null" : ToPrettyString(xform.GridUid.Value))}|{sourceGridVelocity}");
                if (ZDebugEnabled)
                    DebugZ(ent, $"move failed: no current map for offset={offset}");
                return false;
            }

            if (!TryResolveTraversalMapOffset(currentMapUid.Value, offset, out var targetMapUid, out targetZLevel))
            {
                DebugZStairCsv(ent,
                    "move_fail",
                    $"offset={offset},reason=no_target_map,source_world={StairCsvVec2(worldPos)},source_grid={(xform.GridUid == null ? "null" : ToPrettyString(xform.GridUid.Value))},source_grid_vel={sourceGridVelocity}",
                    $"{offset}|no_target_map|{(xform.GridUid == null ? "null" : ToPrettyString(xform.GridUid.Value))}|{sourceGridVelocity}");
                if (ZDebugEnabled)
                    DebugZ(ent, $"move failed: no target map at offset={offset}");
                return false;
            }

            if (!_mapQuery.TryComp(targetMapUid, out var targetMapComp))
            {
                DebugZStairCsv(ent,
                    "move_fail",
                    $"offset={offset},reason=missing_target_map_component,target_map={targetMapUid},source_grid={(xform.GridUid == null ? "null" : ToPrettyString(xform.GridUid.Value))},source_grid_vel={sourceGridVelocity}",
                    $"{offset}|missing_target_map_component|{targetMapUid}|{sourceGridVelocity}");
                if (ZDebugEnabled)
                    DebugZ(ent, $"move failed: target map {targetMapUid} has no map component");
                return false;
            }

            targetMapId = targetMapComp.MapId;
        }

        DebugZStairCsv(ent,
            "move_attempt",
            $"offset={offset},target_z={targetZLevel},source_world={StairCsvVec2(worldPos)},source_local={sourceLocal},source_parent={sourceParentUid},source_parent_world={sourceParentWorld},source_parent_vel={sourceParentVelocity},source_grid={(xform.GridUid == null ? "null" : ToPrettyString(xform.GridUid.Value))},source_map={(xform.MapUid == null ? "null" : xform.MapUid.Value.ToString())},source_grid_vel={sourceGridVelocity},peer_grid={(peerGridUid == null ? "null" : ToPrettyString(peerGridUid.Value))},allow_stair_landing={StairCsvBool(allowStairExitLanding)}");

        if (ZDebugEnabled)
            DebugZ(ent, $"attempting move offset={offset} targetMapId={targetMapId} targetZ={targetZLevel} peerGrid={peerGridUid} sourceWorld={worldPos}");

        var beforeEv = new CEZLevelBeforeMapMoveEvent(offset, targetZLevel);
        RaiseLocalEvent(ent, ref beforeEv);

        var targetWorldPos = targetWorldPositionOverride ?? (peerGridUid != null
            ? GetLinkedMoveTargetPosition(ent, peerGridUid.Value, worldPos)
            : worldPos);

        if (allowStairExitLanding &&
            TryGetForwardLandingPosition(ent, offset, targetWorldPos, peerGridUid, targetMapId, out var forwardLandingWorldPos))
        {
            DebugZVerbose(ent, $"using stair exit nudge landing at {forwardLandingWorldPos} for offset={offset}");

            targetWorldPos = forwardLandingWorldPos;
        }

        var plannedTargetWorldPos = targetWorldPos;

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
        else if (_map.TryGetMap(targetMapId, out var targetMapUid) &&
                 TryResolveGridAtWorldPositionOnMap(targetMapUid.Value, targetWorldPos, out var landingGridUid, out _))
        {
            var gridCoordinates = new EntityCoordinates(
                landingGridUid,
                Vector2.Transform(targetWorldPos, _transform.GetInvWorldMatrix(landingGridUid)));
            _transform.SetCoordinates(ent, gridCoordinates);
        }
        else
        {
            _transform.SetMapCoordinates(ent, new MapCoordinates(targetWorldPos, targetMapId));
        }

        xform = Transform(ent);
        var transferMode = peerGridUid != null
            ? "peer_grid"
            : "map_or_grid";
        if (xform.GridUid == null &&
            _map.TryGetMap(targetMapId, out var reattachMapUid) &&
            TryResolveGridAtWorldPositionOnMap(reattachMapUid.Value, targetWorldPos, out var reattachGridUid, out _))
        {
            var gridCoordinates = new EntityCoordinates(
                reattachGridUid,
                Vector2.Transform(targetWorldPos, _transform.GetInvWorldMatrix(reattachGridUid)));
            _transform.SetCoordinates(ent, gridCoordinates);
            xform = Transform(ent);
            transferMode = "map_reattach";
        }

        // Force set both local rotation and world rotation to ensure consistency.
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

        if (ZPhyzQuery.TryComp(ent, out var zPhysAfterMove))
            ClearDetachedCarrierReference(zPhysAfterMove);

        var actualWorldPos = _transform.GetWorldPosition(ent);
        var finalLocal = xform.GridUid != null
            ? StairCsvVec2(Vector2.Transform(actualWorldPos, _transform.GetInvWorldMatrix(xform.GridUid.Value)))
            : "na";
        var finalParentUid = xform.ParentUid;
        var finalParentWorld = GetEntityWorldPositionCsv(finalParentUid);
        var finalParentVelocity = GetEntityVelocityCsv(finalParentUid);
        DebugZStairCsv(ent,
            "move_commit",
            $"offset={offset},target_z={targetZLevel},mode={transferMode},planned_world={StairCsvVec2(plannedTargetWorldPos)},actual_world={StairCsvVec2(actualWorldPos)},final_parent={finalParentUid},final_parent_world={finalParentWorld},final_parent_vel={finalParentVelocity},final_grid={(xform.GridUid == null ? "null" : ToPrettyString(xform.GridUid.Value))},final_map={(xform.MapUid == null ? "null" : xform.MapUid.Value.ToString())},final_local={finalLocal}");

        if (ZDebugEnabled)
            DebugZ(ent, $"move succeeded offset={offset} newZ={targetZLevel} landing={targetWorldPos}");

        if (offset != 0)
        {
            DebugZStairCsv(ent,
                offset > 0 ? "up_move" : "down_move",
                $"target_z={targetZLevel},landing_x={StairCsvFloat(targetWorldPos.X)},landing_y={StairCsvFloat(targetWorldPos.Y)},peer_grid={(peerGridUid is null ? "null" : ToPrettyString(peerGridUid.Value))}");
        }

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
    public void TeleportToZLevelCoordinates(EntityUid ent, EntityCoordinates targetCoordinates, int targetZLevel, int offset)
    {
        var worldRot = _transform.GetWorldRotation(ent);

        var beforeEv = new CEZLevelBeforeMapMoveEvent(offset, targetZLevel);
        RaiseLocalEvent(ent, ref beforeEv);

        _transform.SetCoordinates(ent, targetCoordinates);

        var xform = Transform(ent);
        var parentRot = _transform.GetWorldRotation(xform.ParentUid);
        _transform.SetLocalRotation(ent, worldRot - parentRot);

        var ev = new CEZLevelMapMoveEvent(offset, targetZLevel);
        RaiseLocalEvent(ent, ref ev);
    }

    [PublicAPI]
    public void NormalizeTransferredPullable(EntityUid ent, int offset)
    {
        if (!ZPhyzQuery.TryComp(ent, out var zPhys))
            return;

        var oldVelocity = zPhys.Velocity;
        var oldLocalPosition = zPhys.LocalPosition;

        zPhys.LocalPosition = MathF.Max(0f, zPhys.CurrentGroundHeight);
        zPhys.Velocity = 0f;

        if (offset > 0)
            zPhys.AutoDownBlockedUntil = _timing.CurTime + TimeSpan.FromSeconds(StairTransferGraceSeconds);
        else if (offset < 0)
            zPhys.AutoUpBlockedUntil = _timing.CurTime + TimeSpan.FromSeconds(StairTransferGraceSeconds);

        if (Math.Abs(oldVelocity - zPhys.Velocity) > 0.01f)
            DirtyField(ent, zPhys, nameof(CEZPhysicsComponent.Velocity));

        if (Math.Abs(oldLocalPosition - zPhys.LocalPosition) > 0.01f)
            DirtyField(ent, zPhys, nameof(CEZPhysicsComponent.LocalPosition));
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
