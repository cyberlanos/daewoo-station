using Content.Pirate.Shared.Showers;
using Content.Pirate.Shared.Stains.Components;
using Content.Pirate.Server.Stains;
using Content.Shared.Inventory;

namespace Content.Pirate.Server.Shower;

public sealed class ShowerSystem : SharedShowerSystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = null!;
    [Dependency] private readonly StainSystem _stains = null!;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<ShowerComponent>();
        while (query.MoveNext(out var uid, out var shower))
        {
            if (!shower.ToggleShower)
                continue;

            shower.StainCleanAccumulator += frameTime;
            if (shower.StainCleanAccumulator < shower.StainCleanInterval)
                continue;

            shower.StainCleanAccumulator = 0f;

            foreach (var (target, _) in _lookup.GetEntitiesInRange<InventoryComponent>(Transform(uid).Coordinates, shower.StainCleanRange))
                _stains.CleanEntityAndEquipment(target);

            // Rinse stains off loose items lying under the shower (mobs are handled above).
            foreach (var (item, _) in _lookup.GetEntitiesInRange<StainableComponent>(Transform(uid).Coordinates, shower.StainCleanRange))
            {
                if (!HasComp<InventoryComponent>(item))
                    _stains.TryCleanStain(item);
            }
        }
    }
}
