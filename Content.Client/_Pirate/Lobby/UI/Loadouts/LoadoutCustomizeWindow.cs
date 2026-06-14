using System.Numerics;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Utility;

namespace Content.Client._Pirate.Lobby.UI.Loadouts;

/// <summary>
/// Combined dialog for customizing a loadout item: display name, description, and (when the
/// item supports tinting) a color picker. Prefilled with the current custom values or the
/// item's defaults.
/// </summary>
public sealed class LoadoutCustomizeWindow : DefaultWindow
{
    /// <summary>
    /// Raised on apply. The color argument is null when the item does not support tinting.
    /// </summary>
    public event Action<string, string, Color?>? OnSubmitted;

    /// <summary>
    /// Raised live as the color slider moves, for immediate preview (not persisted).
    /// </summary>
    public event Action<Color>? OnColorPreview;

    /// <summary>
    /// Raised when the window closes without applying, so the live preview can be reverted.
    /// </summary>
    public event Action? OnReverted;

    private bool _applied;

    private readonly LineEdit _nameEdit = new()
    {
        HorizontalExpand = true,
    };

    private readonly TextEdit _descriptionEdit = new()
    {
        HorizontalExpand = true,
        VerticalExpand = true,
        Margin = new Thickness(3),
    };

    private readonly ColorSelectorSliders? _colorSelector;

    public LoadoutCustomizeWindow(string loadoutName, string name, string description, Color? color)
    {
        Title = loadoutName;
        MinSize = new Vector2(360, color != null ? 560 : 320);

        _nameEdit.Text = name;
        _descriptionEdit.TextRope = new Rope.Leaf(description);

        var body = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            Margin = new Thickness(8),
            Children =
            {
                new Label { Text = Loc.GetString("loadout-name-desc-name-label") },
                _nameEdit,
                new Label
                {
                    Text = Loc.GetString("loadout-name-desc-description-label"),
                    Margin = new Thickness(0, 8, 0, 0),
                },
                new PanelContainer
                {
                    VerticalExpand = true,
                    Children =
                    {
                        _descriptionEdit,
                    },
                },
            },
        };

        if (color != null)
        {
            _colorSelector = new ColorSelectorSliders
            {
                SelectorType = ColorSelectorSliders.ColorSelectorType.Hsv,
                IsAlphaVisible = false,
                HorizontalExpand = true,
                Color = color.Value,
            };

            _colorSelector.OnColorChanged += _ => OnColorPreview?.Invoke(_colorSelector.Color);

            body.AddChild(new Label
            {
                Text = Loc.GetString("loadout-name-desc-color-label"),
                Margin = new Thickness(0, 8, 0, 0),
            });
            body.AddChild(_colorSelector);
        }

        var apply = new Button
        {
            Text = Loc.GetString("loadout-name-desc-apply"),
            HorizontalAlignment = HAlignment.Right,
            MinWidth = 96,
        };
        apply.OnPressed += _ =>
        {
            _applied = true;
            OnSubmitted?.Invoke(_nameEdit.Text, Rope.Collapse(_descriptionEdit.TextRope), _colorSelector?.Color);
            Close();
        };

        // Closing without applying reverts the live preview to the saved color.
        OnClose += () =>
        {
            if (!_applied)
                OnReverted?.Invoke();
        };

        body.AddChild(new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalAlignment = HAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                apply,
            },
        });

        Contents.AddChild(body);
    }
}
