using Robust.Shared.GameStates;

namespace Content.Shared._Pirate.Loadouts;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class LoadoutTintComponent : Component
{
    [DataField, AutoNetworkedField]
    public Color Color = Color.White;
}
