// Ported from ColonialMarinesUniverse Content.Client/_CMU14/ZLevels/Core/CMUClientZLevelsSystem.cs
// (projectile visual offset portions only — lanos already has the rest of the client z-system).

using System.Numerics;
using Content.Shared._Pirate.ZLevels.Core.Components;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Maths;

namespace Content.Client._Pirate.ZLevels.Shooting;

/// <summary>
/// Client-side renderer for the projectile visual offset components. Applies the offset to the
/// projectile sprite so its muzzle flash appears at the shooter's barrel on the source Z layer
/// instead of at the actual (target-layer) spawn point. On component shutdown the original
/// sprite offset is restored.
/// </summary>
public sealed class CEZLevelClientShootingSystem : EntitySystem
{
    [Dependency] private readonly IEyeManager _eye = default!;
    [Dependency] private readonly SpriteSystem _sprite = default!;
    [Dependency] private readonly SharedTransformSystem _transformSystem = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CEZLevelProjectileVisualOffsetComponent, ComponentStartup>(OnSyncedStartup);
        SubscribeLocalEvent<CEZLevelProjectileVisualOffsetComponent, ComponentShutdown>(OnSyncedShutdown);
        SubscribeLocalEvent<CEZLevelPredictedProjectileVisualOffsetComponent, ComponentStartup>(OnPredictedStartup);
        SubscribeLocalEvent<CEZLevelPredictedProjectileVisualOffsetComponent, ComponentShutdown>(OnPredictedShutdown);
    }

    private void OnSyncedStartup(Entity<CEZLevelProjectileVisualOffsetComponent> ent, ref ComponentStartup args)
    {
        TryApplyProjectileVisualOffset(ent.Owner, ent.Comp.Offset, ref ent.Comp.OriginalOffset, ref ent.Comp.AppliedOffset);
    }

    private void OnSyncedShutdown(Entity<CEZLevelProjectileVisualOffsetComponent> ent, ref ComponentShutdown args)
    {
        RestoreProjectileVisualOffset(ent.Owner, ent.Comp.OriginalOffset);
    }

    private void OnPredictedStartup(Entity<CEZLevelPredictedProjectileVisualOffsetComponent> ent, ref ComponentStartup args)
    {
        TryApplyProjectileVisualOffset(ent.Owner, ent.Comp.Offset, ref ent.Comp.OriginalOffset, ref ent.Comp.AppliedOffset);
    }

    private void OnPredictedShutdown(Entity<CEZLevelPredictedProjectileVisualOffsetComponent> ent, ref ComponentShutdown args)
    {
        RestoreProjectileVisualOffset(ent.Owner, ent.Comp.OriginalOffset);
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        // Re-apply each frame so projectile rotation/render-rotation updates keep the offset
        // correctly oriented. Predicted-only projectiles take precedence — skip the synced
        // entry to avoid double-applying.
        var syncedQuery = EntityQueryEnumerator<CEZLevelProjectileVisualOffsetComponent, SpriteComponent, TransformComponent>();
        while (syncedQuery.MoveNext(out var uid, out var visual, out var sprite, out var xform))
        {
            if (HasComp<CEZLevelPredictedProjectileVisualOffsetComponent>(uid))
                continue;

            ApplyProjectileVisualOffset(uid, visual.Offset, ref visual.OriginalOffset, ref visual.AppliedOffset, sprite, xform);
        }

        var predictedQuery = EntityQueryEnumerator<CEZLevelPredictedProjectileVisualOffsetComponent, SpriteComponent, TransformComponent>();
        while (predictedQuery.MoveNext(out var uid, out var visual, out var sprite, out var xform))
        {
            ApplyProjectileVisualOffset(uid, visual.Offset, ref visual.OriginalOffset, ref visual.AppliedOffset, sprite, xform);
        }
    }

    private bool TryApplyProjectileVisualOffset(
        EntityUid uid,
        Vector2 visualOffset,
        ref Vector2? originalOffset,
        ref Vector2 appliedOffset)
    {
        if (!TryComp<SpriteComponent>(uid, out var sprite) || !TryComp(uid, out TransformComponent? xform))
            return false;

        ApplyProjectileVisualOffset(uid, visualOffset, ref originalOffset, ref appliedOffset, sprite, xform);
        return true;
    }

    private void ApplyProjectileVisualOffset(
        EntityUid uid,
        Vector2 visualOffset,
        ref Vector2? originalOffset,
        ref Vector2 appliedOffset,
        SpriteComponent sprite,
        TransformComponent xform)
    {
        // No-rotation sprites stay screen-aligned; rotated sprites need the offset in their own
        // local frame so it doesn't flip with the projectile.
        Angle renderRotation;
        if (sprite.NoRotation)
            renderRotation = _eye.CurrentEye.Rotation * -1;
        else
            renderRotation = _transformSystem.GetWorldRotation(xform);

        var localVisualOffset = (-renderRotation).RotateVec(visualOffset);

        // Capture the pristine sprite offset once so we can undo our shift on shutdown.
        originalOffset ??= sprite.Offset - appliedOffset;
        if (appliedOffset == localVisualOffset)
            return;

        _sprite.SetOffset((uid, sprite), originalOffset.Value + localVisualOffset);
        appliedOffset = localVisualOffset;
    }

    private void RestoreProjectileVisualOffset(EntityUid uid, Vector2? originalOffset)
    {
        if (originalOffset is { } original && TryComp<SpriteComponent>(uid, out var sprite))
            _sprite.SetOffset((uid, sprite), original);
    }
}
