using System.Numerics;
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

        var gridQuery = EntityQueryEnumerator<MapGridComponent, TransformComponent>();
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

        Log.Info($"Linked {gridsByDepth.Count} grids across Z-levels in network {network.Owner}");
    }

    private void OnLinkedGridRemoved(Entity<CEZLinkedGridComponent> ent, ref ComponentRemove args)
    {
        // Unlink this grid from all peers
        foreach (var (_, peerUid) in ent.Comp.PeerGrids)
        {
            if (TryComp<CEZLinkedGridComponent>(peerUid, out var peerLinked))
                peerLinked.PeerGrids.Remove(ent.Comp.Depth);
        }
    }

    /// <summary>
    /// Syncs position, rotation, and velocity from the leader grid (depth 0) to all peers.
    /// Also propagates velocity changes from non-leader grids to the leader, so impacts
    /// on any Z-level affect the whole structure.
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

                // First pass: aggregate velocity deltas from peers.
                // If a non-leader grid was hit by an impulse (meteor, collision, etc.),
                // its velocity will differ from the leader. Propagate that delta to the leader
                // so the whole structure reacts.
                var totalLinearDelta = Vector2.Zero;
                var totalAngularDelta = 0f;

                foreach (var (_, peerUid) in linked.PeerGrids)
                {
                    if (!TryComp<PhysicsComponent>(peerUid, out var peerBody))
                        continue;

                    totalLinearDelta += peerBody.LinearVelocity - body.LinearVelocity;
                    totalAngularDelta += peerBody.AngularVelocity - body.AngularVelocity;
                }

                if (totalLinearDelta.LengthSquared() > 0.0001f)
                    _physics.SetLinearVelocity(uid, body.LinearVelocity + totalLinearDelta, body: body);

                if (MathF.Abs(totalAngularDelta) > 0.0001f)
                    _physics.SetAngularVelocity(uid, body.AngularVelocity + totalAngularDelta, body: body);

                // Second pass: sync all peers to leader's state
                foreach (var (_, peerUid) in linked.PeerGrids)
                {
                    if (!TryComp<TransformComponent>(peerUid, out var peerXform))
                        continue;

                    // Sync local position and rotation (relative to parent map)
                    if (peerXform.LocalPosition != xform.LocalPosition ||
                        peerXform.LocalRotation != xform.LocalRotation)
                    {
                        _transform.SetLocalPositionRotation(peerUid, xform.LocalPosition, xform.LocalRotation, peerXform);
                    }

                    // Sync velocities
                    if (TryComp<PhysicsComponent>(peerUid, out var peerBody))
                    {
                        if (peerBody.LinearVelocity != body.LinearVelocity)
                            _physics.SetLinearVelocity(peerUid, body.LinearVelocity, body: peerBody);

                        if (peerBody.AngularVelocity != body.AngularVelocity)
                            _physics.SetAngularVelocity(peerUid, body.AngularVelocity, body: peerBody);
                    }
                }
            }
        }
        finally
        {
            _gridSyncing = false;
        }
    }
}
