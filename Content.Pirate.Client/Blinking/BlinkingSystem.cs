using Content.Pirate.Shared.Blinking;
using Content.Shared.Bed.Sleep;
using Content.Shared.Eye.Blinding.Components;
using Content.Shared.Humanoid;
using Content.Shared.Mobs.Systems;
using Robust.Client.GameObjects;
using Robust.Shared.Random;
using Robust.Shared.Utility;

namespace Content.Pirate.Client.Blinking;

/// <summary>
/// Client half of the blinking feature. Drives skin-tinted "eyelid" sprite layers whose
/// alpha fades in/out to read as a blink.
///
/// Blinking is event-driven: one shared scheduler fires a blink "event" on a randomized
/// ~5s cadence (and on demand from the blink emotes). Synchronized blinkers animate a single
/// eyelid cloned from the humanoid Eyes layer, so both eyes close together. Asynchronous
/// blinkers (e.g. lizards) animate two eyelid halves from a configured split RSI: each event
/// blinks both eyes, but the trailing eye (chosen at random) is given a small random lead so
/// the eyes don't close in perfect unison while staying locked to the same cadence — a port
/// of tgstation's coupled async blinking.
///
/// Eyelid layers are <b>transient</b>: they only exist while an eye is actually closed (a blink,
/// or held shut for sleep/death). The rest of the time none are present. This is deliberate —
/// the humanoid system rebuilds its sprite by absolute layer index, and the displaced-marking
/// path even mutates the layer map mid-enumeration; a foreign layer parked in the eye region
/// can corrupt that rebuild (e.g. an invisible head when a lizard gains a coloured eye marking).
/// Since appearance/marking changes happen while a mob is idle (eyes open), keeping our layers
/// out of the stack except during the brief blink window keeps those rebuilds clean.
/// </summary>
public sealed class BlinkingSystem : EntitySystem
{
    [Dependency] private readonly SpriteSystem _sprite = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeAllEvent<BlinkEffectEvent>(OnBlinkEffect);
    }

    // FrameUpdate (render frame), not Update: this is purely cosmetic and must run at
    // real wall-clock rate. Update() is re-run by client prediction every frame, which
    // would make the timers advance many times too fast.
    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        var query = AllEntityQuery<BlinkingComponent, SpriteComponent, HumanoidAppearanceComponent>();
        while (query.MoveNext(out var uid, out var blink, out var sprite, out var humanoid))
        {
            if (!blink.Enabled)
            {
                RemoveAllEyelids(uid, sprite);
                continue;
            }

            var async = IsAsync(blink);

            // Newly seen entity: stagger its first blink so crowds don't blink in unison.
            if (!EnsureComp<BlinkingVisualsComponent>(uid, out var visuals))
            {
                visuals.Async = async;
                visuals.MasterDelay = NextDelay(blink);
            }

            UpdateEntity(uid, blink, sprite, humanoid, visuals, async, frameTime);
        }
    }

    private void UpdateEntity(
        EntityUid uid,
        BlinkingComponent blink,
        SpriteComponent sprite,
        HumanoidAppearanceComponent humanoid,
        BlinkingVisualsComponent visuals,
        bool async,
        float frameTime)
    {
        // Gate + eyelid color only (no layer creation). If there are no drawable eyes, make sure
        // any eyelid layers are gone so we don't leave skin-colored lids over a mob whose eyes
        // were removed/hidden.
        if (!TryGetEyelidColor(uid, sprite, humanoid, out var color))
        {
            RemoveAllEyelids(uid, sprite);
            return;
        }

        visuals.EyelidColor = color;

        // Hold the lids shut (and stop auto-blinking) while asleep, or when the eyes are
        // deliberately/forcibly closed via the toggle-eyes action (also used to feign death).
        // SS14 has no client visual for these otherwise, so this is what makes shut eyes visible
        // to onlookers. Eyes reopen and blinking resumes automatically once awake/reopened.
        if (HasComp<SleepingComponent>(uid) ||
            (TryComp<EyeClosingComponent>(uid, out var closing) && closing.EyesClosed))
        {
            HoldClosed(uid, sprite, blink, async, color);
            return;
        }

        // Dead bodies hold their eyes shut by default (matching tgstation); CloseOnDeath can turn
        // that off so the eyes stay open. Either way a dead body never blinks.
        if (_mobState.IsDead(uid))
        {
            if (blink.CloseOnDeath)
                HoldClosed(uid, sprite, blink, async, color);
            else
                RemoveAllEyelids(uid, sprite);

            return;
        }

        // Scheduler: fire the next blink event once the current one has fully cleared.
        visuals.MasterDelay -= frameTime;
        if (visuals.MasterDelay <= 0f && AllEyesIdle(visuals, async))
            LaunchEvent(blink, visuals, async);

        // Animate whichever eyelid(s) this entity uses.
        if (async)
        {
            AnimateEye(uid, sprite, blink, visuals, true, 0, BlinkingVisualsComponent.LeftKey, frameTime);
            AnimateEye(uid, sprite, blink, visuals, true, 1, BlinkingVisualsComponent.RightKey, frameTime);
        }
        else
        {
            AnimateEye(uid, sprite, blink, visuals, false, 0, BlinkingVisualsComponent.UnifiedKey, frameTime);
        }
    }

    /// <summary>
    /// Starts a blink event: both eyes (async) or the unified eyelid (sync) begin closing,
    /// with the trailing async eye offset by a small random lead. Then schedules the next event.
    /// </summary>
    private void LaunchEvent(BlinkingComponent blink, BlinkingVisualsComponent visuals, bool async)
    {
        var rapid = visuals.RapidActive;
        var duration = (float) (rapid ? blink.RapidDuration : blink.Duration).TotalSeconds;

        if (async)
        {
            // One eye leads, the other trails by a random fraction of AsyncOffset.
            var leftLeads = _random.Prob(0.5f);
            var offset = _random.NextFloat() * (float) blink.AsyncOffset.TotalSeconds;
            ArmEye(visuals, 0, leftLeads ? 0f : offset, duration);
            ArmEye(visuals, 1, leftLeads ? offset : 0f, duration);
        }
        else
        {
            ArmEye(visuals, 0, 0f, duration);
        }

        // Schedule the next event: tight RapidInterval spacing during a burst, else the
        // normal randomized cadence.
        if (visuals.RapidEventsLeft > 0)
        {
            visuals.RapidEventsLeft--;
            visuals.RapidActive = true;
            visuals.MasterDelay = (float) blink.RapidInterval.TotalSeconds;
        }
        else
        {
            visuals.RapidActive = false;
            visuals.MasterDelay = NextDelay(blink);
        }
    }

    private static void ArmEye(BlinkingVisualsComponent visuals, int eye, float startDelay, float duration)
    {
        visuals.Pending[eye] = true;
        visuals.StartDelay[eye] = startDelay;
        visuals.CurrentDuration[eye] = duration;
        visuals.Progress[eye] = -1f;
    }

    /// <summary>Advances one eye's pending/blinking state and drives its (transient) eyelid layer.</summary>
    private void AnimateEye(
        EntityUid uid,
        SpriteComponent sprite,
        BlinkingComponent blink,
        BlinkingVisualsComponent visuals,
        bool async,
        int eye,
        string layerKey,
        float frameTime)
    {
        if (visuals.Progress[eye] < 0f)
        {
            // Idle, or waiting out the lead offset: no eyelid layer should exist yet.
            if (!visuals.Pending[eye])
            {
                RemoveEyelid(uid, sprite, layerKey);
                return;
            }

            visuals.StartDelay[eye] -= frameTime;
            if (visuals.StartDelay[eye] > 0f)
            {
                RemoveEyelid(uid, sprite, layerKey);
                return;
            }

            // Blink starts now: create the eyelid layer.
            visuals.Pending[eye] = false;
            visuals.Progress[eye] = 0f;
            EnsureEyelidLayer(uid, sprite, blink, async, layerKey);
        }

        visuals.Progress[eye] += frameTime;
        var hold = MathF.Max(visuals.CurrentDuration[eye], 0.01f);
        var total = hold + 2f * BlinkFade;

        if (visuals.Progress[eye] >= total)
        {
            visuals.Progress[eye] = -1f;
            RemoveEyelid(uid, sprite, layerKey);
            return;
        }

        var alpha = EyelidAlpha(visuals.Progress[eye], hold);
        ShowEyelid(uid, sprite, layerKey, visuals.EyelidColor.WithAlpha(alpha));
    }

    private void OnBlinkEffect(BlinkEffectEvent ev)
    {
        var uid = GetEntity(ev.Target);
        if (Deleted(uid) || !TryComp<BlinkingComponent>(uid, out var blink) || !blink.Enabled)
            return;

        var visuals = EnsureComp<BlinkingVisualsComponent>(uid);

        // Fire an event as soon as the eyes are idle (next frame if currently open).
        visuals.RapidActive = ev.Rapid;
        visuals.RapidEventsLeft = ev.Rapid ? Math.Max(blink.RapidCount - 1, 0) : 0;
        visuals.MasterDelay = 0f;
    }

    private static bool AllEyesIdle(BlinkingVisualsComponent visuals, bool async)
    {
        if (visuals.Pending[0] || visuals.Progress[0] >= 0f)
            return false;
        if (async && (visuals.Pending[1] || visuals.Progress[1] >= 0f))
            return false;
        return true;
    }

    /// <summary>
    /// Gates blinking and computes the skin-tinted eyelid color. Returns false when there are no
    /// drawable eyes (robotic/missing/hidden). Does not touch sprite layers.
    /// </summary>
    private bool TryGetEyelidColor(EntityUid uid, SpriteComponent sprite, HumanoidAppearanceComponent humanoid, out Color color)
    {
        color = default;

        if (!_sprite.LayerMapTryGet((uid, sprite), HumanoidVisualLayers.Eyes, out var eyesIndex, false))
            return false;

        // No blinking when the eyes layer isn't actually drawn (hidden by equipment, a
        // blindfold/closed-eyes state, or a removed eyes organ).
        if (!sprite[eyesIndex].Visible)
            return false;

        var rsi = _sprite.LayerGetEffectiveRsi((uid, sprite), eyesIndex);
        if (rsi == null)
            return false;

        if (!_sprite.LayerGetRsiState((uid, sprite), eyesIndex).IsValid)
            return false;

        // Eyelid is the skin color darkened a touch, matching tgstation's ~85% HSL tint.
        var skin = humanoid.SkinColor;
        color = new Color(skin.R * 0.85f, skin.G * 0.85f, skin.B * 0.85f, 1f);
        return true;
    }

    /// <summary>
    /// Creates the eyelid layer for <paramref name="key"/> if it doesn't already exist. Async
    /// eyes use the configured split RSI/state; the unified eyelid clones the live Eyes layer.
    /// </summary>
    private void EnsureEyelidLayer(EntityUid uid, SpriteComponent sprite, BlinkingComponent blink, bool async, string key)
    {
        if (_sprite.LayerExists((uid, sprite), key))
            return;

        if (!_sprite.LayerMapTryGet((uid, sprite), HumanoidVisualLayers.Eyes, out var eyesIndex, false))
            return;

        int index;
        if (async)
        {
            var stateName = key == BlinkingVisualsComponent.LeftKey ? blink.EyelidStateLeft : blink.EyelidStateRight;
            if (stateName == null || blink.EyelidRsi == null)
                return;

            index = _sprite.AddRsiLayer((uid, sprite), stateName, blink.EyelidRsi.Value, EyelidInsertIndex(uid, sprite, eyesIndex));
        }
        else
        {
            var rsi = _sprite.LayerGetEffectiveRsi((uid, sprite), eyesIndex);
            var state = _sprite.LayerGetRsiState((uid, sprite), eyesIndex);
            if (rsi == null || !state.IsValid)
                return;

            // Clone the eyes layer (using the RSI object directly, no path reload).
            index = _sprite.AddRsiLayer((uid, sprite), state, rsi, EyelidInsertIndex(uid, sprite, eyesIndex));
        }

        _sprite.LayerMapSet((uid, sprite), key, index);
        _sprite.LayerSetVisible((uid, sprite), index, false);
    }

    /// <summary>
    /// Index at which to insert an eyelid layer so it sits above the eye markings. Eye markings
    /// target the Eyes slot and stack directly above the eyeball, so the next base humanoid layer
    /// above Eyes is above them; inserting there puts the eyelid on top of the whole eye region.
    /// </summary>
    private int EyelidInsertIndex(EntityUid uid, SpriteComponent sprite, int eyesIndex)
    {
        var insert = int.MaxValue;
        foreach (var layer in Enum.GetValues<HumanoidVisualLayers>())
        {
            if (_sprite.LayerMapTryGet((uid, sprite), layer, out var idx, false) && idx > eyesIndex && idx < insert)
                insert = idx;
        }

        return insert == int.MaxValue ? eyesIndex + 1 : insert;
    }

    private void ShowEyelid(EntityUid uid, SpriteComponent sprite, string layerKey, Color color)
    {
        if (!_sprite.LayerMapTryGet((uid, sprite), layerKey, out var index, false))
            return;

        _sprite.LayerSetColor((uid, sprite), index, color);
        _sprite.LayerSetVisible((uid, sprite), index, true);
    }

    /// <summary>
    /// Removes an eyelid layer. Drops the layer-map key <b>before</b> removing the layer: the
    /// engine's RemoveLayer(index) mutates the layer map inside a foreach over it, which throws if
    /// a key still points at the removed index.
    /// </summary>
    private void RemoveEyelid(EntityUid uid, SpriteComponent sprite, string layerKey)
    {
        if (!_sprite.LayerMapTryGet((uid, sprite), layerKey, out var index, false))
            return;

        _sprite.LayerMapRemove((uid, sprite), layerKey);
        _sprite.RemoveLayer((uid, sprite), index, false);
    }

    /// <summary>Creates (if needed) and holds the relevant eyelid(s) fully closed (dead / shut eyes).</summary>
    private void HoldClosed(EntityUid uid, SpriteComponent sprite, BlinkingComponent blink, bool async, Color color)
    {
        if (async)
        {
            EnsureEyelidLayer(uid, sprite, blink, true, BlinkingVisualsComponent.LeftKey);
            EnsureEyelidLayer(uid, sprite, blink, true, BlinkingVisualsComponent.RightKey);
            ShowEyelid(uid, sprite, BlinkingVisualsComponent.LeftKey, color);
            ShowEyelid(uid, sprite, BlinkingVisualsComponent.RightKey, color);
        }
        else
        {
            EnsureEyelidLayer(uid, sprite, blink, false, BlinkingVisualsComponent.UnifiedKey);
            ShowEyelid(uid, sprite, BlinkingVisualsComponent.UnifiedKey, color);
        }
    }

    private void RemoveAllEyelids(EntityUid uid, SpriteComponent sprite)
    {
        RemoveEyelid(uid, sprite, BlinkingVisualsComponent.UnifiedKey);
        RemoveEyelid(uid, sprite, BlinkingVisualsComponent.LeftKey);
        RemoveEyelid(uid, sprite, BlinkingVisualsComponent.RightKey);
    }

    private static bool IsAsync(BlinkingComponent blink)
        => blink.Asynchronous && blink.EyelidRsi != null && blink.EyelidStateLeft != null && blink.EyelidStateRight != null;

    /// <summary>Quick eyelid fade in/out around the fully-closed hold, in seconds.</summary>
    private const float BlinkFade = 0.03f;

    /// <summary>
    /// Eyelid alpha over a blink: a short fade closed, a fully-closed hold of <paramref name="hold"/>
    /// seconds (matching tgstation, which keeps the eyelid fully opaque for BLINK_DURATION), then a
    /// short fade open. <paramref name="t"/> is seconds since the blink started.
    /// </summary>
    private static float EyelidAlpha(float t, float hold)
    {
        if (t < BlinkFade)
            return t / BlinkFade;
        if (t < BlinkFade + hold)
            return 1f;
        return MathF.Max(0f, (2f * BlinkFade + hold - t) / BlinkFade);
    }

    private float NextDelay(BlinkingComponent blink)
    {
        var min = (float) blink.MinInterval.TotalSeconds;
        var max = (float) blink.MaxInterval.TotalSeconds;
        return min + _random.NextFloat() * (max - min);
    }
}
