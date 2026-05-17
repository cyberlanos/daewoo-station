using System.Linq;
using Content.Server._Pirate.ZLevels.Core;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Shared._Pirate.ZLevels.Core.Components;
using Content.Shared._Pirate.ZLevels.Shuttles.Components;
using Content.Shared.Maps;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._Pirate.ZLevels.Shuttles;

/// <summary>
/// Generates a temporary "roof" grid one z-level above a shuttle's topmost layer whenever the
/// shuttle is positioned in a z-network that has more levels above it. The roof is a plating-only
/// silhouette of the topmost shuttle grid, linked as a peer in the shuttle's
/// <see cref="CEZLinkedGridComponent"/> so it follows the shuttle through movement and FTL via the
/// existing grid-sync pipeline. The roof is cleaned up on FTL departure and when no level above
/// exists at the arrival point.
/// </summary>
public sealed class CEZShuttleRoofSystem : EntitySystem
{
    [Dependency] private readonly CEZLevelsSystem _zLevels = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    [Dependency] private readonly MapSystem _mapSystem = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDefMan = default!;
    [Dependency] private readonly MetaDataSystem _meta = default!;

    private const string FallbackPlatingTileId = "Plating";

    public override void Initialize()
    {
        base.Initialize();

        // FTL events are subscribed broadcast because ShuttleSystem already owns the per-ShuttleComponent
        // subscription for these and Robust forbids duplicate (component, event) pairs across systems.
        SubscribeLocalEvent<FTLCompletedEvent>(OnFTLCompleted);
        SubscribeLocalEvent<FTLStartedEvent>(OnFTLStarted);
        SubscribeLocalEvent<ShuttleComponent, MapInitEvent>(OnShuttleMapInit);
        SubscribeLocalEvent<ShuttleComponent, EntityTerminatingEvent>(OnShuttleTerminating);
    }

    private void OnFTLCompleted(ref FTLCompletedEvent args)
    {
        if (!HasComp<ShuttleComponent>(args.Entity))
            return;
        EnsureRoof(args.Entity);
    }

    private void OnFTLStarted(ref FTLStartedEvent args)
    {
        if (!HasComp<ShuttleComponent>(args.Entity))
            return;
        // In hyperspace each peer is on its own isolated FTL map, so no upper level exists.
        // Tear the roof down before departure; we will regenerate it at the destination if needed.
        RemoveRoof(args.Entity);
    }

    private void OnShuttleMapInit(Entity<ShuttleComponent> ent, ref MapInitEvent args)
    {
        EnsureRoof(ent);
    }

    private void OnShuttleTerminating(Entity<ShuttleComponent> ent, ref EntityTerminatingEvent args)
    {
        RemoveRoof(ent);
    }

    /// <summary>
    /// Ensures the shuttle has a correctly-placed roof grid above its topmost layer when needed,
    /// creating, repositioning, or removing as appropriate for its current z-network context.
    /// </summary>
    public void EnsureRoof(EntityUid shuttleUid)
    {
        if (!TryFindTopShuttleGrid(shuttleUid, out var topGrid, out var topDepth))
            return;

        var topXform = Transform(topGrid);
        if (topXform.MapUid is not { } topMapUid)
        {
            RemoveRoof(shuttleUid);
            return;
        }

        if (!_zLevels.TryMapOffset(topMapUid, 1, out var aboveMap))
        {
            // No z-level exists above the topmost shuttle layer, so the shuttle is already sealed.
            RemoveRoof(shuttleUid);
            return;
        }

        var aboveMapUid = aboveMap.Value.Owner;
        var roofDepth = topDepth + 1;

        EntityUid roofGrid;
        if (TryFindExistingRoof(shuttleUid, out var existingRoof))
        {
            if (Transform(existingRoof).MapUid != aboveMapUid)
            {
                // The shuttle moved to a new z-network; the old roof is stranded on a different map.
                RemoveRoofGrid(existingRoof);
                roofGrid = CreateRoofGrid(aboveMapUid, topGrid, shuttleUid);
                LinkRoofToShuttle(shuttleUid, roofGrid, roofDepth);
            }
            else
            {
                roofGrid = existingRoof;
            }
        }
        else
        {
            roofGrid = CreateRoofGrid(aboveMapUid, topGrid, shuttleUid);
            LinkRoofToShuttle(shuttleUid, roofGrid, roofDepth);
        }

        SyncRoofTransform(topGrid, roofGrid);
        CopyTiles(topGrid, roofGrid);
    }

    /// <summary>
    /// Removes any roof currently associated with the given shuttle root grid.
    /// </summary>
    public void RemoveRoof(EntityUid shuttleUid)
    {
        if (TryFindExistingRoof(shuttleUid, out var roof))
            RemoveRoofGrid(roof);
    }

    private bool TryFindTopShuttleGrid(EntityUid shuttleUid, out EntityUid topGrid, out int topDepth)
    {
        topGrid = shuttleUid;
        topDepth = 0;

        if (!TryComp<CEZLinkedGridComponent>(shuttleUid, out var linked))
            return true;

        topDepth = linked.Depth;
        foreach (var (depth, peer) in linked.PeerGrids)
        {
            // Skip our own roof so we never treat it as the source for itself.
            if (HasComp<CEZShuttleRoofComponent>(peer))
                continue;

            if (depth > topDepth)
            {
                topDepth = depth;
                topGrid = peer;
            }
        }

        return true;
    }

    private bool TryFindExistingRoof(EntityUid shuttleUid, out EntityUid roof)
    {
        roof = default;
        if (!TryComp<CEZLinkedGridComponent>(shuttleUid, out var linked))
            return false;

        foreach (var (_, peer) in linked.PeerGrids)
        {
            if (HasComp<CEZShuttleRoofComponent>(peer))
            {
                roof = peer;
                return true;
            }
        }

        return false;
    }

