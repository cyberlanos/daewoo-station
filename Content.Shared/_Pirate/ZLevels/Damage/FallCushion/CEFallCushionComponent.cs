using Robust.Shared.GameStates;

namespace Content.Shared._Pirate.ZLevels.Damage.FallCushion;

/// <summary>
/// When another entity falls onto this one from a Z-level above, scales the fall damage and stun the
/// faller would take. Lets surfaces like water break a fall. By default the faller still lands and is
/// knocked down (laying animation) but takes no damage.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class CEFallCushionComponent : Component
{
    /// <summary>
    /// Scales the fall damage the faller takes. 0 negates it entirely; 1 leaves it unchanged.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float DamageMultiplier = 0f;

    /// <summary>
    /// Scales the fall knockdown the faller takes. 1 keeps the normal knockdown so the faller still
    /// drops to the laying state on impact; 0 negates it entirely.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float StunMultiplier = 1f;
}
