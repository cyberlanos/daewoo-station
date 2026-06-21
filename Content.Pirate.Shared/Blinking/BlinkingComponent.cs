using System;
using Robust.Shared.GameStates;
using Robust.Shared.Utility;

namespace Content.Pirate.Shared.Blinking;

/// <summary>
/// Marks a mob with fleshy eyes that blink. Drives a passive client-side eyelid
/// animation and lets the mob blink via the "blink" / "blink rapid" emotes.
/// Functional port of tgstation's blinking (PR #88927).
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class BlinkingComponent : Component
{
    /// <summary>
    /// Whether blinking is currently active. Disabled for e.g. robotic/missing eyes, or for
    /// species without eyelids (moths, slimes, etc.).
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool Enabled = true;

    /// <summary>
    /// Whether a dead body holds its eyes shut. True matches tgstation; set false if dead
    /// characters should keep their eyes open (they stop blinking either way).
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool CloseOnDeath = true;

    /// <summary>
    /// Shortest delay between idle (automatic) blinks.
    /// </summary>
    [DataField, AutoNetworkedField]
    public TimeSpan MinInterval = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Longest delay between idle (automatic) blinks.
    /// </summary>
    [DataField, AutoNetworkedField]
    public TimeSpan MaxInterval = TimeSpan.FromSeconds(6);

    /// <summary>
    /// How long a single blink (eyelid fully closed-to-open cycle) lasts.
    /// </summary>
    [DataField, AutoNetworkedField]
    public TimeSpan Duration = TimeSpan.FromSeconds(0.15);

    /// <summary>How long each blink lasts during a "blink rapid" emote.</summary>
    [DataField, AutoNetworkedField]
    public TimeSpan RapidDuration = TimeSpan.FromSeconds(0.1);

    /// <summary>Start-to-start spacing between blinks during a "blink rapid" emote.</summary>
    [DataField, AutoNetworkedField]
    public TimeSpan RapidInterval = TimeSpan.FromSeconds(0.2);

    /// <summary>Number of blinks performed by a "blink rapid" emote.</summary>
    [DataField, AutoNetworkedField]
    public int RapidCount = 3;

    /// <summary>
    /// If true, each eye blinks independently (lizard-style) instead of both together.
    /// Requires <see cref="EyelidRsi"/> and the two eyelid states to be set; otherwise the
    /// unified single-eyelid path is used.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool Asynchronous;

    /// <summary>
    /// RSI holding the split left/right eyelid states, used only when <see cref="Asynchronous"/>.
    /// Path is relative to the Textures root.
    /// </summary>
    [DataField, AutoNetworkedField]
    public ResPath? EyelidRsi;

    /// <summary>State in <see cref="EyelidRsi"/> for the (screen-)left eye half.</summary>
    [DataField, AutoNetworkedField]
    public string? EyelidStateLeft;

    /// <summary>State in <see cref="EyelidRsi"/> for the (screen-)right eye half.</summary>
    [DataField, AutoNetworkedField]
    public string? EyelidStateRight;

    /// <summary>
    /// Max random lead between the two eyes on each async blink event. The trailing eye
    /// closes up to this much later than the leader. Matches tgstation's RAND_BLINKING_DELAY
    /// (1s), which gives the pronounced lizard stagger rather than a near-simultaneous blink.
    /// </summary>
    [DataField, AutoNetworkedField]
    public TimeSpan AsyncOffset = TimeSpan.FromSeconds(1);
}
