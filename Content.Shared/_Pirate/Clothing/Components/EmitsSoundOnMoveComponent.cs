// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Map;

namespace Content.Shared._Pirate.Clothing.Components;

/// <summary>
/// Clothing that plays an extra worn gear sound on footstep events.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class EmitsSoundOnMoveComponent : Component
{
    [ViewVariables(VVAccess.ReadWrite)]
    [DataField(required: true), AutoNetworkedField]
    public SoundSpecifier SoundCollection = default!;

    [ViewVariables(VVAccess.ReadWrite)]
    [DataField("requiresGravity"), AutoNetworkedField]
    public bool RequiresGravity = true;

    [ViewVariables(VVAccess.ReadOnly)]
    public EntityCoordinates LastPosition = EntityCoordinates.Invalid;

    [ViewVariables(VVAccess.ReadOnly)]
    public float SoundDistance;

    [ViewVariables(VVAccess.ReadOnly)]
    public bool IsSlotValid = true;

    [DataField]
    public float DistanceWalking = 1.5f;

    [DataField]
    public float DistanceSprinting = 2f;

    [DataField]
    public bool RequiresWorn;
}
