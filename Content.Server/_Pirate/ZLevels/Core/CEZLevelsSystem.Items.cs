/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using System.Numerics;
using Content.Shared._Pirate.ZLevels.Core.Components;
using Content.Shared._Pirate.ZLevels.Core.EntitySystems;
using Content.Shared.Gravity;
using Content.Shared.Hands;
using Content.Shared.Interaction.Events;
using Content.Shared.Inventory.Events;
using Content.Shared.Item;
using Content.Shared.Throwing;
using Robust.Shared.Containers;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Prototypes;

namespace Content.Server._Pirate.ZLevels.Core;

public sealed partial class CEZLevelsSystem
{
    private const float ItemZGravityForce = 9.8f;
    private const float ItemZVelocityLimit = 20f;
    private const float ItemImpactVelocityLimit = 3f;
    // Initial upward Z-velocity given to an item thrown while the thrower has LookUp on. Matches
    // CMU's CMUSharedZLevelsSystem.Throwing.cs (ThrowUpZVelocity = 6.5).
    private const float ItemThrowUpZVelocity = 6.5f;
    private static readonly EntProtoId ItemFallVfx = "CEDustEffect";

    private void InitItems()
    {
        SubscribeLocalEvent<ItemComponent, StopThrowEvent>(OnItemStopThrow);
        SubscribeLocalEvent<ItemComponent, DroppedEvent>(OnItemDropped);
        SubscribeLocalEvent<ItemComponent, MoveEvent>(OnItemMoved);
        SubscribeLocalEvent<ItemComponent, IsWeightlessEvent>(OnItemIsWeightless);
        SubscribeLocalEvent<ItemComponent, ThrownEvent>(OnItemThrown);
        SubscribeLocalEvent<CEZItemPhysicsComponent, GotEquippedHandEvent>(OnItemGotEquippedHand);
        SubscribeLocalEvent<CEZItemPhysicsComponent, GotEquippedEvent>(OnItemGotEquipped);
        SubscribeLocalEvent<CEZItemPhysicsComponent, EntGotInsertedIntoContainerMessage>(OnItemInsertedIntoContainer);
        SubscribeLocalEvent<CEZItemPhysicsComponent, EntParentChangedMessage>(OnItemZPhysicsParentChanged);
    }

    // Mirrors CMU's CMUSharedZLevelsSystem.Throwing.cs:OnThrown. Two responsibilities:
    //   1. Ensure the item carries CEZItemPhysicsComponent during the throw flight — lanos
    //      removes the component on pickup, so without this the throw has no Z hooks.
    //   2. If the thrower has LookUp toggled on, seed positive Z velocity (6.5 — same value as
    //      CMU). The Update loop's gravity gate (OnGround || ZVelocity > 0) then decelerates it
    //      naturally into a parabolic arc, and the rise-through path transitions to the Z above.
    private void OnItemThrown(Entity<ItemComponent> ent, ref ThrownEvent args)
    {
        TryAddItemZPhysics(ent.Owner);

        if (args.User is not { } user ||
            !TryComp<CEZLevelViewerComponent>(user, out var viewer) ||
            !viewer.LookUp)
        {
            return;
        }

        if (!TryComp<CEZItemPhysicsComponent>(ent.Owner, out var zItem))
            return;

        if (zItem.ZVelocity >= ItemThrowUpZVelocity)
            return;

        zItem.ZVelocity = ItemThrowUpZVelocity;
        Dirty(ent.Owner, zItem);
    }

    private void OnItemStopThrow(Entity<ItemComponent> ent, ref StopThrowEvent args)
    {
        TryAddItemZPhysics(ent.Owner);
    }

    private void OnItemDropped(Entity<ItemComponent> ent, ref DroppedEvent args)
    {
        TryAddItemZPhysics(ent.Owner);
    }

    private void OnItemMoved(Entity<ItemComponent> ent, ref MoveEvent args)
    {
        if (HasComp<CEZItemPhysicsComponent>(ent.Owner))
            return;

        TryAddItemZPhysics(ent.Owner, true);
    }

