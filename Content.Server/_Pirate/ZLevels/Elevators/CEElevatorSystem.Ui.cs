using System.Linq;
using Content.Server.Power.Components;
using Content.Shared._Pirate.ZLevels.Elevators;
using Content.Shared._Pirate.ZLevels.Elevators.Components;
using Content.Shared.Interaction;
using Robust.Server.GameObjects;

namespace Content.Server._Pirate.ZLevels.Elevators;

// Control panel UI + per-floor call buttons.
public sealed partial class CEElevatorSystem
{
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    private void InitializeUi()
    {
        SubscribeLocalEvent<CEElevatorPanelComponent, BoundUIOpenedEvent>(OnPanelUiOpened);
        SubscribeLocalEvent<CEElevatorPanelComponent, CEElevatorMoveMessage>(OnPanelMove);
        SubscribeLocalEvent<CEElevatorCallButtonComponent, ActivateInWorldEvent>(OnCallButtonActivate);
    }

    private void OnPanelUiOpened(EntityUid uid, CEElevatorPanelComponent comp, BoundUIOpenedEvent args)
    {
        PushPanelState((uid, comp));
    }

    private void OnPanelMove(EntityUid uid, CEElevatorPanelComponent comp, CEElevatorMoveMessage args)
    {
        if (!IsPanelOperational(uid))
            return;

        HandleCall(comp.ElevatorId, args.TargetDepth, uid);
        PushPanelState((uid, comp));
    }

    private void OnCallButtonActivate(Entity<CEElevatorCallButtonComponent> ent, ref ActivateInWorldEvent args)
    {
        if (args.Handled || !args.Complex)
            return;

        var comp = ent.Comp;
        if (_timing.CurTime < comp.NextUse)
            return;
        comp.NextUse = _timing.CurTime + TimeSpan.FromSeconds(comp.Cooldown);

        if (TryGetEntityDeckDepth(ent.Owner, out var depth))
            HandleCall(comp.ElevatorId, depth, ent.Owner);
        else
            _audio.PlayPvs(DenySound, ent.Owner);

        args.Handled = true;
    }

    /// <summary>
    /// Routes a call request. If the cab is already idle on this floor, just open the doors with a
    /// ping (SS13 "elevator is here"). Otherwise dispatch it; a refused request buzzes.
    /// </summary>
    private void HandleCall(string elevatorId, int depth, EntityUid source)
    {
        if (!TryGetController(elevatorId, out var controller) || !controller.Value.Comp.Initialized)
        {
            _audio.PlayPvs(DenySound, source);
            return;
        }

        var comp = controller.Value.Comp;
        if (!comp.Moving && comp.CurrentDepth == depth)
        {
            OpenDoorOnDeck(elevatorId, depth);
            PlayArrivalPing(comp, depth);
            return;
        }

        if (!RequestMove(elevatorId, depth))
            _audio.PlayPvs(DenySound, source);
    }

    /// <summary>Re-pushes UI state to every open panel of an elevator (on move start / finish).</summary>
    private void UpdatePanelUis(string elevatorId)
    {
        var query = AllEntityQuery<CEElevatorPanelComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.ElevatorId == elevatorId)
                PushPanelState((uid, comp));
        }
    }

    private void PushPanelState(Entity<CEElevatorPanelComponent> ent)
    {
        var floors = new List<CEElevatorFloor>();
        var currentDepth = 0;
        var moving = false;
        var operational = IsPanelOperational(ent.Owner);

        if (TryGetController(ent.Comp.ElevatorId, out var controller) && controller.Value.Comp.Initialized)
        {
            var comp = controller.Value.Comp;
            currentDepth = comp.CurrentDepth;
            moving = comp.Moving;

            // Top floor first for display.
            foreach (var depth in comp.ServedDepths.OrderByDescending(d => d))
            {
                var name = comp.FloorNames.TryGetValue(depth, out var custom)
                    ? custom
                    : $"Floor {DisplayFloor(comp, depth)}";
                floors.Add(new CEElevatorFloor(depth, name));
            }
        }
        else
        {
            operational = false;
        }

        _ui.SetUiState(ent.Owner, CEElevatorUiKey.Key, new CEElevatorBuiState(floors, currentDepth, moving, operational));
    }

    private bool IsPanelOperational(EntityUid uid)
    {
        // No power receiver = always operational; otherwise require power.
        return !TryComp<ApcPowerReceiverComponent>(uid, out var power) || power.Powered;
    }
}
