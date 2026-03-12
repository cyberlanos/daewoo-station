using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared._Pirate.NanoChat;

[Serializable, NetSerializable]
public sealed partial class PdaPhotoPrintToFaxDoAfterEvent : SimpleDoAfterEvent;
