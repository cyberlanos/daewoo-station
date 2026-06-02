namespace Content.Shared._Pirate.ZLevels.Elevators.Components;

/// <summary>
/// Add this to ANY door (anything with a vanilla <c>Door</c> component — airlock, shutter, blast door,
/// …) to make it an elevator door for the elevator with the matching <see cref="ElevatorId"/>. The
/// elevator system drives it automatically: it closes every linked door when the cab starts moving and
/// keeps them shut while in transit, then opens only the door on the cab's floor once the cab arrives
/// (and closes it again when the cab next departs). The door needs no other special setup.
/// </summary>
[RegisterComponent]
public sealed partial class CEElevatorDoorComponent : Component
{
    /// <summary>Id of the elevator this door belongs to.</summary>
    [DataField(required: true)]
    public string ElevatorId = string.Empty;
}
