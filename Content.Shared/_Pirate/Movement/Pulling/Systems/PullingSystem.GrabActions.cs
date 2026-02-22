using System;
using Content.Goobstation.Common.MartialArts;
using Content.Goobstation.Maths.FixedPoint;
using Content.Shared._Pirate.Movement.Pulling.Events;
using Content.Shared._Shitmed.Medical.Surgery.Traumas;
using Content.Shared._Shitmed.Medical.Surgery.Traumas.Components;
using Content.Shared._Shitmed.Medical.Surgery.Wounds.Components;
using Content.Shared._Shitmed.Medical.Surgery.Wounds.Systems;
using Content.Shared._Shitmed.Medical.Surgery.Traumas.Systems;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Body.Components;
using Content.Shared.Body.Part;
using Content.Shared.Body.Systems;
using Content.Shared.DoAfter;
using Content.Shared.IdentityManagement;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Popups;
using Content.Shared.Item;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Melee.Events;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Shared.Movement.Pulling.Systems;

public sealed partial class PullingSystem
{
    /// <summary>
    /// prevent infinite loop after attacking the body part
    /// </summary>
    private readonly Dictionary<EntityUid, GrabFollowupSuppression> _followupSuppressions = new();
    private static readonly TimeSpan suppressionDuration = TimeSpan.FromSeconds(1);

    private readonly record struct GrabFollowupSuppression(EntityUid Target, TimeSpan ExpiresAt);

    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedBodySystem _body = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly SharedMeleeWeaponSystem _melee = default!;

    private void InitializePirateGrabActions()
    {
        SubscribeLocalEvent<PullableComponent, BeforeHarmfulActionEvent>(OnPullableBeforeHarmfulAction);
        SubscribeLocalEvent<PullerComponent, BoneCrushDoAfterEvent>(OnGrabBoneBreakDoAfter);
        SubscribeLocalEvent<PullerComponent, ThroatSliceDoAfterEvent>(OnGrabThroatSliceDoAfter);
    }

