#region Pirate: stains
namespace Content.Pirate.Shared.WashingMachine;

public sealed class WashingMachineIsBeingWashed(EntityUid washingMachine, HashSet<EntityUid> items) : EntityEventArgs
{
    public EntityUid WashingMachine = washingMachine;
    public HashSet<EntityUid> Items = items;
}

public sealed class WashingMachineStartedWashingEvent(HashSet<EntityUid> items) : EntityEventArgs
{
    public HashSet<EntityUid> Items = items;
}

public sealed class WashingMachineWashedEvent(EntityUid washingMachine, HashSet<EntityUid> items) : EntityEventArgs
{
    public EntityUid WashingMachine = washingMachine;
    public HashSet<EntityUid> Items = items;
}

public sealed class WashingMachineFinishedWashingEvent(HashSet<EntityUid> items) : EntityEventArgs
{
    public HashSet<EntityUid> Items = items;
}
#endregion
