/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using System.Numerics;
using Content.Client.Damage.Systems;
using Content.Shared._Pirate.ZLevels.Core.Components;
using Content.Shared._Pirate.ZLevels.Core.EntitySystems;
using Content.Shared.Camera;
using Content.Shared.Damage.Components;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Player;

namespace Content.Client._Pirate.ZLevels.Core;

/// <summary>
/// Only process Eye offset and drawdepth on clientside
/// </summary>
public sealed partial class CEClientZLevelsSystem : CESharedZLevelsSystem
{
    private bool _clientInitialized;

    [Dependency] private readonly IOverlayManager _overlay = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly SpriteSystem _sprite = default!;
    [Dependency] private readonly IEyeManager _eye = default!;
    [Dependency] private readonly AnimationPlayerSystem _animation = default!;

    public static float ZLevelOffset = 0.7f;

    public override void Initialize()
    {
        base.Initialize();

        if (_clientInitialized)
            return;

        _clientInitialized = true;
        _overlay.AddOverlay(new CEZLevelBlurOverlay());

        SubscribeLocalEvent<CEZPhysicsComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<CEZPhysicsComponent, AfterAutoHandleStateEvent>(OnZPhysicsHandleState);
        SubscribeLocalEvent<CEZPhysicsComponent, GetEyeOffsetEvent>(OnEyeOffset);
    }

    private void OnEyeOffset(Entity<CEZPhysicsComponent> ent, ref GetEyeOffsetEvent args)
    {
        Angle rotation = _eye.CurrentEye.Rotation * -1;
        var localPosition = GetVisualsLocalPosition((ent, ent), Transform(ent));
        var offset = rotation.RotateVec(new Vector2(0, localPosition * ZLevelOffset));
        args.Offset += offset;
    }

    private void OnStartup(Entity<CEZPhysicsComponent> ent, ref ComponentStartup args)
    {
        if (!TryComp<SpriteComponent>(ent, out var sprite))
            return;

        if (sprite.SnapCardinals)
            return;

        ent.Comp.NoRotDefault = sprite.NoRotation;
        ent.Comp.DrawDepthDefault = sprite.DrawDepth;
        ent.Comp.SpriteOffsetDefault = sprite.Offset;
    }

    private void OnZPhysicsHandleState(Entity<CEZPhysicsComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        if (!ZDebugStairsEnabled ||
            _player.LocalEntity != ent.Owner)
        {
            return;
        }

        DebugZStairCsv(ent,
            "client_z_state_handle",
            $"state={args.State.GetType().Name},local={StairCsvFloat(ent.Comp.LocalPosition)},vel={StairCsvFloat(ent.Comp.Velocity)},current_z={ent.Comp.CurrentZLevel}",
            $"{args.State.GetType().Name}|{StairCsvFloat(ent.Comp.LocalPosition)}|{StairCsvFloat(ent.Comp.Velocity)}|{ent.Comp.CurrentZLevel}|{Transform(ent).ParentUid}|{Transform(ent).GridUid}|{Transform(ent).MapUid}");
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<CEZPhysicsComponent, SpriteComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var zPhys, out var sprite, out var xform))
        {
            var localPosition = GetVisualsLocalPosition((uid, zPhys), xform);

            sprite.NoRotation = localPosition != 0 || zPhys.NoRotDefault;

            _sprite.SetOffset((uid, sprite), zPhys.SpriteOffsetDefault + new Vector2(0, localPosition * ZLevelOffset));
            _sprite.SetDrawDepth((uid, sprite), localPosition > 0 ? (int)Shared.DrawDepth.DrawDepth.OverMobs : zPhys.DrawDepthDefault);
        }

        // Update StartOffset for entities with running fatigue animations
        // This allows animations to follow dynamic offset changes (e.g., from Z-levels system)
        var query2 = EntityQueryEnumerator<StaminaComponent, SpriteComponent, CEZPhysicsComponent>();
        while (query2.MoveNext(out var uid, out var stamina, out var sprite, out var zPhys))
        {
            // Only update if animation is running
            if (!_animation.HasRunningAnimation(uid, StaminaSystem.StaminaAnimationKey))
                continue;

            // Update the base offset to track changes made by other systems
            stamina.StartOffset = zPhys.SpriteOffsetDefault;
        }
    }


    public float GetVisualsLocalPosition(Entity<CEZPhysicsComponent?> ent, TransformComponent? xform = null)
    {
        if (!Resolve(ent, ref ent.Comp, false))
            return 0;
        if (!Resolve(ent, ref xform, false))
            return 0;

        var pos = ent.Comp.LocalPosition;

        if (xform.ParentUid != xform.MapUid && ZPhyzQuery.TryComp(xform.ParentUid, out var parentZPhys))
            pos = parentZPhys.LocalPosition;

        return pos;
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _overlay.RemoveOverlay<CEZLevelBlurOverlay>();
    }
}
