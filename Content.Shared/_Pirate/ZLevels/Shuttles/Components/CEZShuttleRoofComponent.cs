using Robust.Shared.GameStates;

namespace Content.Shared._Pirate.ZLevels.Shuttles.Components;

/// <summary>
/// Marks a grid as a temporary roof generated above a shuttle to seal the topmost shuttle layer
/// when it is positioned in a z-network level that has another level above it. The grid is fully
/// owned by <see cref="Shuttles.CEZShuttleRoofSystem"/> — it should never be authored by hand or
/// serialized to disk.
/// </summary>
[RegisterComponent, NetworkedComponent, UnsavedComponent]
public sealed partial class CEZShuttleRoofComponent : Component
{
    /// <summary>
    /// The shuttle root grid that owns this roof. Used to track the roof for cleanup when the
    /// shuttle is removed or moves to a context where a roof is no longer needed.
    /// </summary>
    [DataField]
    public EntityUid Shuttle;

    /// <summary>
    /// The shuttle's topmost grid whose tile shape was copied to produce this roof. May be the
    /// same as <see cref="Shuttle"/> for single-layer shuttles, or a higher-depth peer for
    /// multi-layer ones.
    /// </summary>
    [DataField]
    public EntityUid SourceGrid;
}
