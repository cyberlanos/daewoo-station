using Content.Shared.DoAfter;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Pirate.Shared.Sink;

/// <summary>
/// Lets a sink wash stains off held items, or off the user's gloves/bare hands when clicked empty-handed.
/// Ported behaviour from tgstation's sink.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class SinkWasherComponent : Component
{
    [DataField]
    public float WashDuration = 2f;

    [DataField]
    public SoundSpecifier WashSound = new SoundPathSpecifier("/Audio/_Pirate/Machines/sink_faucet.ogg");
}

[Serializable, NetSerializable]
public sealed partial class SinkWashDoAfterEvent : SimpleDoAfterEvent;
