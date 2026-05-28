/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Server._Pirate.PVS;
using Content.Shared._Pirate.ZLevels.Core.Components;
using JetBrains.Annotations;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._Pirate.ZLevels.Core;

public sealed partial class CEZLevelsSystem
{
    /// <summary>
    /// Creates a new entity zLevelNetwork
    /// </summary>
    [PublicAPI]
    public Entity<CEZLevelsNetworkComponent> CreateZNetwork(ComponentRegistry? components = null)
    {
        var ent = Spawn();

        var zLevel = EnsureComp<CEZLevelsNetworkComponent>(ent);
        EnsureComp<CEPvsOverrideComponent>(ent);

        zLevel.Components = components ?? new ComponentRegistry();

        return (ent, zLevel);
    }

    /// <summary>
    /// Attempts to add the specified map to the zNetwork network at the specified depth.
    /// </summary>
    private bool TryAddMapIntoZNetwork(Entity<CEZLevelsNetworkComponent> network, EntityUid mapUid, int depth)
    {
        if (!HasComp<MapComponent>(mapUid))
        {
            Log.Error($"Failed to add {ToPrettyString(mapUid)} to ZLevelNetwork {network}: not a map entity.");
            return false;
        }

        if (network.Comp.ZLevels.ContainsKey(depth))
        {
            Log.Error($"Failed to add map {mapUid} to ZLevelNetwork {network}: This depth is already occupied.");
            return false;
        }

        if (TryGetZNetwork(mapUid, out var otherNetwork))
        {
            Log.Error($"Failed attempt to add map {mapUid} to ZLevelNetwork {network}: This map is already in another network {otherNetwork}.");
            return false;
        }

        if (network.Comp.ZLevels.ContainsValue(mapUid))
        {
            Log.Error($"Failed attempt to add map {mapUid} to ZLevelNetwork {network} at depth {depth}: This map is already in this network.");
            return false;
        }

        var zlevel = EnsureComp<CEZLevelMapComponent>(mapUid);
        AttachMapToNetwork(network, (mapUid, zlevel), depth);

        return true;
    }

    public bool TryAddMapsIntoZNetwork(Entity<CEZLevelsNetworkComponent> network, Dictionary<EntityUid, int> maps)
    {
        var success = true;
        var addedMaps = new List<(EntityUid Uid, int Depth)>();

        foreach (var (ent, depth) in maps)
        {
            if (TryAddMapIntoZNetwork(network, ent, depth))
                addedMaps.Add((ent, depth));
            else
                success = false;
        }

        if (success)
        {
            try
            {
                LinkNetworkGrids(network);
            }
            catch (Exception e)
            {
                Log.Error($"LinkNetworkGrids crashed: {e}");
                success = false;
            }
        }

        if (!success)
        {
            // Roll back any partial registrations so callers can dispose the network without
            // leaving orphan CEZLevelMapComponent entries pointing at a soon-to-be-deleted network.
            // Per-map CEMapAddedIntoZNetworkEvent was deliberately NOT raised yet (deferred to
            // the success branch below), so handlers haven't mutated any of these maps and we
            // can detach cleanly.
            //
            // Any component reaching this point is removable: we only EnsureComp'd it after
            // confirming the map wasn't already in a live network (TryGetZNetwork == false), so a
            // pre-existing component here is an orphan with no live owner. Detach clears the
            // network indexes, then RemComp drops the component entirely — leaving it would keep a
            // stale Depth that TryGetTraversalDepth/HasTraversalContext would still observe.
            foreach (var (added, _) in addedMaps)
            {
                if (!TryComp<CEZLevelMapComponent>(added, out var zMap))
                    continue;

                DetachMapFromNetwork(network, (added, zMap));
                RemComp<CEZLevelMapComponent>(added);
            }

            return false;
        }

        // Per-map "added" events are deferred until the entire batch (including LinkNetworkGrids)
        // has committed, so any handler that mutates the map sees a fully-linked network and
        // never runs against a partial state that will get rolled back.
        foreach (var (added, depth) in addedMaps)
        {
            RaiseLocalEvent(added, new CEMapAddedIntoZNetworkEvent(network, depth));
        }

        RaiseLocalEvent(network, new CEZLevelNetworkUpdatedEvent());
        return true;
    }
}

/// <summary>
/// Called on ZLevel Network Entity, when maps added or removed from network
/// </summary>
public sealed class CEZLevelNetworkUpdatedEvent : EntityEventArgs;

/// <summary>
/// Called on map, when it added to ZNetwork
/// </summary>
public sealed class CEMapAddedIntoZNetworkEvent(Entity<CEZLevelsNetworkComponent> network, int depth) : EntityEventArgs
{
    public Entity<CEZLevelsNetworkComponent> Network = network;
    public int Depth = depth;
}
