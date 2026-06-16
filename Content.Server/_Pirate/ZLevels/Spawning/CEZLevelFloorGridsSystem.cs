using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared._Pirate.ZLevels.Core.Components;
using Content.Shared.Station.Components;
using Robust.Shared.Map.Components;
using Robust.Shared.Random;

namespace Content.Server._Pirate.ZLevels.Spawning;

/// <summary>
/// Resolves the station floor grids used by multiz spawners.
/// </summary>
public sealed class CEZLevelFloorGridsSystem : EntitySystem
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly StationSystem _station = default!;

    /// <summary>
    /// Returns the station's main grid plus linked z-peer grids.
    /// </summary>
    public List<EntityUid> GetStationFloorGrids(EntityUid station)
    {
        if (!TryComp<StationDataComponent>(station, out var data))
            return new List<EntityUid>();

        // Any linked station grid can resolve the full z-network.
        foreach (var grid in data.Grids)
        {
            if (HasComp<CEZLinkedGridComponent>(grid))
                return GetFloorGrids(grid);
        }

        EntityUid? main = null;
        foreach (var grid in data.Grids)
        {
            if (HasComp<BecomesStationComponent>(grid))
            {
                main = grid;
                break;
            }
        }

        main ??= _station.GetLargestGrid((station, data));
        return main is { } mainGrid ? GetFloorGrids(mainGrid) : new List<EntityUid>();
    }

    /// <summary>
    /// Returns <paramref name="mainGrid"/> plus its z-peer floor grids.
    /// </summary>
    public List<EntityUid> GetFloorGrids(EntityUid mainGrid)
    {
        var grids = new List<EntityUid> { mainGrid };
        if (!TryComp<CEZLinkedGridComponent>(mainGrid, out var linked))
            return grids;

        foreach (var peer in linked.PeerGrids.Values)
        {
            if (peer != mainGrid)
                grids.Add(peer);
        }

        return grids;
    }

    /// <summary>
    /// Returns a random z-peer floor grid weighted by grid area.
    /// </summary>
    public EntityUid GetRandomFloorGrid(EntityUid anyFloorGrid)
    {
        var floors = GetFloorGrids(anyFloorGrid);
        if (floors.Count <= 1)
            return anyFloorGrid;

        var weighted = new List<(EntityUid Grid, float Cumulative)>(floors.Count);
        var total = 0f;
        foreach (var grid in floors)
        {
            if (!TryComp<MapGridComponent>(grid, out var gridComp))
                continue;

            var area = gridComp.LocalAABB.Width * gridComp.LocalAABB.Height;
            if (area <= 0f)
                continue;

            total += area;
            weighted.Add((grid, total));
        }

        if (total <= 0f)
            return anyFloorGrid;

        var roll = _random.NextFloat() * total;
        foreach (var (grid, cumulative) in weighted)
        {
            if (roll <= cumulative)
                return grid;
        }

        return anyFloorGrid;
    }
}
