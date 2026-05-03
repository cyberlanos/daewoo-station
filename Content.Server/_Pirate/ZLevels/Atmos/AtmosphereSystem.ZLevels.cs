using Content.Server.Atmos.Components;
using Content.Shared._Pirate.ZLevels.Core.Components;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Maps;
using Robust.Shared.Map.Components;
using System.Numerics;

namespace Content.Server.Atmos.EntitySystems;

public sealed partial class AtmosphereSystem
{
    private void PirateInitializeZAtmos()
    {
        SubscribeLocalEvent<CEZLinkedGridComponent, ComponentStartup>(PirateOnZLinkedGridStartup);
    }

    private void PirateOnZLinkedGridStartup(Entity<CEZLinkedGridComponent> ent, ref ComponentStartup args)
    {
        PirateInvalidateZAtmosOpenings(ent.Owner);
    }

    private bool PirateShouldTryZLevelProtectedMixture(Entity<TransformComponent?> entity, EntityUid? gridUid, Vector2i gridTile)
    {
        if (gridUid == null || !gridUid.Value.IsValid())
            return true;

        if (TryComp<CEZPhysicsComponent>(entity.Owner, out var zPhysics) &&
            TryComp<CEZLinkedGridComponent>(gridUid.Value, out var linked) &&
            linked.Depth != zPhysics.CurrentZLevel)
        {
            return true;
        }

        return !TryComp<GridAtmosphereComponent>(gridUid.Value, out var atmos) ||
               !atmos.Tiles.ContainsKey(gridTile);
    }

    private bool PirateTryGetZLevelProtectedTileMixtureForEntity(
        Entity<TransformComponent?> entity,
        bool excite,
        out GasMixture? mixture)
    {
        mixture = null;

        if (!Resolve(entity.Owner, ref entity.Comp))
            return false;

        var worldPos = _transformSystem.GetWorldPosition(entity.Comp);
        var checkedGrids = new HashSet<EntityUid>();
        var targetDepth = TryComp<CEZPhysicsComponent>(entity.Owner, out var zPhysics)
            ? zPhysics.CurrentZLevel
            : (int?) null;
        var bestDepth = int.MinValue;
        EntityUid bestGridUid = EntityUid.Invalid;
        GridAtmosphereComponent? bestAtmos = null;
        GasTileOverlayComponent? bestOverlay = null;
        MapGridComponent? bestGrid = null;
        TransformComponent? bestXform = null;
        Vector2i bestTile = default;

        if (entity.Comp.GridUid is { } gridUid &&
            checkedGrids.Add(gridUid))
        {
            PirateTrySelectZLevelProtectedTileMixtureCandidate(
                gridUid,
                worldPos,
                targetDepth,
                ref bestDepth,
                ref bestGridUid,
                ref bestAtmos,
                ref bestOverlay,
                ref bestGrid,
                ref bestXform,
                ref bestTile);
        }

        foreach (var grid in _mapManager.GetAllGrids(entity.Comp.MapID))
        {
            if (!checkedGrids.Add(grid.Owner))
                continue;

            PirateTrySelectZLevelProtectedTileMixtureCandidate(
                grid.Owner,
                worldPos,
                targetDepth,
                ref bestDepth,
                ref bestGridUid,
                ref bestAtmos,
                ref bestOverlay,
                ref bestGrid,
                ref bestXform,
                ref bestTile);
        }

        if (!bestGridUid.IsValid() ||
            bestAtmos == null ||
            bestOverlay == null ||
            bestGrid == null ||
            bestXform == null)
        {
            return false;
        }

        var ent = new Entity<GridAtmosphereComponent, GasTileOverlayComponent, MapGridComponent, TransformComponent>(
            bestGridUid,
            bestAtmos,
            bestOverlay,
            bestGrid,
            bestXform);

        var tile = GetOrNewTile(bestGridUid, bestAtmos, bestTile);
        PirateEnsureZLevelProtectedTileAir(ent, tile, GetVolumeForTiles(bestGrid));

        if (excite)
        {
            AddActiveTile(bestAtmos, tile);
            InvalidateVisuals((bestGridUid, bestOverlay), bestTile);
        }

        mixture = tile.Air;
        return true;
    }

    private void PirateTrySelectZLevelProtectedTileMixtureCandidate(
        EntityUid gridUid,
        Vector2 worldPos,
        int? targetDepth,
        ref int bestDepth,
        ref EntityUid bestGridUid,
        ref GridAtmosphereComponent? bestAtmos,
        ref GasTileOverlayComponent? bestOverlay,
        ref MapGridComponent? bestGrid,
        ref TransformComponent? bestXform,
        ref Vector2i bestTile)
    {
        if (!TryComp<CEZLinkedGridComponent>(gridUid, out var linked) ||
            targetDepth is { } depth && linked.Depth != depth ||
            targetDepth == null && linked.Depth <= bestDepth ||
            !TryComp<GridAtmosphereComponent>(gridUid, out var atmos) ||
            !TryComp<GasTileOverlayComponent>(gridUid, out var overlay) ||
            !TryComp<MapGridComponent>(gridUid, out var grid) ||
            !TryComp<TransformComponent>(gridUid, out var xform))
        {
            return;
        }

        var gridTile = _mapSystem.WorldToTile(gridUid, grid, worldPos);
        var ent = new Entity<GridAtmosphereComponent, GasTileOverlayComponent, MapGridComponent, TransformComponent>(
            gridUid,
            atmos,
            overlay,
            grid,
            xform);

        if (!PirateHasZLevelAtmosProtection(ent, gridTile))
            return;

        bestDepth = linked.Depth;
        bestGridUid = gridUid;
        bestAtmos = atmos;
        bestOverlay = overlay;
        bestGrid = grid;
        bestXform = xform;
        bestTile = gridTile;
    }

