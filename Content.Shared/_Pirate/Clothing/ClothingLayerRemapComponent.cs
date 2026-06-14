using Robust.Shared.GameStates;

namespace Content.Shared._Pirate.Clothing;

/// <summary>
/// Always redirects a clothing item's sprite map layer from one bookmark to another while equipped.
/// Used so floor-length garments draw over the wearer's feet/shoes instead of behind them.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class ClothingLayerRemapComponent : Component
{
    /// <summary>
    /// The slot bookmark the clothing would normally be inserted at.
    /// </summary>
    [DataField]
    public string FromLayer = "jumpsuit";

    /// <summary>
    /// The bookmark to insert at instead. Must exist on the wearer's sprite, otherwise the
    /// clothing falls back to <see cref="FromLayer"/>.
    /// </summary>
    [DataField]
    public string ToLayer = "jumpsuitOverShoes";
}
