namespace Content.Shared._Pirate.ZLevels.Elevators.Components;

/// <summary>
/// Marks a (vanilla <c>Door</c>) door as an elevator shaft door. The elevator system closes every
/// shaft door of its id before travelling and opens the one on the cab's arrival floor.
/// A closed shaft door is a dense barrier preventing entry into the open shaft.
/// </summary>
[RegisterComponent]
public sealed partial class CEElevatorDoorComponent : Component
{
    /// <summary>Id of the elevator this door belongs to.</summary>
    [DataField(required: true)]
    public string ElevatorId = string.Empty;
}
