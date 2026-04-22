/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Shared._Pirate.ZLevels.Core.Components;
using Content.Shared.ActionBlocker;
using Content.Shared.Gravity;
using Content.Shared.Popups;
using Content.Shared.Shuttles.Components;
using JetBrains.Annotations;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Shared._Pirate.ZLevels.Core.EntitySystems;

public abstract partial class CESharedZLevelsSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly ActionBlockerSystem _blocker = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedGravitySystem _gravity = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;

    private EntityQuery<MapComponent> _mapQuery;
    private EntityQuery<CEZLevelMapComponent> _zMapQuery;
    private EntityQuery<FTLMapComponent> _ftlMapQuery;
    private EntityQuery<MapGridComponent> _gridQuery;
    private bool _sharedInitialized;

    protected EntityQuery<CEZPhysicsComponent> ZPhyzQuery;

    public override void Initialize()
    {
        if (_sharedInitialized)
            return;

        base.Initialize();
        _sharedInitialized = true;

        _mapQuery = GetEntityQuery<MapComponent>();
        _zMapQuery = GetEntityQuery<CEZLevelMapComponent>();
        _ftlMapQuery = GetEntityQuery<FTLMapComponent>();
        _gridQuery = GetEntityQuery<MapGridComponent>();
        ZPhyzQuery = GetEntityQuery<CEZPhysicsComponent>();

        InitializeDebug();
        InitMovement();
        InitView();
        InitOccluders();
        InitializeActivation();
    }

    /// <summary>
    /// Checks whether the map is in the zLevels network. If so, returns true and the current depth + Entity of the current zLevels network.
    /// </summary>
    [PublicAPI]
    public bool TryGetZNetwork(EntityUid mapUid, [NotNullWhen(true)] out Entity<CEZLevelsNetworkComponent>? zLevel)
    {
        zLevel = null;
        var query = EntityQueryEnumerator<CEZLevelsNetworkComponent>();
        while (query.MoveNext(out var uid, out var zLevelComp))
        {
            if (!zLevelComp.ZLevels.ContainsValue(mapUid))
                continue;

            zLevel = (uid, zLevelComp);
            return true;
        }

        return false;
    }

    [PublicAPI]
    public bool TryMapOffset(Entity<CEZLevelMapComponent?> inputMapUid,
        int offset,
        [NotNullWhen(true)] out Entity<CEZLevelMapComponent>? outputMapUid)
    {
        outputMapUid = null;
        if (!Resolve(inputMapUid, ref inputMapUid.Comp, false))
            return false;

        var query = EntityQueryEnumerator<CEZLevelsNetworkComponent>();
        while (query.MoveNext(out var network))
        {
            if (!network.ZLevels.ContainsValue(inputMapUid))
                continue;

            if (!network.ZLevels.TryGetValue(inputMapUid.Comp.Depth + offset, out var targetMapUid))
                continue;

            if (!_zMapQuery.TryComp(targetMapUid, out var targetZLevelComp))
                continue;

            outputMapUid = (targetMapUid.Value, targetZLevelComp);
            return true;
        }

        return false;
    }

    [PublicAPI]
    public bool TryZNetwork(Entity<CEZLevelMapComponent?> inputMapUid,
        [NotNullWhen(true)] out Entity<CEZLevelsNetworkComponent>? zNetwork)
    {
        zNetwork = null;
        if (!Resolve(inputMapUid, ref inputMapUid.Comp, false))
            return false;

        var query = EntityQueryEnumerator<CEZLevelsNetworkComponent>();
        while (query.MoveNext(out var uid, out var network))
        {
            if (!network.ZLevels.ContainsValue(inputMapUid))
                continue;

            zNetwork = (uid, network);
            return true;
        }

        return false;
    }

    [PublicAPI]
    public bool TryMapUp(Entity<CEZLevelMapComponent?> inputMapUid,
        [NotNullWhen(true)] out Entity<CEZLevelMapComponent>? aboveMapUid)
    {
        return TryMapOffset(inputMapUid, 1, out aboveMapUid);
    }

    [PublicAPI]
    public bool TryMapDown(Entity<CEZLevelMapComponent?> inputMapUid,
        [NotNullWhen(true)] out Entity<CEZLevelMapComponent>? belowMapUid)
    {
        return TryMapOffset(inputMapUid, -1, out belowMapUid);
    }

    private bool TryResolveTraversalMapOffset(EntityUid currentMapUid, int offset, out EntityUid targetMapUid, out int targetDepth)
    {
        targetMapUid = EntityUid.Invalid;
        targetDepth = default;

        if (_zMapQuery.TryComp(currentMapUid, out var zMapComp) &&
            TryMapOffset((currentMapUid, zMapComp), offset, out var targetMap))
        {
            targetMapUid = targetMap.Value.Owner;
            targetDepth = targetMap.Value.Comp.Depth;
            return true;
        }

        if (!_ftlMapQuery.TryComp(currentMapUid, out var ftlMapComp))
            return false;

        targetDepth = ftlMapComp.Depth + offset;
        var query = EntityQueryEnumerator<FTLMapComponent>();
        while (query.MoveNext(out var uid, out var candidate))
        {
            if (candidate.Depth != targetDepth)
                continue;

            targetMapUid = uid;
            return true;
        }

        return false;
    }

    private bool TryGetTraversalDepth(TransformComponent xform, out int depth)
    {
        if (xform.MapUid is { } mapUid)
        {
            if (_zMapQuery.TryComp(mapUid, out var zMapComp))
            {
                depth = zMapComp.Depth;
                return true;
            }

            if (_ftlMapQuery.TryComp(mapUid, out var ftlMapComp))
            {
                depth = ftlMapComp.Depth;
                return true;
            }
        }

        if (xform.GridUid is { } gridUid &&
            TryComp<CEZLinkedGridComponent>(gridUid, out var linked))
        {
            depth = linked.Depth;
            return true;
        }

        depth = default;
        return false;
    }

    private bool HasTraversalContext(TransformComponent xform)
    {
        if (xform.MapUid is { } mapUid &&
            (_zMapQuery.HasComp(mapUid) || _ftlMapQuery.HasComp(mapUid)))
        {
            return true;
        }

        return xform.GridUid is { } gridUid && HasComp<CEZLinkedGridComponent>(gridUid);
    }

    /// <summary>
    /// Returns a list of all maps above the specified map. The closest map at the top is returned first.
    /// </summary>
    [PublicAPI]
    public List<EntityUid> GetAllMapsAbove(Entity<CEZLevelMapComponent> inputMapUid)
    {
        var result = new List<EntityUid>();

        var inputDepth = inputMapUid.Comp.Depth;
        var query = EntityQueryEnumerator<CEZLevelsNetworkComponent>();
        while (query.MoveNext(out var network))
        {
            if (!network.ZLevels.ContainsValue(inputMapUid))
                continue;

            result.AddRange(
                network.ZLevels
                    .Where(kv => kv.Value.HasValue && kv.Key > inputDepth)
                    .OrderBy(kv => kv.Key)
                    .Select(kv => kv.Value!.Value)
            );
        }
        return result;
    }

    /// <summary>
    /// Returns a list of all maps below the specified map. The closest map at the bottom is returned first.
    /// </summary>
    [PublicAPI]
    public List<EntityUid> GetAllMapsBelow(Entity<CEZLevelMapComponent> inputMapUid)
    {
        var result = new List<EntityUid>();

        var inputDepth = inputMapUid.Comp.Depth;
        var query = EntityQueryEnumerator<CEZLevelsNetworkComponent>();
        while (query.MoveNext(out var network))
        {
            if (!network.ZLevels.ContainsValue(inputMapUid))
                continue;

            foreach (var zLevelEnt in network.ZLevels
                         .Where(kv => kv.Value.HasValue && kv.Key < inputDepth)
                         .OrderByDescending(kv => kv.Key)
                         .Select(kv => kv.Value!.Value))
            {
                result.Add(zLevelEnt);
            }
        }

        return result;
    }
}
