// SPDX-FileCopyrightText: 2026 CyberLanos <cyber.lanos00@gmail.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared._DV.NanoChat;

[Serializable, NetSerializable]
public sealed partial class PdaPhotoUploadDoAfterEvent : SimpleDoAfterEvent
{
}
