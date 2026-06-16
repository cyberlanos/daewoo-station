using Robust.Shared.GameStates;

namespace Content.Shared._Pirate.ZLevels.Surveillance;

/// <summary>
/// Marks a remote eye/camera entity (AI holo, abductor observation eye, ...) so that when a player
/// looks through it, the server also feeds them the decks below/above it — the same z-level PVS
/// coverage surveillance cameras get. Pairs with the client anchoring its z-render stack to the
/// viewed eye. Triggered off the engine's view-subscription added/removed events.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class CEZViewSourceComponent : Component;
