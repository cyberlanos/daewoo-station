using Robust.Shared.Serialization;

namespace Content.Pirate.Shared.Blinking;

/// <summary>
/// Raised on the server and sent to clients to make an entity blink once, now
/// (used by the blink emotes). <see cref="Rapid"/> requests a quick triple-blink.
/// </summary>
[Serializable, NetSerializable]
public sealed class BlinkEffectEvent : EntityEventArgs
{
    public NetEntity Target;
    public bool Rapid;

    public BlinkEffectEvent(NetEntity target, bool rapid = false)
    {
        Target = target;
        Rapid = rapid;
    }
}
