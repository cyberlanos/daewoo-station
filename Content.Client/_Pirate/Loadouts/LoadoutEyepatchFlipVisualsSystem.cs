using System.Linq;
using System.Numerics;
using Content.Client.Clothing;
using Content.Shared.Clothing;
using Content.Shared.Clothing.Components;
using Content.Shared.Foldable;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Utility;
using Robust.Shared.Graphics.RSI;
using static Robust.Client.GameObjects.SpriteComponent;

namespace Content.Client._Pirate.Loadouts;

public sealed class LoadoutEyepatchFlipVisualsSystem : EntitySystem
{
    private static readonly Vector2 LeftPixelOffset = new(-1f / EyeManager.PixelsPerMeter, 0f);
    private readonly Dictionary<EntityUid, EyepatchFlipState> _states = new();

    [Dependency] private readonly IEyeManager _eye = default!;
    [Dependency] private readonly SpriteSystem _sprite = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<FlippableClothingVisualsComponent, EquipmentVisualsUpdatedEvent>(OnEquipmentVisualsUpdated);
        SubscribeLocalEvent<FlippableClothingVisualsComponent, ClothingGotUnequippedEvent>(OnClothingUnequipped);
        SubscribeLocalEvent<FlippableClothingVisualsComponent, ComponentShutdown>(OnComponentShutdown);
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        var query = EntityQueryEnumerator<FlippableClothingVisualsComponent, FoldableComponent>();
        while (query.MoveNext(out var uid, out _, out var foldable))
        {
            if (_states.TryGetValue(uid, out var state))
                Apply(state, foldable);
        }
    }

    private void OnEquipmentVisualsUpdated(EntityUid uid, FlippableClothingVisualsComponent component, EquipmentVisualsUpdatedEvent args)
    {
        if (!_states.TryGetValue(uid, out var state))
        {
            state = new EyepatchFlipState();
            _states.Add(uid, state);
        }
        state.Wearer = args.Equipee;
        state.HasBaseOffset = false;
        state.LastDirOffset = null;
        state.LastOffset = null;

        if (TryComp(uid, out ClothingComponent? clothing) && clothing.MappedLayer != null)
            state.Layer = clothing.MappedLayer;
        else
            state.Layer = args.RevealedLayers.FirstOrDefault();

        if (TryComp(uid, out FoldableComponent? foldable))
            Apply(state, foldable);
    }

    private void OnClothingUnequipped(EntityUid uid, FlippableClothingVisualsComponent component, ref ClothingGotUnequippedEvent args)
    {
        _states.Remove(uid);
    }

    private void OnComponentShutdown(EntityUid uid, FlippableClothingVisualsComponent component, ref ComponentShutdown args)
    {
        _states.Remove(uid);
    }

    private void Apply(EyepatchFlipState state, FoldableComponent foldable)
    {
        if (state.Wearer is not { } wearer ||
            state.Layer is not { } layerKey ||
            !TryComp(wearer, out SpriteComponent? wearerSprite) ||
            !_sprite.LayerMapTryGet((wearer, wearerSprite), layerKey, out var layerIndex, false) ||
            wearerSprite[layerIndex] is not Layer layer)
        {
            return;
        }

        if (!state.HasBaseOffset)
        {
            state.BaseOffset = layer.Offset;
            state.HasBaseOffset = true;
        }

        var directions = _sprite.LayerGetDirections(layer);
        var direction = wearerSprite.EnableDirectionOverride
            ? wearerSprite.DirectionOverride.Convert(directions)
            : Layer.GetDirection(directions, (_transform.GetWorldRotation(wearer) + _eye.CurrentEye.Rotation).Reduced().FlipPositive());
        var dirOffset = SpriteComponent.DirectionOffset.None;
        var offset = state.BaseOffset;

        if (foldable.IsFolded)
        {
            if (direction is RsiDirection.East or RsiDirection.West)
                dirOffset = SpriteComponent.DirectionOffset.Flip;
            else if (direction is RsiDirection.North or RsiDirection.South)
                offset += LeftPixelOffset;
        }

        if (state.LastDirOffset != dirOffset)
        {
            _sprite.LayerSetDirOffset(layer, dirOffset);
            state.LastDirOffset = dirOffset;
        }

        if (state.LastOffset != offset)
        {
            _sprite.LayerSetOffset(layer, offset);
            state.LastOffset = offset;
        }
    }

    private sealed class EyepatchFlipState
    {
        public EntityUid? Wearer;
        public string? Layer;
        public Vector2 BaseOffset;
        public bool HasBaseOffset;
        public SpriteComponent.DirectionOffset? LastDirOffset;
        public Vector2? LastOffset;
    }
}
