namespace Content.Shared._Pirate.ZLevels.Damage.FallCushion;

/// <summary>
/// Softens or negates fall damage for entities that land on a <see cref="CEFallCushionComponent"/> surface.
/// </summary>
public sealed class CEFallCushionSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CEFallCushionComponent, CEZFallingDamageCalculateEvent>(OnFallingDamageCalculate);
    }

    private void OnFallingDamageCalculate(Entity<CEFallCushionComponent> ent, ref CEZFallingDamageCalculateEvent args)
    {
        // The event also fires on the faller's own components; a cushion only applies when something
        // falls onto it, not when it is itself the faller.
        if (ent.Owner == args.Fallen)
            return;

        args.DamageMultiplier *= ent.Comp.DamageMultiplier;
        args.StunMultiplier *= ent.Comp.StunMultiplier;
    }
}
