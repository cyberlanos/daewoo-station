using Content.Goobstation.Common.Clothing;

namespace Content.Shared._Pirate.Clothing;

public sealed class ClothingLayerRemapSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ClothingLayerRemapComponent, GetActualMapLayerEvent>(OnGetActualMapLayer);
    }

    private void OnGetActualMapLayer(Entity<ClothingLayerRemapComponent> ent, ref GetActualMapLayerEvent args)
    {
        if (args.MapLayer == ent.Comp.FromLayer)
            args.MapLayer = ent.Comp.ToLayer;
    }
}
