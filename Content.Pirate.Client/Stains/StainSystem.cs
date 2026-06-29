using Content.Client.Clothing;
using Content.Client.Items.Systems;
using Content.Pirate.Shared.Stains.Components;
using Content.Pirate.Shared.Stains.Systems;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Clothing;
using Content.Shared.Hands;
using Content.Shared.Humanoid;
using Content.Shared.Inventory;
using Content.Goobstation.Maths.FixedPoint;
using Robust.Client.GameObjects;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.Pirate.Client.Stains;

public sealed class StainSystem : SharedStainSystem
{
    private const string BloodRsiPath = "_Pirate/Effects/blood.rsi";
    private const string ItemBloodState = "itemblood";
    private const string BareFeetLayerKey = "stain-bare-feet";
    private const string BareHandsLayerKey = "stain-bare-hands";
    private const string StainMaskShaderPrefix = "StainItemMask";
    private const int StainMaskVariants = 8;
    private const string StainMaskTextureParam = "stainMask";
    private const string StainMaskUvParam = "stainMaskUV";

    // Stable per-item mask variation.
    private static string StainShaderFor(EntityUid uid)
    {
        return $"{StainMaskShaderPrefix}{(uid.Id % StainMaskVariants + StainMaskVariants) % StainMaskVariants}";
    }

    [Dependency] private readonly IPrototypeManager _prototypeManager = null!;
    [Dependency] private readonly SharedSolutionContainerSystem _solution = null!;
    [Dependency] private readonly SpriteSystem _sprite = null!;
    [Dependency] private readonly ItemSystem _item = null!;

