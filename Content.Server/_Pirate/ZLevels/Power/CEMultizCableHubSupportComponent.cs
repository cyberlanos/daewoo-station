using Content.Shared.Stacks;
using Robust.Shared.Prototypes;

namespace Content.Server._Pirate.ZLevels.Power;

[RegisterComponent]
[Access(typeof(CEMultizCableHubSystem))]
public sealed partial class CEMultizCableHubSupportComponent : Component
{
    [DataField]
    public Dictionary<ProtoId<StackPrototype>, int> SupportLossStacks = new()
    {
        ["Cable"] = 5,
        ["CableMV"] = 5,
        ["CableHV"] = 5,
    };
}
