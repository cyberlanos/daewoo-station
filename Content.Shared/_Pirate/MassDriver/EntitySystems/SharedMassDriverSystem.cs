using Content.Shared._Pirate.MassDriver.Components;
using Content.Shared.Audio;
using Content.Shared.Ghost;
using Content.Shared.Power;
using Content.Shared.Throwing;
using Robust.Shared.Timing;

namespace Content.Shared._Pirate.MassDriver.EntitySystems;

public abstract partial class SharedMassDriverSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly ThrowingSystem _throwing = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;
    [Dependency] private readonly SharedAmbientSoundSystem _audio = default!;

    private EntityQuery<GhostComponent> _ghostQuery;
    private readonly HashSet<EntityUid> _entities = new();

    public override void Initialize()
    {
        base.Initialize();

        _ghostQuery = GetEntityQuery<GhostComponent>();
        SubscribeLocalEvent<MassDriverComponent, PowerChangedEvent>(OnPowerChanged);
    }

    private void OnPowerChanged(EntityUid uid, MassDriverComponent component, ref PowerChangedEvent args)
    {
        if (component.Mode != MassDriverMode.Auto)
            return;

        var active = HasComp<ActiveMassDriverComponent>(uid);
        if (active && !args.Powered)
            RemComp<ActiveMassDriverComponent>(uid);
        else if (!active && args.Powered)
            EnsureComp<ActiveMassDriverComponent>(uid);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<ActiveMassDriverComponent, MassDriverComponent>();

        while (query.MoveNext(out var uid, out var activeMassDriver, out var massDriver))
        {
            if (_timing.CurTime < activeMassDriver.NextUpdateTime ||
                activeMassDriver.NextThrowTime != TimeSpan.Zero && _timing.CurTime < activeMassDriver.NextThrowTime)
            {
                continue;
            }

            activeMassDriver.NextUpdateTime = _timing.CurTime + activeMassDriver.UpdateDelay;
            Dirty(uid, massDriver);

            _entities.Clear();
            _lookup.GetEntitiesIntersecting(uid, _entities, LookupFlags.Dynamic);

            // Pirate: keep the current Starlight fix that prevents ghosts from triggering drivers.
            _entities.RemoveWhere(_ghostQuery.HasComp);
            var entityCount = _entities.Count;

            if (entityCount == 0)
            {
                if (activeMassDriver.NextThrowTime != TimeSpan.Zero)
                {
                    if (TryComp<AmbientSoundComponent>(uid, out var ambient))
                        _audio.SetAmbience(uid, false, ambient);

                    activeMassDriver.NextThrowTime = TimeSpan.Zero;
                    _appearance.SetData(uid, MassDriverVisuals.Launching, false);
                    ChangePowerLoad(uid, massDriver, massDriver.MassDriverPowerLoad);
                    Dirty(uid, massDriver);
                }

                if (massDriver.Mode == MassDriverMode.Manual)
                    RemComp<ActiveMassDriverComponent>(uid);

                continue;
            }

            if (activeMassDriver.NextThrowTime == TimeSpan.Zero)
            {
                activeMassDriver.NextThrowTime = _timing.CurTime + massDriver.ThrowDelay;
                Dirty(uid, massDriver);
                continue;
            }

            ChangePowerLoad(uid, massDriver, massDriver.LaunchPowerLoad);
            _appearance.SetData(uid, MassDriverVisuals.Launching, true);

            ThrowEntities(uid, massDriver, _entities, entityCount);

            if (TryComp<AmbientSoundComponent>(uid, out var ambientSound))
                _audio.SetAmbience(uid, true, ambientSound);
        }
    }

    private void ThrowEntities(
        EntityUid massDriver,
        MassDriverComponent massDriverComponent,
        HashSet<EntityUid> targets,
        int targetCount)
    {
        var xform = Transform(massDriver);
        var distance = massDriverComponent.CurrentThrowDistance - massDriverComponent.ThrowCountDelta * (targets.Count - 1);
        var throwing = xform.LocalRotation.ToWorldVec() * distance;
        var direction = xform.Coordinates.Offset(throwing);
        var speed = massDriverComponent.Hacked
            ? massDriverComponent.HackedSpeedRewrite
            : massDriverComponent.CurrentThrowSpeed - massDriverComponent.ThrowCountDelta * (targetCount - 1);

        foreach (var entity in targets)
            _throwing.TryThrow(entity, direction, speed);
    }

    public abstract void ChangePowerLoad(EntityUid uid, MassDriverComponent component, float powerLoad);
}
