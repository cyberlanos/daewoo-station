// SPDX-FileCopyrightText: 2024 Jake Huxell <JakeHuxell@pm.me>
// SPDX-FileCopyrightText: 2024 Nemanja <98561806+EmoGarbage404@users.noreply.github.com>
// SPDX-FileCopyrightText: 2024 Piras314 <p1r4s@proton.me>
// SPDX-FileCopyrightText: 2024 deltanedas <39013340+deltanedas@users.noreply.github.com>
// SPDX-FileCopyrightText: 2024 deltanedas <@deltanedas:kde.org>
// SPDX-FileCopyrightText: 2025 Aiden <28298836+Aidenkrz@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.Antag;
using Content.Server.Station.Components;
using Content.Server.StationEvents.Components;
using Content.Server._Pirate.ZLevels.Spawning; // Pirate: multiz
using Content.Shared.GameTicking.Components;
using Content.Shared.Station.Components; // Pirate: multiz
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server.StationEvents.Events;

/// <summary>
/// Station event component for spawning this rules antags in space around a station.
/// </summary>
public sealed class SpaceSpawnRule : StationEventSystem<SpaceSpawnRuleComponent>
{
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly CEZLevelFloorGridsSystem _zFloors = default!; // Pirate: multiz

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<SpaceSpawnRuleComponent, AntagSelectLocationEvent>(OnSelectLocation);
    }

    protected override void Added(EntityUid uid, SpaceSpawnRuleComponent comp, GameRuleComponent gameRule, GameRuleAddedEvent args)
    {
        base.Added(uid, comp, gameRule, args);

        if (!TryGetRandomStation(out var station))
        {
            ForceEndSelf(uid, gameRule);
            return;
        }

        #region Pirate: multiz
        if (!TryComp<StationDataComponent>(station, out var stationData)
            || GetStationMainGrid(stationData) is not { } mainGrid)
        {
            Sawmill.Warning($"Chosen station has no main grid, cannot pick location for {ToPrettyString(uid):rule}");
            ForceEndSelf(uid, gameRule);
            return;
        }

        comp.FloorCoords.Clear();
        foreach (var floor in _zFloors.GetFloorGrids(mainGrid.Owner))
        {
            if (TryComp<MapGridComponent>(floor, out var floorGrid))
                comp.FloorCoords.Add(GetSpaceLocationAround(floor, floorGrid, comp.SpawnDistance));
        }

        comp.Coords = comp.FloorCoords.Count > 0 ? comp.FloorCoords[0] : null;
        Sawmill.Info($"Picked {comp.FloorCoords.Count} location(s) for {ToPrettyString(uid):rule}");
        #endregion
    }

    #region Pirate: multiz
    private MapCoordinates GetSpaceLocationAround(EntityUid gridUid, MapGridComponent grid, float spawnDistance)
    {
        var size = grid.LocalAABB.Size.Length() / 2;
        var distance = size + spawnDistance;
        var angle = RobustRandom.NextAngle();
        var location = angle.ToVec() * distance;

        var xform = Transform(gridUid);
        var position = _transform.GetWorldPosition(xform) + location;
        return new MapCoordinates(position, xform.MapID);
    }
    #endregion

    private void OnSelectLocation(Entity<SpaceSpawnRuleComponent> ent, ref AntagSelectLocationEvent args)
    {
        #region Pirate: multiz
        if (ent.Comp.FloorCoords.Count > 0)
        {
            foreach (var floorCoords in ent.Comp.FloorCoords)
                args.Coordinates.Add(floorCoords);
            return;
        }
        #endregion

        if (ent.Comp.Coords is {} coords)
            args.Coordinates.Add(coords);
    }
}
