using Content.Server._Pirate.ZLevels.Core;
using Content.Shared._Pirate.ZLevels.View;
using Content.Shared.Eye;

namespace Content.Server._Pirate.ZLevels.View;

/// <summary>
/// Reusable z-level traversal for eye/camera viewers. Any system that gives a viewer the
/// <see cref="CEZViewUpEvent"/>/<see cref="CEZViewDownEvent"/> actions (abductor observation console,
/// station AI, future camera eyes) gets floor up/down movement of its controlled eye for free.
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
    /// Moves the eye that <paramref name="viewer"/> is currently looking through (its
    /// <see cref="EyeComponent.Target"/>) one floor up (delta &gt; 0) or down (delta &lt; 0).
    /// </summary>
    public bool TryMoveViewerFloor(EntityUid viewer, int delta)
    {
        if (!TryComp<EyeComponent>(viewer, out var eye) || eye.Target is not { } target)
            return false;

        return TryMoveEyeFloor(target, delta);
    }

    /// <summary>
    /// Moves an eye/camera entity one z-level floor up (delta &gt; 0) or down (delta &lt; 0). Uses the
    /// same ghost-style traversal observers use (<c>bypassPassability</c>), so the eye moves between
    /// decks from anywhere — including over open, gridless space.
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
