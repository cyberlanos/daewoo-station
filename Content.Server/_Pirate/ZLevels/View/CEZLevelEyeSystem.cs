using Content.Server._Pirate.ZLevels.Core;
using Content.Shared._Pirate.ZLevels.View;
using Content.Shared.Eye;

namespace Content.Server._Pirate.ZLevels.View;

/// <summary>
/// Shared z-level traversal for remote eye/camera viewers.
/// </summary>
public sealed class CEZLevelEyeSystem : EntitySystem
{
    [Dependency] private readonly CEZLevelsSystem _zLevels = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CEZViewUpEvent>(OnViewUp);
        SubscribeLocalEvent<CEZViewDownEvent>(OnViewDown);
    }

    private void OnViewUp(CEZViewUpEvent ev) => ev.Handled = TryMoveViewerFloor(ev.Performer, 1);
    private void OnViewDown(CEZViewDownEvent ev) => ev.Handled = TryMoveViewerFloor(ev.Performer, -1);

    /// <summary>
    /// Moves the eye <paramref name="viewer"/> is looking through one floor.
    /// </summary>
    public bool TryMoveViewerFloor(EntityUid viewer, int delta)
    {
        if (!TryComp<EyeComponent>(viewer, out var eye) || eye.Target is not { } target)
            return false;

        return TryMoveEyeFloor(target, delta);
    }

    /// <summary>
    /// Moves an eye/camera entity one z-level floor, using ghost-style traversal.
    /// </summary>
    public bool TryMoveEyeFloor(EntityUid eye, int delta)
    {
        if (delta > 0)
            return _zLevels.TryMoveUp(eye, bypassPassability: true);
        if (delta < 0)
            return _zLevels.TryMoveDown(eye, bypassPassability: true);

        return false;
    }
}
