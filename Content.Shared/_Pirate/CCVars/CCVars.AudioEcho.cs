using Robust.Shared.Configuration;

namespace Content.Shared._Pirate.CCVars;

public sealed partial class PirateVars
{
    /// <summary>
    /// Whether to render sounds with echo when they are in large open, roofed areas.
    /// </summary>
    public static readonly CVarDef<bool> AreaEchoEnabled =
        CVarDef.Create("pirate.area_echo.enabled", true, CVar.ARCHIVE | CVar.CLIENTONLY);

    /// <summary>
    /// If false, area echoes calculate with the four cardinal directions.
    /// Otherwise, area echoes calculate with all eight directions.
    /// </summary>
    public static readonly CVarDef<bool> AreaEchoHighResolution =
        CVarDef.Create("pirate.area_echo.alldirections", true, CVar.ARCHIVE | CVar.CLIENTONLY);

    /// <summary>
    /// How many times a ray can bounce off a surface for an echo calculation.
    /// </summary>
    public static readonly CVarDef<int> AreaEchoReflectionCount =
        CVarDef.Create("pirate.area_echo.max_reflections", 1, CVar.ARCHIVE | CVar.CLIENTONLY);

    /// <summary>
    /// Distance interval, in tiles, at which area echo rays sample roofing or space.
    /// Lower values are more accurate and more expensive.
    /// </summary>
    public static readonly CVarDef<float> AreaEchoStepFidelity =
        CVarDef.Create("pirate.area_echo.step_fidelity", 5f, CVar.CLIENTONLY);

    /// <summary>
    /// Interval between full echo refreshes for existing audio entities.
    /// </summary>
    public static readonly CVarDef<TimeSpan> AreaEchoRecalculationInterval =
        CVarDef.Create("pirate.area_echo.recalculation_interval", TimeSpan.FromSeconds(15), CVar.ARCHIVE | CVar.CLIENTONLY);
}
