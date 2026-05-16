// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameStates;

namespace Content.Shared._Pirate.AudioMuffle;

/// <summary>
/// Added to AI eye entities, used for audio muffle calculations.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class AiEyeComponent : Component;