    private void OnPullableBeforeHarmfulAction(Entity<PullableComponent> ent, ref BeforeHarmfulActionEvent args)
    {
        if (args.Type != HarmfulActionType.Harm)
            return;

        if (TryConsumeGrabFollowupSuppression(args.User, ent.Owner))
            return;

        if (!TryComp<PullerComponent>(args.User, out var pullerComp))
            return;

        if (!_handsSystem.TryGetActiveItem(args.User, out var used)
            || used is not { } usedItem)
            return;

        if (CanStartThroatSliceDoAfter((args.User, pullerComp), ent, usedItem, out var throatSlicePart))
        {
            var sliceDoAfter = new DoAfterArgs(
                EntityManager,
                args.User,
                pullerComp.ThroatSliceDelay,
                new ThroatSliceDoAfterEvent(throatSlicePart),
                args.User,
                target: ent.Owner,
                used: usedItem)
            {
                NeedHand = true,
                BreakOnMove = true,
                BreakOnDamage = true,
                MultiplyDelay = false,
                ShowTo = ent.Owner,
            };

            if (!_doAfter.TryStartDoAfter(sliceDoAfter))
                return;

            if (_netManager.IsServer)
            {
                var throatLimb = Loc.GetString("popup-grab-limb-throat");
                var tool = Identity.Entity(usedItem, EntityManager);
                _popup.PopupEntity(
                    Loc.GetString("popup-grab-throat-slice-start-self",
                        ("target", Identity.Entity(ent.Owner, EntityManager)),
                        ("tool", tool),
                        ("limb", throatLimb)),
                    args.User,
                    args.User,
                    PopupType.MediumCaution);

                _popup.PopupEntity(
                    Loc.GetString("popup-grab-throat-slice-start-target",
                        ("puller", Identity.Entity(args.User, EntityManager)),
                        ("tool", tool),
                        ("limb", throatLimb)),
                    ent.Owner,
                    ent.Owner,
                    PopupType.MediumCaution);

                _popup.PopupEntity(
                    Loc.GetString("popup-grab-throat-slice-start-others",
                        ("puller", Identity.Entity(args.User, EntityManager)),
                        ("target", Identity.Entity(ent.Owner, EntityManager)),
                        ("tool", tool),
                        ("limb", throatLimb)),
                    ent.Owner,
                    Filter.Pvs(ent.Owner, entityManager: EntityManager)
                        .RemovePlayerByAttachedEntity(args.User)
                        .RemovePlayerByAttachedEntity(ent.Owner),
                    true,
                    PopupType.MediumCaution);
            }

            args.Cancel();
            return;
        }

        if (!CanStartBoneBreakDoAfter((args.User, pullerComp), ent, usedItem, out var targetPart))
            return;

        var doAfter = new DoAfterArgs(
            EntityManager,
            args.User,
            pullerComp.BoneBreakDelay,
            new BoneCrushDoAfterEvent(targetPart),
            args.User,
            target: ent.Owner,
            used: usedItem)
        {
            NeedHand = true,
            BreakOnMove = true,
            BreakOnDamage = true,
            MultiplyDelay = false,
            ShowTo = ent.Owner,
        };

        if (!_doAfter.TryStartDoAfter(doAfter))
            return;

        if (_netManager.IsServer)
        {
            var limb = GetGrabLimbName(targetPart);
            var tool = Identity.Entity(usedItem, EntityManager);
            _popup.PopupEntity(
                Loc.GetString("popup-grab-bone-break-start-self",
                    ("target", Identity.Entity(ent.Owner, EntityManager)),
                    ("tool", tool),
                    ("limb", limb)),
                args.User,
                args.User,
                PopupType.MediumCaution);

            _popup.PopupEntity(
                Loc.GetString("popup-grab-bone-break-start-target",
                    ("puller", Identity.Entity(args.User, EntityManager)),
                    ("tool", tool),
                    ("limb", limb)),
                ent.Owner,
                ent.Owner,
                PopupType.MediumCaution);

            _popup.PopupEntity(
                Loc.GetString("popup-grab-bone-break-start-others",
                    ("puller", Identity.Entity(args.User, EntityManager)),
                    ("target", Identity.Entity(ent.Owner, EntityManager)),
                    ("tool", tool),
                    ("limb", limb)),
                ent.Owner,
                Filter.Pvs(ent.Owner, entityManager: EntityManager)
                    .RemovePlayerByAttachedEntity(args.User)
                    .RemovePlayerByAttachedEntity(ent.Owner),
                    true,
                    PopupType.MediumCaution);
        }

        args.Cancel();
    }

