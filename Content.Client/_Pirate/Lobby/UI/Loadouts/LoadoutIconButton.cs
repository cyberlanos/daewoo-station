using System.Numerics;
using Content.Shared.Clothing;
using Content.Shared.Preferences.Loadouts;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client._Pirate.Lobby.UI.Loadouts;

public sealed class LoadoutIconButton : Button
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    private readonly EntityUid? _entity;

    public LoadoutIconButton(LoadoutPrototype loadout, string name, FormattedMessage? reason = null)
    {
        IoCManager.InjectDependencies(this);

        ToggleMode = true;
        MinSize = new Vector2(72, 72);
        SetSize = new Vector2(72, 72);
        Margin = new Thickness(0, 0, 6, 6);

        var sprite = new SpriteView
        {
            Scale = new Vector2(3, 3),
            OverrideDirection = Direction.South,
            VerticalAlignment = VAlignment.Center,
            HorizontalAlignment = HAlignment.Center,
            SetSize = new Vector2(64, 64),
        };

        AddChild(sprite);

        var entityProto = loadout.DummyEntity ?? _entManager.System<LoadoutSystem>().GetFirstOrNull(loadout);
        var description = string.Empty;

        if (entityProto != null)
        {
            _entity = _entManager.SpawnEntity(entityProto, MapCoordinates.Nullspace);
            sprite.SetEntity(_entity);

            if (_entManager.TryGetComponent(_entity.Value, out MetaDataComponent? meta))
                description = meta.EntityDescription;
        }

        TooltipSupplier = _ =>
        {
            var tooltip = new Tooltip();
            var text = string.IsNullOrWhiteSpace(description)
                ? name
                : $"{name}\n\n{description}";

            if (reason != null)
                text += $"\n\n{reason}";

            tooltip.SetMessage(FormattedMessage.FromUnformatted(text));
            return tooltip;
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
