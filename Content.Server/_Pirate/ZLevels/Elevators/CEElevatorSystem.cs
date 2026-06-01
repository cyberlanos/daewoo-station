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
using Content.Shared.Tag;
using Content.Shared.Wall;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
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
    [Dependency] private readonly TagSystem _tag = default!;

    /// <summary>Played at a control when a call/move request is refused (SS13 buzz-two).</summary>
    private static readonly SoundSpecifier DenySound = new SoundPathSpecifier("/Audio/Machines/buzz-two.ogg");

    /// <summary>Wall lights carry this tag but (unlike panels/APCs/intercoms) have no
    /// <see cref="WallMountComponent"/>, so it is needed to recognise them as wall-attached.</summary>
    private static readonly ProtoId<TagPrototype> WallLightTag = "WallLight";

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
        // Skip while paused (e.g. mapping): initializing would bake shaft tiles / speakers into a save.
        // Best-effort otherwise: succeeds only once the z-network has linked this deck's grid; the lazy
        // retry in Update() handles it (grids are linked shortly after map load).
        if (Paused(ent))
            return;
        TryInitialize(ent);
    }

    private void OnControllerShutdown(Entity<CEElevatorControllerComponent> ent, ref ComponentShutdown args)
    {
        if (!TerminatingOrDeleted(ent.Comp.MusicSpeaker))
            QueueDel(ent.Comp.MusicSpeaker);
        ent.Comp.MusicSpeaker = EntityUid.Invalid;
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

        // Capture the home-deck floor pattern as the cab (read-only). This is the tile pattern that
        // travels with the cab; every deck the cab is NOT on stays an open shaft (empty tiles) you can
        // fall into. We deliberately do NOT write any tiles at init — the mapper's layout is left
        // exactly as authored, so a shaft mapped open stays open until the cab actually travels.
        comp.ResolvedCabFloorTiles.Clear();
        Tile? cabOverride = comp.CabFloorTile == null ? null : new Tile(_tileDef[comp.CabFloorTile].TileId);
        comp.ResolvedShaftFloorTile = new Tile(_tileDef[comp.ShaftFloorTile].TileId);
        foreach (var tile in FootprintTiles(comp))
        {
            if (!_map.TryGetTileRef(gridUid, grid, tile, out var tileRef))
                continue;

            var offset = tile - comp.OriginTile;
            comp.ResolvedCabFloorTiles[offset] = cabOverride ?? (tileRef.Tile.IsEmpty ? comp.ResolvedShaftFloorTile : tileRef.Tile);
        }

        comp.CurrentDepth = linked.Depth;
        comp.TargetDepth = linked.Depth;

        DiscoverServedDepths(ent, linked.Depth);

        comp.Initialized = true;

        // A single looping music speaker that rides with the cab (see StepCab). Ambient sound is
        // client-side and per-map, so keeping one speaker on the cab's current deck is exactly what
        // riders hear — no per-deck speakers, no enable/disable juggling across maps.
        if (TryGetDeckGrid(comp, comp.CurrentDepth, out var startGrid, out var startGridComp))
        {
            comp.MusicSpeaker = Spawn(comp.MusicSpeakerProto, FootprintCenter(comp, startGrid, startGridComp));
            _ambient.SetRange(comp.MusicSpeaker, MathF.Max(4f, MathF.Max(comp.Width, comp.Height)));
            _ambient.SetAmbience(comp.MusicSpeaker, true);
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

    /// <summary>Lowest served deck — the bottom of the shaft. Keeps a solid floor; every deck above it
    /// is left as open shaft when the cab is gone.</summary>
    private int BasementDepth(CEElevatorControllerComponent comp)
        => comp.ServedDepths.Count > 0 ? comp.ServedDepths[0] : comp.CurrentDepth;

    /// <summary>
    /// Lays the cab's captured floor pattern on the deck the cab now occupies, or clears it when the cab
    /// leaves. Only the basement (bottom of the shaft) keeps a solid floor (the default shaft tile) when
    /// vacated; every deck above is reopened to empty space, so the crew can fall down the shaft.
    /// </summary>
    private void SetFootprintTiles(CEElevatorControllerComponent comp, EntityUid gridUid, MapGridComponent grid, int depth, bool cabPresent)
    {
        var vacatedTile = depth == BasementDepth(comp) ? comp.ResolvedShaftFloorTile : Tile.Empty;
        foreach (var tile in FootprintTiles(comp))
        {
            var offset = tile - comp.OriginTile;
            var newTile = cabPresent && comp.ResolvedCabFloorTiles.TryGetValue(offset, out var cabTile)
                ? cabTile
                : vacatedTile;

            _map.SetTile(gridUid, grid, tile, newTile);
        }
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
            // Never simulate on a paused map. Maps opened for mapping (znetwork-mapping) are loaded
            // uninitialized and paused, so MapInit never fires there — but Update runs globally. If
            // we initialized here, the lazy setup would write shaft-floor tiles and spawn music
            // speakers that then get baked into the saved map. Stay completely inert until the map
            // is actually running (i.e. a live round).
            if (Paused(uid))
                continue;

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

    /// <summary>Moves the cab one deck toward its target: crush, lay floor, carry riders, restore the origin shaft floor.</summary>
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

        // 3) Lay the cab floor on the destination deck FIRST, so arriving anchored parts have a tile to
        //    re-anchor onto.
        SetFootprintTiles(comp, toGridUid, toGrid, toDepth, true);

        // 4) Carry everything riding the cab onto the destination deck. We reparent each rider directly
        //    to the deck grid the controller resolved (toGridUid), preserving its grid-LOCAL position so
        //    it lands exactly over the same footprint tile the cab floor was just written to. This is
        //    deterministic regardless of which deck the rider is currently on — unlike TryMove, which
        //    re-resolves the target from the rider's own grid and can apply a stair-exit landing nudge,
        //    causing anchored platform parts to drift off the footprint and stop being carried on later
        //    legs. Anchored furniture/machines are unanchored, moved, then re-anchored on arrival.
        //    IMPORTANT: this runs BEFORE the origin deck is cleared (step 5). Clearing an upper deck's
        //    footprint to empty space unanchors anything still standing on it; doing it first would flip
        //    each rider's Anchored flag to false here, so it would arrive un-anchored and — since the
        //    platform object has no physics — become impossible to gather on the next leg.
        foreach (var rider in riders)
        {
            if (!_xformQuery.TryComp(rider, out var rxform))
                continue;

            var wasAnchored = rxform.Anchored;
            if (wasAnchored)
                _transform.Unanchor(rider, rxform);

            var localPos = Vector2.Transform(_transform.GetWorldPosition(rider), _transform.GetInvWorldMatrix(fromGridUid));
            _zLevels.TeleportToZLevelCoordinates(rider, new EntityCoordinates(toGridUid, localPos), toDepth, dir);

            if (wasAnchored && _xformQuery.TryComp(rider, out var movedXform))
                _transform.AnchorEntity(rider, movedXform);
        }

        // 5) Now that the riders have left, vacate the origin deck: the basement keeps its solid shaft
        //    floor, upper decks reopen to empty shaft.
        SetFootprintTiles(comp, fromGridUid, fromGrid, fromDepth, false);

        comp.CurrentDepth = toDepth;

        // Carry the music speaker onto the cab's new deck so the muzak follows the cab.
        if (!TerminatingOrDeleted(comp.MusicSpeaker))
            _zLevels.TeleportToZLevelCoordinates(comp.MusicSpeaker, FootprintCenter(comp, toGridUid, toGrid), toDepth, dir);

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

            var anchored = _map.GetAnchoredEntitiesEnumerator(gridUid, grid, tile);
            while (anchored.MoveNext(out var anchoredUid))
            {
                if (anchoredUid is not { } anchoredEnt)
                    continue;
                if (!seen.Add(anchoredEnt))
                    continue;
                if (!CanRideCab(anchoredEnt, comp))
                    continue;

                var anchoredTile = _map.WorldToTile(gridUid, grid, _transform.GetWorldPosition(anchoredEnt));
                if (!footprint.Contains(anchoredTile))
                    continue;

                result.Add(anchoredEnt);
            }

            foreach (var uid in _lookup.GetEntitiesInTile(tileRef, LookupFlags.Dynamic | LookupFlags.Static))
            {
                if (!seen.Add(uid))
                    continue;
                if (!CanRideCab(uid, comp))
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

    /// <summary>
    /// False for things that belong to the deck rather than the cab and so must NOT be carried:
    /// grids, the elevator's own controller/speakers, and anything attached to a wall. Wall-attached
    /// fixtures are detected by <see cref="WallMountComponent"/> (panels, call buttons, indicators,
    /// APCs, intercoms, etc.) or the <c>WallLight</c> tag (light fixtures, which lack that component).
    /// Everything else standing on the footprint — mobs, loose items, anchored furniture/machines —
    /// rides the cab, matching SS13's transport_contents.
    /// </summary>
    private bool CanRideCab(EntityUid uid, CEElevatorControllerComponent comp)
    {
        if (!_xformQuery.HasComponent(uid))
            return false;
        if (HasComp<MapGridComponent>(uid) ||
            HasComp<CEElevatorControllerComponent>(uid) ||
            HasComp<WallMountComponent>(uid) ||
            _tag.HasTag(uid, WallLightTag) ||
            uid == comp.MusicSpeaker)
            return false;

        return true;
    }

    private static CEElevatorDirection CEElevatorDirectionFor(CEElevatorControllerComponent comp)
    {
        if (!comp.Moving || comp.TargetDepth == comp.CurrentDepth)
            return CEElevatorDirection.Idle;
        return comp.TargetDepth > comp.CurrentDepth ? CEElevatorDirection.Up : CEElevatorDirection.Down;
    }
}
