/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Shared._Pirate.ZLevels.Core.Components;
using Content.Shared._Pirate.ZLevels.Core.EntitySystems;
using Content.Shared.ActionBlocker;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Robust.Shared.Network;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Dynamics.Joints;
using Robust.Shared.Timing;

namespace Content.Shared._Pirate.ZLevels.Pulling;

public sealed class CEZLevelPullingSystem : EntitySystem
{
    private const float PullRangeTolerance = 0.15f;
    private static readonly TimeSpan TransitionTimeout = TimeSpan.FromSeconds(3);

    [Dependency] private readonly PullingSystem _pulling = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private readonly CESharedZLevelsSystem _zLevels = default!;
    [Dependency] private readonly INetManager _net = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ActivePullerComponent, CEZLevelBeforeMapMoveEvent>(OnPullerMove);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_timing.IsFirstTimePredicted)
            return;

        var query = EntityQueryEnumerator<CEZLevelPullingTransitionComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.TargetPuller is not { } puller || !Exists(puller))
            {
                RemComp<CEZLevelPullingTransitionComponent>(uid);
                continue;
            }

            if (comp.NextTransition is { } deadline && deadline < _timing.CurTime)
            {
                RemComp<CEZLevelPullingTransitionComponent>(uid);
                continue;
            }

            if (!comp.TransferAttempted)
            {
                // Wait until the puller has actually arrived on the target level.
                if (!TryComp<CEZPhysicsComponent>(puller, out var pullerZ) || pullerZ.CurrentZLevel != comp.TargetZLevel)
                    continue;

                // Carry the pulled entity to wherever the puller ended up — its final landing tile,
                // which is solid ground after an ascent and the stair/exit tile after a descent —
                // instead of letting it drop onto its own old x,y one level away.
                _zLevels.TeleportToZLevelCoordinates(uid, Transform(puller).Coordinates, comp.TargetZLevel, comp.TargetOffset);
                _zLevels.NormalizeTransferredPullable(uid, comp.TargetOffset);
                comp.TransferAttempted = true;
                Dirty(uid, comp);

                Log.Debug(
                    $"[ZPull] carried pulled={ToPrettyString(uid)} to puller={ToPrettyString(puller)} " +
                    $"targetZ={comp.TargetZLevel} offset={comp.TargetOffset}");

                // Rebuild the pull on the next tick, once both bodies are fully settled on the new
                // map, so the joint isn't created against a still-transferring (nullspace) body.
                continue;
            }

            if (!OnSameRealMap(uid, puller))
                continue;

            TryResumePulling(uid, comp);
        }
    }

    private void OnPullerMove(Entity<ActivePullerComponent> ent, ref CEZLevelBeforeMapMoveEvent args)
    {
        if (!TryComp<PullerComponent>(ent, out var puller) || puller.Pulling == null)
            return;

        var pulledEntity = puller.Pulling.Value;

        // This fires before the puller reparents to the target z-map. Fully stop the pull *now* so
        // the puller crosses alone — a distance joint can't span two maps, and leaving it active lets
        // the engine drag the mob / re-add the joint against mismatched bodies ("ineligible bodies").
        //
        // Do this on EVERY prediction pass, not just the first-predicted tick. During client
        // re-prediction the joint is restored from server state at the start of each cycle, so if we
        // only stopped on the first-predicted tick a re-simulated cross-map step would still carry a
        // live cross-map joint. TryStopPull is a no-op once the pull is already cleared.
        //
        // Drive the stop off the pullable's own Puller back-reference rather than ent.Owner: during
        // prediction those can be momentarily out of sync, and a mismatch here would skip the stop
        // and let the joint ride across the map (seen as "Removed all joints from <puller>" firing
        // during the reparent instead of here, followed by a cross-map re-add).
        if (TryComp<PullableComponent>(pulledEntity, out var pullable) && pullable.Puller is { } activePuller)
            _pulling.TryStopPull(pulledEntity, pullable, activePuller, ignoreGrab: true);

        // Queue the deferred carry once, on the authoritative / first-predicted pass. The pull is
        // rebuilt in TryResumePulling after the mob is carried to the puller's level.
        if (!_timing.IsFirstTimePredicted)
            return;

        var pullDistance = (_transform.GetWorldPosition(pulledEntity) - _transform.GetWorldPosition(ent)).Length();

        var transComp = EnsureComp<CEZLevelPullingTransitionComponent>(pulledEntity);
        transComp.TargetPuller = ent;
        transComp.PullDistance = pullDistance;
        transComp.TargetZLevel = args.CurrentZLevel;
        transComp.TargetOffset = args.Offset;
        transComp.TransferAttempted = false;
        transComp.NextTransition = _timing.CurTime + TransitionTimeout;
        Dirty(pulledEntity, transComp);

        Log.Debug(
            $"[ZPull] puller={ToPrettyString(ent)} crossing offset={args.Offset} targetZ={args.CurrentZLevel} " +
            $"pulled={ToPrettyString(pulledEntity)} dist={pullDistance:F2}; pull stopped, deferred carry queued");
    }

    /// <summary>
    /// Re-establishes the pull once the pulled entity has been carried to the puller's z-level.
    /// </summary>
    private void TryResumePulling(EntityUid uid, CEZLevelPullingTransitionComponent comp)
    {
        if (comp.TargetPuller is not { } puller || !Exists(puller))
        {
            RemComp<CEZLevelPullingTransitionComponent>(uid);
            return;
        }

        // Both must be settled on the same, real (non-nullspace) map before we build the joint.
        // A distance joint across two maps — or with either body still in nullspace mid-transfer —
        // is rejected by the physics engine ("Tried to add joint to ineligible bodies").
        if (!OnSameRealMap(uid, puller))
            return;

        // Rebuild the pull on the server only. TryStartPull calls CreateDistanceJoint directly, and
        // a client predicting that during the cross-map transition (its two bodies briefly on
        // different maps) is exactly what logs "ineligible bodies". The server rebuilds it once both
        // bodies are confirmed on the same map and replicates the joint to the client via state
        // (InitJoint), which never hits that error path.
        if (!_net.IsServer)
        {
            RemComp<CEZLevelPullingTransitionComponent>(uid);
            return;
        }

        if (!_actionBlocker.CanInteract(puller, uid))
            return;

        if (TryComp<PullableComponent>(uid, out var pullable) && pullable.Puller != null)
            _pulling.TryStopPull(uid, pullable, puller, true);

        var pulledXform = Transform(uid);
        var pullerXform = Transform(puller);
        Log.Debug(
            $"[ZPull] resume start ({(_net.IsServer ? "server" : "client")}): pulled={ToPrettyString(uid)} " +
            $"pulledMap={pulledXform.MapID} pulledGrid={(pulledXform.GridUid == null ? "null" : ToPrettyString(pulledXform.GridUid.Value))} " +
            $"pulledParent={ToPrettyString(pulledXform.ParentUid)} | puller={ToPrettyString(puller)} " +
            $"pullerMap={pullerXform.MapID} pullerGrid={(pullerXform.GridUid == null ? "null" : ToPrettyString(pullerXform.GridUid.Value))} " +
            $"pullerParent={ToPrettyString(pullerXform.ParentUid)}");

        if (_pulling.TryStartPull(puller, uid, force: true))
        {
            RestorePullRange(uid, comp.PullDistance);
            RemComp<CEZLevelPullingTransitionComponent>(uid);
        }
        else
        {
            Log.Warning(
                $"[ZPull] failed to resume pull of {ToPrettyString(uid)} by {ToPrettyString(puller)} " +
                $"(pulledMap={Transform(uid).MapID} pullerMap={Transform(puller).MapID})");
        }
    }

    /// <summary>
    /// True when both entities share the same non-nullspace map — the precondition for a valid pull joint.
    /// </summary>
    private bool OnSameRealMap(EntityUid a, EntityUid b)
    {
        return Transform(a).MapUid is { } mapA && mapA == Transform(b).MapUid;
    }

    private void RestorePullRange(EntityUid pulled, float pullDistance)
    {
        if (pullDistance <= 0.001f ||
            !TryComp<PullableComponent>(pulled, out var pullable) ||
            pullable.PullJointId is not { } jointId ||
            !TryComp<JointComponent>(pulled, out var jointComp) ||
            !jointComp.GetJoints.TryGetValue(jointId, out var joint) ||
            joint is not DistanceJoint distanceJoint)
        {
            return;
        }

        distanceJoint.Length = pullDistance;
        distanceJoint.MaxLength = pullDistance + PullRangeTolerance;
        distanceJoint.MinLength = 0f;
    }
}
