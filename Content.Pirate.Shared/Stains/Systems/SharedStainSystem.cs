#region Pirate: stains
using Content.Pirate.Shared.Stains.Components;
using Content.Goobstation.Common.Footprints;
using Content.Shared._Pirate.Fluids;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Body.Components;
using Content.Shared.Body.Systems;
using Content.Shared.Chemistry;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.DoAfter;
using Content.Shared.Fluids;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Item;
using Content.Shared.Popups;
using Content.Shared.Standing;
using Content.Shared.Verbs;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Melee.Events;
using Content.Goobstation.Maths.FixedPoint;
using Robust.Shared.Containers;
using Robust.Shared.Network;
using Robust.Shared.Random;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Pirate.Shared.Stains.Systems;

[Serializable, NetSerializable]
public enum StainVisuals : byte
{
    /// <summary>Current stain volume. Mirrored into appearance data so visuals refresh when it changes.</summary>
    Volume,

    /// <summary>Exposed body slots (feet/gloves) that are stained, for bare-body humanoid overlays.</summary>
    BodySlots
}

public abstract class SharedStainSystem : EntitySystem
{
    private const string FootprintSolutionName = "print";
    private const string ShoesSlot = "shoes";
    private const string GlovesSlot = "gloves";

    [Dependency] private readonly SharedSolutionContainerSystem _solution = null!;
    [Dependency] private readonly SharedItemSystem _item = null!;
    [Dependency] private readonly SharedAppearanceSystem _appearance = null!;
    [Dependency] private readonly SharedContainerSystem _container = null!;
    [Dependency] private readonly SharedHandsSystem _hands = null!;
    [Dependency] private readonly INetManager _net = null!;
    [Dependency] private readonly InventorySystem _inventory = null!;
    [Dependency] private readonly SharedBloodstreamSystem _bloodstream = null!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = null!;
    [Dependency] private readonly SharedPuddleSystem _puddle = null!;
    [Dependency] private readonly SharedPopupSystem _popup = null!;
    [Dependency] private readonly IRobustRandom _random = null!;
    [Dependency] private readonly IGameTiming _timing = null!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<StainableComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<StainableComponent, SpilledOnEvent>(OnSpilledOn);
        SubscribeLocalEvent<StainableComponent, InventoryRelayedEvent<SpilledOnEvent>>(OnInventorySpilledOn);
        SubscribeLocalEvent<StainableComponent, GetVerbsEvent<Verb>>(OnGetVerbs);
        SubscribeLocalEvent<StainableComponent, WringStainDoAfterEvent>(OnWring);
        SubscribeLocalEvent<StainableComponent, SolutionContainerChangedEvent>(OnSolutionChanged);
        SubscribeLocalEvent<StainableComponent, ReactionEntityEvent>(OnReaction);
        SubscribeLocalEvent<InventoryComponent, ReactionEntityEvent>(OnMobReaction);
        SubscribeLocalEvent<HandsComponent, SpilledOnEvent>(OnHandsSpilledOn);
        SubscribeLocalEvent<FootprintOwnerComponent, SpilledOnEvent>(OnFootSpilledOn);
        SubscribeLocalEvent<MeleeWeaponComponent, MeleeHitEvent>(OnMeleeHit);
    }

    private void OnSolutionChanged(Entity<StainableComponent> ent, ref SolutionContainerChangedEvent args)
    {
        if (args.SolutionId == ent.Comp.SolutionName)
            UpdateVisuals(ent);
    }

    private void OnMapInit(Entity<StainableComponent> ent, ref MapInitEvent args)
    {
        if (!_solution.TryGetSolution(ent.Owner, ent.Comp.SolutionName, out _) && _net.IsServer)
            _solution.EnsureSolution(ent.Owner, ent.Comp.SolutionName, out _, ent.Comp.MaxStainVolume);

        if (_solution.TryGetSolution(ent.Owner, ent.Comp.SolutionName, out var sol))
            sol.Value.Comp.Solution.CanReact = false;
    }

    private void OnSpilledOn(Entity<StainableComponent> ent, ref SpilledOnEvent args)
    {
        if (args.Handled)
            return;

        if (HasComp<FootprintOwnerComponent>(ent.Owner) &&
            (args.TargetSlots & (SlotFlags.FEET | SlotFlags.GLOVES)) != 0)
        {
            return;
        }

        TryStain(ent, args.Solution);
    }

    private void OnInventorySpilledOn(Entity<StainableComponent> ent, ref InventoryRelayedEvent<SpilledOnEvent> args)
    {
        if (args.Args.Handled)
            return;

        if (TryStain(ent, args.Args.Solution.Clone()) && args.Args.TargetSlots == SlotFlags.FEET)
            args.Args.Handled = true;
    }

    private void OnHandsSpilledOn(Entity<HandsComponent> ent, ref SpilledOnEvent args)
    {
        if ((args.TargetSlots & SlotFlags.GLOVES) != 0 &&
            !_inventory.TryGetSlotEntity(ent.Owner, GlovesSlot, out _))
        {
            var stainable = EnsureComp<StainableComponent>(ent.Owner);
            stainable.BodyStainSlots |= SlotFlags.GLOVES;

            if (TryStain((ent.Owner, stainable), args.TargetSlots == SlotFlags.GLOVES ? args.Solution : args.Solution.Clone()) &&
                args.TargetSlots == SlotFlags.GLOVES)
            {
                args.Handled = true;
            }
        }

        if (!args.StainHeldItems)
            return;

        foreach (var handId in ent.Comp.Hands.Keys)
        {
            if (!_hands.TryGetHeldItem(ent.AsNullable(), handId, out var held))
                continue;

            RaiseLocalEvent(held.Value, args);

            if (args.Solution.Volume <= 0)
                break;
        }
    }

    private void OnFootSpilledOn(Entity<FootprintOwnerComponent> ent, ref SpilledOnEvent args)
    {
        if (args.Handled)
            return;

        if ((args.TargetSlots & SlotFlags.FEET) == 0)
            return;

        if (_inventory.TryGetSlotEntity(ent.Owner, ShoesSlot, out var shoes) &&
            TryComp<StainableComponent>(shoes, out var shoeStainable))
        {
            if (TryStain((shoes.Value, shoeStainable), args.TargetSlots == SlotFlags.FEET ? args.Solution : args.Solution.Clone()) &&
                args.TargetSlots == SlotFlags.FEET)
            {
                args.Handled = true;
            }

            return;
        }

        var stainable = EnsureComp<StainableComponent>(ent.Owner);
        stainable.BodyStainSlots |= SlotFlags.FEET;

        if (TryStain((ent.Owner, stainable), args.TargetSlots == SlotFlags.FEET ? args.Solution : args.Solution.Clone()) &&
            args.TargetSlots == SlotFlags.FEET)
        {
            args.Handled = true;
        }
    }

    private bool TryStain(Entity<StainableComponent> ent, Solution solution)
    {
        if (IsStainBlocked(ent))
            return false;

        if (!_solution.TryGetSolution(ent.Owner, ent.Comp.SolutionName, out var stainSolution))
        {
            if (!_net.IsServer ||
                !_solution.EnsureSolution(ent.Owner, ent.Comp.SolutionName, out _, ent.Comp.MaxStainVolume) ||
                !_solution.TryGetSolution(ent.Owner, ent.Comp.SolutionName, out stainSolution))
            {
                return false;
            }

            stainSolution.Value.Comp.Solution.CanReact = false;
        }

        // Once an item is fully stained, stop re-staining it. Otherwise ambient sources (notably bleeding,
        // which sprays self + neighbours every puddle tick) keep topping it up forever and flood events/logs.
        if (stainSolution.Value.Comp.Solution.Volume >= ent.Comp.MaxStainVolume)
            return false;

        var transferAmount = FixedPoint2.Min(solution.Volume, ent.Comp.SpillTransferAmount);
        var split = solution.SplitSolution(transferAmount);

        for (var i = split.Contents.Count - 1; i >= 0; i--)
        {
            if (split.Contents[i].Reagent.Prototype == "Water")
                split.RemoveReagent(split.Contents[i].Reagent, split.Contents[i].Quantity);
        }

        if (split.Volume <= 0)
            return false;

        _solution.TryAddSolution(stainSolution.Value, split);
        if (_net.IsServer)
            EnsureComp<AppearanceComponent>(ent.Owner);

        UpdateVisuals(ent);
        OnStained(ent, stainSolution.Value);
        LogStain($"stained {ToPrettyString(ent.Owner)} +{split.Volume}u [{Describe(split)}] total={stainSolution.Value.Comp.Solution.Volume}u");
        return true;
    }

    /// <summary>Server-side diagnostic logging for stains. Gated to the server to avoid client prediction spam.</summary>
    private void LogStain(string message)
    {
        if (_net.IsServer)
            Log.Info($"[stains] {message}");
    }

    private static string Describe(Solution solution)
    {
        if (solution.Contents.Count == 0)
            return "empty";

        var parts = new List<string>(solution.Contents.Count);
        foreach (var content in solution.Contents)
            parts.Add($"{content.Reagent.Prototype}:{content.Quantity}");

        return string.Join(",", parts);
    }

    protected virtual void OnStained(Entity<StainableComponent> ent, Entity<SolutionComponent> solution)
    {
    }

    protected virtual void OnCleaned(Entity<StainableComponent> ent)
    {
    }

    private void OnReaction(Entity<StainableComponent> ent, ref ReactionEntityEvent args)
    {
        if (args.Method != ReactionMethod.Touch || !IsCleaningReagent(args.Reagent.ID))
            return;

        TryCleanStain(ent.Owner);
    }

    private void OnMobReaction(Entity<InventoryComponent> ent, ref ReactionEntityEvent args)
    {
        if (args.Method != ReactionMethod.Touch || args.Reagent.ID != "SpaceCleaner")
            return;

        CleanEntityAndEquipment(ent.Owner);
    }

    private void OnMeleeHit(Entity<MeleeWeaponComponent> ent, ref MeleeHitEvent args)
    {
        if (!args.IsHit ||
            args.Weapon == args.User ||
            args.HitEntities.Count == 0 ||
            args.BaseDamage.GetTotal() <= 0)
        {
            return;
        }

        var damage = args.BaseDamage.GetTotal();
        if (!_random.Prob(Math.Clamp((25f + damage.Float() * 2f) / 100f, 0f, 1f)))
            return;

        var solution = new Solution();

        foreach (var target in args.HitEntities)
        {
            if (!HasComp<BloodstreamComponent>(target))
                continue;

            solution.AddReagent(new ReagentId("Blood", _bloodstream.GetEntityBloodData(target)), 0.5f);
        }

        if (solution.Volume <= 0)
            return;

        var stainable = EnsureComp<StainableComponent>(ent.Owner);
        TryStain((ent.Owner, stainable), solution.Clone());

        // Gloves always; stronger hits progressively reach the suit/uniform, then shoes, mask and headwear.
        var bloodiedSlots = SlotFlags.GLOVES;
        if (damage >= 20 || damage >= 15 && _random.Prob(0.25f))
        {
            bloodiedSlots |= SlotFlags.INNERCLOTHING | SlotFlags.OUTERCLOTHING;
            if (_random.Prob(0.33f))
                bloodiedSlots |= SlotFlags.FEET;
            if (damage >= 24 && _random.Prob(0.33f))
                bloodiedSlots |= SlotFlags.MASK;
            if (damage >= 30 && _random.Prob(0.33f))
                bloodiedSlots |= SlotFlags.HEAD;
        }

        LogStain($"melee: {ToPrettyString(args.User)} hit with {ToPrettyString(ent.Owner)} dmg={damage} attackerSlots={bloodiedSlots}");

        // Route the attacker splatter through a spill so bare hands/feet stain when the relevant slot is empty.
        RaiseLocalEvent(args.User, new SpilledOnEvent(args.User, solution.Clone(), bloodiedSlots));
        StainVictimSlots(args.User, args.HitEntities, solution);
    }

    private void StainEquippedSlots(EntityUid uid, Solution source, SlotFlags slots)
    {
        if (!_inventory.TryGetContainerSlotEnumerator(uid, out var enumerator, slots))
            return;

        while (enumerator.NextItem(out var item))
        {
            if (!TryComp<StainableComponent>(item, out var stainable))
                continue;

            TryStain((item, stainable), source.Clone());
        }
    }

    private void StainVictimSlots(EntityUid user, IReadOnlyList<EntityUid> targets, Solution source)
    {
        var targetPart = TryComp<TargetingComponent>(user, out var targeting)
            ? PickHitPart(targeting)
            : TargetBodyPart.Chest;

        var victimSlots = targetPart switch
        {
            TargetBodyPart.Head => SlotFlags.MASK | SlotFlags.HEAD | (_random.Prob(0.33f) ? SlotFlags.EYES : SlotFlags.NONE),
            TargetBodyPart.Chest => SlotFlags.INNERCLOTHING | SlotFlags.OUTERCLOTHING,
            _ => SlotFlags.NONE
        };

        if (victimSlots == SlotFlags.NONE)
            return;

        foreach (var target in targets)
            StainEquippedSlots(target, source, victimSlots);
    }

    private TargetBodyPart PickHitPart(TargetingComponent targeting)
    {
        if (!targeting.TargetOdds.TryGetValue(targeting.Target, out var odds))
            return targeting.Target;

        var roll = _random.NextFloat();
        var total = 0f;

        foreach (var (part, chance) in odds)
        {
            total += chance;
            if (roll <= total)
                return part;
        }

        return targeting.Target;
    }

    private bool IsStainBlocked(Entity<StainableComponent> ent)
    {
        if (!_container.TryGetContainingContainer(ent.Owner, out var container) ||
            !TryComp<InventoryComponent>(container.Owner, out var inv))
        {
            return false;
        }

        if (!_inventory.TryGetSlot(container.Owner, container.ID, out var slotDef, inv))
            return false;

        foreach (var slot in inv.Slots)
        {
            if (!_inventory.TryGetSlotEntity(container.Owner, slot.Name, out var slotEnt, inv))
                continue;

            if (TryComp<StainBlockerComponent>(slotEnt, out var blocker) &&
                (blocker.BlockedSlots & slotDef.SlotFlags) != 0)
            {
                return true;
            }
        }

        return false;
    }

    public bool TryCleanStain(EntityUid uid)
    {
        if (!TryComp<StainableComponent>(uid, out var stainable) ||
            !_solution.TryGetSolution(uid, stainable.SolutionName, out var solComp, out var sol) ||
            sol.Volume <= 0)
        {
            return false;
        }

        LogStain($"cleaned {ToPrettyString(uid)} (was {sol.Volume}u)");
        stainable.BodyStainSlots = SlotFlags.NONE;
        _solution.RemoveAllSolution(solComp.Value);
        UpdateVisuals((uid, stainable));
        OnCleaned((uid, stainable));
        return true;
    }

    public bool CleanEntityAndEquipment(EntityUid uid)
    {
        var cleaned = false;
        var seen = new HashSet<EntityUid>();

        cleaned |= TryCleanStain(uid);
        seen.Add(uid);

        if (_inventory.TryGetContainerSlotEnumerator(uid, out var enumerator, SlotFlags.WITHOUT_POCKET))
        {
            while (enumerator.NextItem(out var item))
                cleaned |= TryCleanSeen(item, seen);
        }

        if (TryComp<HandsComponent>(uid, out var hands))
        {
            foreach (var held in _hands.EnumerateHeld((uid, hands)))
                cleaned |= TryCleanSeen(held, seen);
        }

        cleaned |= TryCleanFootprints(uid);

        return cleaned;
    }

    private bool TryCleanSeen(EntityUid uid, HashSet<EntityUid> seen)
    {
        if (!seen.Add(uid))
            return false;

        return TryCleanStain(uid);
    }

    private bool TryCleanFootprints(EntityUid uid)
    {
        if (!HasComp<FootprintOwnerComponent>(uid) ||
            !_solution.TryGetSolution(uid, FootprintSolutionName, out var solComp, out var sol) ||
            sol.Volume <= 0)
        {
            return false;
        }

        _solution.RemoveAllSolution(solComp.Value);
        return true;
    }

    private static bool IsCleaningReagent(string reagent)
    {
        return reagent is "Water" or "SoapReagent" or "SpaceCleaner";
    }

    /// <summary>
    /// Mirrors the current stain state (volume + body slots) into appearance data so the client rebuilds
    /// visuals, but only when something actually changed.
    /// </summary>
    /// <remarks>
    /// Stains are server-authoritative, so the client only acts on the leading predicted tick. The change
    /// check compares against <see cref="AppearanceComponent"/> rather than the <see cref="StainableComponent"/>:
    /// the latter is networked-but-stateless, so prediction rebuilds it with default fields every tick and any
    /// guard stored on it never matches. Encoding the volume into appearance data also makes the SetData calls
    /// no-ops when unchanged, so repeated triggers (prediction, bleeding, wearer re-renders) don't churn.
    /// </remarks>
    public void UpdateVisuals(Entity<StainableComponent> ent)
    {
        if (!_timing.IsFirstTimePredicted)
            return;

        if (!TryComp<AppearanceComponent>(ent.Owner, out var app))
            return;

        var volume = _solution.TryGetSolution(ent.Owner, ent.Comp.SolutionName, out _, out var sol)
            ? sol.Volume
            : FixedPoint2.Zero;
        var slots = ent.Comp.BodyStainSlots;

        if (_appearance.TryGetData(ent.Owner, StainVisuals.Volume, out FixedPoint2 prevVolume, app)
            && prevVolume == volume
            && _appearance.TryGetData(ent.Owner, StainVisuals.BodySlots, out SlotFlags prevSlots, app)
            && prevSlots == slots)
        {
            return;
        }

        _appearance.SetData(ent.Owner, StainVisuals.Volume, volume, app);
        _appearance.SetData(ent.Owner, StainVisuals.BodySlots, slots, app);
        _item.VisualsChanged(ent.Owner);

        if (_container.TryGetContainingContainer(ent.Owner, out var container) &&
            TryComp<AppearanceComponent>(container.Owner, out var wearerApp))
        {
            _appearance.QueueUpdate(container.Owner, wearerApp);
            Dirty(container.Owner, wearerApp);
        }
    }

    private void OnGetVerbs(Entity<StainableComponent> ent, ref GetVerbsEvent<Verb> args)
    {
        if (!args.CanInteract || !args.CanAccess || args.Using != ent.Owner)
            return;

        if (!_solution.TryGetSolution(ent.Owner, ent.Comp.SolutionName, out _, out var sol) || sol.Volume <= 0)
            return;

        var user = args.User;
        args.Verbs.Add(new Verb
        {
            Text = Loc.GetString("stain-verb-wring"),
            Icon = new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/bubbles.svg.192dpi.png")),
            Act = () =>
            {
                _doAfter.TryStartDoAfter(new DoAfterArgs(EntityManager, user, ent.Comp.WringDoAfterDuration, new WringStainDoAfterEvent(), ent.Owner)
                {
                    BreakOnMove = true,
                    BreakOnDamage = true,
                    NeedHand = true
                });
            }
        });
    }

    private void OnWring(Entity<StainableComponent> ent, ref WringStainDoAfterEvent args)
    {
        if (args.Handled || args.Cancelled)
            return;
        args.Handled = true;

        if (!_solution.TryGetSolution(ent.Owner, ent.Comp.SolutionName, out var solComp, out var sol))
            return;

        var split = _solution.SplitSolution(solComp.Value, sol.Volume);
        UpdateVisuals(ent);

        if (_puddle.TrySpillAt(args.User, split, out _))
            _popup.PopupEntity(Loc.GetString("stain-verb-wring-success"), args.User, args.User);
    }
}
#endregion
