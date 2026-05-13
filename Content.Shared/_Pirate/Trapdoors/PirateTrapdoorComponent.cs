using Content.Shared.Maps;
using Robust.Shared.Audio;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._Pirate.Trapdoors;

[RegisterComponent]
public sealed partial class PirateTrapdoorComponent : Component
{
    [DataField]
    public bool StartsOpen;

    [ViewVariables]
    public bool Open;

    [DataField]
    public ProtoId<ContentTileDefinition> DefaultClosedTile = "Plating";

    [DataField]
    public SoundSpecifier OpenSound = new SoundPathSpecifier("/Audio/_Pirate/Machines/Trapdoor/trapdoor_open.ogg");

    [DataField]
    public SoundSpecifier CloseSound = new SoundPathSpecifier("/Audio/_Pirate/Machines/Trapdoor/trapdoor_shut.ogg");

    [ViewVariables]
    public bool HasStoredTile;

    [ViewVariables]
    public Tile StoredTile;

    [ViewVariables]
    public bool HasTilePosition;

    [ViewVariables]
    public EntityUid GridUid = EntityUid.Invalid;

    [ViewVariables]
    public Vector2i TileIndices;
}

[Serializable, NetSerializable]
public enum PirateTrapdoorVisuals : byte
{
    State,
}

[Serializable, NetSerializable]
public enum PirateTrapdoorVisualLayers : byte
{
    Base,
}
