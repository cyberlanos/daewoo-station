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
    /// <summary>
    /// Eye-independent barrel-shift (source barrel minus the projectile's target-layer spawn), in
    /// world space. Render-displacement compensation is added client-side from the live eye, since
    /// lanos's eye can be rotated.
    /// </summary>
    [DataField, AutoNetworkedField]
    public Vector2 Offset;

    /// <summary>Shot Z offset (+1 up / -1 down).</summary>
    [DataField, AutoNetworkedField]
    public int Depth;

    /// <summary>Sprite offset present before we applied ours; restored on shutdown.</summary>
    public Vector2? OriginalOffset;

    public Vector2 AppliedOffset;
}
