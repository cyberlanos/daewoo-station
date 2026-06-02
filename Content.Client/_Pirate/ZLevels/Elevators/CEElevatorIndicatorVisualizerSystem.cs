using System;
using Content.Shared._Pirate.ZLevels.Elevators;
using Content.Shared._Pirate.ZLevels.Elevators.Components;
using Robust.Client.GameObjects;

namespace Content.Client._Pirate.ZLevels.Elevators;

/// <summary>
/// Draws the elevator's current floor number on its indicator by swapping the digit sprite layer
/// (lift_indo-num0..9) from the <see cref="CEElevatorIndicatorVisuals.Floor"/> appearance value the
/// server sets. SS13's lift_indicator showed the number via maptext; SS14 maptext doesn't scale with
/// the world, so we use pre-rendered digit sprites that live in the display window instead.
/// </summary>
public sealed class CEElevatorIndicatorVisualizerSystem : VisualizerSystem<CEElevatorIndicatorComponent>
{
    [Dependency] private readonly SpriteSystem _sprite = default!;

    protected override void OnAppearanceChange(EntityUid uid, CEElevatorIndicatorComponent component, ref AppearanceChangeEvent args)
    {
        if (args.Sprite == null)
            return;

        if (!AppearanceSystem.TryGetData<int>(uid, CEElevatorIndicatorVisuals.Floor, out var floor, args.Component))
            return;

        // Single-digit readout (floors 1-9 cover virtually every elevator). Higher floors show the
        // ones digit rather than nothing.
        var digit = Math.Abs(floor) % 10;
        _sprite.LayerSetRsiState((uid, args.Sprite), CEElevatorVisualLayers.Digit, $"lift_indo-num{digit}");
    }
}
