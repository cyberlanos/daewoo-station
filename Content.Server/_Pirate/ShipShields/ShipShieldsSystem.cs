// SPDX-FileCopyrightText: 2025 ark1368
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._Pirate.ShipShields;
using Content.Shared._Pirate.ZLevels.Core.Components;
using Content.Shared.Physics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Events;
using Robust.Server.GameStates;
using Content.Server.Power.Components;
using Content.Server._Pirate.ZLevels.Power;
using Robust.Shared.Physics;
using Content.Shared._Mono.SpaceArtillery;
using Content.Shared.Projectiles;


namespace Content.Server._Pirate.ShipShields;
public sealed partial class ShipShieldsSystem : EntitySystem
{
    private const string ShipShieldPrototype = "ShipShield";
    private const float Padding = 10f;
    private const float CollisionThreshold = 50f;
    private const float EmitterUpdateRate = 1.5f;

    [Dependency] private readonly SharedTransformSystem _transformSystem = default!;

    [Dependency] private readonly FixtureSystem _fixtureSystem = default!;

    [Dependency] private readonly PhysicsSystem _physicsSystem = default!;

    [Dependency] private readonly PvsOverrideSystem _pvsSys = default!;

    // Scratch buffers reused across reconcile passes so we don't allocate per tick.
    private readonly HashSet<EntityUid> _scratchTargetGrids = new();
    private readonly List<EntityUid> _scratchObsoleteGrids = new();

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<ShipShieldEmitterComponent, ApcPowerReceiverComponent>();
        while (query.MoveNext(out var uid, out var emitter, out var power))
        {
            emitter.Accumulator += frameTime;

            if (emitter.Accumulator < EmitterUpdateRate)
                continue;

            if ((float) Math.Pow(emitter.Damage, emitter.DamageExp) >= emitter.MaxDraw)
                emitter.Recharging = true;
            if (!power.Powered)
                emitter.Recharging = true;

            emitter.Accumulator -= EmitterUpdateRate;
            if (emitter.OverloadAccumulator > 0)
            {
                emitter.OverloadAccumulator -= EmitterUpdateRate;
            }

            float healed = emitter.HealPerSecond * EmitterUpdateRate;

            if (emitter.Recharging)
                healed *= emitter.UnpoweredBonus;

            emitter.Damage -= healed;

            if (emitter.Damage < 0)
            {
                emitter.Damage = 0;
                if (power.Powered)
                    emitter.Recharging = false;
            }

            AdjustEmitterLoad(uid, emitter, power);

            if (emitter.Damage > emitter.DamageLimit)
                emitter.OverloadAccumulator = emitter.DamageOverloadTimePunishment;

            var shouldShield = !emitter.Recharging && emitter.OverloadAccumulator < 1;

            if (shouldShield && !emitter.Active)
            {
                if (SyncShields(uid, emitter))
                {
                    emitter.Active = true;
                    var filter = _station.GetInOwningStation(uid);
                    _audio.PlayGlobal(emitter.PowerUpSound, filter, true, emitter.PowerUpSound.Params);
                }
            }
            else if (!shouldShield && emitter.Active)
            {
                ClearShields(emitter);
                emitter.Active = false;
                var filter = _station.GetInOwningStation(uid);
                _audio.PlayGlobal(emitter.PowerDownSound, filter, true, emitter.PowerUpSound.Params);
            }
            else if (emitter.Active)
            {
                // Already active: catch up with z-network changes that happened between events.
                SyncShields(uid, emitter);
            }
        }
    }
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ShipShieldComponent, StartCollideEvent>(OnCollide);
        SubscribeLocalEvent<ShipShieldEmitterComponent, ComponentShutdown>(OnEmitterShutdown);
        // Broadcast subscription: CEMultizCableHubSystem already owns the per-CEZLinkedGridComponent
        // subscription for this event and Robust forbids duplicate (component, event) pairs across systems.
        SubscribeLocalEvent<CEMultizLinkedGridPeersChangedEvent>(OnLinkedPeersChanged);

        InitializeCommands();
        InitializeEmitters();
    }

    private void OnCollide(EntityUid uid, ShipShieldComponent component, StartCollideEvent args)
    {
        if (Transform(args.OtherEntity).Anchored)
            return;

        if (!TryComp<PhysicsComponent>(Transform(uid).GridUid, out var ourPhysics) || !TryComp<PhysicsComponent>(args.OtherEntity, out var theirPhysics))
            return;

        if (!HasComp<ShipWeaponProjectileComponent>(args.OtherEntity))
            return;

        if (!TryComp<ProjectileComponent>(args.OtherEntity, out var projectile))
            return;
        if (projectile.Weapon is not null)
        {
            if (component.Shielded == Transform(projectile.Weapon.Value).GridUid)
                return;
        }

        var ourVelocity = ourPhysics.LinearVelocity;
        var velocity = theirPhysics.LinearVelocity;

        var collisionSpeedVector = Vector2.Subtract(ourVelocity, velocity);

        if (Math.Abs(collisionSpeedVector.Length()) < CollisionThreshold)
        {
            return;
        }

        if (component.Source != null)
        {
            var ev = new ShieldDeflectedEvent(args.OtherEntity);
            RaiseLocalEvent(component.Source.Value, ref ev);
        }
    }

    private void OnEmitterShutdown(EntityUid uid, ShipShieldEmitterComponent emitter, ComponentShutdown args) // Mono
    {
        ClearShields(emitter);
        emitter.Active = false;
    }

    /// <summary>
    /// Re-runs the shielded-grid reconcile pass for every active emitter when a z-network's peer
    /// set changes, so newly-linked grids gain shields and unlinked grids lose theirs immediately
    /// without waiting for the next emitter tick.
    /// </summary>
    private void OnLinkedPeersChanged(ref CEMultizLinkedGridPeersChangedEvent args)
    {
        var query = EntityQueryEnumerator<ShipShieldEmitterComponent>();
        while (query.MoveNext(out var emitterUid, out var emitter))
        {
            if (!emitter.Active)
                continue;
            SyncShields(emitterUid, emitter);
        }
    }

    /// <summary>
    /// Reconciles the emitter's owned shields against the set of grids it should currently be
    /// protecting. The target set is the emitter's host grid plus every peer reachable via
    /// <see cref="CEZLinkedGridComponent.PeerGrids"/>. Returns true if at least one shield exists
    /// at the end of the pass.
    /// </summary>
    private bool SyncShields(EntityUid emitterUid, ShipShieldEmitterComponent emitter)
    {
        var hostGrid = Transform(emitterUid).GridUid;
        if (hostGrid is null || TerminatingOrDeleted(hostGrid.Value))
        {
            ClearShields(emitter);
            emitter.TemplateGrid = null;
            return false;
        }

        _scratchTargetGrids.Clear();
        _scratchTargetGrids.Add(hostGrid.Value);

        if (TryComp<CEZLinkedGridComponent>(hostGrid.Value, out var linked))
        {
            foreach (var (_, peerGrid) in linked.PeerGrids)
            {
                if (!TerminatingOrDeleted(peerGrid))
                    _scratchTargetGrids.Add(peerGrid);
            }
        }

        // Pick the grid with the largest AABB as the shape template. Each peer's bubble will be
        // sized and centered off this template instead of off its own LocalAABB so all layers
        // display one matching silhouette — without it, decks of different shapes (e.g. the
        // tile-only roof grid that sits over the shuttle) produce mismatched bubbles per layer.
        // Ties are broken by lowest uid: HashSet enumeration order is not stable across ticks,
        // so without a deterministic tiebreaker the template could flip between equal-area grids
        // every tick and thrash all shields through clear+respawn forever.
        EntityUid templateGrid = EntityUid.Invalid;
        MapGridComponent? templateMapGrid = null;
        var bestArea = -1f;
        foreach (var gridUid in _scratchTargetGrids)
        {
            if (!TryComp<MapGridComponent>(gridUid, out var grid))
                continue;

            var aabb = grid.LocalAABB;
            var area = aabb.Width * aabb.Height;
            var isBetter = area > bestArea
                || (area == bestArea && (templateGrid == EntityUid.Invalid || gridUid.Id < templateGrid.Id));
            if (isBetter)
            {
                bestArea = area;
                templateGrid = gridUid;
                templateMapGrid = grid;
            }
        }

        // Fallback if every target somehow lacked MapGridComponent (shouldn't happen, but
        // keeps the rest of the method safe).
        if (templateGrid == EntityUid.Invalid)
            templateGrid = hostGrid.Value;

        // If the template changed (largest peer was added/removed/destroyed) the existing
        // bubble shapes are stale. Physics shapes can't be resized in place cleanly, so we
        // tear down every shield and let the spawn pass below rebuild them with the new shape.
        if (emitter.TemplateGrid != templateGrid)
        {
            ClearShields(emitter);
            emitter.TemplateGrid = templateGrid;
        }

        // Drop shields for grids that have left the network, been deleted, or whose shield
        // entity died out from under us (e.g. another emitter racing on the same grid).
        _scratchObsoleteGrids.Clear();
        foreach (var (gridUid, shieldUid) in emitter.Shields)
        {
            if (!_scratchTargetGrids.Contains(gridUid)
                || TerminatingOrDeleted(gridUid)
                || TerminatingOrDeleted(shieldUid))
            {
                _scratchObsoleteGrids.Add(gridUid);
            }
        }
        foreach (var gridUid in _scratchObsoleteGrids)
        {
            if (!TerminatingOrDeleted(gridUid))
                UnshieldEntity(gridUid);
            emitter.Shields.Remove(gridUid);
        }

        // Spawn shields for grids newly present in the network, all using the template shape.
        foreach (var gridUid in _scratchTargetGrids)
        {
            if (emitter.Shields.ContainsKey(gridUid))
                continue;

            var shield = ShieldEntity(gridUid, source: emitterUid, templateMapGrid: templateMapGrid);
            if (shield != EntityUid.Invalid)
                emitter.Shields[gridUid] = shield;
        }

        return emitter.Shields.Count > 0;
    }

    /// <summary>
    /// Tears down every shield this emitter owns. Used when the emitter goes offline, overloads,
    /// or is destroyed.
    /// </summary>
    private void ClearShields(ShipShieldEmitterComponent emitter)
    {
        foreach (var (gridUid, _) in emitter.Shields)
        {
            if (!TerminatingOrDeleted(gridUid))
                UnshieldEntity(gridUid);
        }
        emitter.Shields.Clear();
    }

    private EntityUid ShieldEntity(EntityUid entity, MapGridComponent? mapGrid = null, EntityUid? source = null, MapGridComponent? templateMapGrid = null)
    {
        if (TryComp<ShipShieldedComponent>(entity, out var existingShielded))
            return existingShielded.Shield;

        if (!Resolve(entity, ref mapGrid, false))
            return EntityUid.Invalid;

        // The template grid drives the bubble's size and centering so every peer in a z-linked
        // shuttle gets an identical-looking shield. Falls back to the parented grid when the
        // caller doesn't supply one (e.g. the debug shieldentity command).
        var shapeGrid = templateMapGrid ?? mapGrid;

        var prototype = ShipShieldPrototype;

        var shield = Spawn(prototype, Transform(entity).Coordinates);
        // Disable grid traversal before SetCoordinates so the shield stays parented to the grid we
        // pick. Without this, robust's auto-traversal can re-parent the shield onto a different
        // overlapping grid on the same map (e.g. when two shuttle grids share a deck/map and one
        // sits at a non-origin LocalPosition), which corrupts the shield's local pos/rot and
        // breaks the client overlay's edge-thickness math.
        Transform(shield).GridTraversal = false;
        var shieldPhysics = EnsureComp<PhysicsComponent>(shield);
        var shieldComp = EnsureComp<ShipShieldComponent>(shield);
        shieldComp.Shielded = entity;
        shieldComp.Source = source;

        var shieldVisuals = EnsureComp<ShipShieldVisualsComponent>(shield);
        if (source != null && TryComp<ShipShieldEmitterComponent>(source.Value, out var emitter))
        {
            shieldVisuals.ShieldColor = emitter.ShieldColor;
            Dirty(shield, shieldVisuals);
        }

        // Parent to the host grid AND set local offset in one call. The original three-step sequence
        // (SetLocalPosition before SetParent) interpreted the offset in the spawned-into-map's coord
        // space and then SetParent preserved world position when reparenting to the grid, which left
        // the shield's grid-local position offset by the grid's own LocalPosition. That was harmless
        // when the grid sat at map origin (0,0) but produced wildly wrong localPos for grids parked
        // anywhere else (e.g. an auto-generated roof grid floating far from the map origin), which
        // in turn broke the client overlay's Corner() inward-edge math and rendered patches of the
        // bubble as thin/transparent.
        // rotation: Angle.Zero matches the original intent of "rotate with the grid" — local rotation
        // 0 means world rotation tracks the parent grid's world rotation.
        _transformSystem.SetCoordinates(
            (shield, Transform(shield), MetaData(shield)),
            new EntityCoordinates(entity, shapeGrid.LocalAABB.Center),
            rotation: Angle.Zero);

        var chain = GenerateOvalFixture(shield, "shield", shieldPhysics, shapeGrid);

        List<Vector2> roughPoly = new();

        var interval = chain.Count / PhysicsConstants.MaxPolygonVertices;

        int i = 0;

        while (i < PhysicsConstants.MaxPolygonVertices)
        {
            roughPoly.Add(chain.Vertices[i * interval]);
            i++;
        }

        var internalPoly = new PolygonShape();
        internalPoly.Set(roughPoly);

        _fixtureSystem.TryCreateFixture(shield, internalPoly, "internalShield",
            hard: false,
            collisionLayer: (int)CollisionGroup.BulletImpassable,
            body: shieldPhysics);

        _physicsSystem.WakeBody(shield, body: shieldPhysics);
        _physicsSystem.SetSleepingAllowed(shield, shieldPhysics, false);

        _pvsSys.AddGlobalOverride(shield);

        var shieldedComp = EnsureComp<ShipShieldedComponent>(entity);
        shieldedComp.Shield = shield;
        shieldedComp.Source = source;

        return shield;
    }

    private bool UnshieldEntity(EntityUid uid, ShipShieldedComponent? component = null)
    {
        if (!Resolve(uid, ref component, false))
            return false;

        Del(component.Shield);
        RemComp<ShipShieldedComponent>(uid);
        return true;
    }

    private ChainShape GenerateOvalFixture(EntityUid uid, string name, PhysicsComponent physics, MapGridComponent mapGrid, float padding = Padding)
    {
        float radius;
        float scale;
        var scaleX = true;

        var height = mapGrid.LocalAABB.Height + padding;
        var width = mapGrid.LocalAABB.Width + padding;

        if (width > height)
        {
            radius = 0.5f * height;
            scale = width / height;
        }
        else
        {
            radius = 0.5f * width;
            scale = height / width;
            scaleX = false;
        }

        var chain = new ChainShape();

        chain.CreateLoop(Vector2.Zero, radius);

        for (int i = 0; i < chain.Vertices.Length; i++)
        {
            if (scaleX)
            {
                chain.Vertices[i].X *= scale;
            }
            else
            {
                chain.Vertices[i].Y *= scale;
            }
        }

        _fixtureSystem.TryCreateFixture(uid, chain, name,
            hard: false,
            collisionLayer: (int) CollisionGroup.FullTileLayer,
            body: physics);

        return chain;
    }

    [ByRefEvent]
    public record struct ShieldDeflectedEvent(EntityUid Deflected)
    {

    }
}
