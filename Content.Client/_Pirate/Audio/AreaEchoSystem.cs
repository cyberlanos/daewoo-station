using System.Diagnostics.Contracts;
using System.Numerics;
using System.Runtime.CompilerServices;
using Content.Client.Light.EntitySystems;
using Content.Shared._Pirate.CCVars;
using Content.Shared.Light.Components;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Robust.Client.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Components;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using DependencyAttribute = Robust.Shared.IoC.DependencyAttribute;

namespace Content.Client._Pirate.Audio;

/// <summary>
/// Handles making sounds echo in large, open, roofed spaces.
/// </summary>
public sealed class AreaEchoSystem : EntitySystem
{
    [Dependency] private readonly PirateAudioEffectSystem _audioEffect = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly MapSystem _map = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly RoofSystem _roof = default!;

    private static readonly Angle[] CardinalDirections =
    {
        Direction.North.ToAngle(),
        Direction.West.ToAngle(),
        Direction.South.ToAngle(),
        Direction.East.ToAngle()
    };

    private static readonly List<(float Distance, ProtoId<AudioPresetPrototype> Preset)> DistancePresets =
    new()
    {
        (12f, "Hallway"),
        (20f, "Auditorium"),
        (30f, "ConcertHall"),
        (40f, "Hangar")
    };

    private readonly int _echoLayer = (int) (CollisionGroup.Opaque | CollisionGroup.Impassable);

    private const int ExistingAudioUpdatesPerTick = 4;

    private readonly Queue<EntityUid> _pendingEchoUpdates = new();

    private Angle[] _calculatedDirections = CardinalDirections;
    private TimeSpan _nextExistingUpdate = TimeSpan.Zero;
    private int _echoMaxReflections;
    private bool _echoEnabled = true;
    private TimeSpan _calculationInterval = TimeSpan.FromSeconds(15);
    private float _calculationFidelity = 5f;

    private EntityQuery<MapGridComponent> _gridQuery;
    private EntityQuery<RoofComponent> _roofQuery;

    public override void Initialize()
    {
        base.Initialize();

        _cfg.OnValueChanged(PirateVars.AreaEchoReflectionCount, x => _echoMaxReflections = Math.Max(0, x), true);
        _cfg.OnValueChanged(PirateVars.AreaEchoEnabled, OnEchoEnabledChanged, true);
        _cfg.OnValueChanged(PirateVars.AreaEchoHighResolution, x => _calculatedDirections = GetEffectiveDirections(x), true);
        _cfg.OnValueChanged(PirateVars.AreaEchoRecalculationInterval, x => _calculationInterval = x, true);
        _cfg.OnValueChanged(PirateVars.AreaEchoStepFidelity, x => _calculationFidelity = Math.Max(0.1f, x), true);

        _gridQuery = GetEntityQuery<MapGridComponent>();
        _roofQuery = GetEntityQuery<RoofComponent>();

        SubscribeLocalEvent<AudioComponent, EntParentChangedMessage>(OnAudioParentChanged);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_echoEnabled)
            return;

        if (_timing.CurTime >= _nextExistingUpdate)
        {
            _nextExistingUpdate = _timing.CurTime + _calculationInterval;
            QueueExistingAudioUpdates();
        }