    private void OnGrabBoneBreakDoAfter(Entity<PullerComponent> ent, ref BoneCrushDoAfterEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = true;

        if (args.Cancelled || args.Target is not { } target || args.Used is not { } used)
        {
            PopupGrabActionCancelled(ent.Owner, "popup-grab-bone-break-cancel-interrupted");
            return;
        }

        if (!TryComp<PullableComponent>(target, out var pullableComp))
        {
            PopupGrabActionCancelled(ent.Owner, "popup-grab-bone-break-cancel-target-lost");
            return;
        }

        if (!CanStartBoneBreakDoAfter(ent, (target, pullableComp), used, out var targetPart)
            || targetPart != args.TargetPart)
        {
            PopupGrabActionCancelled(ent.Owner, "popup-grab-bone-break-cancel-conditions");
            return;
        }

        if (!TryGetLegBone(target, args.TargetPart, out var bone, out var broken))
        {
            if (broken)
                PopupGrabActionCancelled(ent.Owner, "popup-grab-bone-break-cancel-already-broken");
            else
                PopupGrabActionCancelled(ent.Owner, "popup-grab-bone-break-cancel-no-bone");
            return;
        }

        var bluntDamage = GetBluntDamage(used);
        if (bluntDamage <= 0f)
        {
            PopupGrabActionCancelled(ent.Owner, "popup-grab-bone-break-cancel-invalid-weapon");
            return;
        }

        var chance = Math.Clamp(
            ent.Comp.BoneBreakBaseChance + bluntDamage * ent.Comp.BoneBreakChancePerBluntDamage,
            ent.Comp.BoneBreakBaseChance,
            ent.Comp.BoneBreakMaxChance);

        if (!_random.Prob(chance))
        {
            ApplyEmpoweredMeleeHit(ent.Owner, target, used, args.TargetPart, ent.Comp.BoneBreakStrikeMultiplier);

            if (_netManager.IsServer)
            {
                _popup.PopupEntity(
                    Loc.GetString("popup-grab-bone-break-fail-self",
                        ("target", Identity.Entity(target, EntityManager))),
                    ent.Owner,
                    ent.Owner,
                    PopupType.MediumCaution);

                _popup.PopupEntity(
                    Loc.GetString("popup-grab-bone-break-fail-target",
                        ("puller", Identity.Entity(ent.Owner, EntityManager))),
                    target,
                    target,
                    PopupType.MediumCaution);

                _popup.PopupEntity(
                    Loc.GetString("popup-grab-bone-break-fail-others",
                        ("puller", Identity.Entity(ent.Owner, EntityManager)),
                        ("target", Identity.Entity(target, EntityManager))),
                    target,
                    Filter.Pvs(target, entityManager: EntityManager)
                        .RemovePlayerByAttachedEntity(ent.Owner)
                        .RemovePlayerByAttachedEntity(target),
                    true,
                    PopupType.MediumCaution);
            }

            return;
        }

        ApplyEmpoweredMeleeHit(ent.Owner, target, used, args.TargetPart, ent.Comp.BoneBreakStrikeMultiplier);
        ApplyFinisherDamage(target,
            ent.Owner,
            args.TargetPart,
            "Blunt",
            ent.Comp.BoneBreakBaseDamage + bluntDamage * ent.Comp.BoneBreakDamagePerBluntDamage,
            7f);

        if (_netManager.IsServer)
        {
            _popup.PopupEntity(
                Loc.GetString("popup-grab-bone-break-success-self",
                    ("target", Identity.Entity(target, EntityManager))),
                ent.Owner,
                ent.Owner,
                PopupType.LargeCaution);

            _popup.PopupEntity(
                Loc.GetString("popup-grab-bone-break-success-target",
                    ("puller", Identity.Entity(ent.Owner, EntityManager))),
                target,
                target,
                PopupType.LargeCaution);

            _popup.PopupEntity(
                Loc.GetString("popup-grab-bone-break-success-others",
                    ("puller", Identity.Entity(ent.Owner, EntityManager)),
                    ("target", Identity.Entity(target, EntityManager))),
                target,
                Filter.Pvs(target, entityManager: EntityManager)
                    .RemovePlayerByAttachedEntity(ent.Owner)
                    .RemovePlayerByAttachedEntity(target),
                true,
                PopupType.LargeCaution);
        }
    }

