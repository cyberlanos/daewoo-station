// Ported from CMU. Predicted-only twin of <see cref="CEZLevelProjectileVisualOffsetComponent"/>:
// attached client-side during prediction so we don't dirty server-owned entities; replaced by
// the synced variant once the server confirms the shot.
using System.Numerics;

namespace Content.Shared._Pirate.ZLevels.Core.Components;

[RegisterComponent]
public sealed partial class CEZLevelPredictedProjectileVisualOffsetComponent : Component
{
    public Vector2 Offset;

    public Vector2? OriginalOffset;

    public Vector2 AppliedOffset;
}
