using Robust.Shared.GameStates;

namespace Content.Shared._Pirate.ZLevels.Core.Components;

/// <summary>
/// Marks an entity as exempt from automatic z-level physics (falling through holes, gravity,
/// stair/auto-descend). Intended for free-floating camera/observation eyes (station AI eye,
/// abductor eye) so they stay on whatever deck they are placed on instead of dropping down.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class CEZLevelPhysicsExemptComponent : Component;
