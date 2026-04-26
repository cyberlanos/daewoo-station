/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using System.Numerics;
using Content.Shared.Gravity;
using Content.Shared.Interaction.Events;
using Content.Shared.Item;
using Content.Shared.Throwing;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;

namespace Content.Server._Pirate.ZLevels.Core;

public sealed partial class CEZLevelsSystem
{
    private readonly HashSet<EntityUid> _pendingItemDescents = new();

    private void InitItems()
    {
        SubscribeLocalEvent<ItemComponent, StopThrowEvent>(OnItemStopThrow);
        SubscribeLocalEvent<ItemComponent, DroppedEvent>(OnItemDropped);
        SubscribeLocalEvent<ItemComponent, IsWeightlessEvent>(OnItemIsWeightless);
    }

    private void OnItemStopThrow(Entity<ItemComponent> ent, ref StopThrowEvent args)
    {
        QueueItemDescend(ent.Owner);
    }

    private void OnItemDropped(Entity<ItemComponent> ent, ref DroppedEvent args)
    {
        QueueItemDescend(ent.Owner);
    }

    private void QueueItemDescend(EntityUid item)
    {
        _pendingItemDescents.Add(item);
    }

    private void UpdateItems(float frameTime)
    {
        if (_pendingItemDescents.Count == 0)
            return;

        var pending = new List<EntityUid>(_pendingItemDescents);
        _pendingItemDescents.Clear();

        foreach (var item in pending)
        {
            if (TerminatingOrDeleted(item))
                continue;

            var xform = Transform(item);
            if (!IsItemRestingOnMapOrGrid(xform))
                continue;

            if (HasComp<ThrownItemComponent>(item) ||
                TryComp<PhysicsComponent>(item, out var physics) && physics.Awake)
            {
                _pendingItemDescents.Add(item);
                continue;
            }

            TryItemDescend(item);
        }
    }

    private void OnItemIsWeightless(Entity<ItemComponent> ent, ref IsWeightlessEvent args)
    {
        if (args.Handled || !TryComp(ent.Owner, out TransformComponent? xform))
            return;

        if (!CanItemDescendFromCurrentTile(ent.Owner, xform))
            return;

        args.IsWeightless = false;
        args.Handled = true;
    }

    private bool TryItemDescend(EntityUid item)
    {
        if (HasComp<ThrownItemComponent>(item))
            return false;

        var xform = Transform(item);
        if (!CanItemDescendFromCurrentTile(item, xform))
            return false;

        return TryMoveDown(item);
    }

    private bool CanItemDescendFromCurrentTile(EntityUid item, TransformComponent xform)
    {
        if (!IsItemRestingOnMapOrGrid(xform))
            return false;

        var worldPos = _transform.GetWorldPosition(item);
        if (HasCurrentLevelFloor(xform, worldPos))
            return false;

        if (!TryGetSupportBelow(item, xform, out _, out var isHighGround))
            return false;

        return isHighGround || HasEffectiveGravityFromBelow(item, xform);
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
