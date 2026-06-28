using System.Linq;
using Content.Pirate.Shared.Stains.Components;
using Content.Shared.Chemistry.Reaction;
using Content.Shared.Chemistry.Reagent;
using Content.Goobstation.Maths.FixedPoint;
using Content.Shared.Inventory;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.Pirate.Server.Stains;

/// <summary>
/// Cleans stains off anything sitting on a tile a cleaning reagent reacts with - cleaner foam, a
/// space-cleaner spray puddle, a mopped floor, etc. Mobs also get their worn/held items cleaned.
/// </summary>
[DataDefinition]
public sealed partial class CleanStainsTileReaction : ITileReaction
{
    FixedPoint2 ITileReaction.TileReact(TileRef tile,
        ReagentPrototype reagent,
        FixedPoint2 reactVolume,
        IEntityManager entityManager,
        List<ReagentData>? data)
    {
        var lookup = entityManager.System<EntityLookupSystem>();
        var stains = entityManager.System<StainSystem>();

        foreach (var entity in lookup.GetLocalEntitiesIntersecting(tile, 0f).ToArray())
        {
            if (entityManager.HasComponent<InventoryComponent>(entity))
                stains.CleanEntityAndEquipment(entity);
            else if (entityManager.HasComponent<StainableComponent>(entity))
                stains.TryCleanStain(entity);
        }

        // Cleaning stains is a free side effect; don't consume the reagent so it still does its main job.
        return FixedPoint2.Zero;
    }
}
