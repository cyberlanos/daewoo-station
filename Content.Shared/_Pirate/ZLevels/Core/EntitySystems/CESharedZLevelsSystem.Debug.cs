/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Shared._Pirate.ZLevels.Core.Components;
using Content.Shared.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.Network;

namespace Content.Shared._Pirate.ZLevels.Core.EntitySystems;

public abstract partial class CESharedZLevelsSystem
{
    [Dependency] private readonly IConfigurationManager _config = default!;
    [Dependency] private readonly INetManager _net = default!;

    private bool _zDebugEnabled;
    private bool _zDebugVerboseEnabled;

    private void InitializeDebug()
    {
        if (!_net.IsServer)
            return;

        _zDebugEnabled = _config.GetCVar(CCVars.CEDebugMovement);
        _zDebugVerboseEnabled = _config.GetCVar(CCVars.CEDebugMovementVerbose);
        _config.OnValueChanged(CCVars.CEDebugMovement, OnMovementDebugChanged);
        _config.OnValueChanged(CCVars.CEDebugMovementVerbose, OnMovementVerboseDebugChanged);
    }

    private void OnMovementDebugChanged(bool enabled)
    {
        if (_zDebugEnabled == enabled)
            return;

        _zDebugEnabled = enabled;
        Log.Info($"[CEZDebug] movement logging {(enabled ? "enabled" : "disabled")} via cvar {CCVars.CEDebugMovement.Name}");
    }

    private void OnMovementVerboseDebugChanged(bool enabled)
    {
        if (_zDebugVerboseEnabled == enabled)
            return;

        _zDebugVerboseEnabled = enabled;
        Log.Info($"[CEZDebug] verbose movement logging {(enabled ? "enabled" : "disabled")} via cvar {CCVars.CEDebugMovementVerbose.Name}");
    }

    protected bool ZDebugEnabled => _net.IsServer && _zDebugEnabled;
    protected bool ZDebugVerboseEnabled => ZDebugEnabled && _zDebugVerboseEnabled;

    protected static bool ShouldLogMovementTick(CEZPhysicsComponent zPhys, float oldHeight)
    {
        return oldHeight > 0.01f
               || zPhys.LocalPosition > 0.01f
               || zPhys.CurrentGroundHeight > 0.01f
               || zPhys.CurrentGroundHeight < -0.01f
               || zPhys.CurrentStickyGround;
    }

    protected void DebugZVerbose(EntityUid ent, string message)
    {
        if (!ZDebugVerboseEnabled)
            return;

        DebugZ(ent, message);
    }

    protected void DebugZ(EntityUid ent, string message)
    {
        if (!ZDebugEnabled)
            return;

        var xform = Transform(ent);
        var zState = ZPhyzQuery.TryComp(ent, out var zPhys)
            ? $" local={zPhys.LocalPosition:0.00} vel={zPhys.Velocity:0.00} ground={zPhys.CurrentGroundHeight:0.00} sticky={zPhys.CurrentStickyGround}"
            : string.Empty;

        Log.Info($"[CEZDebug] {ToPrettyString(ent)} parent={xform.ParentUid} grid={xform.GridUid} map={xform.MapUid}{zState} :: {message}");
    }
}
