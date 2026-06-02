using System.Numerics;
using Content.Shared.Item;
using Content.Shared.Mobs.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.Server._Pirate.ZLevels.Core;

/// <summary>
/// Re-homes "orphaned" fixtures onto the deck grid they belong to.
///
/// When a mapper places an anchored fixture (a wall light, sign, wallmount, machine, …) directly over
/// an EMPTY tile — e.g. a hole in a deck that opens onto the level below — the engine cannot attach it
/// to the grid (there is no tile to snap to), so it parents the entity to the MAP instead of the grid
/// and saves it unanchored + dynamic. In the paused mapping view that loose body just hangs there and
/// looks fine, but in a live round a grid-less, unanchored, dynamic fixture drifts/falls and is lost.
///
/// At map-init we catch any such map-parented entity that is sitting over a grid, re-parent it onto that
/// grid (so it lives on the deck like any other fixture) and pin it as a Static body so it stays put
/// over the opening instead of vanishing. Mobs and items are intentionally skipped — they have their own
/// z-falling and SHOULD fall through a hole. This is fully general: it is not specific to lights.
/// </summary>
public sealed class CEZOrphanedFixtureSystem : EntitySystem
{
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<TransformComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(EntityUid uid, TransformComponent xform, ref MapInitEvent args)
    {
        // Only entities parented DIRECTLY to a map (no grid) are orphans. Everything that anchored
        // normally already lives on its grid and is left untouched.
        if (xform.GridUid != null || xform.MapUid is not { } mapUid || xform.ParentUid != mapUid)
            return;

        // Skip maps/grids themselves, and skip mobs/items — those are meant to fall through a hole,
        // not be pinned over it.
        if (HasComp<MapComponent>(uid) ||
            HasComp<MapGridComponent>(uid) ||
            HasComp<MobStateComponent>(uid) ||
            HasComp<ItemComponent>(uid))
            return;

        var worldPos = _transform.GetWorldPosition(xform);

        // Approximate match so a point over a hole still resolves to the deck grid that surrounds it
        // (the empty tile has no fixture, so a non-approximate test would miss it).
        var aabb = new Box2(worldPos - new Vector2(0.15f, 0.15f), worldPos + new Vector2(0.15f, 0.15f));
        var grids = new List<Entity<MapGridComponent>>();
        _mapManager.FindGridsIntersecting(xform.MapID, aabb, ref grids, approx: true, includeMap: false);
        if (grids.Count == 0)
            return;

        var deck = grids[0];

        // Disable grid traversal FIRST. Otherwise the engine's SharedGridTraversalSystem immediately
        // reparents a non-anchored entity back to the map the moment it sits over an empty (hole) tile —
        // TryFindGridAt treats empty tiles as "no grid". With traversal off, the entity stays bound to the
        // deck grid over the opening (it's pinned Static below, so the broadphase never loses it).
        xform.GridTraversal = false;

        // Re-parent onto the deck grid, preserving world position.
        var localPos = Vector2.Transform(worldPos, _transform.GetInvWorldMatrix(deck.Owner));
        _transform.SetCoordinates(uid, new EntityCoordinates(deck.Owner, localPos));

        // If it happens to sit over a solid tile, anchor it properly; otherwise pin it as a Static body
        // so it stays fixed over the opening (a Static body neither drifts nor z-falls).
        var tile = _map.TileIndicesFor(deck.Owner, deck.Comp, Transform(uid).Coordinates);
        if (_map.TryGetTileRef(deck.Owner, deck.Comp, tile, out var tileRef) && !tileRef.Tile.IsEmpty)
        {
            _transform.AnchorEntity(uid, Transform(uid));
        }
        else if (TryComp<PhysicsComponent>(uid, out var body))
        {
            _physics.SetBodyType(uid, BodyType.Static, body: body);
        }
    }
}