    private bool PirateHasZLevelTileBelow(
        Entity<GridAtmosphereComponent, GasTileOverlayComponent, MapGridComponent, TransformComponent> ent,
        Vector2i gridTile)
    {
        return PirateHasZLevelTileBelow(ent.Owner, ent.Comp3, gridTile);
    }

    private bool PirateHasZLevelTileBelow(EntityUid gridUid, MapGridComponent grid, Vector2i gridTile)
    {
        return PirateTryGetZLevelTileBelow(gridUid, grid, gridTile, out _, out _, out _);
    }

    private bool PirateTryGetZLevelTileBelow(
        EntityUid gridUid,
        MapGridComponent grid,
        Vector2i gridTile,
        out EntityUid belowGridUid,
        out MapGridComponent belowGrid,
        out Vector2i belowTile)
    {
        belowGridUid = EntityUid.Invalid;
        belowGrid = default!;
        belowTile = default;

        if (!TryComp<CEZLinkedGridComponent>(gridUid, out var linked) ||
            !linked.PeerGrids.TryGetValue(linked.Depth - 1, out belowGridUid) ||
            !TryComp<MapGridComponent>(belowGridUid, out var foundBelowGrid))
        {
            return false;
        }

        belowGrid = foundBelowGrid;
        var worldPos = _mapSystem.GridTileToWorldPos(gridUid, grid, gridTile);
        belowTile = _mapSystem.WorldToTile(belowGridUid, belowGrid, worldPos);

        return _mapSystem.TryGetTile(belowGrid, belowTile, out var tile) && !tile.IsEmpty;
    }

    private bool PirateTryUpdateZLevelProtectedTileAir(
        Entity<GridAtmosphereComponent, GasTileOverlayComponent, MapGridComponent, TransformComponent> ent,
        TileAtmosphere tile,
        float volume)
    {
        if (!PirateHasZLevelAtmosProtection(ent, tile.GridIndices))
            return false;

        PirateEnsureZLevelProtectedTileAir(ent, tile, volume);
        return true;
    }

    private void PirateEnsureZLevelProtectedTileAir(
        Entity<GridAtmosphereComponent, GasTileOverlayComponent, MapGridComponent, TransformComponent> ent,
        TileAtmosphere tile,
        float volume)
    {
        if (tile.Air is { TotalMoles: > Atmospherics.GasMinMoles })
            return;

        tile.Air ??= new GasMixture(volume) { Temperature = Atmospherics.T20C };
        tile.Air.Clear();
        tile.Air.Temperature = Atmospherics.T20C;
        tile.AirArchived = null;
        tile.ArchivedCycle = 0;
        tile.LastShare = 0f;
        tile.Hotspot = new Hotspot();

        GridFixTileVacuum(tile);
        NotifyDeviceTileChanged((ent.Owner, ent.Comp1, ent.Comp3), tile.GridIndices);
    }

    private bool PirateHasZLevelAtmosProtection(
        Entity<GridAtmosphereComponent, GasTileOverlayComponent, MapGridComponent, TransformComponent> ent,
        Vector2i gridTile)
    {
        var hasGridTile = _map.TryGetTile(ent.Comp3, gridTile, out var gridTileRef) && !gridTileRef.IsEmpty;
        var isCandidate = !hasGridTile;

        if (hasGridTile)
        {
            var contentDef = (ContentTileDefinition) _tileDefinitionManager[gridTileRef.TypeId];
            isCandidate = contentDef.MapAtmosphere;
        }

        return isCandidate && PirateHasZLevelTileBelow(ent.Owner, ent.Comp3, gridTile);
    }

    private void PirateInvalidateZAtmosOpenings(EntityUid gridUid)
    {
        if (!TryComp<MapGridComponent>(gridUid, out var grid) ||
            !TryComp<GridAtmosphereComponent>(gridUid, out var atmos))
        {
            return;
        }

        var enumerator = _mapSystem.GetAllTilesEnumerator(gridUid, grid);
        while (enumerator.MoveNext(out var tileRef))
        {
            PirateInvalidateZAtmosTileAndNeighbors(atmos, tileRef.Value.GridIndices);
        }
    }

    private void PirateInvalidateZAtmosPeers(Entity<MapGridComponent> changedGrid, Vector2i changedTile)
    {
        if (!TryComp<CEZLinkedGridComponent>(changedGrid.Owner, out var linked) ||
            !linked.PeerGrids.TryGetValue(linked.Depth + 1, out var aboveGridUid) ||
            !TryComp<MapGridComponent>(aboveGridUid, out var aboveGrid) ||
            !TryComp<GridAtmosphereComponent>(aboveGridUid, out var aboveAtmos))
        {
            return;
        }

        var worldPos = _mapSystem.GridTileToWorldPos(changedGrid.Owner, changedGrid.Comp, changedTile);
        var aboveTile = _mapSystem.WorldToTile(aboveGridUid, aboveGrid, worldPos);

        PirateInvalidateZAtmosTileAndNeighbors(aboveAtmos, aboveTile);
    }

    private void PirateInvalidateZAtmosTileAndNeighbors(GridAtmosphereComponent atmos, Vector2i tile)
    {
        atmos.InvalidatedCoords.Add(tile);

        for (var i = 0; i < Atmospherics.Directions; i++)
        {
            var direction = (AtmosDirection) (1 << i);
            atmos.InvalidatedCoords.Add(tile.Offset(direction));
        }
    }
}
