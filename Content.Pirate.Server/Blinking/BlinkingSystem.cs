using Content.Pirate.Shared.Blinking;
using Content.Server.Chat.Systems;
using Robust.Shared.Player;

namespace Content.Pirate.Server.Blinking;

/// <summary>
/// Server half of the blinking feature: turns the blink emotes into networked
/// <see cref="BlinkEffectEvent"/>s so every nearby client plays the animation.
/// </summary>
public sealed class BlinkingSystem : EntitySystem
{
    public const string BlinkEmote = "Blink";
    public const string BlinkRapidEmote = "BlinkRapid";

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<BlinkingComponent, EmoteEvent>(OnEmote);
    }

    private void OnEmote(Entity<BlinkingComponent> ent, ref EmoteEvent args)
    {
        if (!ent.Comp.Enabled)
            return;

        switch (args.Emote.ID)
        {
            case BlinkEmote:
                Blink(ent.Owner);
                break;
            case BlinkRapidEmote:
                Blink(ent.Owner, rapid: true);
                break;
        }
    }

    /// <summary>
    /// Make an entity visibly blink for everyone who can currently see it.
    /// </summary>
    public void Blink(EntityUid uid, bool rapid = false)
    {
        RaiseNetworkEvent(new BlinkEffectEvent(GetNetEntity(uid), rapid), Filter.Pvs(uid, entityManager: EntityManager));
    }
}
