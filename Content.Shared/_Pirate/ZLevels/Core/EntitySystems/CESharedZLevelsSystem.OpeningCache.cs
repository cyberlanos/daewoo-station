using System.Numerics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Map.Events;

namespace Content.Shared._Pirate.ZLevels.Core.EntitySystems;

/// <summary>
/// Public surface over <see cref="CEZLevelOpeningCache"/>. Consumers (audio, voice, probe-eyes)
/// call <see cref="TryFindOpeningNear"/> and <see cref="HasOpeningInTileBounds"/> instead of
/// scanning tiles manually. The cache invalidates per-chunk via
/// <see cref="InvalidateOpeningCache(Entity{MapGridComponent}, ReadOnlySpan{TileChangedEntry})"/>
/// (called from the existing <c>OnTileChanged</c> handler) and on grid removal.
/// </summary>
public abstract partial class CESharedZLevelsSystem
{
    private void InitOpeningCache()
    {
        // Grid removal: prune the cache entry so we don't pin EntityUids that will never resolve.
        SubscribeLocalEvent<MapGridComponent, GridRemovalEvent>(OnGridRemovalForCache);
    }

    private void ShutdownOpeningCache()
    {
        _openingCache.Clear();
    }

    private void OnGridRemovalForCache(EntityUid uid, MapGridComponent comp, GridRemovalEvent args)
    {
        _openingCache.RemoveGrid(uid);
    }

    /// <summary>Invalidates the changed chunks for a grid.</summary>
    protected void InvalidateOpeningCache(Entity<MapGridComponent> grid, ReadOnlySpan<TileChangedEntry> changes)
    {
        _openingCache.InvalidateTiles(grid, changes);
    }

    /// <summary>True if the given map's primary grid (planet-style) has an opening tile at <paramref name="worldPos"/>.</summary>
    public bool IsOpeningAt(EntityUid mapUid, Vector2 worldPos)
    {
        if (!_gridQuery.TryComp(mapUid, out var grid))
            return true;

        return CEZLevelOpeningCache.IsOpeningTile(mapUid, grid, worldPos, _map, TilDefMan);
    }

    /// <summary>
    /// Returns true if at least one opening tile exists within <paramref name="radius"/> of
    /// <paramref name="position"/> on <paramref name="mapUid"/>'s grids; the nearest opening
    /// center is written to <paramref name="openingPosition"/>.
    /// </summary>
    public bool TryFindOpeningNear(EntityUid mapUid, Vector2 position, float radius, out Vector2 openingPosition)
    {
        openingPosition = default;

        // No grid on the map -> the map itself is one continuous opening (e.g. space).
        if (!_gridQuery.TryComp(mapUid, out _))
        {
            openingPosition = position;
            return true;
        }

        if (!_mapQuery.TryComp(mapUid, out var mapComp))
            return false;

        return _openingCache.TryFindNearestOpeningCenterNear(
            mapComp.MapId,
            position,
            radius,
            out openingPosition,
            _openingGridScratch,
            _mapMan,
            _map,
            _transform,
            TilDefMan,
            edgeOnly: false);
    }

    /// <summary>
    /// Cheap "is there any opening at all?" probe for a tile-bounded region on a specific grid.
    /// Used by probe-eye gating to decide whether to subscribe to lower Z layers.
    /// </summary>
    public bool HasOpeningInTileBounds(Entity<MapGridComponent> grid, Vector2i start, Vector2i end)
    {
        return _openingCache.HasOpeningInTileBounds(grid, start, end, _map, TilDefMan);
    }

