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
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;
using Robust.Shared.Prototypes;

namespace Content.Server._Pirate.ZLevels.Core;

public sealed partial class CEZLevelsSystem
{
    private const float ItemZGravityForce = 9.8f;
    private const float ItemZVelocityLimit = 20f;
    private const float ItemImpactVelocityLimit = 3f;
    private static readonly EntProtoId ItemFallVfx = "CEDustEffect";

    private void InitItems()
    {
        SubscribeLocalEvent<ItemComponent, StopThrowEvent>(OnItemStopThrow);
        SubscribeLocalEvent<ItemComponent, DroppedEvent>(OnItemDropped);
        SubscribeLocalEvent<ItemComponent, MoveEvent>(OnItemMoved);
        SubscribeLocalEvent<ItemComponent, IsWeightlessEvent>(OnItemIsWeightless);
        SubscribeLocalEvent<CEZItemPhysicsComponent, GotEquippedHandEvent>(OnItemGotEquippedHand);
        SubscribeLocalEvent<CEZItemPhysicsComponent, GotEquippedEvent>(OnItemGotEquipped);
        SubscribeLocalEvent<CEZItemPhysicsComponent, EntGotInsertedIntoContainerMessage>(OnItemInsertedIntoContainer);
        SubscribeLocalEvent<CEZItemPhysicsComponent, EntParentChangedMessage>(OnItemZPhysicsParentChanged);
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

            if (HasComp<ThrownItemComponent>(uid) ||
                TryComp<PhysicsComponent>(uid, out var physics) && physics.Awake)
                continue;

            var oldLocalPosition = zItem.LocalPosition;
            var worldPos = _transform.GetWorldPosition(uid);
            var hasCurrentLevelFloor = HasCurrentLevelFloor(xform, worldPos);
            if (hasCurrentLevelFloor && zItem.LocalPosition <= 0f)
            {
                FinishItemZFall(uid, zItem);
                continue;
            }

            if (hasCurrentLevelFloor)
            {
                RemComp<CEZGravityInfluencedComponent>(uid);
            }
            else if (!UpdateItemGravityInfluence(uid, xform))
            {
                FinishItemZFall(uid, zItem, false);
                continue;
            }

            zItem.ZVelocity = MathF.Max(zItem.ZVelocity - ItemZGravityForce * frameTime, -ItemZVelocityLimit);
            zItem.LocalPosition += zItem.ZVelocity * frameTime;

            if (zItem.LocalPosition > 0f)
            {
                DirtyItemZVisuals(uid, zItem, oldLocalPosition);
                continue;
            }

            if (!CanItemDescendFromCurrentTile(uid, xform) || !TryMoveDown(uid, bypassPassability: true))
            {
                FinishItemZFall(uid, zItem);
                continue;
            }

            zItem.LocalPosition += 1f;
            zItem.HadZFall = true;
            var fallEv = new CEZLevelFallMapEvent();
            RaiseLocalEvent(uid, ref fallEv);

            DirtyItemZVisuals(uid, zItem, oldLocalPosition);
        }
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
