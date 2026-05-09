// SPDX-FileCopyrightText: 2023 Kara <lunarautomaton6@gmail.com>
// SPDX-FileCopyrightText: 2023 metalgearsloth <31366439+metalgearsloth@users.noreply.github.com>
// SPDX-FileCopyrightText: 2024 Pieter-Jan Briers <pieterjan.briers+git@gmail.com>
// SPDX-FileCopyrightText: 2025 Aiden <28298836+Aidenkrz@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.CCVar;
using Robust.Client.Audio;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface.Controllers;
using Robust.Shared.Audio.Sources;
using Robust.Shared.Configuration;

namespace Content.Client.Audio;

public sealed class AudioUIController : UIController
{
    [Dependency] private readonly IAudioManager _audioManager = default!;
    [Dependency] private readonly IConfigurationManager _configManager = default!;
    [Dependency] private readonly IResourceCache _cache = default!;

    private float _interfaceGain;
    private IAudioSource? _clickSource;
    private IAudioSource? _hoverSource;

    private const float ClickGain = 0.25f;
    private const float HoverGain = 0.05f;

    public override void Initialize()
    {
        base.Initialize();

        /*
         * This exists to load UI sounds outside of the game sim.
         */

        // No unsub coz never shuts down until program exit.
        _configManager.OnValueChanged(CCVars.UIClickSound, SetClickSound, true);
        _configManager.OnValueChanged(CCVars.UIHoverSound, SetHoverSound, true);

        _configManager.OnValueChanged(CCVars.InterfaceVolume, SetInterfaceVolume, true);
    }

    private void SetInterfaceVolume(float obj)
    {
        _interfaceGain = obj;

        if (_clickSource != null)
        {
            _clickSource.Gain = ClickGain * _interfaceGain;
        }

        if (_hoverSource != null)
        {
            _hoverSource.Gain = HoverGain * _interfaceGain;
        }
    }

    private void SetClickSound(string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            #region Pirate: multiz
            var resource = GetSoundOrFallback(value, CCVars.UIClickSound.DefaultValue);
            if (resource == null)
            {
                _clickSource = null;
                UIManager.SetClickSound(null);
                return;
            }
            #endregion

            var source =
                _audioManager.CreateAudioSource(resource);

            if (source != null)
            {
                source.Gain = ClickGain * _interfaceGain;
                source.Global = true;
            }

            _clickSource = source;
            UIManager.SetClickSound(source);
        }
        else
        {
            UIManager.SetClickSound(null);
        }
    }

    private void SetHoverSound(string value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            #region Pirate: multiz
            var hoverResource = GetSoundOrFallback(value, CCVars.UIHoverSound.DefaultValue);
            if (hoverResource == null)
            {
                _hoverSource = null;
                UIManager.SetHoverSound(null);
                return;
            }
            #endregion

            var hoverSource =
                _audioManager.CreateAudioSource(hoverResource);

            if (hoverSource != null)
            {
                hoverSource.Gain = HoverGain * _interfaceGain;
                hoverSource.Global = true;
            }

            _hoverSource = hoverSource;
            UIManager.SetHoverSound(hoverSource);
        }
        else
        {
            UIManager.SetHoverSound(null);
        }
    }

    #region Pirate: multiz
    private AudioResource? GetSoundOrFallback(string path, string fallback)
    {
        try
        {
            if (!_cache.TryGetResource(path, out AudioResource? resource))
                return _cache.GetResource<AudioResource>(fallback);

            return resource;
        }
        catch (Exception e)
        {
            Logger.ErrorS("audio.ui", $"Failed to load UI sound '{path}' or fallback '{fallback}': {e}");
            return null;
        }
    }
    #endregion
}
