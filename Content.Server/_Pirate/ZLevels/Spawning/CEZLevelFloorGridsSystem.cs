using Content.Shared._Pirate.ZLevels.Core.Components;

namespace Content.Server._Pirate.ZLevels.Spawning;

/// <summary>
/// Helper for game rules that pick a random station tile (round-start variation, antag spawns)
/// so they can spread across every z-level floor grid of a station instead of only the main grid.
/// </summary>
public sealed class CEZLevelFloorGridsSystem : EntitySystem
{
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
}
