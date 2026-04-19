/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using System.Numerics;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared._Pirate.ZLevels.Pulling;

/// <summary>
/// Component added to a pulled entity when the puller transitions to another z-level.
/// Handles smooth movement of the pulled entity towards the puller's position until
/// the pulled entity also transitions to the target z-level.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState, UnsavedComponent, Access(typeof(CEZLevelPullingSystem))]
public sealed partial class CEZLevelPullingTransitionComponent : Component
{
    /// <summary>
    /// The starting world position of the pulled entity when the transition began.
    /// </summary>
    [DataField, AutoNetworkedField]
    public Vector2 StartPosition;

    /// <summary>
    /// The position of the puller when the transition began (target position to move towards).
    /// </summary>
    [DataField, AutoNetworkedField]
    public Vector2 TargetPosition;

    /// <summary>
    /// The normalized world-space direction from the puller to the pulled entity before the z-transition.
    /// Used to restore the original pull spacing after both entities reunite on the new level.
    /// </summary>
    [DataField, AutoNetworkedField]
    public Vector2 PullDirection;

    /// <summary>
    /// The separation distance between puller and pulled entity before the z-transition.
    /// The resumed pull joint should preserve this spacing instead of inheriting the temporary stacked landing.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float PullDistance;

    /// <summary>
    /// Reference to the puller entity.
    /// </summary>
    [DataField, AutoNetworkedField]
    public EntityUid? TargetPuller;

    /// <summary>
    /// The z-level where the puller is now.
    /// </summary>
    [DataField, AutoNetworkedField]
    public int TargetZLevel;

    /// <summary>
    /// How many z-levels the pulled entity still needs to move to reunite with the puller.
    /// </summary>
    [DataField, AutoNetworkedField]
    public int TargetOffset;

    /// <summary>
    /// Time when the transition should be complete.
    /// </summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField]
    public TimeSpan? NextTransition;

    /// <summary>
    /// Ensures the cross-z move is only attempted once per transition.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool TransferAttempted;

    /// <summary>
    /// How fast the entity moves during z-level transition (units per second).
    /// </summary>
    [DataField]
    public float TransitionSpeed = 5f;
}