    private void OnGrabThroatSliceDoAfter(Entity<PullerComponent> ent, ref ThroatSliceDoAfterEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = true;

        if (args.Cancelled || args.Target is not { } target || args.Used is not { } used)
        {
            PopupGrabActionCancelled(ent.Owner, "popup-grab-throat-slice-cancel-interrupted");
            return;
        }

        if (!TryComp<PullableComponent>(target, out var pullableComp))
        {
            PopupGrabActionCancelled(ent.Owner, "popup-grab-throat-slice-cancel-target-lost");
            return;
        }

        if (!CanStartThroatSliceDoAfter(ent, (target, pullableComp), used, out var targetPart)
            || targetPart != args.TargetPart)
        {
            PopupGrabActionCancelled(ent.Owner, "popup-grab-throat-slice-cancel-conditions");
            return;
        }

        if (!TryGetHeadWoundable(target, out var headWoundable))
        {
            PopupGrabActionCancelled(ent.Owner, "popup-grab-throat-slice-cancel-no-throat");
            return;
        }

        var sharpDamage = GetSharpDamage(used);
        if (sharpDamage <= 0f)
        {
            PopupGrabActionCancelled(ent.Owner, "popup-grab-throat-slice-cancel-invalid-weapon");
            return;
        }

        ApplyEmpoweredMeleeHit(ent.Owner, target, used, args.TargetPart, ent.Comp.ThroatSliceStrikeMultiplier);
        ApplyFinisherDamage(target,
            ent.Owner,
            args.TargetPart,
            "Slash",
            ent.Comp.ThroatSliceBaseSeverity + sharpDamage * ent.Comp.ThroatSliceSeverityPerSharpDamage,
            8f);

        if (_netManager.IsServer)
        {
            var tool = Identity.Entity(used, EntityManager);
            _popup.PopupEntity(
                Loc.GetString("popup-grab-throat-slice-success-self",
                    ("target", Identity.Entity(target, EntityManager)),
                    ("tool", tool)),
                ent.Owner,
                ent.Owner,
                PopupType.LargeCaution);

            _popup.PopupEntity(
                Loc.GetString("popup-grab-throat-slice-success-target",
                    ("puller", Identity.Entity(ent.Owner, EntityManager)),
                    ("tool", tool)),
                target,
                target,
                PopupType.LargeCaution);

            _popup.PopupEntity(
                Loc.GetString("popup-grab-throat-slice-success-others",
                    ("puller", Identity.Entity(ent.Owner, EntityManager)),
                    ("target", Identity.Entity(target, EntityManager)),
                    ("tool", tool)),
                target,
                Filter.Pvs(target, entityManager: EntityManager)
                    .RemovePlayerByAttachedEntity(ent.Owner)
                    .RemovePlayerByAttachedEntity(target),
                true,
                PopupType.LargeCaution);
        }
    }

    private bool CanStartBoneBreakDoAfter(
        Entity<PullerComponent> puller,
        Entity<PullableComponent> pullable,
        EntityUid used,
        out TargetBodyPart targetPart)
    {
        targetPart = TargetBodyPart.Chest;

        if (puller.Comp.Pulling != pullable.Owner
            || pullable.Comp.Puller != puller.Owner
            || puller.Comp.GrabStage != GrabStage.Hard
            || !_combatMode.IsInCombatMode(puller.Owner)
            || !_blocker.CanAttack(puller.Owner, pullable.Owner)
            || !TryComp<MobStateComponent>(pullable.Owner, out _)
            || !TryComp<TargetingComponent>(puller.Owner, out var targeting))
            return false;

        if (TryComp<PullableComponent>(puller.Owner, out var pullerAsPullable) && pullerAsPullable.Puller != null)
            return false;

        if (targeting.Target is not (TargetBodyPart.LeftLeg or TargetBodyPart.RightLeg))
            return false;

        targetPart = targeting.Target;
        return GetBluntDamage(used) > 0f;
    }

    private bool CanStartThroatSliceDoAfter(
        Entity<PullerComponent> puller,
        Entity<PullableComponent> pullable,
        EntityUid used,
        out TargetBodyPart targetPart)
    {
        targetPart = TargetBodyPart.Chest;

        if (puller.Comp.Pulling != pullable.Owner
            || pullable.Comp.Puller != puller.Owner
            || puller.Comp.GrabStage != GrabStage.Hard
            || !_combatMode.IsInCombatMode(puller.Owner)
            || !_blocker.CanAttack(puller.Owner, pullable.Owner)
            || !TryComp<MobStateComponent>(pullable.Owner, out _)
            || !TryComp<TargetingComponent>(puller.Owner, out var targeting))
            return false;

        if (TryComp<PullableComponent>(puller.Owner, out var pullerAsPullable) && pullerAsPullable.Puller != null)
            return false;

        if (targeting.Target != TargetBodyPart.Head)
            return false;

        if (!TryGetHeadWoundable(pullable.Owner, out _))
            return false;

        targetPart = targeting.Target;
        return GetSharpDamage(used) > 0f;
    }