    private void OnItemGotEquippedHand(Entity<CEZItemPhysicsComponent> ent, ref GotEquippedHandEvent args)
    {
        RemoveItemZPhysics(ent.Owner);
    }

    private void OnItemGotEquipped(Entity<CEZItemPhysicsComponent> ent, ref GotEquippedEvent args)
    {
        RemoveItemZPhysics(ent.Owner);
    }

    private void OnItemInsertedIntoContainer(Entity<CEZItemPhysicsComponent> ent, ref EntGotInsertedIntoContainerMessage args)
    {
        RemoveItemZPhysics(ent.Owner);
    }

    private void OnItemZPhysicsParentChanged(Entity<CEZItemPhysicsComponent> ent, ref EntParentChangedMessage args)
    {
        if (!IsItemRestingOnMapOrGrid(args.Transform))
            RemoveItemZPhysics(ent.Owner);
    }

    private void TryAddItemZPhysics(EntityUid item, bool requireZGravity = false)
    {
        var xform = Transform(item);
        if (!IsItemRestingOnMapOrGrid(xform))
            return;

        if (requireZGravity && !CanItemExperienceZGravity(item, xform))
            return;

        var zItem = EnsureComp<CEZItemPhysicsComponent>(item);
        zItem.LocalPosition = 0f;
        zItem.ZVelocity = 0f;
        zItem.HadZFall = false;
        Dirty(item, zItem);
        UpdateItemGravityInfluence(item, xform);
    }

    private void RemoveItemZPhysics(EntityUid item)
    {
        RemComp<CEZItemPhysicsComponent>(item);
        RemComp<CEZGravityInfluencedComponent>(item);
    }

