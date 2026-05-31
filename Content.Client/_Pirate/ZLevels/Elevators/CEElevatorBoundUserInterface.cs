using Content.Shared._Pirate.ZLevels.Elevators;
using JetBrains.Annotations;

namespace Content.Client._Pirate.ZLevels.Elevators;

[UsedImplicitly]
public sealed class CEElevatorBoundUserInterface : BoundUserInterface
{
    private CEElevatorWindow? _window;

    public CEElevatorBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
    }

    protected override void Open()
    {
        base.Open();

        _window = new CEElevatorWindow();
        _window.OnClose += Close;
        _window.OnFloorSelected += depth => SendMessage(new CEElevatorMoveMessage(depth));

        if (State != null)
            UpdateState(State);

        _window.OpenCentered();
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        _window?.UpdateState(state);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _window?.Dispose();
    }
}