    private float GetBluntDamage(EntityUid used)
    {
        if (!TryComp<ItemComponent>(used, out _)
            || !TryComp<MeleeWeaponComponent>(used, out var melee))
            return 0f;

        return melee.Damage.DamageDict.TryGetValue("Blunt", out var blunt)
            ? blunt.Float()
            : 0f;
    }

    private float GetSharpDamage(EntityUid used)
    {
        if (!TryComp<ItemComponent>(used, out _)
            || !TryComp<MeleeWeaponComponent>(used, out var melee))
            return 0f;

        var slash = melee.Damage.DamageDict.TryGetValue("Slash", out var slashDamage)
            ? slashDamage.Float()
            : 0f;

        var piercing = melee.Damage.DamageDict.TryGetValue("Piercing", out var piercingDamage)
            ? piercingDamage.Float()
            : 0f;

        return slash + piercing;
    }

    private bool TryGetLegBone(
        EntityUid target,
        TargetBodyPart targetPart,
        out (EntityUid Bone, BoneComponent BoneComp) bone,
        out bool alreadyBroken)
    {
        bone = default;
        alreadyBroken = false;

        if (!TryComp<BodyComponent>(target, out var body))
            return false;

        var (partType, symmetry) = _body.ConvertTargetBodyPart(targetPart);
        if (partType != BodyPartType.Leg)
            return false;

        var bodyPart = _body.GetBodyChildrenOfType(target, partType, body, symmetry).FirstOrNull();
        if (bodyPart == null
            || !TryComp<WoundableComponent>(bodyPart.Value.Id, out var woundable))
            return false;

        var boneEnt = woundable.Bone.ContainedEntities.FirstOrNull();
        if (boneEnt == null
            || !TryComp<BoneComponent>(boneEnt.Value, out var boneComp))
            return false;

        if (boneComp.BoneSeverity == BoneSeverity.Broken)
        {
            alreadyBroken = true;
            return false;
        }

        bone = (boneEnt.Value, boneComp);
        return true;
    }

    private bool TryGetHeadWoundable(EntityUid target, out EntityUid headWoundable)
    {
        headWoundable = default;

        if (!TryComp<BodyComponent>(target, out var body))
            return false;

        var headPart = _body.GetBodyChildrenOfType(target, BodyPartType.Head, body, BodyPartSymmetry.None).FirstOrNull();
        if (headPart == null
            || !TryComp<WoundableComponent>(headPart.Value.Id, out _))
            return false;

        headWoundable = headPart.Value.Id;
        return true;
    }

    private void ApplyEmpoweredMeleeHit(
        EntityUid attacker,
        EntityUid target,
        EntityUid used,
        TargetBodyPart targetPart,
        float multiplier)
    {
        if (multiplier <= 0f
            || !TryComp<MeleeWeaponComponent>(used, out var melee))
            return;

        if (!TryComp<TargetingComponent>(attacker, out var targeting))
            return;

        var originalTarget = targeting.Target;
        targeting.Target = targetPart;
        Dirty(attacker, targeting);

        var originalDamage = new DamageSpecifier(melee.Damage);
        melee.Damage = originalDamage * multiplier;
        Dirty(used, melee);

        TryPerformFollowupAttack(attacker, target, used, melee);

        melee.Damage = originalDamage;
        Dirty(used, melee);

        targeting.Target = originalTarget;
        Dirty(attacker, targeting);
    }