        ProcessPendingAudioUpdates();
    }

    private void QueueExistingAudioUpdates()
    {
        _pendingEchoUpdates.Clear();

        var minimumMagnitude = DistancePresets[0].Distance;
        DebugTools.Assert(minimumMagnitude > 0f, "First distance preset was less than or equal to 0.");
        if (minimumMagnitude <= 0f)
            return;

        var audioEnumerator = EntityQueryEnumerator<AudioComponent>();

        while (audioEnumerator.MoveNext(out var uid, out var audioComponent))
        {
            if (!CanAudioEcho(audioComponent) || !audioComponent.Playing)
                continue;

            _pendingEchoUpdates.Enqueue(uid);
        }
    }

    private void ProcessPendingAudioUpdates()
    {
        if (_pendingEchoUpdates.Count == 0)
            return;

        var minimumMagnitude = DistancePresets[0].Distance;
        DebugTools.Assert(minimumMagnitude > 0f, "First distance preset was less than or equal to 0.");
        if (minimumMagnitude <= 0f)
        {
            _pendingEchoUpdates.Clear();
            return;
        }

        var maximumMagnitude = DistancePresets[^1].Distance;
        var processed = 0;

        while (processed < ExistingAudioUpdatesPerTick && _pendingEchoUpdates.TryDequeue(out var uid))
        {
            if (!TryComp<AudioComponent>(uid, out var audioComponent))
                continue;

            if (!CanAudioEcho(audioComponent) || !audioComponent.Playing)
                continue;

            ProcessAudioEntity((uid, audioComponent), Transform(uid), minimumMagnitude, maximumMagnitude);
            processed++;
        }
    }

    [Pure]
    public static Angle[] GetEffectiveDirections(bool highResolution)
    {
        if (!highResolution)
            return CardinalDirections;

        var allDirections = DirectionExtensions.AllDirections;
        var directions = new Angle[allDirections.Length];

        for (var i = 0; i < allDirections.Length; i++)
            directions[i] = allDirections[i].ToAngle();

        return directions;
    }

    private List<Entity<TransformComponent>> TryGetHierarchyBeforeMap(Entity<TransformComponent> originEntity)
    {
        var hierarchy = new List<Entity<TransformComponent>> { originEntity };
        var current = originEntity;
        var mapUid = current.Comp.MapUid;

        while (current.Comp.ParentUid != mapUid && current.Comp.ParentUid.IsValid())
        {
            var nextUid = current.Comp.ParentUid;
            current = (nextUid, Transform(nextUid));

            hierarchy.Add(current);
        }

        DebugTools.Assert(hierarchy.Count >= 1, "Malformed entity hierarchy.");
        return hierarchy;
    }

    public bool CanAudioEcho(AudioComponent audioComponent)
        => !audioComponent.Global && _echoEnabled;

    public bool TryProcessAreaSpaceMagnitude(Entity<TransformComponent> entity, float maximumMagnitude, out float magnitude)
    {
        magnitude = 0f;
        var transformComponent = entity.Comp;
        var entityHierarchy = TryGetHierarchyBeforeMap(entity);

        if (entityHierarchy.Count <= 1)
            return false;

        var entityGrid = entityHierarchy[^1];
        var lastEntityBeforeGrid = entityHierarchy[^2];

        if (!_gridQuery.TryGetComponent(entityGrid, out var gridComponent))
            return false;

        var checkRoof = _roofQuery.TryGetComponent(entityGrid, out var roofComponent);
        var tileRef = _map.GetTileRef(entityGrid, gridComponent, lastEntityBeforeGrid.Comp.Coordinates);

        if (tileRef.Tile.IsEmpty)
            return false;

        var gridRoofEntity = new Entity<MapGridComponent, RoofComponent?>(entityGrid, gridComponent, roofComponent);

        if (checkRoof && !_roof.IsRooved(gridRoofEntity!, tileRef.GridIndices))
            return false;

        var originTileIndices = tileRef.GridIndices;
        var worldPosition = _transform.GetWorldPosition(transformComponent);

        foreach (var direction in _calculatedDirections)
        {
            var currentDirectionVector = direction.ToVec();
            var currentTargetEntityUid = lastEntityBeforeGrid.Owner;

            var totalDistance = 0f;
            var remainingDistance = maximumMagnitude;
            var currentOriginWorldPosition = worldPosition;
            var currentOriginTileIndices = originTileIndices;

            for (var reflectIteration = 0; reflectIteration <= _echoMaxReflections; reflectIteration++)
            {
                var (distanceCovered, raycastResults) = CastEchoRay(
                    currentOriginWorldPosition,
                    currentOriginTileIndices,
                    currentDirectionVector,
                    transformComponent.MapID,
                    currentTargetEntityUid,
                    gridRoofEntity,
                    checkRoof,
                    remainingDistance);

                totalDistance += distanceCovered;
                remainingDistance -= distanceCovered;

                if (reflectIteration == _echoMaxReflections || raycastResults is not { })
                    break;

                var previousRayWorldOriginPosition = currentOriginWorldPosition;
                currentOriginWorldPosition = raycastResults.Value.HitPos;
                currentTargetEntityUid = raycastResults.Value.HitEntity;

                if (!_map.TryGetTileRef(entityGrid, gridComponent, currentOriginWorldPosition, out var hitTileRef))
                    break;

                currentOriginTileIndices = hitTileRef.GridIndices;

                var worldMatrix = _transform.GetInvWorldMatrix(gridRoofEntity);
                var previousRayOriginLocalPosition = Vector2.Transform(previousRayWorldOriginPosition, worldMatrix);
                var currentOriginLocalPosition = Vector2.Transform(currentOriginWorldPosition, worldMatrix);

                var delta = currentOriginLocalPosition - previousRayOriginLocalPosition;
                if (delta.LengthSquared() <= float.Epsilon + float.Epsilon)
                    break;

                var normalVector = GetTileHitNormal(
                    currentOriginLocalPosition,
                    _map.TileToVector(gridRoofEntity, currentOriginTileIndices),
                    gridRoofEntity.Comp1.TileSize);

                currentDirectionVector = Reflect(currentDirectionVector, normalVector);
            }

            magnitude += totalDistance;
        }

        magnitude /= _calculatedDirections.Length * Math.Max(1, _echoMaxReflections + 1);
        return true;
    }

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector2 GetTileHitNormal(Vector2 rayHitPos, Vector2 tileOrigin, float tileSize)
    {
        var local = rayHitPos - tileOrigin;

        var left = local.X;
        var right = tileSize - local.X;
        var bottom = local.Y;
        var top = tileSize - local.Y;

        var minDist = MathF.Min(MathF.Min(left, right), MathF.Min(bottom, top));

        if (minDist == left)
            return new Vector2(-1, 0);

        if (minDist == right)
            return new Vector2(1, 0);

        if (minDist == bottom)
            return new Vector2(0, -1);

        return new Vector2(0, 1);
    }

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector2 Reflect(Vector2 direction, Vector2 normal)
        => direction - 2 * Vector2.Dot(direction, normal) * normal;

    private (float Distance, RayCastResults? Results) CastEchoRay(
        Vector2 originWorldPosition,
        Vector2i originTileIndices,
        Vector2 directionVector,
        MapId mapId,
        EntityUid ignoredEntity,
        Entity<MapGridComponent, RoofComponent?> gridRoofEntity,
        bool checkRoof,
        float maximumDistance)
    {
        var directionFidelityStep = directionVector * _calculationFidelity;
        var ray = new CollisionRay(originWorldPosition, directionVector, _echoLayer);
        var rayResults = _physics.IntersectRay(mapId, ray, maxLength: maximumDistance, ignoredEnt: ignoredEntity, returnOnFirstHit: true);

        var rayMagnitude = rayResults.TryFirstOrNull(out var firstResult)
            ? MathF.Min(firstResult.Value.Distance, maximumDistance)
            : maximumDistance;

        var nextCheckedPosition = new Vector2(originTileIndices.X, originTileIndices.Y) * gridRoofEntity.Comp1.TileSize + directionFidelityStep;
        var incrementedRayMagnitude = MarchRayByTiles(
            rayMagnitude,
            gridRoofEntity,
            directionFidelityStep,
            ref nextCheckedPosition,
            gridRoofEntity.Comp1.TileSize,
            checkRoof);

        return (incrementedRayMagnitude, firstResult);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float MarchRayByTiles(
        float rayMagnitude,
        Entity<MapGridComponent, RoofComponent?> gridRoofEntity,
        Vector2 directionFidelityStep,
        ref Vector2 nextCheckedPosition,
        ushort gridTileSize,
        bool checkRoof)
    {
        var fidelityStepLength = directionFidelityStep.Length();
        var incrementedRayMagnitude = 0f;

        for (; incrementedRayMagnitude < rayMagnitude;)
        {
            var nextCheckedTilePosition = new Vector2i(
                (int) MathF.Floor(nextCheckedPosition.X / gridTileSize),
                (int) MathF.Floor(nextCheckedPosition.Y / gridTileSize));

            if (checkRoof)
            {
                if (!_roof.IsRooved(gridRoofEntity!, nextCheckedTilePosition))
                    break;
            }
            else if (!_map.TryGetTileRef(gridRoofEntity, gridRoofEntity, nextCheckedTilePosition, out var tile) || tile.Tile.IsEmpty)
            {
                break;
            }

            nextCheckedPosition += directionFidelityStep;
            incrementedRayMagnitude += fidelityStepLength;
        }

        return MathF.Min(incrementedRayMagnitude, rayMagnitude);
    }

    private void ProcessAudioEntity(Entity<AudioComponent> entity, TransformComponent transformComponent, float minimumMagnitude, float maximumMagnitude)
    {
        if (!TryProcessAreaSpaceMagnitude((entity, transformComponent), maximumMagnitude, out var echoMagnitude) ||
            echoMagnitude <= minimumMagnitude)
        {
            _audioEffect.TryRemoveEffect(entity);
            return;
        }

        ProtoId<AudioPresetPrototype>? bestPreset = null;

        foreach (var preset in DistancePresets)
        {
            bestPreset = preset.Preset;

            if (echoMagnitude <= preset.Distance)
                break;
        }

        if (bestPreset != null)
            _audioEffect.TryAddEffect(entity, bestPreset.Value);
    }

    private void OnAudioParentChanged(Entity<AudioComponent> entity, ref EntParentChangedMessage args)
    {
        if (args.Transform.MapID == MapId.Nullspace || !CanAudioEcho(entity))
            return;

        ProcessAudioEntity(entity, args.Transform, DistancePresets[0].Distance, DistancePresets[^1].Distance);
    }

    private void OnEchoEnabledChanged(bool enabled)
    {
        _echoEnabled = enabled;

        if (enabled)
            return;

        var audioEnumerator = EntityQueryEnumerator<AudioComponent>();
        while (audioEnumerator.MoveNext(out var uid, out var audioComponent))
            _audioEffect.TryRemoveEffect((uid, audioComponent));
    }
}
