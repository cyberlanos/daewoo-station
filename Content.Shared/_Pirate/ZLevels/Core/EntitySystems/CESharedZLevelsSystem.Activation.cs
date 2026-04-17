/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Shared._Pirate.ZLevels.Core.Components;
using Content.Shared._Pirate.ZLevels.Ghost;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Events;

namespace Content.Shared._Pirate.ZLevels.Core.EntitySystems;

public abstract partial class CESharedZLevelsSystem
{
    private void InitializeActivation()
    {
        SubscribeLocalEvent<CEZPhysicsComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<CEZPhysicsComponent, AnchorStateChangedEvent>(OnAnchorStateChange);
        SubscribeLocalEvent<CEZPhysicsComponent, PhysicsBodyTypeChangedEvent>(OnPhysicsBodyTypeChange);
        SubscribeLocalEvent<CEZPhysicsComponent, EntParentChangedMessage>(OnParentChanged);
    }

    private void OnAnchorStateChange(Entity<CEZPhysicsComponent> ent, ref AnchorStateChangedEvent args)
    {
        CheckActivation(ent);
    }

    private void OnMapInit(Entity<CEZPhysicsComponent> ent, ref MapInitEvent args)
    {
        CheckActivation(ent);

        if (!TryComp<CEZLevelMapComponent>(Transform(ent).MapUid, out var zLevelMap))
            return;

        ent.Comp.CurrentZLevel = zLevelMap.Depth;
        DirtyField(ent, ent.Comp, nameof(CEZPhysicsComponent.CurrentZLevel));
    }

    private void OnPhysicsBodyTypeChange(Entity<CEZPhysicsComponent> ent, ref PhysicsBodyTypeChangedEvent args)
    {
        CheckActivation(ent);
    }

    private void OnParentChanged(Entity<CEZPhysicsComponent> ent, ref EntParentChangedMessage args)
    {
        CheckActivation(ent);

        if (ZPhyzQuery.TryComp(args.OldParent, out var oldParentZPhys))
            SetZPosition((ent, ent), oldParentZPhys.LocalPosition);
    }

    private void CheckActivation(Entity<CEZPhysicsComponent> ent)
    {
        if (TerminatingOrDeleted(ent))
            return;

        // Ghost movers use actions only — exclude from automatic Z-physics entirely
        if (HasComp<CEZLevelGhostMoverComponent>(ent))
        {
            SetActiveStatus(ent, false);
            return;
        }

        var xform = Transform(ent);

        if (xform.ParentUid != xform.MapUid && xform.ParentUid != xform.GridUid)
        {
            DebugZ(ent, "z-physics inactive: parent is neither the map nor the grid");
            SetActiveStatus(ent, false);
            return;
        }

        if (xform.Anchored)
        {
            DebugZ(ent, "z-physics inactive: entity is anchored");
            SetActiveStatus(ent, false);
            return;
        }

        if (TryComp<PhysicsComponent>(ent, out var physics))
        {
            if (physics.BodyType == BodyType.Static)
            {
                DebugZ(ent, "z-physics inactive: body type is static");
                SetActiveStatus(ent, false);
                return;
            }
        }

        DebugZ(ent, "z-physics active");
        SetActiveStatus(ent, true);
    }

    private void SetActiveStatus(EntityUid ent, bool active)
    {
        if (!_timing.IsFirstTimePredicted)
            return;

        if (active)
            EnsureComp<CEActiveZPhysicsComponent>(ent);
        else
            RemComp<CEActiveZPhysicsComponent>(ent);
    }
}
