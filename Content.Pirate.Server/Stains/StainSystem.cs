using Content.Pirate.Shared.Stains.Components;
using Content.Pirate.Shared.Stains.Systems;
using Content.Shared.Chemistry.Components;
using Content.Shared.Tag;

namespace Content.Pirate.Server.Stains;

public sealed class StainSystem : SharedStainSystem
{
    [Dependency] private readonly TagSystem _tag = null!;

    protected override void OnStained(Entity<StainableComponent> ent, Entity<SolutionComponent> solution)
    {
        base.OnStained(ent, solution);

        _tag.AddTag(ent.Owner, "DNASolutionScannable");
    }

    protected override void OnCleaned(Entity<StainableComponent> ent)
    {
        base.OnCleaned(ent);

        _tag.RemoveTag(ent.Owner, "DNASolutionScannable");
    }
}
