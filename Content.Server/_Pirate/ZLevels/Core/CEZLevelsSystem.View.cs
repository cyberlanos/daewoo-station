/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

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
    }

    private void UpdateView(float frameTime)
    {
        if (_timing.CurTime < _nextZLevelViewerUpdate)
            return;
        _nextZLevelViewerUpdate = _timing.CurTime + _zLevelViewerUpdateRate;

        var query = EntityQueryEnumerator<CEZLevelViewerComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var viewer, out var xform))
        {
            var desiredMaps = GetViewerAdjacentMaps((uid, xform));

            if (!ViewerEyesMatch(viewer, desiredMaps))
            {
                UpdateViewer((uid, viewer), xform, desiredMaps);
                continue;
            }

            foreach (var eye in viewer.Eyes)
            {
                _transform.SetWorldPosition(eye, _transform.GetWorldPosition(xform));
            }
        }
    }

    private void OnViewerInit(Entity<CEZLevelViewerComponent> ent, ref MapInitEvent args)
    {
        _actions.AddAction(ent, ref ent.Comp.ZLevelActionEntity, ent.Comp.ActionProto);
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
        UpdateViewer((ev.Entity, viewer));
    }

    private void OnPlayerDetached(PlayerDetachedEvent ev)
    {
        RemComp<CEZLevelViewerComponent>(ev.Entity);
    }

    private void OnViewerMapUidChanged(Entity<CEZLevelViewerComponent> ent, ref MapUidChangedEvent args)
    {
        UpdateViewer(ent);
    }

    // Build the exact map subscription set for the viewer, preferring linked shuttle peers so multiz decks stay visible together.
    private List<EntityUid> GetViewerAdjacentMaps(Entity<TransformComponent> ent)
    {
        var result = new List<EntityUid>();

        for (var i = 1; i <= MaxZLevelsBelowRendering; i++)
        {
            if (!TryResolveViewerMap(ent, -i, out var belowMapUid))
                break;

            result.Add(belowMapUid);
        }

        // We constantly load the upper z-level for the client so that you can quickly look up and climb stairs without PVS lag.
        if (TryResolveViewerMap(ent, 1, out var aboveMapUid))
            result.Add(aboveMapUid);

        return result;
    }

    private bool TryResolveViewerMap(Entity<TransformComponent> ent, int depthOffset, out EntityUid mapUid)
    {
        mapUid = default;

        if (ent.Comp.GridUid is { } gridUid &&
            TryComp<CEZLinkedGridComponent>(gridUid, out var linked))
        {
            var targetDepth = linked.Depth + depthOffset;

            if (linked.PeerGrids.TryGetValue(targetDepth, out var peerGridUid) &&
                TryComp<TransformComponent>(peerGridUid, out var peerXform) &&
                peerXform.MapUid is { } peerMapUid)
            {
                mapUid = peerMapUid;
                return true;
            }
        }

        if (ent.Comp.MapUid is not { } currentMapUid ||
            !TryMapOffset(currentMapUid, depthOffset, out var targetMapUid))
        {
            return false;
        }

        mapUid = targetMapUid.Value;
        return true;
    }

    // Avoid respawning view eyes every tick when the adjacent-map set has not actually changed.
    private bool ViewerEyesMatch(CEZLevelViewerComponent viewer, IReadOnlyCollection<EntityUid> desiredMaps)
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

        return currentMaps.SetEquals(desiredMaps);
    }

    private void UpdateViewer(Entity<CEZLevelViewerComponent> ent, TransformComponent? xform = null, List<EntityUid>? desiredMaps = null)
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

        var globalPos = _transform.GetWorldPosition(xform);
        // Reuse the precomputed desired-map list when UpdateView already resolved it, so eye rebuilds and movement stay in sync.
        desiredMaps ??= GetViewerAdjacentMaps((ent.Owner, xform));

        foreach (var targetMapUid in desiredMaps)
        {
            var newEye = SpawnAtPosition(_zEyeProto, new EntityCoordinates(targetMapUid, globalPos));

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
}
