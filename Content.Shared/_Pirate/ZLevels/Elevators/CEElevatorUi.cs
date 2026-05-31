using Robust.Shared.Serialization;

namespace Content.Shared._Pirate.ZLevels.Elevators;

[Serializable, NetSerializable]
public enum CEElevatorUiKey : byte
{
    Key
}

/// <summary>One selectable floor in the elevator panel UI.</summary>
[Serializable, NetSerializable]
public struct CEElevatorFloor
{
    public int Depth;
    public string Name;

    public CEElevatorFloor(int depth, string name)
    {
        Depth = depth;
        Name = name;
    }
}

[Serializable, NetSerializable]
public sealed class CEElevatorBuiState : BoundUserInterfaceState
{
    /// <summary>Served floors, ordered top-to-bottom for display.</summary>
    public List<CEElevatorFloor> Floors;

    /// <summary>Depth the cab is currently on.</summary>
    public int CurrentDepth;

    /// <summary>True while the cab is travelling (controls disabled).</summary>
    public bool Moving;

    /// <summary>False if the panel has no linked controller / is unpowered.</summary>
    public bool Operational;

    public CEElevatorBuiState(List<CEElevatorFloor> floors, int currentDepth, bool moving, bool operational)
    {
        Floors = floors;
        CurrentDepth = currentDepth;
        Moving = moving;
        Operational = operational;
    }
}

/// <summary>Client → server: send the cab to the chosen floor.</summary>
[Serializable, NetSerializable]
public sealed class CEElevatorMoveMessage : BoundUserInterfaceMessage
{
    public int TargetDepth;

    public CEElevatorMoveMessage(int targetDepth)
    {
        TargetDepth = targetDepth;
    }
}
