using System.Numerics;
using Content.Server._Pirate.ZLevels.Power;
using Content.Shared._Pirate.ZLevels.Core.Components;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.Server._Pirate.ZLevels.Core;

/// <summary>
/// Handles physical synchronization of grids across Z-levels in a Z-network.
/// When grids are linked, the leader grid (depth 0) drives position, rotation,
/// and velocity for all peer grids, keeping multi-level structures moving as one.
/// </summary>
public sealed partial class CEZLevelsSystem
{
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;

    private bool _gridSyncing;

    // Per-network record of the leader's velocity at the END of the previous sync — i.e., the
    // velocity that was just written into every peer. If a peer's current velocity is now lower
    // than this in the leader's direction of motion, physics has since slowed it (collision on
    // its own map). Comparing against the just-written value avoids the false-positive that hit
    // when comparing against the leader's *current* velocity, since the leader's velocity grows
    // as thrust accumulates and the peer takes a frame to catch up.
    private readonly Dictionary<EntityUid, System.Numerics.Vector2> _lastSyncedVelocity = new();

    private void InitGridSync()
    {
        SubscribeLocalEvent<CEZLinkedGridComponent, ComponentRemove>(OnLinkedGridRemoved);
    }

    /// <summary>
    /// Finds the main grid on each map in the Z-network and links them together.
    /// </summary>
    private void LinkNetworkGrids(Entity<CEZLevelsNetworkComponent> network)
    {
        var grids = new Dictionary<int, EntityUid>();

        var mapToDepth = new Dictionary<EntityUid, int>();
        foreach (var (depth, mapUid) in network.Comp.ZLevels)
        {
            if (mapUid is null)
                continue;
            mapToDepth[mapUid.Value] = depth;
        }

        var gridQuery = AllEntityQuery<MapGridComponent, TransformComponent>();
        while (gridQuery.MoveNext(out var gridUid, out _, out var gridXform))
        {
            if (gridXform.MapUid is null)
                continue;

            if (!mapToDepth.TryGetValue(gridXform.MapUid.Value, out var depth))
                continue;

            grids.TryAdd(depth, gridUid);
        }

        if (grids.Count < 2)
            return;

        foreach (var (depth, gridUid) in grids)
        {
            var comp = EnsureComp<CEZLinkedGridComponent>(gridUid);
            comp.ZNetwork = network.Owner;
            comp.Depth = depth;
            comp.PeerGrids = new Dictionary<int, EntityUid>(grids);
            comp.PeerGrids.Remove(depth);
            Dirty(gridUid, comp);
        }

        RaiseLinkedGridPeersChanged(grids.Values);

        Log.Info($"Linked {grids.Count} grids across Z-levels in network {network.Owner}");
    }

    /// <summary>
    /// Links grids directly using known grid UIDs (bypasses grid discovery).
    /// </summary>
    private void LinkGridsDirectly(Entity<CEZLevelsNetworkComponent> network, Dictionary<int, EntityUid> gridsByDepth)
    {
        if (gridsByDepth.Count < 2)
            return;

        foreach (var (depth, gridUid) in gridsByDepth)
        {
            var comp = EnsureComp<CEZLinkedGridComponent>(gridUid);
            comp.ZNetwork = network.Owner;
            comp.Depth = depth;
            comp.PeerGrids = new Dictionary<int, EntityUid>(gridsByDepth);
            comp.PeerGrids.Remove(depth);
            Dirty(gridUid, comp);
        }

        RaiseLinkedGridPeersChanged(gridsByDepth.Values);

        Log.Info($"Linked {gridsByDepth.Count} grids across Z-levels in network {network.Owner}");
    }

    private void OnLinkedGridRemoved(Entity<CEZLinkedGridComponent> ent, ref ComponentRemove args)
    {
        // Unlink this grid from all peers
        foreach (var (_, peerUid) in ent.Comp.PeerGrids)
        {
            if (TryComp<CEZLinkedGridComponent>(peerUid, out var peerLinked))
            {
                peerLinked.PeerGrids.Remove(ent.Comp.Depth);
                var ev = new CEMultizLinkedGridPeersChangedEvent();
                RaiseLocalEvent(peerUid, ref ev);
            }
        }

        // Drop the cached last-synced-velocity entry so EntityUid recycling can't leave us
        // matching peers against a velocity from a long-dead network.
        if (ent.Comp.Depth == 0)
            _lastSyncedVelocity.Remove(ent.Comp.ZNetwork);
    }

