/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Robust.Shared;
using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    public static readonly CVarDef<float>
        CEBaseFallingDamage = CVarDef.Create("zlevels.ce_base_falling_damage", 1.5f, CVar.SERVER | CVar.REPLICATED);

    public static readonly CVarDef<float>
        CEBaseFallingOtherDamage = CVarDef.Create("zlevels.ce_base_falling_other_damage", 0.1f, CVar.SERVER | CVar.REPLICATED);

    public static readonly CVarDef<float>
        CEBaseFallingStunTime = CVarDef.Create("zlevels.ce_base_falling_stun_time", 0.1f, CVar.SERVER | CVar.REPLICATED);

    public static readonly CVarDef<float>
        CEBaseFallingOtherStunTime = CVarDef.Create("zlevels.ce_base_falling_other_stun_time", 0.01f, CVar.SERVER | CVar.REPLICATED);

    public static readonly CVarDef<bool>
        CEPostProcess = CVarDef.Create("shaders.ce_post_process", true, CVar.CLIENTONLY | CVar.ARCHIVE);

    public static readonly CVarDef<int>
        CEZLevelsVisibleBelow = CVarDef.Create("zlevels.ce_visible_below", 6, CVar.CLIENTONLY | CVar.ARCHIVE);

    public static readonly CVarDef<bool>
        CEDebugMovement = CVarDef.Create("zlevels.ce_debug_movement", false, CVar.SERVERONLY | CVar.ARCHIVE);

    public static readonly CVarDef<bool>
        CEDebugMovementVerbose = CVarDef.Create("zlevels.ce_debug_movement_verbose", false, CVar.SERVERONLY | CVar.ARCHIVE);

    public static readonly CVarDef<bool>
        CEDebugStairs = CVarDef.Create("zlevels.ce_debug_stairs", false, CVar.SERVERONLY | CVar.ARCHIVE);

    public static readonly CVarDef<bool>
        CEDebugMovementClient = CVarDef.Create("zlevels.ce_debug_movement_client", false, CVar.CLIENTONLY | CVar.ARCHIVE);

    public static readonly CVarDef<bool>
        CEDebugMovementVerboseClient = CVarDef.Create("zlevels.ce_debug_movement_verbose_client", false, CVar.CLIENTONLY | CVar.ARCHIVE);

    public static readonly CVarDef<bool>
        CEDebugStairsClient = CVarDef.Create("zlevels.ce_debug_stairs_client", false, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>
    /// Internal z-physics tick rate, in Hz. The Update loop accumulates engine frametime and
    /// advances physics in fixed-size substeps of <c>1/this</c> seconds. Defaults to 30 Hz to
    /// match the typical engine tickrate (one substep per engine tick, no behavior change).
    /// Bump it for smoother z-physics on hosts that run faster; capped at
    /// <see cref="CESharedZLevelsSystem.MaxStepsPerFrame"/> substeps per frame either way.
    /// </summary>
    public static readonly CVarDef<float>
        CEZPhysicsTickRate = CVarDef.Create("zlevels.ce_physics_tickrate", 30f, CVar.SERVER | CVar.REPLICATED | CVar.ARCHIVE);
}
