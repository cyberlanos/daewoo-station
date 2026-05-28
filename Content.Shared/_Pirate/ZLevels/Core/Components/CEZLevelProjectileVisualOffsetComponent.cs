// Ported from CMU.
using System.Numerics;
using Robust.Shared.GameStates;

namespace Content.Shared._Pirate.ZLevels.Core.Components;

/// <summary>
/// Server-confirmed sprite offset for a projectile that was redirected across a Z layer.
/// Physics on the target layer is unchanged; only the rendered sprite is shifted so the
/// muzzle flash appears at the gun on the source layer rather than at the projectile's
/// real (target-layer) spawn point.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(fieldDeltas: true)]
public sealed partial class CEZLevelProjectileVisualOffsetComponent : Component
{
    [DataField, AutoNetworkedField]
    public Vector2 Offset;

    /// <summary>Sprite offset present before we applied ours; restored on shutdown.</summary>
    public Vector2? OriginalOffset;

    public Vector2 AppliedOffset;
}