    private EntityUid CreateRoofGrid(EntityUid mapUid, EntityUid topShuttleGrid, EntityUid shuttleUid)
    {
        var grid = _mapManager.CreateGridEntity(mapUid);
        var gridUid = grid.Owner;

        var roofComp = AddComp<CEZShuttleRoofComponent>(gridUid);
        roofComp.Shuttle = shuttleUid;
        roofComp.SourceGrid = topShuttleGrid;

        _meta.SetEntityName(gridUid, $"Shuttle Roof ({ToPrettyString(shuttleUid)})");

        return gridUid;
    }

    /// <summary>
    /// Snaps the roof grid onto the topmost shuttle grid's transform so the first frame is aligned
    /// before the regular grid-sync update kicks in.
    /// </summary>
    private void SyncRoofTransform(EntityUid topShuttleGrid, EntityUid roofGrid)
    {
        var topXform = Transform(topShuttleGrid);
        var roofXform = Transform(roofGrid);

        _transform.SetLocalPositionRotation(roofGrid, topXform.LocalPosition, topXform.LocalRotation, roofXform);
    }

    /// <summary>
    /// Mirrors the topmost shuttle grid's tile silhouette onto the roof using subfloor tiles.
    /// Floor tiles fall back to their <see cref="ContentTileDefinition.BaseTurf"/> so the roof
    /// never carries the surface tile placed on top of a plating — only the structural base.
    /// </summary>
    private void CopyTiles(EntityUid topGrid, EntityUid roofGrid)
    {
        if (!TryComp<MapGridComponent>(topGrid, out var topMapGrid) ||
            !TryComp<MapGridComponent>(roofGrid, out var roofMapGrid))
        {
            return;
        }

        var fallback = _tileDefMan[FallbackPlatingTileId];

        var tilesToSet = new List<(Vector2i, Tile)>();
        var sourcePositions = new HashSet<Vector2i>();

        foreach (var tileRef in _mapSystem.GetAllTiles(topGrid, topMapGrid))
        {
            sourcePositions.Add(tileRef.GridIndices);

            var sourceDef = (ContentTileDefinition)_tileDefMan[tileRef.Tile.TypeId];
            ITileDefinition targetDef;

            if (sourceDef.IsSubFloor)
            {
                // Plating / lattice / etc. is the structural base — copy as-is.
                targetDef = sourceDef;
            }
            else if (!string.IsNullOrEmpty(sourceDef.BaseTurf) &&
                     _tileDefMan.TryGetDefinition(sourceDef.BaseTurf, out var baseDef))
            {
                // Floor tiles drop down to the subfloor they sit on.
                targetDef = baseDef;
            }
            else
            {
                targetDef = fallback;
            }

            tilesToSet.Add((tileRef.GridIndices, new Tile(targetDef.TileId)));
        }

        // Clear any roof tiles that no longer exist on the source so resyncs after shuttle damage
        // or construction don't leave orphan plating floating in the air above the shuttle.
        foreach (var existingTile in _mapSystem.GetAllTiles(roofGrid, roofMapGrid))
        {
            if (!sourcePositions.Contains(existingTile.GridIndices))
                tilesToSet.Add((existingTile.GridIndices, Tile.Empty));
        }

        if (tilesToSet.Count > 0)
            _mapSystem.SetTiles(roofGrid, roofMapGrid, tilesToSet);
    }

    /// <summary>
    /// Adds the roof to the shuttle's <see cref="CEZLinkedGridComponent"/> peer graph so the
    /// existing grid-sync pipeline moves it together with the shuttle.
    /// </summary>
    private void LinkRoofToShuttle(EntityUid shuttleUid, EntityUid roofUid, int roofDepth)
    {
        var shuttleLinked = EnsureComp<CEZLinkedGridComponent>(shuttleUid);

        var fullGraph = new Dictionary<int, EntityUid>(shuttleLinked.PeerGrids)
        {
            [shuttleLinked.Depth] = shuttleUid,
        };

        if (fullGraph.ContainsKey(roofDepth))
        {
            Log.Error($"Cannot link shuttle roof {ToPrettyString(roofUid)} for {ToPrettyString(shuttleUid)} at depth {roofDepth}: a peer already occupies that depth.");
            QueueDel(roofUid);
            return;
        }

        fullGraph[roofDepth] = roofUid;

        foreach (var (depth, gridUid) in fullGraph)
        {
            var comp = EnsureComp<CEZLinkedGridComponent>(gridUid);
            comp.Depth = depth;
            comp.ZNetwork = shuttleLinked.ZNetwork;
            comp.PeerGrids = new Dictionary<int, EntityUid>(fullGraph);
            comp.PeerGrids.Remove(depth);
            Dirty(gridUid, comp);
        }
    }

    private void RemoveRoofGrid(EntityUid roofUid)
    {
        // Pre-emptively drop the roof from peers and dirty them; CEZLinkedGridComponent's own
        // ComponentRemove handler does the same removal but doesn't dirty, which would let
        // clients see a stale peer entry pointing at a deleted grid.
        if (TryComp<CEZLinkedGridComponent>(roofUid, out var roofLinked))
        {
            var roofDepth = roofLinked.Depth;
            foreach (var (_, peer) in roofLinked.PeerGrids.ToArray())
            {
                if (TryComp<CEZLinkedGridComponent>(peer, out var peerLinked) &&
                    peerLinked.PeerGrids.Remove(roofDepth))
                {
                    Dirty(peer, peerLinked);
                }
            }

            roofLinked.PeerGrids.Clear();
            Dirty(roofUid, roofLinked);
        }

        QueueDel(roofUid);
    }
}