    /// <summary>
    /// Tries to perform a normal light attack after a grab action.
    /// Falls back to bypassing cooldown once, then to direct light-attack execution.
    /// This keeps normal melee visuals/sounds/effects while avoiding random no-hit outcomes.
    /// </summary>
    private void TryPerformFollowupAttack(
        EntityUid attacker,
        EntityUid target,
        EntityUid used,
        MeleeWeaponComponent melee)
    {
        ArmGrabFollowupSuppression(attacker, target);
        if (_melee.AttemptLightAttack(attacker, used, melee, target))
            return;
        ClearGrabFollowupSuppression(attacker);

        var originalNextAttack = melee.NextAttack;
        var touchedCooldown = false;

        if (melee.NextAttack > _timing.CurTime)
        {
            melee.NextAttack = _timing.CurTime;
            Dirty(used, melee);
            touchedCooldown = true;
        }

        ArmGrabFollowupSuppression(attacker, target);
        if (_melee.AttemptLightAttack(attacker, used, melee, target))
            return;
        ClearGrabFollowupSuppression(attacker);

        // Last resort: run the light attack path directly. This still does range/LOS,
        // harm event checks, sounds, lunge and damage effects.
        var coords = Transform(target).Coordinates;
        ArmGrabFollowupSuppression(attacker, target);
        var forcedLight = new LightAttackEvent(
            GetNetEntity(target),
            GetNetEntity(used),
            GetNetCoordinates(coords));
        _melee.DoLightAttack(attacker, forcedLight, used, melee, null);
        ClearGrabFollowupSuppression(attacker);

        if (!touchedCooldown)
            return;

        // If both attempt paths failed and direct path didn't consume cooldown,
        // preserve the old timing window.
        if (melee.NextAttack <= _timing.CurTime)
        {
            melee.NextAttack = originalNextAttack;
            Dirty(used, melee);
        }
    }

    private void ArmGrabFollowupSuppression(EntityUid attacker, EntityUid target)
    {
        _followupSuppressions[attacker] = new GrabFollowupSuppression(
            target,
            _timing.CurTime + suppressionDuration);
    }

    private void ClearGrabFollowupSuppression(EntityUid attacker)
    {
        _followupSuppressions.Remove(attacker);
    }

    private bool TryConsumeGrabFollowupSuppression(EntityUid attacker, EntityUid target)
    {
        if (!_followupSuppressions.TryGetValue(attacker, out var suppression))
            return false;

        if (suppression.ExpiresAt < _timing.CurTime)
        {
            _followupSuppressions.Remove(attacker);
            return false;
        }

        if (suppression.Target != target)
            return false;

        _followupSuppressions.Remove(attacker);
        return true;
    }

    private void ApplyFinisherDamage(
        EntityUid target,
        EntityUid attacker,
        TargetBodyPart part,
        string damageType,
        float damageAmount,
        float woundSeverityMultiplier)
    {
        if (damageAmount <= 0f)
            return;

        var spec = new DamageSpecifier();
        spec.DamageDict[damageType] = FixedPoint2.New(damageAmount);
        spec.WoundSeverityMultipliers[damageType] = woundSeverityMultiplier;

        _damageable.TryChangeDamage(target,
            spec,
            origin: attacker,
            targetPart: part,
            canMiss: false);
    }

    private void PopupGrabActionCancelled(EntityUid user, string message)
    {
        if (!_netManager.IsServer)
            return;

        _popup.PopupEntity(
            Loc.GetString(message),
            user,
            user,
            PopupType.MediumCaution);
    }

    private string GetGrabLimbName(TargetBodyPart targetPart)
    {
        return targetPart switch
        {
            TargetBodyPart.LeftLeg => Loc.GetString("popup-grab-limb-left-leg"),
            TargetBodyPart.RightLeg => Loc.GetString("popup-grab-limb-right-leg"),
            _ => Loc.GetString("popup-grab-limb-leg"),
        };
    }
}
