/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using System.Numerics;
using Content.Shared._Pirate.ZLevels.Core.Components;
using Content.Shared._Pirate.ZLevels.Core.EntitySystems;
using Content.Shared.ActionBlocker;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Dynamics.Joints;
using Robust.Shared.Timing;

namespace Content.Shared._Pirate.ZLevels.Pulling;

public sealed class CEZLevelPullingSystem : EntitySystem
{
    private const float PullRangeTolerance = 0.15f;

    [Dependency] private readonly PullingSystem _pulling = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private readonly CESharedZLevelsSystem _zLevels = default!;

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
            if (!Exists(uid) || comp.TargetPuller is not { } puller || !Exists(puller))
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
                if (!TryComp<CEZPhysicsComponent>(puller, out var pullerZ) || pullerZ.CurrentZLevel != comp.TargetZLevel)
                    continue;

                _zLevels.TeleportToZLevelCoordinates(uid, Transform(puller).Coordinates, comp.TargetZLevel, comp.TargetOffset);
                _zLevels.NormalizeTransferredPullable(uid, comp.TargetOffset);
                comp.TransferAttempted = true;
                comp.TargetPosition = GetDesiredFollowPosition(puller);
                Dirty(uid, comp);
            }

            if (Transform(uid).MapUid != Transform(puller).MapUid)
                continue;

            comp.TargetPosition = GetDesiredFollowPosition(puller);
            Dirty(uid, comp);

            _transform.SetWorldPosition(uid, comp.TargetPosition);
            TryResumePulling(uid, comp);
        }
    }

    private void OnPullerMove(Entity<ActivePullerComponent> ent, ref CEZLevelBeforeMapMoveEvent args)
    {
        if (!_timing.IsFirstTimePredicted)
            return;

        if (!TryComp<PullerComponent>(ent, out var puller) || puller.Pulling == null)
            return;

        var pulledEntity = puller.Pulling.Value;

        var pullerPos = _transform.GetWorldPosition(ent);
        var pulledPos = _transform.GetWorldPosition(pulledEntity);
        var pullOffset = pulledPos - pullerPos;
        var pullDistance = pullOffset.Length();
        var pullDirection = pullDistance > 0.001f
            ? Vector2.Normalize(pullOffset)
            : -Transform(ent).LocalRotation.ToWorldVec();

        // Add transition component to the pulled entity
        var transComp = EnsureComp<CEZLevelPullingTransitionComponent>(pulledEntity);
        transComp.TargetPuller = ent;
        transComp.StartPosition = pulledPos;
        transComp.TargetPosition = pullerPos;
        transComp.PullDirection = pullDirection;
        transComp.PullDistance = pullDistance;
        transComp.TargetZLevel = args.CurrentZLevel;
        transComp.TargetOffset = args.Offset;
        transComp.TransferAttempted = false;

        var distance = Vector2.Distance(transComp.StartPosition, transComp.TargetPosition);
        var duration = TimeSpan.FromSeconds(Math.Max(0.25f, distance / transComp.TransitionSpeed));
        transComp.NextTransition = _timing.CurTime + duration + TimeSpan.FromSeconds(0.75f);

        Dirty(pulledEntity, transComp);
    }

    /// <summary>
    /// Attempts to resume pulling after the pulled entity has been moved to the puller's z-level.
    /// </summary>
    private void TryResumePulling(EntityUid uid, CEZLevelPullingTransitionComponent comp)
    {
        if (comp.TargetPuller is not { } puller || !Exists(puller))
        {
            RemComp<CEZLevelPullingTransitionComponent>(uid);
            return;
        }

        if (Transform(uid).MapUid != Transform(puller).MapUid)
            return;

        if (!_actionBlocker.CanInteract(puller, uid))
            return;

        if (TryComp<PullableComponent>(uid, out var pullable) &&
            pullable.Puller == puller &&
            pullable.PullJointId != null)
        {
            RemComp<CEZLevelPullingTransitionComponent>(uid);
            return;
        }

        if (TryComp<PullableComponent>(uid, out pullable) &&
            pullable.Puller != null)
        {
            _pulling.TryStopPull(uid, pullable, puller, true);
        }

        if (_pulling.TryStartPull(puller, uid, force: true))
        {
            RestorePullRange(uid, comp.PullDistance);
            RemComp<CEZLevelPullingTransitionComponent>(uid);
        }
    }

    private Vector2 GetDesiredFollowPosition(EntityUid puller)
    {
        return _transform.GetWorldPosition(puller);
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
