using System.Numerics;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;

namespace Content.Client._Pirate.Lobby.UI.Loadouts;

public sealed class LoadoutColorPickerWindow : DefaultWindow
{
    public event Action<Color>? OnColorSubmitted;

    private readonly ColorSelectorSliders _colorSelector = new()
    {
        SelectorType = ColorSelectorSliders.ColorSelectorType.Hsv,
        IsAlphaVisible = false,
        HorizontalExpand = true,
    };

    public LoadoutColorPickerWindow(string loadoutName, Color color)
    {
        Title = loadoutName;
        MinSize = new Vector2(360, 330);
        _colorSelector.Color = color;

        var apply = new Button
        {
            Text = "Apply",
            HorizontalAlignment = HAlignment.Right,
            MinWidth = 96,
        };
        apply.OnPressed += _ =>
        {
            OnColorSubmitted?.Invoke(_colorSelector.Color);
            Close();
        };

        Contents.AddChild(new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            Margin = new Thickness(8),
            Children =
            {
                _colorSelector,
                new BoxContainer
                {
                    Orientation = BoxContainer.LayoutOrientation.Horizontal,
                    HorizontalAlignment = HAlignment.Right,
                    Margin = new Thickness(0, 8, 0, 0),
                    Children =
                    {
                        apply,
                    },
                },
            },
        });
    }
}
