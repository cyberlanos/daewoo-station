using System.Numerics;
using Content.Server.Popups;
using Content.Shared._Pirate.ZLevels.Core.Components;
using Content.Shared._Pirate.ZLevels.Core.EntitySystems;
using Content.Shared._Pirate.ZLevels.Ladders.Components;
using Content.Shared._White.RadialSelector;
using Content.Shared.Gravity;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Utility;

namespace Content.Server._Pirate.ZLevels.Ladders;

public sealed class CEZLevelLadderSystem : EntitySystem
{
    private const string ClimbUpAction = "zlevel-ladder-climb-up";
    private const string ClimbDownAction = "zlevel-ladder-climb-down";
    private const float FallStartPosition = 0.99f;
    private const float FallStartVelocity = -0.1f;

    private static readonly SpriteSpecifier UpIcon = new SpriteSpecifier.Rsi(new ResPath("/Textures/_Pirate/Actions/misc.rsi"), "up");
    private static readonly SpriteSpecifier DownIcon = new SpriteSpecifier.Rsi(new ResPath("/Textures/_Pirate/Actions/misc.rsi"), "down");

    [Dependency] private readonly SharedGravitySystem _gravity = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly TurfSystem _turf = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly CESharedZLevelsSystem _zLevels = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CEZLevelLadderComponent, ActivatableUIOpenAttemptEvent>(OnOpenAttempt);
        SubscribeLocalEvent<CEZLevelLadderComponent, BeforeActivatableUIOpenEvent>(OnBeforeUiOpen);
        SubscribeLocalEvent<CEZLevelLadderComponent, RadialSelectorSelectedMessage>(OnRadialSelected);
    }

    private void OnOpenAttempt(Entity<CEZLevelLadderComponent> ent, ref ActivatableUIOpenAttemptEvent args)
    {
        if (GetEntries(ent, args.User).Count > 0)
            return;

        args.Cancel();
        _popup.PopupEntity(Loc.GetString("zlevel-ladder-popup-no-route"), ent, args.User);
    }

    private void OnBeforeUiOpen(Entity<CEZLevelLadderComponent> ent, ref BeforeActivatableUIOpenEvent args)
    {
        _ui.SetUiState(ent.Owner, RadialSelectorUiKey.Key, new RadialSelectorState(GetEntries(ent, args.User)));
    }

    private void OnRadialSelected(Entity<CEZLevelLadderComponent> ent, ref RadialSelectorSelectedMessage args)
    {
        switch (args.SelectedItem)
        {
            case ClimbUpAction:
                TryClimbUp(ent, args.Actor);
                break;
            case ClimbDownAction:
                TryClimbDown(ent, args.Actor);
                break;
        }

        _ui.CloseUi(ent.Owner, RadialSelectorUiKey.Key, args.Actor);
    }

    private List<RadialSelectorEntry> GetEntries(Entity<CEZLevelLadderComponent> ladder, EntityUid user)
    {
        var entries = new List<RadialSelectorEntry>(2);

        if (CanClimbUp(ladder, user))
        {
            entries.Add(new RadialSelectorEntry
            {
                Prototype = ClimbUpAction,
                Icon = UpIcon,
            });
        }

        if (CanClimbDown(ladder, user))
        {
            entries.Add(new RadialSelectorEntry
            {
                Prototype = ClimbDownAction,
                Icon = DownIcon,
            });
        }

        return entries;
    }

    private bool TryClimbUp(Entity<CEZLevelLadderComponent> ladder, EntityUid user)
    {
        if (!CanClimbUp(ladder, user))
            return false;

        var ladderWorldPos = _transform.GetWorldPosition(ladder);
        if (!_zLevels.TryMove(user, 1, targetWorldPositionOverride: ladderWorldPos, allowStairExitLanding: false))
            return false;

        _zLevels.NormalizeTransferredPullable(user, 1);
        return true;
    }

    private bool TryClimbDown(Entity<CEZLevelLadderComponent> ladder, EntityUid user)
    {
        if (!CanClimbDown(ladder, user))
            return false;

        var ladderWorldPos = _transform.GetWorldPosition(ladder);
        var hasLadderBelow = HasLadderAtOffset(ladder, user, -1);
        var wasWeightless = IsWeightless(user);

        if (!_zLevels.TryMove(user, -1, targetWorldPositionOverride: ladderWorldPos, allowStairExitLanding: false))
            return false;

        if (hasLadderBelow || wasWeightless)
        {
            _zLevels.NormalizeTransferredPullable(user, -1);
            return true;
        }

        StartFallingFromUpperOpening(user);
        return true;
    }

    private bool CanClimbUp(Entity<CEZLevelLadderComponent> ladder, EntityUid user)
    {
        var ladderWorldPos = _transform.GetWorldPosition(ladder);
        if (!_zLevels.TryResolveTraversalGridForOffsetAtWorldPosition(user, 1, ladderWorldPos, out var aboveGridUid, out var aboveGrid))
            return false;

        if (_zLevels.HasLandingBlockerOnGridAtWorld(aboveGridUid, aboveGrid, ladderWorldPos))
            return false;

        if (!_zLevels.HasSupportAtWorldPositionOnGrid(aboveGridUid, aboveGrid, ladderWorldPos))
            return true;

        if (IsPassableSupportTileAtWorldPosition(ladder, aboveGridUid, aboveGrid, ladderWorldPos))
            return true;

        return HasAscentPassageAtWorldPosition(aboveGridUid, aboveGrid, ladderWorldPos);
    }

    private bool CanClimbDown(Entity<CEZLevelLadderComponent> ladder, EntityUid user)
    {
        var ladderWorldPos = _transform.GetWorldPosition(ladder);
        if (!_zLevels.TryResolveTraversalGridForOffsetAtWorldPosition(user, -1, ladderWorldPos, out var belowGridUid, out var belowGrid))
            return false;

        return !_zLevels.HasLandingBlockerOnGridAtWorld(belowGridUid, belowGrid, ladderWorldPos);
    }

    private bool HasLadderAtOffset(Entity<CEZLevelLadderComponent> ladder, EntityUid user, int offset)
    {
        var ladderWorldPos = _transform.GetWorldPosition(ladder);
        if (!_zLevels.TryResolveTraversalGridForOffsetAtWorldPosition(user, offset, ladderWorldPos, out var gridUid, out var grid))
            return false;

        return HasAnchoredComponentAtWorldPosition<CEZLevelLadderComponent>(gridUid, grid, ladderWorldPos);
    }

    private bool HasAscentPassageAtWorldPosition(EntityUid gridUid, MapGridComponent grid, Vector2 worldPos)
    {
        return HasAnchoredComponentAtWorldPosition<CEZLevelAscentPassageComponent>(gridUid, grid, worldPos);
    }

    private bool IsPassableSupportTileAtWorldPosition(Entity<CEZLevelLadderComponent> ladder, EntityUid gridUid, MapGridComponent grid, Vector2 worldPos)
    {
        if (!_map.TryGetTileRef(gridUid, grid, worldPos, out var tileRef) || tileRef.Tile.IsEmpty)
            return false;

        var tileId = _turf.GetContentTileDefinition(tileRef).ID;
        if (ladder.Comp.PassableSupportTiles.Contains(tileId))
            return true;

        foreach (var prefix in ladder.Comp.PassableSupportTilePrefixes)
        {
            if (tileId.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private bool HasAnchoredComponentAtWorldPosition<T>(EntityUid gridUid, MapGridComponent grid, Vector2 worldPos)
        where T : IComponent
    {
        var tile = _map.WorldToTile(gridUid, grid, worldPos);
        var anchored = _map.GetAnchoredEntitiesEnumerator(gridUid, grid, tile);
        while (anchored.MoveNext(out var uid))
        {
            if (HasComp<T>(uid.Value))
                return true;
        }

        return false;
    }

    private bool IsWeightless(EntityUid user)
    {
        return TryComp<PhysicsComponent>(user, out var physics) &&
               TryComp(user, out TransformComponent? xform) &&
               _gravity.IsWeightless(user, physics, xform);
    }

    private void StartFallingFromUpperOpening(EntityUid user)
    {
        if (!TryComp<CEZPhysicsComponent>(user, out var zPhysics))
            return;

        _zLevels.SetZPosition((user, zPhysics), FallStartPosition);
        if (zPhysics.Velocity > FallStartVelocity)
            _zLevels.SetZVelocity((user, zPhysics), FallStartVelocity);

        var fallEv = new CEZLevelFallMapEvent();
        RaiseLocalEvent(user, ref fallEv);
    }
}
