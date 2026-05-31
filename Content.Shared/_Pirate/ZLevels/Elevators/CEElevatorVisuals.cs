using Robust.Shared.Serialization;

namespace Content.Shared._Pirate.ZLevels.Elevators;

/// <summary>Appearance keys set by the elevator system on floor indicators.</summary>
[Serializable, NetSerializable]
public enum CEElevatorIndicatorVisuals : byte
{
    /// <summary>Current cab floor number (int, 1-based for display).</summary>
    Floor,

    /// <summary>Travel direction (<see cref="CEElevatorDirection"/>).</summary>
    Direction
}

[Serializable, NetSerializable]
public enum CEElevatorDirection : byte
{
    Idle,
    Up,
    Down
}

/// <summary>Sprite layer map ids for elevator parts.</summary>
[Serializable, NetSerializable]
public enum CEElevatorVisualLayers : byte
{
    /// <summary>The indicator housing — always visible.</summary>
    Base,

    /// <summary>The up/down arrow overlay — only visible while travelling.</summary>
    Arrow
}
