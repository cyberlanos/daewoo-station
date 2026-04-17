/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Shared._Pirate.ZLevels.Core.Components;
using SharedCCVars = Content.Shared.CCVar.CCVars;
using Robust.Shared.Configuration;
using Robust.Shared.Network;
using System.Collections.Generic;
using System.Globalization;

namespace Content.Shared._Pirate.ZLevels.Core.EntitySystems;

public abstract partial class CESharedZLevelsSystem
{
    [Dependency] private readonly IConfigurationManager _config = default!;
    [Dependency] private readonly INetManager _net = default!;

    private bool _zDebugEnabled;
    private bool _zDebugVerboseEnabled;
    private bool _zDebugStairsEnabled;
    private readonly Dictionary<(EntityUid Uid, string EventName), string> _stairDebugKeys = new();

    private void InitializeDebug()
    {
        if (!_net.IsServer)
            return;

        _zDebugEnabled = _config.GetCVar(SharedCCVars.CEDebugMovement);
        _zDebugVerboseEnabled = _config.GetCVar(SharedCCVars.CEDebugMovementVerbose);
        _zDebugStairsEnabled = _config.GetCVar(SharedCCVars.CEDebugStairs);
        _config.OnValueChanged(SharedCCVars.CEDebugMovement, OnMovementDebugChanged);
        _config.OnValueChanged(SharedCCVars.CEDebugMovementVerbose, OnMovementVerboseDebugChanged);
        _config.OnValueChanged(SharedCCVars.CEDebugStairs, OnStairDebugChanged);
    }

    private void OnMovementDebugChanged(bool enabled)
    {
        if (_zDebugEnabled == enabled)
            return;

        _zDebugEnabled = enabled;
        Log.Info($"[CEZDebug] movement logging {(enabled ? "enabled" : "disabled")} via cvar {SharedCCVars.CEDebugMovement.Name}");
    }

    private void OnMovementVerboseDebugChanged(bool enabled)
    {
        if (_zDebugVerboseEnabled == enabled)
            return;

        _zDebugVerboseEnabled = enabled;
        Log.Info($"[CEZDebug] verbose movement logging {(enabled ? "enabled" : "disabled")} via cvar {SharedCCVars.CEDebugMovementVerbose.Name}");
    }

    private void OnStairDebugChanged(bool enabled)
    {
        if (_zDebugStairsEnabled == enabled)
            return;

        _zDebugStairsEnabled = enabled;
        _stairDebugKeys.Clear();
        Log.Info($"[CEZStairCsv] stair logging {(enabled ? "enabled" : "disabled")} via cvar {SharedCCVars.CEDebugStairs.Name}");
    }

    protected bool ZDebugEnabled => _net.IsServer && _zDebugEnabled;
    protected bool ZDebugVerboseEnabled => ZDebugEnabled && _zDebugVerboseEnabled;
    protected bool ZDebugStairsEnabled => _net.IsServer && _zDebugStairsEnabled;

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

    protected static string StairCsvFloat(float value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    protected static string StairCsvBool(bool value)
    {
        return value ? "1" : "0";
    }

    protected void DebugZStairCsv(EntityUid ent, string eventName, string payload, string? dedupeKey = null)
    {
        if (!ZDebugStairsEnabled)
            return;

        if (dedupeKey != null)
        {
            var key = (ent, eventName);
            if (_stairDebugKeys.TryGetValue(key, out var previous) && previous == dedupeKey)
                return;

            _stairDebugKeys[key] = dedupeKey;
        }

        var xform = Transform(ent);
        var basePayload =
            $"uid={ToPrettyString(ent)},parent={xform.ParentUid},grid={xform.GridUid},map={xform.MapUid}";

        if (ZPhyzQuery.TryComp(ent, out var zPhys))
        {
            basePayload +=
                $",local={StairCsvFloat(zPhys.LocalPosition)},vel={StairCsvFloat(zPhys.Velocity)},ground={StairCsvFloat(zPhys.CurrentGroundHeight)},sticky={StairCsvBool(zPhys.CurrentStickyGround)}";
        }

        Log.Info($"[CEZStairCsv] event={eventName},{basePayload},{payload}");
    }
}
