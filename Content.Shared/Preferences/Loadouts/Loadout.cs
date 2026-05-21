// SPDX-FileCopyrightText: 2024 Firewatch <54725557+musicmanvr@users.noreply.github.com>
// SPDX-FileCopyrightText: 2024 Mr. 27 <45323883+Dutch-VanDerLinde@users.noreply.github.com>
// SPDX-FileCopyrightText: 2024 Mr. 27 <koolthunder019@gmail.com>
// SPDX-FileCopyrightText: 2024 metalgearsloth <31366439+metalgearsloth@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 Aiden <28298836+Aidenkrz@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.Preferences.Loadouts;

/// <summary>
/// Specifies the selected prototype and custom data for a loadout.
/// </summary>
[Serializable, NetSerializable, DataDefinition]
public sealed partial class Loadout : IEquatable<Loadout>
{
    [DataField]
    public ProtoId<LoadoutPrototype> Prototype;

#region Pirate: loadout
    /// <summary>
    /// Optional custom tint stored as a hex color string accepted by <see cref="Color.FromHex"/>.
    /// </summary>
    /// <remarks>
    /// Persisted loadout tint strings must not exceed 16 characters.
    /// </remarks>
    [DataField]
    public string? CustomColorTint;

    /// <summary>
    /// Checks whether the custom tint is empty or a persisted parser-valid hex color string.
    /// </summary>
    public bool IsValidColorTint()
    {
        return string.IsNullOrEmpty(CustomColorTint) ||
               CustomColorTint.Length <= 16 && Color.TryFromHex(CustomColorTint) != null;
    }

    public Loadout Clone()
    {
        return new Loadout
        {
            Prototype = Prototype,
            CustomColorTint = CustomColorTint,
        };
    }
#endregion

    public bool Equals(Loadout? other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        return Prototype.Equals(other.Prototype) && CustomColorTint == other.CustomColorTint; // Pirate: loadout
    }

    public override bool Equals(object? obj)
    {
        return ReferenceEquals(this, obj) || obj is Loadout other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Prototype, CustomColorTint); // Pirate: loadout
    }
}
