/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using System.Numerics;
using Content.Shared._Pirate.ZLevels.Core.Components;
using Content.Shared._Pirate.ZLevels.Core.EntitySystems;
using Content.Shared.Actions;
using Content.Shared.IdentityManagement;
using Content.Shared.Popups;
using Robust.Shared.GameObjects;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._Pirate.ZLevels.Core;

public sealed partial class CEZLevelsSystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly ViewSubscriberSystem _viewSubscriber = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;

    private readonly EntProtoId _zEyeProto = "CEZLevelEye";

    // Poll faster so eye subscriptions react during FTL/map swaps before adjacent layers visibly disappear for the player.
    private readonly TimeSpan _zLevelViewerUpdateRate = TimeSpan.FromSeconds(0.1f);
    private TimeSpan _nextZLevelViewerUpdate = TimeSpan.Zero;

    private void InitView()
    {
        SubscribeLocalEvent<PlayerAttachedEvent>(OnPlayerAttached);
        SubscribeLocalEvent<PlayerDetachedEvent>(OnPlayerDetached);

        SubscribeLocalEvent<CEZLevelViewerComponent, MapInitEvent>(OnViewerInit);
        SubscribeLocalEvent<CEZLevelViewerComponent, ComponentRemove>(OnCompRemove);

        SubscribeLocalEvent<CEZLevelViewerComponent, MapUidChangedEvent>(OnViewerMapUidChanged);
        SubscribeLocalEvent<CEZPhysicsComponent, CEZLevelFallMapEvent>(OnZLevelFall);
        SubscribeLocalEvent<CEZItemPhysicsComponent, CEZLevelFallMapEvent>(OnItemZLevelFall);
    }

    private void UpdateView(float frameTime)
    {
        if (_timing.CurTime < _nextZLevelViewerUpdate)
            return;
        _nextZLevelViewerUpdate = _timing.CurTime + _zLevelViewerUpdateRate;

        var query = EntityQueryEnumerator<CEZLevelViewerComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var viewer, out var xform))
        {
            UpdateLookUpAction((uid, viewer), xform);

            var desiredMaps = GetViewerAdjacentMaps((uid, xform));

            if (!ViewerEyesMatch(viewer, desiredMaps))
            {
                UpdateViewer((uid, viewer), xform, desiredMaps);
                continue;
            }

            // viewer.Eyes order matches desiredMaps insertion order from UpdateViewer / GetViewerAdjacentMaps.
            var i = 0;
            foreach (var eye in viewer.Eyes)
            {
                if (i >= desiredMaps.Count)
                    break;

                _transform.SetWorldPosition(eye, desiredMaps[i].WorldPosition);
                i++;
            }
        }
    }

    private void OnViewerInit(Entity<CEZLevelViewerComponent> ent, ref MapInitEvent args)
    {
        UpdateLookUpAction(ent);
        _meta.AddFlag(ent, MetaDataFlags.ExtraTransformEvents);
    }

    private void OnCompRemove(Entity<CEZLevelViewerComponent> ent, ref ComponentRemove args)
    {
        _actions.RemoveAction(ent.Comp.ZLevelActionEntity);
        _meta.RemoveFlag(ent, MetaDataFlags.ExtraTransformEvents);

        foreach (var eye in ent.Comp.Eyes)
        {
            QueueDel(eye);
        }
    }

    private void OnPlayerAttached(PlayerAttachedEvent ev)
    {
        var viewer = EnsureComp<CEZLevelViewerComponent>(ev.Entity);
        UpdateLookUpAction((ev.Entity, viewer));
        UpdateViewer((ev.Entity, viewer));
    }

    private void OnPlayerDetached(PlayerDetachedEvent ev)
    {
        RemComp<CEZLevelViewerComponent>(ev.Entity);
    }

    private void OnViewerMapUidChanged(Entity<CEZLevelViewerComponent> ent, ref MapUidChangedEvent args)
    {
        // UpdateLookUpAction is intentionally omitted here: AddAction creates a child entity, which
        // violates ChangeMapIdRecursive's assert that ChildCount stays constant during MapUidChangedEvent.
        // The 0.1s UpdateView poll handles the action update with negligible delay.
        UpdateViewer(ent);
    }

    private void UpdateLookUpAction(Entity<CEZLevelViewerComponent> ent, TransformComponent? xform = null)
    {
        if (!Resolve(ent, ref xform, false))
            return;

        if (TryResolveViewerMap((ent.Owner, xform), 1, out _))
        {
            if (ent.Comp.ZLevelActionEntity is { } existing && Exists(existing))
                return;

            ent.Comp.ZLevelActionEntity = null;
            _actions.AddAction(ent, ref ent.Comp.ZLevelActionEntity, ent.Comp.ActionProto);
            DirtyField(ent, ent.Comp, nameof(CEZLevelViewerComponent.ZLevelActionEntity));
            return;
        }

        if (ent.Comp.LookUp)
        {
            ent.Comp.LookUp = false;
            DirtyField(ent, ent.Comp, nameof(CEZLevelViewerComponent.LookUp));
        }

        if (ent.Comp.ZLevelActionEntity is not { } action)
            return;

        _actions.RemoveAction(action);
        ent.Comp.ZLevelActionEntity = null;
        DirtyField(ent, ent.Comp, nameof(CEZLevelViewerComponent.ZLevelActionEntity));
    }

    // Build the exact map subscription set for the viewer, preferring linked shuttle peers so multiz decks stay visible together.
    private List<CEZLevelViewerTarget> GetViewerAdjacentMaps(Entity<TransformComponent> ent)
    {
        var result = new List<CEZLevelViewerTarget>();

        for (var i = 1; i <= MaxZLevelsBelowRendering; i++)
        {
            if (!TryResolveViewerMap(ent, -i, out var belowTarget))
                break;

            result.Add(belowTarget);
        }

        // We constantly load the upper z-level for the client so that you can quickly look up and climb stairs without PVS lag.
        if (TryResolveViewerMap(ent, 1, out var aboveTarget))
            result.Add(aboveTarget);

        return result;
    }

    private bool TryResolveViewerMap(Entity<TransformComponent> ent, int depthOffset, out CEZLevelViewerTarget target)
    {
        target = default;
        var viewerWorld = _transform.GetWorldPosition(ent.Comp);

        if (ent.Comp.GridUid is { } gridUid &&
            TryComp<CEZLinkedGridComponent>(gridUid, out var linked))
        {
            var targetDepth = linked.Depth + depthOffset;

            if (linked.PeerGrids.TryGetValue(targetDepth, out var peerGridUid) &&
                TryComp<TransformComponent>(peerGridUid, out var peerXform) &&
                peerXform.MapUid is { } peerMapUid)
            {
                // Reproject the viewer's world pos via the source grid's inverse and the peer grid's matrix
                // so an eye on the peer deck lines up with the viewer's corresponding tile.
                var srcInv = _transform.GetInvWorldMatrix(gridUid);
                var local = Vector2.Transform(viewerWorld, srcInv);
                var peerWorld = Vector2.Transform(local, _transform.GetWorldMatrix(peerGridUid));
                target = new CEZLevelViewerTarget(peerMapUid, peerWorld);
                return true;
            }
        }

        if (ent.Comp.MapUid is not { } currentMapUid ||
            !TryMapOffset(currentMapUid, depthOffset, out var targetMapUid))
        {
            return false;
        }

        // No peer grid; map-aligned z-stacks share world XY, so reuse the viewer's world pos as-is.
        target = new CEZLevelViewerTarget(targetMapUid.Value, viewerWorld);
        return true;
    }

    private readonly record struct CEZLevelViewerTarget(EntityUid MapUid, Vector2 WorldPosition);

    // Avoid respawning view eyes every tick when the adjacent-map set has not actually changed.
    private bool ViewerEyesMatch(CEZLevelViewerComponent viewer, IReadOnlyList<CEZLevelViewerTarget> desiredMaps)
    {
        if (viewer.Eyes.Count != desiredMaps.Count)
            return false;

        var currentMaps = new HashSet<EntityUid>();
        foreach (var eye in viewer.Eyes)
        {
            if (!TryComp<TransformComponent>(eye, out var eyeXform) || eyeXform.MapUid is not { } eyeMapUid)
                return false;

            currentMaps.Add(eyeMapUid);
        }

        var desiredSet = new HashSet<EntityUid>();
        foreach (var t in desiredMaps)
            desiredSet.Add(t.MapUid);

        return currentMaps.SetEquals(desiredSet);
    }

    private void UpdateViewer(Entity<CEZLevelViewerComponent> ent, TransformComponent? xform = null, List<CEZLevelViewerTarget>? desiredMaps = null)
    {
        var eyes = ent.Comp.Eyes;
        foreach (var eye in ent.Comp.Eyes)
        {
            QueueDel(eye);
        }
        eyes.Clear();

        if (!TryComp<ActorComponent>(ent, out var actor))
            return;

        if (!Resolve(ent, ref xform))
            return;

        // Reuse the precomputed desired-map list when UpdateView already resolved it, so eye rebuilds and movement stay in sync.
        desiredMaps ??= GetViewerAdjacentMaps((ent.Owner, xform));

        foreach (var target in desiredMaps)
        {
            var newEye = SpawnAtPosition(_zEyeProto, new EntityCoordinates(target.MapUid, target.WorldPosition));

            Transform(newEye).GridTraversal = false;
            _viewSubscriber.AddViewSubscriber(newEye, actor.PlayerSession);
            eyes.Add(newEye);
        }
    }

    private void OnZLevelFall(Entity<CEZPhysicsComponent> ent, ref CEZLevelFallMapEvent args)
    {
        //A dirty trick: we call PredictedPopup on the falling entity on SERVER.
        //This means that the one who is falling does not see the popup itself, but everyone around them does. This is what we need.
        _popup.PopupPredictedCoordinates(Loc.GetString("ce-zlevel-falling-popup", ("name", Identity.Name(ent, EntityManager))), Transform(ent).Coordinates, ent);
    }

    private void OnItemZLevelFall(Entity<CEZItemPhysicsComponent> ent, ref CEZLevelFallMapEvent args)
    {
        _popup.PopupPredictedCoordinates(Loc.GetString("ce-zlevel-falling-popup", ("name", Identity.Name(ent, EntityManager))), Transform(ent).Coordinates, ent);
    }
}
