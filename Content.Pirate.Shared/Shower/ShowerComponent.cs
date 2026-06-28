using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Pirate.Shared.Showers
{
    /// <summary>
    /// showers that can be enabled
    /// </summary>
    [RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
    public sealed partial class ShowerComponent : Component
    {
        /// <summary>
        /// Toggles shower.
        /// </summary>
        [DataField, AutoNetworkedField]
        public bool ToggleShower;

        [DataField("enableShowerSound")]
        public SoundSpecifier EnableShowerSound = new SoundPathSpecifier("/Audio/_Pirate/Ambience/Objects/shower_enable.ogg");

        public EntityUid? PlayingStream;

        [DataField("loopingSound")]
        public SoundSpecifier LoopingSound = new SoundPathSpecifier("/Audio/_Pirate/Ambience/Objects/shower_running.ogg");

        [DataField]
        public float StainCleanRange = 0.8f;

        [DataField]
        public float StainCleanInterval = 1f;

        [ViewVariables]
        public float StainCleanAccumulator;
    }


    [Serializable, NetSerializable]
    public enum ShowerVisuals : byte
    {
        ShowerVisualState,
    }

    [Serializable, NetSerializable]
    public enum ShowerVisualState : byte
    {
        Off,
        On
    }
}

