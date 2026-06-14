using Robust.Shared.Configuration;

namespace Content.Shared._Pirate.CCVars;

public sealed partial class PirateVars
{
    /// <summary>
    /// When true, station event / game rule start delays are skipped and rules fire immediately
    /// once added. Intended for testing (e.g. triggering events with addgamerule). Server-only,
    /// toggle at runtime with: cvar pirate.events.skip_delay true
    /// </summary>
    public static readonly CVarDef<bool> EventsSkipDelay =
        CVarDef.Create("pirate.events.skip_delay", false, CVar.SERVERONLY);
}
