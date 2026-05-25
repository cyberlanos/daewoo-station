/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using Content.Server._Pirate.ZLevels.Core.Components;
using Content.Shared.Station.Components;
using Content.Server.Station.Events;
using Content.Server.Station.Systems;
using Content.Shared._Pirate.ZLevels.Core.EntitySystems;
using Robust.Server.GameObjects;
using Robust.Shared.EntitySerialization.Systems;

namespace Content.Server._Pirate.ZLevels.Core;

public sealed partial class CEZLevelsSystem : CESharedZLevelsSystem
{
    [Dependency] private readonly MapSystem _map = default!;
    [Dependency] private readonly MapLoaderSystem _mapLoader = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    [Dependency] private readonly MetaDataSystem _meta = default!;
    [Dependency] private readonly StationSystem _station = default!;
    private bool _serverInitialized;

    public override void Initialize()
    {
        if (_serverInitialized)
            return;

        base.Initialize();
        _serverInitialized = true;
        InitView();
        InitGridSync();
        InitItems(); // Pirate: multiz

        SubscribeLocalEvent<CEStationZLevelsComponent, StationPostInitEvent>(OnStationPostInit);
    }

    private void OnStationPostInit(Entity<CEStationZLevelsComponent> ent, ref StationPostInitEvent args)
    {
        if (ent.Comp.MapsAbove.Count == 0 && ent.Comp.MapsBelow.Count == 0)
            return;

        // Validate base map before creating the network so a missing base grid doesn't leak a network entity.
        EntityUid? mainMap = null;
        EntityUid? mainGrid = null;
        if (TryComp<StationDataComponent>(ent, out var stationData))
        {
            foreach (var grid in stationData.Grids)
            {
                if (Transform(grid).MapUid is not { } gridMap)
                    continue;

                mainMap = gridMap;
                mainGrid = grid;
                break;
            }
        }

        if (mainMap is null || mainGrid is null)
        {
            Log.Error($"Station {ToPrettyString(ent.Owner)} has no grids to base z-levels off of; skipping z-network setup.");
            return;
        }

        var stationName = MetaData(ent).EntityName;
        var stationNetwork = CreateZNetwork(ent.Comp.ZLevelsComponentOverrides);
        _meta.SetEntityName(stationNetwork, $"Station z-Network: {stationName}");

        Dictionary<EntityUid, int> dict = new();
        dict.Add(mainMap.Value, 0);

        // Collect grid UIDs per depth for direct linking
        var gridsByDepth = new Dictionary<int, EntityUid>
        {
            [0] = mainGrid.Value,
        };

        //Loading maps below first
        var depth = ent.Comp.MapsBelow.Count * -1;
        foreach (var mapBelow in ent.Comp.MapsBelow)
        {
            if (!_mapLoader.TryLoadMap(mapBelow, out var mapEnt, out var mapGrids)) // Pirate: multiz
            {
                Log.Error($"Failed to load map for Station zNetwork at depth {depth}!");
                continue;
            }

            Log.Info($"Created map {mapEnt.Value.Comp.MapId} for Station zNetwork at level {depth}");
            _map.InitializeMap(mapEnt.Value.Comp.MapId);
            _meta.SetEntityName(mapEnt.Value, $"{stationName} [{depth}]");
            foreach (var grid in mapGrids!) // Pirate: multiz
            {
                _station.AddGridToStation(ent, grid);
                gridsByDepth.TryAdd(depth, grid);
            }
            dict.Add(mapEnt.Value, depth);
            depth++;
        }

        //Loading maps above next
        depth = 1;
        foreach (var mapAbove in ent.Comp.MapsAbove)
        {
            if (!_mapLoader.TryLoadMap(mapAbove, out var mapEnt, out var mapGrids)) // Pirate: multiz
            {
                Log.Error($"Failed to load map for Station zNetwork at depth {depth}!");
                continue;
            }

            Log.Info($"Created map {mapEnt.Value.Comp.MapId} for Station zNetwork at level {depth}");
            _map.InitializeMap(mapEnt.Value.Comp.MapId);
            _meta.SetEntityName(mapEnt.Value, $"{stationName} [{depth}]");
            foreach (var grid in mapGrids!) // Pirate: multiz
            {
                _station.AddGridToStation(ent, grid);
                gridsByDepth.TryAdd(depth, grid);
            }
            dict.Add(mapEnt.Value, depth);
            depth++;
        }

        if (!TryAddMapsIntoZNetwork(stationNetwork, dict))
        {
            Log.Error($"Failed to populate station z-network {ToPrettyString(stationNetwork)}; tearing it down.");
            QueueDel(stationNetwork);
            return;
        }

        ent.Comp.ZNetworkEntity = stationNetwork;
        LinkGridsDirectly(stationNetwork, gridsByDepth);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        UpdateView(frameTime);
        UpdateGridSync(frameTime);
        UpdateItems(frameTime);
    }
}
