using Robust.Shared.GameStates;

namespace Content.Shared._Pirate.Loadouts;

/// <summary>
/// Networked tint data for a loadout equipment item.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class LoadoutTintComponent : Component
{
    /// <summary>
    /// The loadout tint color applied to this item. Defaults to <see cref="Color.White"/>.
    /// </summary>
    /// <remarks>
    /// This <see cref="Color"/> field is networked through <see cref="AutoNetworkedFieldAttribute"/>.
    /// </remarks>
    [DataField, AutoNetworkedField]
    public Color Color = Color.White;
}
