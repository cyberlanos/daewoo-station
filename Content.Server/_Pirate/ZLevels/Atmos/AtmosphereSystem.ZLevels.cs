using Content.Server.Atmos.Components;
using Content.Shared._Pirate.ZLevels.Core.Components;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Robust.Shared.Map.Components;

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

    private bool PirateHasZLevelTileBelow(
        Entity<GridAtmosphereComponent, GasTileOverlayComponent, MapGridComponent, TransformComponent> ent,
        Vector2i gridTile)
    {
        if (!TryComp<CEZLinkedGridComponent>(ent.Owner, out var linked) ||
            !linked.PeerGrids.TryGetValue(linked.Depth - 1, out var belowGridUid) ||
            !TryComp<MapGridComponent>(belowGridUid, out var belowGrid))
        {
            return false;
        }

        var worldPos = _mapSystem.GridTileToWorldPos(ent.Owner, ent.Comp3, gridTile);
        var belowTile = _mapSystem.WorldToTile(belowGridUid, belowGrid, worldPos);

        return _mapSystem.TryGetTile(belowGrid, belowTile, out var tile) && !tile.IsEmpty;
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
