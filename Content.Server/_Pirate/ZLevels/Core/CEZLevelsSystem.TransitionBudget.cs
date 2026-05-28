using Content.Shared.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.Timing;
using DiagnosticStopwatch = System.Diagnostics.Stopwatch;

namespace Content.Server._Pirate.ZLevels.Core;

/// <summary>
/// Ported from CMU (<c>CMUZLevelsSystem.Movement.cs</c>): per-tick count + wallclock budget for
/// Z-level transitions. Each call site that performs a map-crossing move (TryMoveDown/TryMoveUp)
/// asks <see cref="CanProcessZLevelTransition"/> first; once the budget is exhausted the rest
/// of the tick defers and the engine catches up on the next tick.
/// </summary>
public sealed partial class CEZLevelsSystem
{
    [Dependency] private readonly IConfigurationManager _config = default!;

    private int _maxZTransitionsPerTick = 64;
    private TimeSpan _zTransitionBudget = TimeSpan.FromMilliseconds(1);
    private GameTick _zTransitionBudgetTick;
    private int _zTransitionsThisTick;
    private long _zTransitionBudgetStart;

    private void InitTransitionBudget()
    {
        Subs.CVar(_config, CCVars.CEZMaxTransitionsPerTick, value => _maxZTransitionsPerTick = Math.Max(0, value), true);
        Subs.CVar(_config, CCVars.CEZTransitionBudgetMs, value => _zTransitionBudget = TimeSpan.FromMilliseconds(Math.Max(0f, value)), true);
    }

    protected override bool CanProcessZLevelTransition(EntityUid ent, int offset)
    {
        if (_maxZTransitionsPerTick <= 0)
            return false;

        var curTick = _timing.CurTick;
        if (_zTransitionBudgetTick != curTick)
        {
            _zTransitionBudgetTick = curTick;
            _zTransitionsThisTick = 0;
            _zTransitionBudgetStart = DiagnosticStopwatch.GetTimestamp();
        }

        if (_zTransitionsThisTick >= _maxZTransitionsPerTick)
            return false;

        if (_zTransitionBudget > TimeSpan.Zero &&
            DiagnosticStopwatch.GetElapsedTime(_zTransitionBudgetStart) >= _zTransitionBudget)
        {
            return false;
        }

        _zTransitionsThisTick++;
        return true;
    }
}
