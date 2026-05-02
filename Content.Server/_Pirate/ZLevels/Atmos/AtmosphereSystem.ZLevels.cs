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
    private readonly HashSet<(EntityUid Grid, Vector2i Tile, string Key)> _pirateZAtmosLogged = new();

    private void PirateInitializeZAtmos()
    {
        SubscribeLocalEvent<CEZLinkedGridComponent, ComponentStartup>(PirateOnZLinkedGridStartup);
    }

    private void PirateOnZLinkedGridStartup(Entity<CEZLinkedGridComponent> ent, ref ComponentStartup args)
    {
        PirateInvalidateZAtmosOpenings(ent.Owner);
    }

    private bool PirateShouldTryZLevelProtectedMixture(EntityUid? gridUid, Vector2i gridTile)
    {
        if (gridUid == null || !gridUid.Value.IsValid())
            return true;

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

        if (entity.Comp.GridUid is { } gridUid &&
            checkedGrids.Add(gridUid) &&
            PirateTryGetZLevelProtectedTileMixtureFromGrid(entity.Owner, gridUid, worldPos, excite, out mixture))
        {
            return true;
        }

        foreach (var grid in _mapManager.GetAllGrids(entity.Comp.MapID))
        {
            if (!checkedGrids.Add(grid.Owner))
                continue;

            if (PirateTryGetZLevelProtectedTileMixtureFromGrid(entity.Owner, grid.Owner, worldPos, excite, out mixture))
                return true;
        }

        return false;
    }

    private bool PirateTryGetZLevelProtectedTileMixtureFromGrid(
        EntityUid source,
        EntityUid gridUid,
        Vector2 worldPos,
        bool excite,
        out GasMixture? mixture)
    {
        mixture = null;

        if (!TryComp<GridAtmosphereComponent>(gridUid, out var atmos) ||
            !TryComp<GasTileOverlayComponent>(gridUid, out var overlay) ||
            !TryComp<MapGridComponent>(gridUid, out var grid) ||
            !TryComp<TransformComponent>(gridUid, out var xform))
        {
            return false;
        }

        var gridTile = _mapSystem.WorldToTile(gridUid, grid, worldPos);
        var ent = new Entity<GridAtmosphereComponent, GasTileOverlayComponent, MapGridComponent, TransformComponent>(
            gridUid,
            atmos,
            overlay,
            grid,
            xform);

        if (!PirateTryGetZLevelAtmosProtection(ent, gridTile, out var protection, out var belowGridUid, out var belowTile))
            return false;

        var tile = GetOrNewTile(gridUid, atmos, gridTile);
        PirateTryUpdateZLevelProtectedTileAir(ent, tile, GetVolumeForTiles(grid));

        if (excite)
        {
            AddActiveTile(atmos, tile);
            InvalidateVisuals((gridUid, overlay), gridTile);
        }

        mixture = tile.Air;
        PirateLogZAtmosOnce(gridUid, gridTile, $"entity-query-{protection}",
            $"[Pirate z-atmos] Entity query for {ToPrettyString(source)} resolved protected {protection} {gridTile} on {ToPrettyString(gridUid)}; " +
            $"below={ToPrettyString(belowGridUid)} tile={belowTile}; air={PirateDescribeZAtmosAirForLog(atmos, gridTile)}");
        return true;
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
        if (!PirateTryGetZLevelAtmosProtection(ent, tile.GridIndices, out var protection, out var belowGridUid, out var belowTile))
            return false;

        if (tile.Air is { TotalMoles: > Atmospherics.GasMinMoles })
        {
            PirateLogZAtmosOnce(ent.Owner, tile.GridIndices, $"has-air-{protection}",
                $"[Pirate z-atmos] Protected {protection} {tile.GridIndices} on {ToPrettyString(ent.Owner)} already has gas: " +
                $"moles={tile.Air.TotalMoles:0.#####}, pressure={tile.Air.Pressure:0.###}, temp={tile.Air.Temperature:0.###}; " +
                $"below={ToPrettyString(belowGridUid)} tile={belowTile}; adj=[{PirateDescribeZAtmosAdjacent(tile)}]");
            return true;
        }

        var beforeMoles = tile.Air?.TotalMoles;
        var beforePressure = tile.Air?.Pressure;
        var beforeTemp = tile.Air?.Temperature;
        var beforeAdj = PirateDescribeZAtmosAdjacent(tile);

        tile.Air ??= new GasMixture(volume) { Temperature = Atmospherics.T20C };
        tile.Air.Clear();
        tile.Air.Temperature = Atmospherics.T20C;
        tile.AirArchived = null;
        tile.ArchivedCycle = 0;
        tile.LastShare = 0f;
        tile.Hotspot = new Hotspot();

        GridFixTileVacuum(tile);

        PirateLogZAtmosOnce(ent.Owner, tile.GridIndices, $"seed-{protection}",
            $"[Pirate z-atmos] Seeded protected {protection} {tile.GridIndices} on {ToPrettyString(ent.Owner)}; " +
            $"below={ToPrettyString(belowGridUid)} tile={belowTile}; " +
            $"before=moles:{PirateFormatNullable(beforeMoles)} pressure:{PirateFormatNullable(beforePressure)} temp:{PirateFormatNullable(beforeTemp)}; " +
            $"after=moles:{tile.Air.TotalMoles:0.#####} pressure:{tile.Air.Pressure:0.###} temp:{tile.Air.Temperature:0.###}; " +
            $"adjBefore=[{beforeAdj}]; adjAfter=[{PirateDescribeZAtmosAdjacent(tile)}]");

        NotifyDeviceTileChanged((ent.Owner, ent.Comp1, ent.Comp3), tile.GridIndices);
        return true;
    }

    private bool PirateTryGetZLevelAtmosProtection(
        Entity<GridAtmosphereComponent, GasTileOverlayComponent, MapGridComponent, TransformComponent> ent,
        Vector2i gridTile,
        out string protection,
        out EntityUid belowGridUid,
        out Vector2i belowTile)
    {
        protection = string.Empty;
        belowGridUid = EntityUid.Invalid;
        belowTile = default;

        var hasGridTile = _map.TryGetTile(ent.Comp3, gridTile, out var gridTileRef) && !gridTileRef.IsEmpty;
        var isCandidate = !hasGridTile;

        if (hasGridTile)
        {
            var contentDef = (ContentTileDefinition) _tileDefinitionManager[gridTileRef.TypeId];
            isCandidate = contentDef.MapAtmosphere;
            protection = $"map-atmos tile type={gridTileRef.TypeId}";
        }
        else
        {
            protection = "empty tile";
        }

        if (!isCandidate)
            return false;

        if (PirateTryGetZLevelTileBelow(ent.Owner, ent.Comp3, gridTile, out belowGridUid, out _, out belowTile))
            return true;

        PirateLogZAtmosOnce(ent.Owner, gridTile, $"no-lower-{protection}",
            $"[Pirate z-atmos] Candidate {protection} {gridTile} on {ToPrettyString(ent.Owner)} has no lower z tile; " +
            $"noGrid={!hasGridTile}; currentAir={PirateDescribeZAtmosAirForLog(ent.Comp1, gridTile)}");
        return false;
    }

    private string PirateDescribeZAtmosAdjacent(TileAtmosphere tile)
    {
        Span<AtmosDirection> directions =
        [
            AtmosDirection.North,
            AtmosDirection.South,
            AtmosDirection.East,
            AtmosDirection.West
        ];

        var parts = new string[Atmospherics.Directions];
        for (var i = 0; i < Atmospherics.Directions; i++)
        {
            var adj = tile.AdjacentTiles[i];
            parts[i] = adj == null
                ? $"{directions[i]}=null"
                : $"{directions[i]}=moles:{PirateFormatNullable(adj.Air?.TotalMoles)} pressure:{PirateFormatNullable(adj.Air?.Pressure)} noGrid:{adj.NoGridTile} map:{adj.MapAtmosphere}";
        }

        return string.Join("; ", parts);
    }

    private string PirateDescribeZAtmosAirForLog(GridAtmosphereComponent atmos, Vector2i tile)
    {
        if (!atmos.Tiles.TryGetValue(tile, out var atmosTile))
            return "missing-atmos-tile";

        return atmosTile.Air == null
            ? "null"
            : $"moles:{atmosTile.Air.TotalMoles:0.#####} pressure:{atmosTile.Air.Pressure:0.###} temp:{atmosTile.Air.Temperature:0.###}";
    }

    private static string PirateFormatNullable(float? value)
    {
        return value == null ? "null" : $"{value.Value:0.#####}";
    }

    private void PirateLogZAtmosOnce(EntityUid gridUid, Vector2i tile, string key, string message)
    {
        if (_pirateZAtmosLogged.Add((gridUid, tile, key)))
            Log.Info(message);
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
