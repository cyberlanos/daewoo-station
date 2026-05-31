using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using Content.Server.Body.Systems;
using Content.Shared._Pirate.ZLevels.Core.Components;
using Content.Shared._Pirate.ZLevels.Core.EntitySystems;
using Content.Shared._Pirate.ZLevels.Elevators;
using Content.Shared._Pirate.ZLevels.Elevators.Components;
using Content.Shared.Audio;
using Content.Shared.Body.Components;
using Content.Shared.Damage;
using Content.Shared.Examine;
using Content.Shared.Maps;
using Content.Shared.Wall;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Spawners;
using Robust.Shared.Timing;

namespace Content.Server._Pirate.ZLevels.Elevators;

/// <summary>
/// Drives SS13-style elevators on the multiz z-network: a cab whose floor tiles + riders travel
/// between decks, leaving an open shaft behind. See <see cref="CEElevatorControllerComponent"/>.
/// </summary>
public sealed partial class CEElevatorSystem : EntitySystem
{
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDef = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly CESharedZLevelsSystem _zLevels = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;
    [Dependency] private readonly SharedAmbientSoundSystem _ambient = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly BodySystem _body = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    /// <summary>Played at a control when a call/move request is refused (SS13 buzz-two).</summary>
    private static readonly SoundSpecifier DenySound = new SoundPathSpecifier("/Audio/Machines/buzz-two.ogg");

    private EntityQuery<MapGridComponent> _gridQuery;
    private EntityQuery<TransformComponent> _xformQuery;

    public override void Initialize()
    {
        base.Initialize();

        _gridQuery = GetEntityQuery<MapGridComponent>();
        _xformQuery = GetEntityQuery<TransformComponent>();

        SubscribeLocalEvent<CEElevatorControllerComponent, MapInitEvent>(OnControllerMapInit);
        SubscribeLocalEvent<CEElevatorControllerComponent, ComponentShutdown>(OnControllerShutdown);
        SubscribeLocalEvent<CEElevatorIndicatorComponent, ExaminedEvent>(OnIndicatorExamine);

        InitializeUi();
    }

    private void OnControllerMapInit(Entity<CEElevatorControllerComponent> ent, ref MapInitEvent args)
    {
        // Best-effort: succeeds only once the z-network has linked this deck's grid. Otherwise the
        // lazy retry in Update() handles it (grids are linked shortly after map load).
        TryInitialize(ent);
    }

    private void OnControllerShutdown(Entity<CEElevatorControllerComponent> ent, ref ComponentShutdown args)
    {
        foreach (var speaker in ent.Comp.MusicSpeakers.Values)
        {
            if (!TerminatingOrDeleted(speaker))
                QueueDel(speaker);
        }
        ent.Comp.MusicSpeakers.Clear();
    }

    /// <summary>
    /// Caches footprint, cab floor tile and served decks. Requires the controller's deck grid to be
    /// part of a z-network (<see cref="CEZLinkedGridComponent"/>) so depths resolve correctly.
    /// </summary>
    private bool TryInitialize(Entity<CEElevatorControllerComponent> ent)
    {
        var comp = ent.Comp;
        if (comp.Initialized)
            return true;

        var xform = Transform(ent);
        if (xform.GridUid is not { } gridUid || !_gridQuery.TryComp(gridUid, out var grid))
            return false;

        // Depth + peer decks come from the z-network linkage, which may be applied after map-init.
        if (!TryComp<CEZLinkedGridComponent>(gridUid, out var linked))
            return false;

        comp.AnchorGrid = gridUid;
        comp.OriginTile = _map.TileIndicesFor(gridUid, grid, xform.Coordinates);

        // Resolved only so discovery can recognise an already-floored shaft. The elevator does NOT
        // place or remove shaft tiles — the mapper controls them (so a mapped-empty shaft stays empty).
        comp.ResolvedShaftFloorTile = new Tile(_tileDef[comp.ShaftFloorTile].TileId);

        comp.CurrentDepth = linked.Depth;
        comp.TargetDepth = linked.Depth;

        DiscoverServedDepths(ent, linked.Depth);

        comp.Initialized = true;

        // One stationary music speaker per served deck; only the cab's current-floor speaker is enabled.
        // The muzak stays on the cab's floor with no teleporting (which caused glitchy overlapping audio).
        foreach (var depth in comp.ServedDepths)
        {
            if (!TryGetDeckGrid(comp, depth, out var deckGrid, out var deckGridComp))
                continue;
            var speaker = Spawn(comp.MusicSpeakerProto, FootprintCenter(comp, deckGrid, deckGridComp));
            _ambient.SetRange(speaker, MathF.Max(comp.Width, comp.Height));
            _ambient.SetAmbience(speaker, depth == comp.CurrentDepth);
            comp.MusicSpeakers[depth] = speaker;
        }

        // Cab starts present on its deck: open that floor's door, shut the rest, set indicators.
        OpenDoorOnDeck(comp.ElevatorId, comp.CurrentDepth);
        UpdateIndicators(comp.ElevatorId, DisplayFloor(comp, comp.CurrentDepth), CEElevatorDirection.Idle);
        return true;
    }

