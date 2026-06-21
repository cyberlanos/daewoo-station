namespace Content.Pirate.Client.Blinking;

/// <summary>
/// Purely client-side, per-entity runtime state for the eyelid blink animation.
/// Added automatically by <see cref="BlinkingSystem"/>; never networked.
///
/// Blinks are event-driven: a single shared scheduler (<see cref="MasterDelay"/>) fires a
/// blink "event" that drives both eyes at once. Synchronized blinkers animate one unified
/// eyelid; asynchronous (lizard) blinkers animate two eyelid halves, with the trailing eye
/// given a small random lead so the eyes don't close in perfect unison — matching tgstation.
///
/// Per-eye arrays are indexed by eye: 0 = left (or the unified eyelid), 1 = right (async only).
/// </summary>
[RegisterComponent]
public sealed partial class BlinkingVisualsComponent : Component
{
    /// <summary>Layer-map key for the single eyelid used by synchronized blinkers.</summary>
    public const string UnifiedKey = "PirateBlinkEyelid";

    /// <summary>Layer-map key for the left eyelid half (async blinkers).</summary>
    public const string LeftKey = "PirateBlinkEyelidL";

    /// <summary>Layer-map key for the right eyelid half (async blinkers).</summary>
    public const string RightKey = "PirateBlinkEyelidR";

    /// <summary>Whether this entity is set up with the two-layer async eyelids.</summary>
    public bool Async;

    /// <summary>Skin-tinted eyelid color, refreshed from the current skin tone.</summary>
    public Color EyelidColor = Color.Black;

    // --- Shared blink scheduler (one event drives both eyes, coupled like tgstation) ---

    /// <summary>Time until the next blink event, in seconds.</summary>
    public float MasterDelay;

    /// <summary>Remaining queued events in a "blink rapid" burst.</summary>
    public int RapidEventsLeft;

    /// <summary>Whether the current/next event is a short rapid-burst blink.</summary>
    public bool RapidActive;

    // --- Per-eye animation state ---

    /// <summary>Event launched for this eye; it's waiting out its lead offset before closing.</summary>
    public bool[] Pending = new bool[2];

    /// <summary>Remaining lead/lag offset (seconds) before this eye starts closing.</summary>
    public float[] StartDelay = new float[2];

    /// <summary>Progress of this eye's current blink in seconds, or -1 while open/idle.</summary>
    public float[] Progress = { -1f, -1f };

    /// <summary>Duration in seconds of this eye's blink currently in progress.</summary>
    public float[] CurrentDuration = new float[2];
}
