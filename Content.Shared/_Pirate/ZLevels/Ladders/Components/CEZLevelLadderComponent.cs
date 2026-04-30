using Robust.Shared.GameStates;

namespace Content.Shared._Pirate.ZLevels.Ladders.Components;

[RegisterComponent, NetworkedComponent]
public sealed partial class CEZLevelLadderComponent : Component
{
    [DataField]
    public HashSet<string> PassableSupportTiles = new()
    {
        "Lattice",
        "TrainLattice",
    };

    [DataField]
    public List<string> PassableSupportTilePrefixes = new()
    {
        "LatticeDiagonal",
        "LatticeHalf",
        "LatticeWedge",
    };
}
