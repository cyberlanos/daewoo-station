// SPDX-FileCopyrightText: 2026 Pirate
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Shared._Pirate.Ambience.Areas;

[RegisterComponent]
public sealed partial class AreaComponent : Component;

[RegisterComponent]
public sealed partial class StationAreaComponent : Component;

[RegisterComponent]
public sealed partial class DepartmentAreaComponent : Component
{
    [DataField(required: true)]
    public ProtoId<DepartmentPrototype> Department;
}

[RegisterComponent]
public sealed partial class HolyAreaComponent : Component;

[RegisterComponent]
public sealed partial class PrisonAreaComponent : Component;

// Pirate: source area markers reference these components, but Pirate has no matching systems yet.
[RegisterComponent]
public sealed partial class CarpMigrationTargetComponent : Component;

[RegisterComponent]
public sealed partial class LuckyWinnerTargetComponent : Component;
