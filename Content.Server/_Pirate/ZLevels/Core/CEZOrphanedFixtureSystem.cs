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
/// A fixture (wall light, sign, wallmount, machine, …) mapped over an EMPTY tile has no tile to snap to,
/// so the engine parents it to the MAP, unanchored and dynamic. It looks fine in the paused mapping view
/// but drifts/falls and is lost in a live round.
///
/// At map-init we re-parent any such map-parented entity onto the grid it sits over and pin it Static so
/// it stays put over the opening. Mobs and items are skipped — they have their own z-falling.
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
        // Only entities parented directly to a map (no grid) are orphans.
        if (xform.GridUid != null || xform.MapUid is not { } mapUid || xform.ParentUid != mapUid)
            return;

        // Skip maps/grids, and mobs/items (those should fall through the hole).
        if (HasComp<MapComponent>(uid) ||
            HasComp<MapGridComponent>(uid) ||
            HasComp<MobStateComponent>(uid) ||
            HasComp<ItemComponent>(uid))
            return;

        var worldPos = _transform.GetWorldPosition(xform);

        // Approximate match so a point over a hole still resolves to the surrounding deck grid.
        var aabb = new Box2(worldPos - new Vector2(0.15f, 0.15f), worldPos + new Vector2(0.15f, 0.15f));
        var grids = new List<Entity<MapGridComponent>>();
        _mapManager.FindGridsIntersecting(xform.MapID, aabb, ref grids, approx: true, includeMap: false);
        if (grids.Count == 0)
            return;

        var deck = grids[0];

        // Disable grid traversal first, or SharedGridTraversalSystem reparents the entity back to the map
        // (TryFindGridAt treats the empty hole tile as "no grid"). Off, it stays bound to the deck grid.
        xform.GridTraversal = false;

        // Re-parent onto the deck grid, preserving world position.
        var localPos = Vector2.Transform(worldPos, _transform.GetInvWorldMatrix(deck.Owner));
        _transform.SetCoordinates(uid, new EntityCoordinates(deck.Owner, localPos));

        // Over a solid tile: anchor normally. Over a hole: pin Static (neither drifts nor z-falls).
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