    // Cached across prediction rollbacks.
    private readonly Dictionary<EntityUid, (Color Color, SlotFlags Slots, bool HasStain, string Frame)> _lastDrawn = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<StainableComponent, AppearanceChangeEvent>(OnAppearanceChanged);
        SubscribeLocalEvent<StainableComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<StainableComponent, GetEquipmentVisualsEvent>(OnEquipmentVisuals, after: [typeof(ClientClothingSystem)]);
        SubscribeLocalEvent<StainableComponent, GetInhandVisualsEvent>(OnInhandVisuals, after: [typeof(ItemSystem)]);
    }

    private void OnShutdown(Entity<StainableComponent> ent, ref ComponentShutdown args)
    {
        _lastDrawn.Remove(ent.Owner);
    }

    private void OnAppearanceChanged(Entity<StainableComponent> ent, ref AppearanceChangeEvent args)
    {
        if (args.Sprite == null)
            return;

        var hasStain = TryGetStainColor(ent, out var color);
        var slots = ent.Comp.BodyStainSlots;
        if (args.AppearanceData.TryGetValue(StainVisuals.BodySlots, out var bodySlotsData) && bodySlotsData is SlotFlags bodySlotFlags)
            slots = bodySlotFlags;

        var spriteEnt = new Entity<SpriteComponent?>(ent.Owner, args.Sprite);

        // Include the base frame for dynamic silhouettes.
        var drawn = (color, slots, hasStain, BaseFrameFingerprint(spriteEnt));
        if (_lastDrawn.TryGetValue(ent.Owner, out var last) && last == drawn)
            return;
        _lastDrawn[ent.Owner] = drawn;

        // Refresh held/worn visuals on a real stain change. The shared UpdateVisuals gates this behind the
        // networked appearance data, which the client receives already-updated, so its guard skips the call
        // and a held item's in-hand sprite would stay stale (e.g. after wringing) until it leaves the hand.
        _item.VisualsChanged(ent.Owner);

        foreach (var key in ent.Comp.RevealedLayerKeys)
        {
            _sprite.RemoveLayer(spriteEnt, key, false);
        }

        ent.Comp.RevealedLayerKeys.Clear();

        var layers = new List<int>(ent.Comp.RevealedLayers);
        layers.Sort((a, b) => b.CompareTo(a));

        foreach (var layer in layers)
        {
            _sprite.RemoveLayer(spriteEnt, layer, false);
        }

        ent.Comp.RevealedLayers.Clear();

        if (!hasStain)
            return;

        var addedPrototypeVisuals = false;
        foreach (var (key, layerData) in BuildVisuals(ent, ent.Comp.IconVisuals, "icon"))
        {
            ent.Comp.RevealedLayerKeys.Add(key);
            _sprite.AddLayer(spriteEnt, layerData, null);
            addedPrototypeVisuals = true;
        }

        if (HasComp<HumanoidAppearanceComponent>(ent.Owner))
            AddBodyStainVisuals(ent, args, spriteEnt, color);
        else if (!addedPrototypeVisuals)
            AddItemBloodIconVisual(ent, spriteEnt, color);
    }

    private void OnEquipmentVisuals(Entity<StainableComponent> ent, ref GetEquipmentVisualsEvent args)
    {
        if (ent.Comp.ClothingVisuals.TryGetValue(args.Slot, out var layers))
            args.Layers.AddRange(BuildVisuals(ent, layers, args.Slot));
    }

    private void OnInhandVisuals(Entity<StainableComponent> ent, ref GetInhandVisualsEvent args)
    {
        if (ent.Comp.ItemVisuals.TryGetValue(args.Location.ToString(), out var layers))
        {
            args.Layers.AddRange(BuildVisuals(ent, layers, args.Location.ToString()));
            return;
        }

        if (!TryGetStainColor(ent, out var color) || args.Layers.Count == 0)
            return;

        // Snapshot before appending stain layers.
        var baseLayers = new List<(string, PrototypeLayerData)>(args.Layers);
        for (var i = 0; i < baseLayers.Count; i++)
        {
            var source = baseLayers[i].Item2;
            if (source.Visible == false)
                continue;

            var bloodKey = $"stain-inhand-{args.Location}-{i}";

            // Always clip the blood to the held silhouette. Items (e.g. gloves) often define inhandVisuals with
            // only a State and rely on the item's BaseRSI, leaving RsiPath empty; HandsSystem resolves that RSI
            // for our mask layer the same way it does for the base layer. No flat fallback - never cover the
            // whole in-hand sprite with blood.
            var maskKey = $"stain-inhand-mask-{args.Location}-{i}";
            args.Layers.Add((maskKey, BuildMaskLayer(source, maskKey, bloodKey)));

            var masked = BuildItemBloodLayer(bloodKey, source);
            masked.Shader = StainShaderFor(ent.Owner);
            masked.Color = color;
            args.Layers.Add((bloodKey, masked));
        }
    }

    // Tracks dynamic icon silhouettes.
    private string BaseFrameFingerprint(Entity<SpriteComponent?> sprite)
    {
        if (HasComp<HumanoidAppearanceComponent>(sprite.Owner))
            return string.Empty;

        // AddItemBloodIconVisual masks the blood onto every visible base layer, so the cache must
        // invalidate when any of them changes state/visibility - not just layer 0 (e.g. Soap fill levels).
        var fingerprint = string.Empty;
        for (var i = 0; _sprite.TryGetLayer(sprite, i, out var layer, false); i++)
        {
            var state = _sprite.LayerGetRsiState(sprite, i);
            fingerprint += $"{(_sprite.IsVisible(layer) ? 1 : 0)}:{(state.IsValid ? state.Name : string.Empty)}|";
        }

        return fingerprint;
    }

    private IEnumerable<(string, PrototypeLayerData)> BuildVisuals(Entity<StainableComponent> ent, List<PrototypeLayerData> templates, string prefix)
    {
        if (!TryGetStainColor(ent, out var color))
            yield break;

        for (var i = 0; i < templates.Count; i++)
        {
            var layer = templates[i];
            var key = $"stain-{prefix}-{i}";
            yield return (key, CopyVisualLayer(layer, color, key));
        }
    }

    private bool TryGetStainColor(Entity<StainableComponent> ent, out Color color)
    {
        color = Color.White;

        if (!_solution.TryGetSolution(ent.Owner, ent.Comp.SolutionName, out _, out var sol) || sol.Volume <= FixedPoint2.Zero)
            return false;

        color = sol.GetColor(_prototypeManager);
        return true;
    }

    private void AddItemBloodIconVisual(Entity<StainableComponent> ent, Entity<SpriteComponent?> sprite, Color color)
    {
        var baseCount = 0;
        while (_sprite.TryGetLayer(sprite, baseCount, out _, false))
            baseCount++;

        for (var i = 0; i < baseCount; i++)
        {
            if (!_sprite.TryGetLayer(sprite, i, out var layer, false) || !_sprite.IsVisible(layer))
                continue;

            var state = _sprite.LayerGetRsiState(sprite, i);
            var rsi = _sprite.LayerGetEffectiveRsi(sprite, i);
            if (rsi == null || !state.IsValid)
                continue;

            var bloodKey = $"stain-icon-{i}";
            var maskKey = $"stain-icon-mask-{i}";

            var maskSource = new PrototypeLayerData { RsiPath = rsi.Path.ToString(), State = state.Name };
            ent.Comp.RevealedLayerKeys.Add(maskKey);
            _sprite.AddLayer(sprite, BuildMaskLayer(maskSource, maskKey, bloodKey), null);

            var masked = BuildItemBloodLayer(bloodKey);
            masked.Shader = StainShaderFor(ent.Owner);
            masked.Color = color;
            ent.Comp.RevealedLayerKeys.Add(bloodKey);
            _sprite.AddLayer(sprite, masked, null);
        }
        // No flat fallback: if nothing can be masked, draw no blood rather than a full-box overlay.
    }

    private static PrototypeLayerData BuildMaskLayer(PrototypeLayerData source, string maskKey, string targetKey)
    {
        return new PrototypeLayerData
        {
            RsiPath = source.RsiPath,
            TexturePath = source.TexturePath,
            State = source.State,
            Scale = source.Scale,
            Rotation = source.Rotation,
            Offset = source.Offset,
            RenderingStrategy = source.RenderingStrategy,
            MapKeys = new() { maskKey },
            CopyToShaderParameters = new PrototypeCopyToShaderParameters
            {
                LayerKey = targetKey,
                ParameterTexture = StainMaskTextureParam,
                ParameterUV = StainMaskUvParam,
            }
        };
    }

    private void AddBodyStainVisuals(Entity<StainableComponent> ent, AppearanceChangeEvent args, Entity<SpriteComponent?> sprite, Color color)
    {
        var slots = ent.Comp.BodyStainSlots;
        if (args.AppearanceData.TryGetValue(StainVisuals.BodySlots, out var bodySlots) &&
            bodySlots is SlotFlags bodySlotFlags)
        {
            slots = bodySlotFlags;
        }

        // Insert the bare-body stains just below the matching clothing slot's bookmark layer so worn gear
        // (and its own stains) draws over them: body -> body stain -> gear -> gear stain. This keeps e.g.
        // dirty bare feet hidden once boots are on, while still showing through where there's no gear.
        if ((slots & SlotFlags.FEET) != 0)
            AddBodyStainVisual(ent, sprite, color, BareFeetLayerKey, "shoeblood", "shoes");

        if ((slots & SlotFlags.GLOVES) != 0)
            AddBodyStainVisual(ent, sprite, color, BareHandsLayerKey, "gloveblood", "gloves");
    }

    private void AddBodyStainVisual(Entity<StainableComponent> ent, Entity<SpriteComponent?> sprite, Color color, string key, string state, string slotBookmark)
    {
        var layerData = new PrototypeLayerData
        {
            RsiPath = "_Pirate/Effects/blood.rsi",
            State = state,
            Color = color,
            MapKeys = new() { key }
        };

        ent.Comp.RevealedLayerKeys.Add(key);

        // Below the slot's clothing bookmark (which sits above the bare body) when it exists; on top otherwise.
        if (_sprite.LayerMapTryGet(sprite, slotBookmark, out var index, false))
            _sprite.AddLayer(sprite, layerData, index);
        else
            _sprite.AddLayer(sprite, layerData, null);
    }

    private static PrototypeLayerData BuildItemBloodLayer(string key, PrototypeLayerData? source = null)
    {
        return new PrototypeLayerData
        {
            RsiPath = BloodRsiPath,
            State = ItemBloodState,
            Scale = source?.Scale,
            Rotation = source?.Rotation,
            Offset = source?.Offset,
            Visible = source?.Visible,
            RenderingStrategy = source?.RenderingStrategy,
            MapKeys = new() { key }
        };
    }

    private static PrototypeLayerData CopyVisualLayer(PrototypeLayerData source, Color color, string key)
    {
        return new PrototypeLayerData
        {
            TexturePath = source.TexturePath,
            RsiPath = source.RsiPath,
            State = source.State,
            Scale = source.Scale,
            Rotation = source.Rotation,
            Offset = source.Offset,
            Visible = source.Visible,
            RenderingStrategy = source.RenderingStrategy,
            Color = color,
            MapKeys = new() { key }
        };
    }
}
