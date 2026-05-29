// Ported from CMU.
using Content.Shared._Pirate.ZLevels.Core.EntitySystems;
using Content.Shared._Pirate.ZLevels.Shooting;
using Robust.Shared.GameStates;

namespace Content.Shared._Pirate.ZLevels.Core.Components;

/// <summary>
/// Toggle marker for "shoot down through floor opening". Set via the
/// <c>CEToggleShootDownZLevel</c> keybind (Ctrl+Shift+Space). Mutually exclusive with
/// <see cref="CEZLevelViewerComponent.LookUp"/> — the latter routes shots upward instead.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(fieldDeltas: true), UnsavedComponent, Access(typeof(CEZLevelShootingSystem), typeof(CESharedZLevelsSystem))]
public sealed partial class CEZLevelShooterComponent : Component
{
    [DataField, AutoNetworkedField]
    public bool ShootDown;
}