    private void RaiseLinkedGridPeersChanged(IEnumerable<EntityUid> gridUids)
    {
        foreach (var gridUid in gridUids)
        {
            var ev = new CEMultizLinkedGridPeersChangedEvent();
            RaiseLocalEvent(gridUid, ref ev);
        }
    }

    /// <summary>
    /// Syncs position, rotation, and velocity from the leader grid (depth 0) to all peers.
    /// This is intentionally one-way: feeding peer velocity back into the leader every frame
    /// cancels freshly-applied shuttle thrust before the peers have caught up.
    /// </summary>
    private void UpdateGridSync(float frameTime)
    {
        if (_gridSyncing)
            return;

        _gridSyncing = true;
        try
        {
            var query = EntityQueryEnumerator<CEZLinkedGridComponent, TransformComponent, PhysicsComponent>();
            while (query.MoveNext(out var uid, out var linked, out var xform, out var body))
            {
                // Only process leaders (depth 0)
                if (linked.Depth != 0)
                    continue;

                if (linked.PeerGrids.Count == 0)
                    continue;

                // What every peer was told to move at last frame — exactly the velocity we wrote
                // into them at the end of the previous UpdateGridSync pass. Compared against the
                // peer's current velocity below, this isolates "physics decelerated me" from
                // "I haven't been re-synced after the leader's thrust changed."
                _lastSyncedVelocity.TryGetValue(linked.ZNetwork, out var lastSyncedLeaderVel);

                // Pass 1: scan peers for collision blockage on their own maps. The leader's map
                // is usually empty of whatever a peer is bumping into, so the leader's physics
                // never sees the obstacle. If any peer's velocity has been decelerated below the
                // value sync wrote into it last frame, physics held it back — propagate that
                // constraint up to the leader BEFORE the per-peer sync runs, so every peer in
                // pass 2 sees the corrected leader state. Without this two-pass split, peers
                // visited earlier in the dictionary keep their old velocity and physics drifts
                // them forward indefinitely on subsequent ticks.
                if (lastSyncedLeaderVel.LengthSquared() > 0.0001f)
                {
                    foreach (var (_, peerUid) in linked.PeerGrids)
                    {
                        if (!TryComp<TransformComponent>(peerUid, out var peerXform))
                            continue;
                        if (!TryComp<CEZLinkedGridComponent>(peerUid, out var peerLinked) ||
                            peerLinked.ZNetwork != linked.ZNetwork)
                            continue;
                        if (peerXform.MapUid == xform.MapUid)
                            continue;
                        if (!TryComp<PhysicsComponent>(peerUid, out var peerBody))
                            continue;

                        var velDot = System.Numerics.Vector2.Dot(peerBody.LinearVelocity, lastSyncedLeaderVel);
                        if (velDot < lastSyncedLeaderVel.LengthSquared() * 0.5f)
                        {
                            _transform.SetLocalPositionRotation(uid, peerXform.LocalPosition, peerXform.LocalRotation, xform);
                            _physics.SetLinearVelocity(uid, peerBody.LinearVelocity, body: body);
                            _physics.SetAngularVelocity(uid, peerBody.AngularVelocity, body: body);
                            break;
                        }
                    }
                }

                // Pass 2: sync all peers to leader's state (possibly already corrected by pass 1).
                foreach (var (_, peerUid) in linked.PeerGrids)
                {
                    if (!TryComp<TransformComponent>(peerUid, out var peerXform))
                        continue;

                    // Mutual-link guard. The leader's PeerGrids dictionary can carry stale entries
                    // pointing at grids that have since been re-linked into a different ZNetwork
                    // (e.g., a multi-level shuttle that crossed paths with a station network's
                    // re-linking pass). Writing position/velocity into such a grid would teleport
                    // an unrelated body and bypass its physics — observed as a moving grid passing
                    // straight through static targets with no collision events firing. We require
                    // the peer to agree that it belongs to the same network before syncing it.
                    if (!TryComp<CEZLinkedGridComponent>(peerUid, out var peerLinked) ||
                        peerLinked.ZNetwork != linked.ZNetwork)
                    {
                        continue;
                    }

                    // Same-map fallback guard. When FTL arrival cannot find a destination z-map
                    // at a peer's depth offset, TryResolvePeerArrivalMap falls back to the leader's
                    // arrival map. The peer then sits on the same map as the leader, and the sync
                    // below would teleport that peer onto the leader's exact position every frame,
                    // stacking dynamic shuttle bodies at the same coordinates. Box2D treats that as
                    // perpetually-penetrating overlapping fixtures, which can corrupt the broadphase
                    // contact graph for the leader and silently drop legitimate collisions with
                    // unrelated grids on this map. Skip the forcible position write in that state;
                    // the peer keeps its own physics-driven position rather than teleporting onto
                    // the leader.
                    if (peerXform.MapUid == xform.MapUid)
                        continue;

                    TryComp<PhysicsComponent>(peerUid, out PhysicsComponent? peerBody);

                    var watchedPair = IsGridSyncPairWatched(uid, peerUid);
                    var transformMismatch = peerXform.LocalPosition != xform.LocalPosition ||
                                            peerXform.LocalRotation != xform.LocalRotation;
                    var leaderVelocity = body.LinearVelocity;
                    var velocityMismatch = peerBody != null && peerBody.LinearVelocity != leaderVelocity;

                    if (watchedPair &&
                        (transformMismatch || velocityMismatch))
                    {
                        var dedupeKey =
                            $"{StairCsvDedupeVec2(xform.LocalPosition, 2)}|{StairCsvDedupeFloat((float) xform.LocalRotation.Degrees, 3)}|{StairCsvDedupeVec2(leaderVelocity, 3)}|" +
                            $"{StairCsvDedupeVec2(peerXform.LocalPosition, 2)}|{StairCsvDedupeFloat((float) peerXform.LocalRotation.Degrees, 3)}|{(peerBody != null ? StairCsvDedupeVec2(peerBody.LinearVelocity, 3) : "na")}";
                        DebugZStairCsv(uid,
                            "grid_sync_pair",
                            $"leader={ToPrettyString(uid)},peer={ToPrettyString(peerUid)},leader_local={StairCsvVec2(xform.LocalPosition)},leader_rot={StairCsvFloat((float) xform.LocalRotation.Degrees)},leader_vel={StairCsvVec2(leaderVelocity)},peer_local={StairCsvVec2(peerXform.LocalPosition)},peer_rot={StairCsvFloat((float) peerXform.LocalRotation.Degrees)},peer_vel={(peerBody != null ? StairCsvVec2(peerBody.LinearVelocity) : "na")},transform_mismatch={StairCsvBool(transformMismatch)},velocity_mismatch={StairCsvBool(velocityMismatch)}",
                            dedupeKey);
                    }

                    // Sync local position and rotation (relative to parent map)
                    if (transformMismatch)
                    {
                        _transform.SetLocalPositionRotation(peerUid, xform.LocalPosition, xform.LocalRotation, peerXform);
                    }

                    // Sync velocities
                    if (peerBody != null)
                    {
                        if (velocityMismatch)
                            _physics.SetLinearVelocity(peerUid, body.LinearVelocity, body: peerBody);

                        if (peerBody.AngularVelocity != body.AngularVelocity)
                            _physics.SetAngularVelocity(peerUid, body.AngularVelocity, body: peerBody);
                    }
                }

                // Record what we just wrote into peers so the next tick's collision check has a
                // reference point. Skipped on the early-break path above, which already wrote a
                // fresher value reflecting the corrected leader velocity.
                _lastSyncedVelocity[linked.ZNetwork] = body.LinearVelocity;
            }
        }
        finally
        {
            _gridSyncing = false;
        }
    }
}
