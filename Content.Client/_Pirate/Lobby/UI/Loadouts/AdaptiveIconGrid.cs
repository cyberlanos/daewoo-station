using System.Numerics;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._Pirate.Lobby.UI.Loadouts;

public sealed class AdaptiveIconGrid : GridContainer
{
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        if (!float.IsInfinity(availableSize.X) && availableSize.X > 0)
            MaxGridWidth = availableSize.X;

        return base.MeasureOverride(availableSize);
    }
}