    // Mirrors CMU's CESharedZLevelsSystem.Update.cs ProcessZPhysics flow, scoped to items:
    //   1. Gravity is gated on BodyStatus.OnGround. Thrown items (InAir) keep their height.
    //   2. AutoStep snaps LocalPosition up to ground height every tick, including over walls
    //      with CEZLevelHighGround (HeightCurve = [1.05, ...]) — this produces the "flies above"
    //      visual as a thrown item crosses a wall.
    //   3. LocalPosition < 0 → fall-through to lower Z, raises CEZLevelFallMapEvent which fires
    //      the "X falls!" popup for bystanders on the layer below.
    //   4. LocalPosition >= 1 with open ceiling → transition up one Z-level. With a closed
    //      ceiling, cap at 1 and kill upward velocity (item rides the wall top).
    private void UpdateItems(float frameTime)
    {
        var query = EntityQueryEnumerator<CEZItemPhysicsComponent, ItemComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var zItem, out _, out var xform))
        {
            if (!IsItemRestingOnMapOrGrid(xform))
            {
                RemoveItemZPhysics(uid);
                continue;
            }

            var oldLocalPosition = zItem.LocalPosition;
            TryComp<PhysicsComponent>(uid, out var physics);
            var onGround = physics is null || physics.BodyStatus == BodyStatus.OnGround;

            var groundHeight = ComputeItemGroundHeight(uid, xform);
            var hasSupport = groundHeight >= 0f;

            // Mirrors CMU's gate: gravity applies when on the ground OR moving upward. The
            // `|| ZVelocity > 0` clause is what produces the parabolic arc on a LookUp-throw —
            // upward velocity decelerates each tick, peaks, then crosses zero. Once it crosses
            // zero while still InAir, gravity stops; the item drifts horizontally with whatever
            // residual descent velocity it has until the throw ends and BodyStatus flips back to
            // OnGround, at which point gravity resumes to complete the descent.
            if (onGround || zItem.ZVelocity > 0f)
                zItem.ZVelocity = MathF.Max(zItem.ZVelocity - ItemZGravityForce * frameTime, -ItemZVelocityLimit);

            zItem.LocalPosition += zItem.ZVelocity * frameTime;
            var distanceToGround = zItem.LocalPosition - groundHeight;

            // AutoStep — snap up to ground top (wall HighGround lifts the item to ~1.05). When
            // the floor catches a falling item we also kill the downward velocity here, otherwise
            // every tick of gravity accumulates and the settle check below never trips.
            if (hasSupport && distanceToGround < 0f)
            {
                zItem.LocalPosition = groundHeight;
                if (zItem.ZVelocity < 0f)
                    zItem.ZVelocity = 0f;
            }

            // Fall-through: below local floor and no support here → descend a Z.
            if (zItem.LocalPosition < 0f)
            {
                if (CanItemDescendFromCurrentTile(uid, xform) && TryMoveDown(uid, bypassPassability: true))
                {
                    zItem.LocalPosition += 1f;
                    zItem.HadZFall = true;
                    var fallEv = new CEZLevelFallMapEvent();
                    RaiseLocalEvent(uid, ref fallEv);
                }
                else
                {
                    FinishItemZFall(uid, zItem);
                    continue;
                }
            }

            // Rise-through: reached the ceiling. Open above → move up; closed → cap at 1.
            // CMU does NOT fire a fall event on rise-through — that popup is reserved for the
            // descent path. So an item that arcs up through an open ceiling lands on the upper Z
            // silently; it'll only produce the "falls from above" popup later if it then descends
            // through a hole.
            if (zItem.LocalPosition >= 1f)
            {
                if (!HasTileAbove(uid) && TryMoveUp(uid, bypassPassability: true))
                {
                    zItem.LocalPosition -= 1f;
                }
                else
                {
                    zItem.LocalPosition = 1f;
                    if (zItem.ZVelocity > 0f)
                        zItem.ZVelocity = 0f;
                }
            }

            // Settle: on the ground at low velocity AND throw finished → clean up Z-physics.
            // Keeping the component around while the upstream throw is still active so AutoStep
            // can still trigger on the next tile crossed.
            if (onGround &&
                hasSupport &&
                !HasComp<ThrownItemComponent>(uid) &&
                MathF.Abs(zItem.LocalPosition - MathF.Max(0f, groundHeight)) <= 0.05f &&
                MathF.Abs(zItem.ZVelocity) < 0.3f)
            {
                zItem.LocalPosition = MathF.Max(0f, groundHeight);
                FinishItemZFall(uid, zItem);
                continue;
            }

            DirtyItemZVisuals(uid, zItem, oldLocalPosition);
        }
    }

    /// <summary>
    /// Returns the height of solid support at the item's current tile.
    ///   <c>&gt;= 0</c> → support exists (0 for plain floor, 1.05 for walls etc).
    ///   <c>&lt; 0</c> → no support; AutoStep can't lift, and the item will fall-through.
    /// Lightweight mirror of CMU's <c>ComputeGroundHeightInternal</c>: scans anchored
    /// <see cref="CEZLevelHighGroundComponent"/> entities at the item's tile, then falls back
    /// to plain floor presence.
    /// </summary>
    private float ComputeItemGroundHeight(EntityUid uid, TransformComponent xform)
    {
        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return -1f;

        var worldPos = _transform.GetWorldPosition(uid);
        var tileIndices = _map.WorldToTile(gridUid, grid, worldPos);

        var found = false;
        var peakHeight = 0f;
        var enumerator = _map.GetAnchoredEntitiesEnumerator(gridUid, grid, tileIndices);
        while (enumerator.MoveNext(out var anchored))
        {
            // SupportOnlyFromAbove high-ground (e.g. ladder bases) holds climbers from the level
            // above only — it must not pull an item resting on the same tile onto its curve.
            // Mirrors the floor==0 skip in the mob ground-probe.
            if (anchored is not { } anchoredUid ||
                !TryComp<CEZLevelHighGroundComponent>(anchoredUid, out var hg) ||
                hg.SupportOnlyFromAbove ||
                hg.HeightCurve.Count == 0)
                continue;

            var peak = 0f;
            for (var i = 0; i < hg.HeightCurve.Count; i++)
                peak = MathF.Max(peak, hg.HeightCurve[i]);

            if (!found || peak > peakHeight)
            {
                peakHeight = peak;
                found = true;
            }
        }

        if (found)
            return peakHeight;

        if (_map.TryGetTileRef(gridUid, grid, tileIndices, out var tileRef) && !tileRef.Tile.IsEmpty)
            return 0f;

        return -1f;
    }

    private void DirtyItemZVisuals(EntityUid item, CEZItemPhysicsComponent zItem, float oldLocalPosition)
    {
        if (Math.Abs(oldLocalPosition - zItem.LocalPosition) > 0.01f)
            DirtyField(item, zItem, nameof(CEZItemPhysicsComponent.LocalPosition));
    }

    private void FinishItemZFall(EntityUid item, CEZItemPhysicsComponent zItem, bool spawnImpact = true)
    {
        if (spawnImpact &&
            zItem.HadZFall &&
            MathF.Abs(zItem.ZVelocity) >= ItemImpactVelocityLimit)
        {
            SpawnAtPosition(ItemFallVfx, Transform(item).Coordinates);
        }

        zItem.LocalPosition = 0f;
        zItem.ZVelocity = 0f;
        RemoveItemZPhysics(item);
    }

    private bool UpdateItemGravityInfluence(EntityUid item, TransformComponent xform)
    {
        var hasZGravity = CanItemExperienceZGravity(item, xform);

        if (hasZGravity)
            EnsureComp<CEZGravityInfluencedComponent>(item);
        else
            RemComp<CEZGravityInfluencedComponent>(item);

        return hasZGravity;
    }

    private void OnItemIsWeightless(Entity<ItemComponent> ent, ref IsWeightlessEvent args)
    {
        if (args.Handled || !TryComp(ent.Owner, out TransformComponent? xform))
            return;

        if (!CanItemExperienceZGravity(ent.Owner, xform))
            return;

        args.IsWeightless = false;
        args.Handled = true;
    }

    private bool CanItemExperienceZGravity(EntityUid item, TransformComponent xform)
    {
        if (!IsItemRestingOnMapOrGrid(xform))
            return false;

        var worldPos = _transform.GetWorldPosition(item);
        if (HasCurrentLevelFloor(xform, worldPos))
            return false;

        return HasGridGravityFromBelow(item, xform);
    }

    private bool CanItemDescendFromCurrentTile(EntityUid item, TransformComponent xform)
    {
        if (!CanItemExperienceZGravity(item, xform))
            return false;

        if (IsLandingBlocked(item, xform))
            return false;

        return true;
    }

    private bool HasCurrentLevelFloor(TransformComponent xform, Vector2 worldPos)
    {
        if (xform.GridUid is { } currentGridUid &&
            TryComp<MapGridComponent>(currentGridUid, out var currentGrid))
        {
            return HasFloorTile(currentGridUid, currentGrid, worldPos);
        }

        if (xform.MapUid is { } mapUid &&
            TryResolveGridAtWorldPositionOnMap(mapUid, worldPos, out var resolvedGridUid, out var resolvedGrid))
        {
            return HasFloorTile(resolvedGridUid, resolvedGrid, worldPos);
        }

        return false;
    }

    private bool HasFloorTile(EntityUid gridUid, MapGridComponent grid, Vector2 worldPos)
    {
        var tile = _map.GetTileRef(gridUid, grid, _map.WorldToTile(gridUid, grid, worldPos));
        return !tile.Tile.IsEmpty;
    }

    private static bool IsItemRestingOnMapOrGrid(TransformComponent xform)
    {
        if (xform.MapUid is { } mapUid && xform.ParentUid == mapUid)
            return true;

        return xform.GridUid is { } gridUid && xform.ParentUid == gridUid;
    }
}
