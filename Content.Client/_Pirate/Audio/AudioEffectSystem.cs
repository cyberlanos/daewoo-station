using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using Content.Shared.GameTicking;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Components;
using Robust.Shared.Audio.Effects;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;
using DependencyAttribute = Robust.Shared.IoC.DependencyAttribute;

namespace Content.Client._Pirate.Audio;

/// <summary>
/// Handles client-side audio effects used by Pirate audio systems.
/// </summary>
public sealed class PirateAudioEffectSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototype = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;

    private bool? _auxiliariesSafe;
    private readonly Dictionary<ProtoId<AudioPresetPrototype>, (EntityUid AuxiliaryUid, EntityUid EffectUid)> _cachedEffects = new();
    private readonly HashSet<EntityUid> _activeEffectAudio = new();
    private const float ReverbGainScale = 0.58f;
    private const float EarlyReflectionGainScale = 0.62f;
    private const float LateReverbGainScale = 0.48f;
    private const float LargeAreaGainScale = 0.85f;
    private const float LargeAreaLateReverbScale = 0.75f;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<RoundRestartCleanupEvent>(_ => Cleanup());
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypeReload);
        SubscribeLocalEvent<AudioComponent, EntityTerminatingEvent>(OnAudioTerminating);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        Cleanup();
    }

    private void OnPrototypeReload(PrototypesReloadedEventArgs args)
    {
        if (!args.WasModified<AudioPresetPrototype>())
            return;

        var oldPresets = _cachedEffects.Keys.ToArray();
        Cleanup();

        foreach (var oldPreset in oldPresets)
            ResolveCachedEffect(oldPreset, out _, out _);
    }

    private void Cleanup()
    {
        foreach (var cache in _cachedEffects.Values)
        {
            TryQueueDel(cache.AuxiliaryUid);
            TryQueueDel(cache.EffectUid);
        }

        _cachedEffects.Clear();
        _activeEffectAudio.Clear();
    }

    private void OnAudioTerminating(Entity<AudioComponent> entity, ref EntityTerminatingEvent args)
    {
        if (_activeEffectAudio.Remove(entity.Owner) && _activeEffectAudio.Count == 0)
            Cleanup();
    }

    private void CleanupIfIdle()
    {
        if (_activeEffectAudio.Count == 0)
            Cleanup();
    }

    private bool DetermineAuxiliarySafety([NotNullWhen(true)] out (EntityUid Entity, AudioAuxiliaryComponent Component)? auxiliaryPair, bool keepPair)
    {
        (EntityUid Entity, AudioAuxiliaryComponent Component)? maybeAuxiliaryPair = null;

        try
        {
            maybeAuxiliaryPair = _audio.CreateAuxiliary();
            _auxiliariesSafe = true;
        }
        catch (Exception ex)
        {
            Log.Info($"Determined audio auxiliaries are unsafe in this run. Exception: {ex}");
            _auxiliariesSafe = false;
            TryQueueDel(maybeAuxiliaryPair?.Entity);
            auxiliaryPair = null;
            return false;
        }

        if (keepPair)
        {
            auxiliaryPair = maybeAuxiliaryPair.Value;
            return true;
        }

        QueueDel(maybeAuxiliaryPair.Value.Entity);
        auxiliaryPair = null;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool AuxiliariesAreSafe()
    {
        if (_auxiliariesSafe == null)
            DetermineAuxiliarySafety(out _, false);

        return _auxiliariesSafe == true;
    }

    public bool TryAddEffect(Entity<AudioComponent> entity, ProtoId<AudioPresetPrototype> preset)
    {
        if (!AuxiliariesAreSafe() || !ResolveCachedEffect(preset, out var auxiliaryUid, out _))
            return false;

        _audio.SetAuxiliary(entity, entity.Comp, auxiliaryUid);
        _activeEffectAudio.Add(entity.Owner);
        return true;
    }

    public bool TryRemoveEffect(Entity<AudioComponent> entity)
    {
        if (!AuxiliariesAreSafe())
            return false;

        if (entity.Comp.Auxiliary is not { } existingAuxiliary || !existingAuxiliary.IsValid())
        {
            _activeEffectAudio.Remove(entity.Owner);
            CleanupIfIdle();
            return true;
        }

        if (!_cachedEffects.Values.Any(cache => cache.AuxiliaryUid == existingAuxiliary))
        {
            _activeEffectAudio.Remove(entity.Owner);
            CleanupIfIdle();
            return true;
        }

        _audio.SetAuxiliary(entity, entity.Comp, null);
        _activeEffectAudio.Remove(entity.Owner);
        CleanupIfIdle();
        return true;
    }

    public bool ResolveCachedEffect(ProtoId<AudioPresetPrototype> preset, [NotNullWhen(true)] out EntityUid? auxiliaryUid, [NotNullWhen(true)] out EntityUid? effectUid)
    {
        if (_auxiliariesSafe == false)
        {
            auxiliaryUid = null;
            effectUid = null;
            return false;
        }

        if (_cachedEffects.TryGetValue(preset, out var cached))
        {
            if (!Exists(cached.AuxiliaryUid) || !Exists(cached.EffectUid))
            {
                _cachedEffects.Remove(preset);
                return TryCacheEffect(preset, out auxiliaryUid, out effectUid);
            }

            auxiliaryUid = cached.AuxiliaryUid;
            effectUid = cached.EffectUid;
            return true;
        }

        return TryCacheEffect(preset, out auxiliaryUid, out effectUid);
    }

    public bool TryCacheEffect(ProtoId<AudioPresetPrototype> preset, [NotNullWhen(true)] out EntityUid? auxiliaryUid, [NotNullWhen(true)] out EntityUid? effectUid)
    {
        auxiliaryUid = null;
        effectUid = null;

        if (_auxiliariesSafe == false || !_prototype.TryIndex(preset, out var presetPrototype))
            return false;

        (EntityUid Entity, AudioAuxiliaryComponent Component)? maybeAuxiliaryPair = null;

        if (_auxiliariesSafe == null && !DetermineAuxiliarySafety(out maybeAuxiliaryPair, true))
            return false;

        var auxiliaryPair = maybeAuxiliaryPair ?? _audio.CreateAuxiliary();

        DebugTools.Assert(Exists(auxiliaryPair.Entity), "Audio auxiliary entity does not exist.");
        if (!Exists(auxiliaryPair.Entity))
            return false;

        var effectPair = _audio.CreateEffect();
        _audio.SetEffectPreset(effectPair.Entity, effectPair.Component, AttenuateAreaEcho(presetPrototype));
        _audio.SetEffect(auxiliaryPair.Entity, auxiliaryPair.Component, effectPair.Entity);

        if (!_cachedEffects.TryAdd(preset, (auxiliaryPair.Entity, effectPair.Entity)))
        {
            TryQueueDel(auxiliaryPair.Entity);
            TryQueueDel(effectPair.Entity);
            return false;
        }

        auxiliaryUid = auxiliaryPair.Entity;
        effectUid = effectPair.Entity;
        return true;
    }

    private static ReverbProperties AttenuateAreaEcho(AudioPresetPrototype preset)
    {
        var largeAreaScale = preset.ID is "ConcertHall" or "Hangar" ? LargeAreaGainScale : 1f;
        var largeAreaLateScale = preset.ID is "ConcertHall" or "Hangar" ? LargeAreaLateReverbScale : 1f;

        return new ReverbProperties(
            preset.Density,
            preset.Diffusion,
            preset.Gain * ReverbGainScale * largeAreaScale,
            preset.GainHF,
            preset.GainLF,
            preset.DecayTime,
            preset.DecayHFRatio,
            preset.DecayLFRatio,
            preset.ReflectionsGain * EarlyReflectionGainScale * largeAreaScale,
            preset.ReflectionsDelay,
            preset.ReflectionsPan,
            preset.LateReverbGain * LateReverbGainScale * largeAreaLateScale,
            preset.LateReverbDelay,
            preset.LateReverbPan,
            preset.EchoTime,
            preset.EchoDepth,
            preset.ModulationTime,
            preset.ModulationDepth,
            preset.AirAbsorptionGainHF,
            preset.HFReference,
            preset.LFReference,
            preset.RoomRolloffFactor,
            preset.DecayHFLimit);
    }
}