    /// <summary>Walks the z-network up and down from the start deck while the footprint is an open shaft.</summary>
    private void DiscoverServedDepths(Entity<CEElevatorControllerComponent> ent, int startDepth)
    {
        var comp = ent.Comp;
        var served = new HashSet<int> { startDepth };

        for (var d = startDepth + 1; ShaftOpenAtDepth(comp, d); d++)
            served.Add(d);
        for (var d = startDepth - 1; ShaftOpenAtDepth(comp, d); d--)
            served.Add(d);

        comp.ServedDepths = served.OrderBy(d => d).ToList();
    }

    /// <summary>
    /// True if a grid exists at <paramref name="depth"/> and every footprint tile is "shaft": either
    /// empty (open shaft) or already the shaft floor tile. Lets the mapper build the shaft either way.
    /// </summary>
    private bool ShaftOpenAtDepth(CEElevatorControllerComponent comp, int depth)
    {
        if (!TryGetDeckGrid(comp, depth, out var gridUid, out var grid))
            return false;

        foreach (var tile in FootprintTiles(comp))
        {
            if (!_map.TryGetTileRef(gridUid, grid, tile, out var tileRef))
                return false;
            if (!tileRef.Tile.IsEmpty && tileRef.Tile.TypeId != comp.ResolvedShaftFloorTile.TypeId)
                return false;
        }

        return true;
    }

    /// <summary>Resolves the grid for a deck depth via the controller's linked-grid peer map.</summary>
    private bool TryGetDeckGrid(CEElevatorControllerComponent comp, int depth, out EntityUid gridUid, out MapGridComponent grid)
    {
        gridUid = EntityUid.Invalid;
        grid = default!;

        if (!TryComp<CEZLinkedGridComponent>(comp.AnchorGrid, out var linked))
            return false;

        if (depth == linked.Depth)
            gridUid = comp.AnchorGrid;
        else if (!linked.PeerGrids.TryGetValue(depth, out gridUid))
            return false;

        if (!_gridQuery.TryComp(gridUid, out var gridComp))
            return false;

        grid = gridComp;
        return true;
    }

    private IEnumerable<Vector2i> FootprintTiles(CEElevatorControllerComponent comp)
    {
        for (var i = 0; i < comp.Width; i++)
        for (var j = 0; j < comp.Height; j++)
            yield return comp.OriginTile + new Vector2i(i, j);
    }

    /// <summary>Local coordinates at the centre of the cab footprint on a given deck grid (for sound).</summary>
    private EntityCoordinates FootprintCenter(CEElevatorControllerComponent comp, EntityUid gridUid, MapGridComponent grid)
    {
        var center = comp.OriginTile + new Vector2i(comp.Width / 2, comp.Height / 2);
        return TileCenter(gridUid, grid, center);
    }

    private EntityCoordinates TileCenter(EntityUid gridUid, MapGridComponent grid, Vector2i tile)
    {
        var local = (new Vector2(tile.X, tile.Y) + new Vector2(0.5f, 0.5f)) * grid.TileSize;
        return new EntityCoordinates(gridUid, local);
    }

    /// <summary>Spawns the descent telegraph on every footprint tile of the given deck.</summary>
    private void SpawnTravelWarnings(CEElevatorControllerComponent comp, int depth)
    {
        if (!TryGetDeckGrid(comp, depth, out var gridUid, out var grid))
            return;

        foreach (var tile in FootprintTiles(comp))
        {
            var warning = Spawn(comp.TravelWarningProto, TileCenter(gridUid, grid, tile));
            if (TryComp<TimedDespawnComponent>(warning, out var despawn))
                despawn.Lifetime = comp.PerDeckTravelSeconds;
        }
    }

    /// <summary>If the cab's next step is downward, telegraph the destination floor (SS13 lift_travel).</summary>
    private void MaybeWarnNextDescent(CEElevatorControllerComponent comp)
    {
        if (!comp.WarnsOnDownMovement)
            return;
        var dir = Math.Sign(comp.TargetDepth - comp.CurrentDepth);
        if (dir < 0)
            SpawnTravelWarnings(comp, comp.CurrentDepth + dir);
    }

