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

    public override void Initialize()
    {
        base.Initialize();
        InitView();

        SubscribeLocalEvent<CEStationZLevelsComponent, StationPostInitEvent>(OnStationPostInit);
    }

    private void OnStationPostInit(Entity<CEStationZLevelsComponent> ent, ref StationPostInitEvent args)
    {
        if (ent.Comp.MapsAbove.Count == 0 && ent.Comp.MapsBelow.Count == 0)
            return;

        var stationName = MetaData(ent).EntityName;
        var stationNetwork = CreateZNetwork(ent.Comp.ZLevelsComponentOverrides);
        ent.Comp.ZNetworkEntity = stationNetwork;
        _meta.SetEntityName(ent.Comp.ZNetworkEntity.Value, $"Station z-Network: {stationName}");

        // Pirate: multiz - lanos uses StationDataComponent.Grids instead of GetLargestGrid
        EntityUid? mainMap = null;
        if (TryComp<StationDataComponent>(ent, out var stationData))
        {
            foreach (var grid in stationData.Grids)
            {
                mainMap = Transform(grid).MapUid;
                break;
            }
        }

        if (mainMap is null)
            throw new Exception("Station has no grids to base z-levels off of!");

        Dictionary<EntityUid, int> dict = new();
        dict.Add(mainMap.Value, 0);

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
            foreach (var grid in mapGrids!) { _station.AddGridToStation(ent, grid); } // Pirate: multiz
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
            foreach (var grid in mapGrids!) { _station.AddGridToStation(ent, grid); } // Pirate: multiz
            dict.Add(mapEnt.Value, depth);
            depth++;
        }

        TryAddMapsIntoZNetwork(stationNetwork, dict);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        UpdateView(frameTime);
    }
}
