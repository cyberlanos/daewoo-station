using System.Numerics;
using Content.Client._Pirate.Loadouts;
using Content.Shared.Clothing;
using Content.Shared.Preferences.Loadouts;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client._Pirate.Lobby.UI.Loadouts;

public sealed class LoadoutIconButton : Button
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public event Action? OnCustomizePressed;

    private static readonly StyleBoxFlat NormalStyle = CreateStyle("#2a2a35", "#32323e");
    private static readonly StyleBoxFlat HoverStyle = CreateStyle("#2a3a4a", "#32323e");
    private static readonly StyleBoxFlat SelectedStyle = CreateStyle("#2a3a4a", "#60a5fa");
    private static readonly StyleBoxFlat DisabledStyle = CreateStyle("#1a1a22", "#2a2a2a");

    private readonly EntityUid? _entity;

    public LoadoutIconButton(LoadoutPrototype loadout, string name, string? customColorTint = null, FormattedMessage? reason = null)
    {
        IoCManager.InjectDependencies(this);

        ToggleMode = true;
        MinSize = new Vector2(108, 108);
        SetSize = new Vector2(108, 108);
        Margin = new Thickness(0, 0, 6, 6);
        StyleBoxOverride = NormalStyle;
        ModulateSelfOverride = Color.White;

        var sprite = new SpriteView
        {
            Scale = new Vector2(4.5f, 4.5f),
            OverrideDirection = Direction.South,
            VerticalAlignment = VAlignment.Center,
            HorizontalAlignment = HAlignment.Center,
            SetSize = new Vector2(96, 96),
        };

        AddChild(sprite);

        var entityProto = ResolveDisplayEntity(loadout);
        var displayName = name;
        var description = string.Empty;

        if (entityProto != null)
        {
            _entity = _entManager.SpawnEntity(entityProto, MapCoordinates.Nullspace);
            SetCustomColor(customColorTint);
            sprite.SetEntity(_entity);

            if (_entManager.TryGetComponent(_entity.Value, out MetaDataComponent? meta))
            {
                displayName = meta.EntityName;
                description = meta.EntityDescription;
            }
        }

        if (string.IsNullOrWhiteSpace(displayName))
            displayName = loadout.ID;

        TooltipSupplier = _ =>
        {
            var tooltip = new Tooltip();
            var text = string.IsNullOrWhiteSpace(description)
                ? displayName
                : $"{displayName}\n\n{description}";

            if (reason != null)
                text += $"\n\n{reason}";

            tooltip.SetMessage(FormattedMessage.FromUnformatted(text));
            return tooltip;
        };

        if (loadout.CustomColorTint)
            AddCustomizeButton();
    }

    private EntProtoId? ResolveDisplayEntity(LoadoutPrototype loadout)
    {
        var entity = loadout.DummyEntity ?? _entManager.System<LoadoutSystem>().GetFirstOrNull(loadout);
        if (entity != null)
            return entity;

        foreach (var equipment in loadout.Equipment.Values)
            return equipment;

        if (loadout.Inhand.Count != 0)
            return loadout.Inhand[0];

        foreach (var storage in loadout.Storage.Values)
        {
            if (storage.Count != 0)
                return storage[0];
        }

        return null;
    }

    public void SetCustomColor(string? customColorTint)
    {
        if (_entity == null || string.IsNullOrEmpty(customColorTint))
            return;

        _entManager.System<LoadoutTintSystem>().SetTint(_entity.Value, Color.FromHex(customColorTint));
    }

    private void AddCustomizeButton()
    {
        var customize = new ContainerButton
        {
            StyleBoxOverride = new StyleBoxEmpty(),
            MinSize = new Vector2(24, 24),
            SetSize = new Vector2(24, 24),
            HorizontalAlignment = HAlignment.Left,
            VerticalAlignment = VAlignment.Top,
            ToolTip = "Customize color",
        };

        customize.AddChild(new TextureRect
        {
            TexturePath = "/Textures/Interface/Nano/gear.svg.192dpi.png",
            SetSize = new Vector2(16, 16),
            VerticalAlignment = VAlignment.Center,
            HorizontalAlignment = HAlignment.Center,
            Stretch = TextureRect.StretchMode.KeepAspectCentered,
        });

        customize.OnPressed += _ => OnCustomizePressed?.Invoke();
        AddChild(customize);
    }

    protected override void DrawModeChanged()
    {
        base.DrawModeChanged();

        StyleBoxOverride = DrawMode switch
        {
            DrawModeEnum.Disabled => DisabledStyle,
            DrawModeEnum.Pressed => SelectedStyle,
            DrawModeEnum.Hover => HoverStyle,
            _ => NormalStyle,
        };
        ModulateSelfOverride = DrawMode == DrawModeEnum.Disabled
            ? new Color(1f, 1f, 1f, 0.55f)
            : Color.White;
        InvalidateMeasure();
    }

    private static StyleBoxFlat CreateStyle(string backgroundColor, string borderColor)
    {
        return new StyleBoxFlat
        {
            BackgroundColor = Color.FromHex(backgroundColor),
            BorderColor = Color.FromHex(borderColor),
            BorderThickness = new Thickness(1),
            ContentMarginLeftOverride = 4,
            ContentMarginRightOverride = 4,
            ContentMarginTopOverride = 4,
            ContentMarginBottomOverride = 4,
        };
    }

    [Obsolete("Controls should only be removed from UI tree instead of being disposed")]
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing || _entity == null)
            return;

        _entManager.DeleteEntity(_entity);
    }
}
