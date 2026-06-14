using Content.Shared._Pirate.ZLevels.Core.Components;
using Robust.Shared.Map.Components;
using Robust.Shared.Random;

namespace Content.Server._Pirate.ZLevels.Spawning;

/// <summary>
/// Helper for game rules that pick a random station tile/grid (round-start variation, antag spawns,
/// station events, anomalies) so they spread across every z-level floor grid of a station instead of
/// only the main grid.
/// </summary>
public sealed class CEZLevelFloorGridsSystem : EntitySystem
{
    [Dependency] private readonly IRobustRandom _random = default!;

    /// <summary>
    /// Returns <paramref name="mainGrid"/> plus every z-level peer floor grid linked to it.
    /// Docked shuttles and other non-floor grids are excluded. If the station has no z-network
    /// the result is just the main grid, matching pre-multiz behaviour.
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
    /// Picks one floor grid of the station <paramref name="anyFloorGrid"/> belongs to, weighted by
    /// grid area so per-tile odds stay uniform across floors. Returns <paramref name="anyFloorGrid"/>
    /// unchanged when the station has no z-network (or no usable area).
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