    /// <summary>
    /// Like <see cref="TryFindOpeningNear"/> but only counts holes that are *inside an existing
    /// grid tile* — off-grid space (deck edge, vacuum) does NOT count as an opening. Used by
    /// cross-Z audio so a sound source near the hull edge doesn't leak through solid floor as if
    /// the deck-edge gap were a hole. Direct scan rather than cache-backed because the typical
    /// audio radius (~1.5 tiles) is far smaller than the cache chunk size (8 tiles) — chunk-level
    /// lookup would be more work, not less.
    /// </summary>
    public bool TryFindRealOpeningNear(EntityUid mapUid, Vector2 position, float radius, out Vector2 openingPosition)
    {
        openingPosition = default;

        if (!_mapQuery.TryComp(mapUid, out var mapComp))
            return false;

        var bounds = Box2.CenteredAround(position, new Vector2(radius * 2f, radius * 2f));
        _openingGridScratch.Clear();
        var grids = _openingGridScratch;
        _mapMan.FindGridsIntersecting(mapComp.MapId, bounds, ref grids, approx: true, includeMap: true);

        if (grids.Count == 0)
            return false;

        var radiusSq = radius * radius;
        var bestDistSq = float.PositiveInfinity;
        var found = false;
        var tileRadius = (int) MathF.Ceiling(radius) + 1;

        foreach (var grid in grids)
        {
            var gridWorld = _transform.GetWorldMatrix(grid.Owner);
            if (!Matrix3x2.Invert(gridWorld, out var gridInv))
                continue;

            var local = Vector2.Transform(position, gridInv);
            var centerTile = new Vector2i((int) MathF.Floor(local.X), (int) MathF.Floor(local.Y));

            for (var dx = -tileRadius; dx <= tileRadius; dx++)
            {
                for (var dy = -tileRadius; dy <= tileRadius; dy++)
                {
                    var tile = centerTile + new Vector2i(dx, dy);
                    if (!CEZLevelOpeningCache.IsExistingOpeningTile(grid, tile, _map, TilDefMan))
                        continue;

                    var tileCenter = Vector2.Transform(
                        new Vector2(tile.X + 0.5f, tile.Y + 0.5f),
                        gridWorld);
                    var distSq = Vector2.DistanceSquared(position, tileCenter);
                    if (distSq > radiusSq || distSq >= bestDistSq)
                        continue;

                    bestDistSq = distSq;
                    openingPosition = tileCenter;
                    found = true;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Enumerates opening tile CENTERS within <paramref name="radius"/> of <paramref name="sourcePosition"/>
    /// on <paramref name="mapId"/>'s grids into <paramref name="openings"/>. Caller-owned
    /// <paramref name="gridScratch"/> is cleared and reused. Used by projected-lighting to find
    /// every hole a source light's radius could spill through.
    /// </summary>
    public void FindOpeningCentersNear(
        MapId mapId,
        Vector2 sourcePosition,
        float radius,
        List<(Vector2 Center, float Distance)> openings,
        List<Entity<MapGridComponent>> gridScratch,
        bool edgeOnly = true)
    {
        _openingCache.FindOpeningCentersNear(
            mapId,
            sourcePosition,
            radius,
            openings,
            gridScratch,
            _mapMan,
            _map,
            _transform,
            TilDefMan,
            edgeOnly);
    }

    /// <summary>
    /// DDA-raycasts a tile-grid line from <paramref name="from"/> to <paramref name="to"/> on
    /// the floor between <paramref name="sourceMap"/> and <paramref name="targetMap"/> (depending
    /// on <paramref name="offset"/> sign) and returns the center of the first opening tile hit.
    /// Used by cross-Z shooting: the projectile spawns at the chosen opening's center.
    /// </summary>
    /// <param name="preferOpeningAwayFromSource">If true, skip an opening centered exactly on the
    /// shooter's tile (so the shot goes through a hole the shooter is NEXT to, not standing in);
    /// keep that as a fallback only if no other opening on the line works.</param>
    /// <param name="maxSourceDistanceFromOpeningEdgeTiles">Reject openings whose center is farther
    /// than this (in tiles) from the source — keeps cross-Z shots from teleporting across long lines.</param>
    public bool TryFindZShotOpening(
        EntityUid sourceMap,
        EntityUid targetMap,
        int offset,
        Vector2 from,
        Vector2 to,
        out EntityCoordinates opening,
        bool preferOpeningAwayFromSource = false,
        float maxSourceDistanceFromOpeningEdgeTiles = float.PositiveInfinity,
        bool debug = false)
    {
        opening = default;
        if (offset == 0)
            return false;

        // For DOWN, the floor we're piercing belongs to the source (above) map; for UP it
        // belongs to the target (above) map. Either way, the higher of the two carries the hole.
        var openingMapUid = offset < 0 ? sourceMap : targetMap;

        // Map entity itself may be planet-style (has MapGridComponent) or multi-grid (only a
        // MapComponent container; grids are children). Resolve via MapId so both work.
        if (!_mapQuery.TryComp(openingMapUid, out var openingMapComp))
            return false;

        // Find the grid at the shooter's world XY on the opening map. If nothing's there, the
        // column is unobstructed — no floor/ceiling to gate the shot. Returned coord is anchored
        // to the map entity (no grid), which still maps to the same world XY.
        if (!_mapMan.TryFindGridAt(openingMapComp.MapId, from, out var gridUidValue, out var grid))
        {
            if (debug) Log.Info($"[crossz-shoot/dda] no grid at from={from} on mapId={openingMapComp.MapId} → open column");
            opening = new EntityCoordinates(openingMapUid, from);
            return true;
        }

        var openingGrid = gridUidValue;
        // All math below stays in GRID-LOCAL space so the result is grid-anchored — moving
        // shuttles don't invalidate the returned coordinate. Invert the world matrix once to
        // bring `from` into local space for the distance comparison.
        var gridWorldMatrix = _transform.GetWorldMatrix(openingGrid);
        Matrix3x2.Invert(gridWorldMatrix, out var gridInvMatrix);
        var localFromForCmp = Vector2.Transform(from, gridInvMatrix);
        if (debug) Log.Info($"[crossz-shoot/dda] resolved grid={ToPrettyString(openingGrid)} tileSize={grid.TileSize} localFromForCmp={localFromForCmp}");

        var sourceTile = preferOpeningAwayFromSource
            ? _map.WorldToTile(openingGrid, grid, from)
            : default;
        var fallbackOpeningLocal = Vector2.Zero;
        var hasFallbackOpening = false;
        var maxSourceDistanceFromOpeningCenter = float.IsPositiveInfinity(maxSourceDistanceFromOpeningEdgeTiles)
            ? float.PositiveInfinity
            : grid.TileSize * (0.5f + Math.Max(0f, maxSourceDistanceFromOpeningEdgeTiles));
        var maxSourceDistanceSquared = maxSourceDistanceFromOpeningCenter * maxSourceDistanceFromOpeningCenter;
        var selectedOpeningLocal = Vector2.Zero;

        bool TryUseOpeningTile(Vector2i tile)
        {
            var hasTileRef = _map.TryGetTileRef(openingGrid, grid, tile, out var tileRef);
            var tileIsOpening = !hasTileRef || CEZLevelOpeningCache.IsOpeningTile(tileRef.Tile, TilDefMan);

            if (hasTileRef && !tileIsOpening)
            {
                if (debug) Log.Info($"[crossz-shoot/dda]   tile={tile} hasTile=true id={tileRef.Tile.TypeId} → solid, reject");
                return false;
            }

            // Local-to-grid: ToCenterCoordinates returns EntityCoordinates rooted at the grid;
            // its .Position is grid-local, which is exactly the frame we want to stay in.
            var openingCenterLocal = _map.ToCenterCoordinates(openingGrid, tile, grid).Position;
            var dist2 = Vector2.DistanceSquared(localFromForCmp, openingCenterLocal);
            if (dist2 > maxSourceDistanceSquared)
            {
                if (debug) Log.Info($"[crossz-shoot/dda]   tile={tile} localCenter={openingCenterLocal} dist={MathF.Sqrt(dist2):F2} > {MathF.Sqrt(maxSourceDistanceSquared):F2} → too far, reject");
                return false;
            }

            if (preferOpeningAwayFromSource &&
                tile == sourceTile)
            {
                if (!hasFallbackOpening)
                {
                    fallbackOpeningLocal = openingCenterLocal;
                    hasFallbackOpening = true;
                }

                if (debug) Log.Info($"[crossz-shoot/dda]   tile={tile} localCenter={openingCenterLocal} → shooter's own tile, set as fallback");
                return false;
            }

            if (debug) Log.Info($"[crossz-shoot/dda]   tile={tile} localCenter={openingCenterLocal} hasTile={hasTileRef} → ACCEPT");
            selectedOpeningLocal = openingCenterLocal;
            return true;
        }

        var localFrom = _map.WorldToLocal(openingGrid, grid, from) / grid.TileSize;
        var localTo = _map.WorldToLocal(openingGrid, grid, to) / grid.TileSize;
        var localDelta = localTo - localFrom;
        var currentTile = new Vector2i((int) MathF.Floor(localFrom.X), (int) MathF.Floor(localFrom.Y));
        var endTile = new Vector2i((int) MathF.Floor(localTo.X), (int) MathF.Floor(localTo.Y));
        if (debug) Log.Info($"[crossz-shoot/dda] localFrom={localFrom} localTo={localTo} startTile={currentTile} endTile={endTile} sourceTile={sourceTile} preferAway={preferOpeningAwayFromSource} maxDist={MathF.Sqrt(maxSourceDistanceSquared):F2}");

        var stepX = Math.Sign(localDelta.X);
        var stepY = Math.Sign(localDelta.Y);
        var tDeltaX = stepX == 0 ? float.PositiveInfinity : MathF.Abs(1f / localDelta.X);
        var tDeltaY = stepY == 0 ? float.PositiveInfinity : MathF.Abs(1f / localDelta.Y);
        var nextBoundaryX = stepX > 0 ? currentTile.X + 1f : currentTile.X;
        var nextBoundaryY = stepY > 0 ? currentTile.Y + 1f : currentTile.Y;
        var tMaxX = stepX == 0 ? float.PositiveInfinity : (nextBoundaryX - localFrom.X) / localDelta.X;
        var tMaxY = stepY == 0 ? float.PositiveInfinity : (nextBoundaryY - localFrom.Y) / localDelta.Y;

        while (true)
        {
            if (TryUseOpeningTile(currentTile))
            {
                opening = new EntityCoordinates(openingGrid, selectedOpeningLocal);
                return true;
            }

            if (currentTile == endTile)
                break;

            if (tMaxX < tMaxY)
            {
                currentTile += new Vector2i(stepX, 0);
                tMaxX += tDeltaX;
            }
            else if (tMaxY < tMaxX)
            {
                currentTile += new Vector2i(0, stepY);
                tMaxY += tDeltaY;
            }
            else
            {
                currentTile += new Vector2i(stepX, stepY);
                tMaxX += tDeltaX;
                tMaxY += tDeltaY;
            }
        }

        if (hasFallbackOpening)
        {
            if (debug) Log.Info($"[crossz-shoot/dda] no line opening; using shooter's-tile fallback at local={fallbackOpeningLocal}");
            opening = new EntityCoordinates(openingGrid, fallbackOpeningLocal);
            return true;
        }

        if (debug) Log.Info($"[crossz-shoot/dda] no opening found and no fallback");
        return false;
    }
}
