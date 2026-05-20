using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;

namespace Content.Client._Pirate.UserInterface.Controls;

/// <summary>
/// Lays out children in rows: left-to-right, wrapping to the next row when the next child
/// would exceed the available width. Each child gets its desired size (no fixed grid cells).
/// Similar to CSS flex-wrap.
/// </summary>
[Virtual]
public class WrapContainer : Container
{
    public const string StylePropertySeparation = "separation";
    private const int DefaultSeparation = 4;

    public int? SeparationOverride { get; set; }

    private float _lastArrangeWidth;

    private int ActualSeparation =>
        SeparationOverride ?? (TryGetStyleProperty(StylePropertySeparation, out int separation) ? separation : DefaultSeparation);

    private delegate void ArrangeChild(Control child, float x, float y, float w, float h);

    private static float ComputeLayout(
        float maxWidth,
        int sep,
        List<Control> visible,
        ArrangeChild? onArrange)
    {
        float x = 0, y = 0, rowHeight = 0;
        var firstInRow = true;
        foreach (var child in visible)
        {
            var w = child.DesiredSize.X;
            var h = child.DesiredSize.Y;
            if (!firstInRow && x + sep + w > maxWidth)
            {
                y += rowHeight + sep;
                x = 0;
                rowHeight = 0;
                firstInRow = true;
            }
            if (!firstInRow)
                x += sep;
            firstInRow = false;
            onArrange?.Invoke(child, x, y, w, h);
            x += w;
            rowHeight = Math.Max(rowHeight, h);
        }
        return y + rowHeight;
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        var sep = ActualSeparation;
        var visible = Children.Where(c => c.Visible).ToList();
        if (visible.Count == 0)
            return Vector2.Zero;

        foreach (var child in visible)
            child.Measure(new Vector2(availableSize.X, float.PositiveInfinity));

        var widestChild = visible.Max(child => child.DesiredSize.X);

        float wrapWidth;
        var hasFiniteAvailable = !float.IsPositiveInfinity(availableSize.X) && availableSize.X > 0f;
        if (hasFiniteAvailable && _lastArrangeWidth > 0f)
            wrapWidth = Math.Min(availableSize.X, _lastArrangeWidth);
        else if (hasFiniteAvailable)
            wrapWidth = availableSize.X;
        else if (_lastArrangeWidth > 0f)
            wrapWidth = _lastArrangeWidth;
        else
            wrapWidth = widestChild;

        wrapWidth = Math.Max(wrapWidth, widestChild);

        var totalHeight = ComputeLayout(wrapWidth, sep, visible, null);
        var desiredWidth = hasFiniteAvailable ? availableSize.X : wrapWidth;
        return new Vector2(desiredWidth, totalHeight);
    }

    protected override Vector2 ArrangeOverride(Vector2 finalSize)
    {
        var sep = ActualSeparation;
        var visible = Children.Where(c => c.Visible).ToList();
        ComputeLayout(finalSize.X, sep, visible, (child, x, y, w, h) =>
            child.Arrange(UIBox2.FromDimensions(x, y, w, h)));

        if (!MathHelper.CloseTo(_lastArrangeWidth, finalSize.X, 0.5f))
        {
            _lastArrangeWidth = finalSize.X;
            InvalidateMeasure();
        }

        return finalSize;
    }
}