    /// <summary>Public entry: send the cab of an elevator id to a served depth.</summary>
    public bool RequestMove(string elevatorId, int targetDepth)
    {
        if (!TryGetController(elevatorId, out var ent))
            return false;

        var comp = ent.Value.Comp;
        if (!comp.Initialized || comp.Moving)
            return false;
        if (!comp.ServedDepths.Contains(targetDepth) || targetDepth == comp.CurrentDepth)
            return false;

        comp.TargetDepth = targetDepth;
        comp.Moving = true;
        comp.NextStepTime = _timing.CurTime + TimeSpan.FromSeconds(comp.PerDeckTravelSeconds);

        // Lock the shaft: every floor's door shuts before we move.
        CloseAllDoors(comp.ElevatorId);
        UpdateIndicators(comp.ElevatorId, DisplayFloor(comp, comp.CurrentDepth), CEElevatorDirectionFor(comp));
        UpdatePanelUis(comp.ElevatorId);
        MaybeWarnNextDescent(comp);
        PlayLegStartSound(comp);
        return true;
    }

    private bool TryGetController(string elevatorId, [NotNullWhen(true)] out Entity<CEElevatorControllerComponent>? ent)
    {
        var query = AllEntityQuery<CEElevatorControllerComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.ElevatorId != elevatorId)
                continue;
            ent = (uid, comp);
            return true;
        }

        ent = null;
        return false;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = AllEntityQuery<CEElevatorControllerComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (!comp.Initialized && !TryInitialize((uid, comp)))
                continue;

            if (!comp.Moving || now < comp.NextStepTime)
                continue;

            StepCab((uid, comp));

            if (comp.CurrentDepth == comp.TargetDepth)
            {
                FinishMove((uid, comp));
            }
            else
            {
                comp.NextStepTime = now + TimeSpan.FromSeconds(comp.PerDeckTravelSeconds);
                MaybeWarnNextDescent(comp);
                PlayLegStartSound(comp);
            }
        }
    }

    /// <summary>Moves the cab one deck toward its target: crush, lay floor, carry riders, reopen the origin shaft.</summary>
    private void StepCab(Entity<CEElevatorControllerComponent> ent)
    {
        var comp = ent.Comp;
        var dir = Math.Sign(comp.TargetDepth - comp.CurrentDepth); // +1 up, -1 down
        if (dir == 0)
            return;

        var fromDepth = comp.CurrentDepth;
        var toDepth = comp.CurrentDepth + dir;

        if (!TryGetDeckGrid(comp, fromDepth, out var fromGridUid, out var fromGrid) ||
            !TryGetDeckGrid(comp, toDepth, out var toGridUid, out var toGrid))
        {
            // Lost a deck mid-travel — abort gracefully.
            comp.TargetDepth = comp.CurrentDepth;
            return;
        }

        // 1) Gather everything riding the cab (current deck) before anything changes — like SS13's
        //    transport_contents, this includes anchored structures/machines built on the platform.
        var riders = GatherFootprintEntities(comp, fromGridUid, fromGrid);

        // 2) Crush whatever is sitting in the destination footprint.
        CrushDestination(comp, toGridUid, toGrid);

        // 3) Carry the cab one deck: the platform objects placed on the footprint AND the riders. The
        //    permanent shaft floor underneath never changes — only these objects move. Anchored ones
        //    (the platforms, plus any furniture built on the cab) are unanchored, moved, re-anchored.
        foreach (var rider in riders)
        {
            if (!_xformQuery.TryComp(rider, out var rxform))
                continue;

            var wasAnchored = rxform.Anchored;
            if (wasAnchored)
                _transform.Unanchor(rider, rxform);

            _zLevels.TryMove(rider, dir);

            if (wasAnchored && _xformQuery.TryComp(rider, out var movedXform))
                _transform.AnchorEntity(rider, movedXform);
        }

        comp.CurrentDepth = toDepth;

        // Move the music to the cab's new floor: enable the arrival deck's speaker, mute the old one.
        if (comp.MusicSpeakers.TryGetValue(fromDepth, out var oldSpeaker) && !TerminatingOrDeleted(oldSpeaker))
            _ambient.SetAmbience(oldSpeaker, false);
        if (comp.MusicSpeakers.TryGetValue(toDepth, out var newSpeaker) && !TerminatingOrDeleted(newSpeaker))
            _ambient.SetAmbience(newSpeaker, true);

        UpdateIndicators(comp.ElevatorId, DisplayFloor(comp, comp.CurrentDepth), dir > 0 ? CEElevatorDirection.Up : CEElevatorDirection.Down);
    }

    private void FinishMove(Entity<CEElevatorControllerComponent> ent)
    {
        var comp = ent.Comp;
        comp.Moving = false;

        OpenDoorOnDeck(comp.ElevatorId, comp.CurrentDepth);
        PlayArrivalPing(comp, comp.CurrentDepth);

        UpdateIndicators(comp.ElevatorId, DisplayFloor(comp, comp.CurrentDepth), CEElevatorDirection.Idle);
        UpdatePanelUis(comp.ElevatorId);
    }

    /// <summary>Lift sound when a leg starts — on the cab's level and the levels directly above/below it.</summary>
    private void PlayLegStartSound(CEElevatorControllerComponent comp)
    {
        if (comp.TravelSound == null)
            return;

        foreach (var depth in new[] { comp.CurrentDepth, comp.CurrentDepth + 1, comp.CurrentDepth - 1 })
        {
            if (TryGetDeckGrid(comp, depth, out var gridUid, out var grid))
                _audio.PlayPvs(comp.TravelSound, FootprintCenter(comp, gridUid, grid));
        }
    }

    /// <summary>Arrival ping, played from a linked control (call button / panel) on that floor.</summary>
    private void PlayArrivalPing(CEElevatorControllerComponent comp, int depth)
    {
        if (comp.ArriveSound == null)
            return;

        if (TryGetControlOnDeck(comp.ElevatorId, depth, out var control))
            _audio.PlayPvs(comp.ArriveSound, control);
        else if (TryGetDeckGrid(comp, depth, out var gridUid, out var grid))
            _audio.PlayPvs(comp.ArriveSound, FootprintCenter(comp, gridUid, grid));
    }

    /// <summary>Finds a call button (preferred) or panel of this elevator on the given deck.</summary>
    private bool TryGetControlOnDeck(string elevatorId, int depth, out EntityUid control)
    {
        var buttons = AllEntityQuery<CEElevatorCallButtonComponent>();
        while (buttons.MoveNext(out var uid, out var button))
        {
            if (button.ElevatorId == elevatorId && TryGetEntityDeckDepth(uid, out var d) && d == depth)
            {
                control = uid;
                return true;
            }
        }

        var panels = AllEntityQuery<CEElevatorPanelComponent>();
        while (panels.MoveNext(out var uid, out var panel))
        {
            if (panel.ElevatorId == elevatorId && TryGetEntityDeckDepth(uid, out var d) && d == depth)
            {
                control = uid;
                return true;
            }
        }

        control = default;
        return false;
    }

    /// <summary>Crushes living mobs / damages structures standing in the destination shaft footprint.</summary>
    private void CrushDestination(CEElevatorControllerComponent comp, EntityUid gridUid, MapGridComponent grid)
    {
        var victims = GatherFootprintEntities(comp, gridUid, grid);
        foreach (var victim in victims)
        {
            if (comp.ViolentLanding && HasComp<BodyComponent>(victim))
            {
                _body.GibBody(victim);
                continue;
            }

            if (comp.CrushDamage.DamageDict.Count > 0 && HasComp<DamageableComponent>(victim))
                _damageable.TryChangeDamage(victim, comp.CrushDamage, ignoreResistances: true);
        }
    }

    /// <summary>
    /// Entities standing on the cab footprint on the given deck (mobs, items and anchored structures
    /// alike — like SS13's transport_contents). The controller, the music speaker and grids are excluded.
    /// </summary>
    private List<EntityUid> GatherFootprintEntities(CEElevatorControllerComponent comp, EntityUid gridUid, MapGridComponent grid)
    {
        var footprint = new HashSet<Vector2i>(FootprintTiles(comp));
        var result = new List<EntityUid>();
        var seen = new HashSet<EntityUid>();

        foreach (var tile in footprint)
        {
            if (!_map.TryGetTileRef(gridUid, grid, tile, out var tileRef))
                continue;

            foreach (var uid in _lookup.GetEntitiesInTile(tileRef, LookupFlags.Dynamic | LookupFlags.Static))
            {
                if (!seen.Add(uid))
                    continue;
                // Skip grids, our own parts, and anything attached to a wall (lights, APCs, intercoms,
                // the elevator's own panels/buttons/indicators) — those belong to the deck, not the cab.
                if (HasComp<MapGridComponent>(uid) ||
                    HasComp<CEElevatorControllerComponent>(uid) ||
                    HasComp<WallMountComponent>(uid) ||
                    comp.MusicSpeakers.ContainsValue(uid))
                    continue;
                if (!_xformQuery.TryComp(uid, out _))
                    continue;

                // The tile lookup catches entities that merely overlap a footprint tile; restrict to
                // those actually standing on the footprint so the cab moves exactly its own square.
                var entTile = _map.WorldToTile(gridUid, grid, _transform.GetWorldPosition(uid));
                if (!footprint.Contains(entTile))
                    continue;

                result.Add(uid);
            }
        }

        return result;
    }

    private static CEElevatorDirection CEElevatorDirectionFor(CEElevatorControllerComponent comp)
    {
        if (!comp.Moving || comp.TargetDepth == comp.CurrentDepth)
            return CEElevatorDirection.Idle;
        return comp.TargetDepth > comp.CurrentDepth ? CEElevatorDirection.Up : CEElevatorDirection.Down;
    }
}
