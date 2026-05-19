// SPDX-FileCopyrightText: 2025 GoobBot <uristmchands@proton.me>
// SPDX-FileCopyrightText: 2025 Roudenn <romabond091@gmail.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

// Copyright Rane (elijahrane@gmail.com) 2025
// All rights reserved. Relicensed under AGPL with permission

using System.Linq;
using System.Numerics;
using Content.Server.Shuttles.Systems;
using Content.Shared._Mono.FireControl;
using Content.Shared._Pirate.ZLevels.Core.Components; // Pirate: multiz
using Content.Shared._Pirate.ZLevels.Core.EntitySystems; // Pirate: multiz
using Content.Shared._Pirate.ZLevels.FireControl; // Pirate: multiz
using Content.Shared.Power;
using Content.Shared.Shuttles.BUIStates;
using Robust.Server.GameObjects;
using Robust.Shared.Map;

namespace Content.Server._Mono.FireControl;

public sealed partial class FireControlSystem : EntitySystem
{
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly ShuttleConsoleSystem _shuttleConsoleSystem = default!;

    private const int DefaultGunLayerReach = 1; // Pirate: multiz

    private void InitializeConsole()
    {
        SubscribeLocalEvent<FireControlConsoleComponent, PowerChangedEvent>(OnPowerChanged);
        SubscribeLocalEvent<FireControlConsoleComponent, ComponentShutdown>(OnComponentShutdown);
        SubscribeLocalEvent<FireControlConsoleComponent, FireControlConsoleRefreshServerMessage>(OnRefreshServer);
        SubscribeLocalEvent<FireControlConsoleComponent, FireControlConsoleFireMessage>(OnFire);
        SubscribeLocalEvent<FireControlConsoleComponent, BoundUIOpenedEvent>(OnUIOpened);
        SubscribeLocalEvent<FireControlConsoleComponent, FireControlConsoleSelectLayerMessage>(OnSelectLayer); // Pirate: multiz
    }

    private void OnPowerChanged(EntityUid uid, FireControlConsoleComponent component, PowerChangedEvent args)
    {
        if (args.Powered)
            TryRegisterConsole(uid, component);
        else
            UnregisterConsole(uid, component);
    }

    private void OnComponentShutdown(EntityUid uid, FireControlConsoleComponent component, ComponentShutdown args)
    {
        UnregisterConsole(uid, component);
    }

    private void OnRefreshServer(EntityUid uid, FireControlConsoleComponent component, FireControlConsoleRefreshServerMessage args)
    {
        if (component.ConnectedServer == null)
        {
            TryRegisterConsole(uid, component);
        }

        if (component.ConnectedServer != null &&
            TryComp<FireControlServerComponent>(component.ConnectedServer, out var server) &&
            server.ConnectedGrid != null)
        {
            RefreshControllables((EntityUid)server.ConnectedGrid);
        }
    }

    private void OnFire(EntityUid uid, FireControlConsoleComponent component, FireControlConsoleFireMessage args)
    {
        if (component.ConnectedServer == null || !TryComp<FireControlServerComponent>(component.ConnectedServer, out var server))
            return;

        // Fire the actual weapons
        FireWeapons((EntityUid)component.ConnectedServer, args.Selected, args.Coordinates, server);

        // Raise an event to track the cursor position even when not firing
        var fireEvent = new FireControlConsoleFireEvent(args.Coordinates, args.Selected);
        RaiseLocalEvent(uid, fireEvent);
    }

    public void OnUIOpened(EntityUid uid, FireControlConsoleComponent component, BoundUIOpenedEvent args)
    {
        UpdateUi(uid, component);
    }

    #region Pirate: multiz
    private void OnSelectLayer(EntityUid uid, FireControlConsoleComponent component, FireControlConsoleSelectLayerMessage args)
    {
        component.SelectedLayerDepth = args.Depth;
        UpdateUi(uid, component);
    }
    #endregion Pirate: multiz

    private void UnregisterConsole(EntityUid console, FireControlConsoleComponent? component = null)
    {
        if (!Resolve(console, ref component))
            return;

        if (component.ConnectedServer == null || !TryComp<FireControlServerComponent>(component.ConnectedServer, out var server))
            return;

        server.Consoles.Remove(console);
        component.ConnectedServer = null;
        UpdateUi(console, component);
    }
    private bool TryRegisterConsole(EntityUid console, FireControlConsoleComponent? consoleComponent = null)
    {
        if (!Resolve(console, ref consoleComponent))
            return false;

        var gridServer = TryGetGridServer(console);

        if (gridServer.ServerComponent == null)
            return false;

        if (gridServer.ServerComponent.Consoles.Add(console))
        {
            consoleComponent.ConnectedServer = gridServer.ServerUid;
            UpdateUi(console, consoleComponent);
            return true;
        }
        else
        {
            return false;
        }
    }

    private void UpdateUi(EntityUid uid, FireControlConsoleComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        #region Pirate: multiz
        // Enumerate every layer in this console's z-network (sourced from the network's full
        // ZLevels dict, so layers that have a map but no peer grid are still selectable). For
        // each layer that does have a grid+server, collect the guns.
        var consoleGrid = _xform.GetGrid(uid);
        var hostDepth = GetGridDepth(consoleGrid) ?? 0;

        var networkLayers = GetNetworkLayers(consoleGrid);

        var controllables = new List<FireControllableEntry>();
        var gunDepths = new List<int>();

        foreach (var (depth, layer) in networkLayers)
        {
            if (layer.Grid is not { } gridUid)
                continue;
            if (!TryComp<FireControlGridComponent>(gridUid, out var fcGrid) || fcGrid.ControllingServer is not { } serverUid)
                continue;
            if (!TryComp<FireControlServerComponent>(serverUid, out var serverComp))
                continue;

            foreach (var controllable in serverComp.Controlled)
            {
                if (!Exists(controllable))
                    continue;

                var reach = TryComp<CEZGunLayerReachComponent>(controllable, out var reachComp) ? reachComp.Reach : DefaultGunLayerReach;

                var entry = new FireControllableEntry
                {
                    NetEntity = EntityManager.GetNetEntity(controllable),
                    Coordinates = GetNetCoordinates(Transform(controllable).Coordinates),
                    Name = MetaData(controllable).EntityName,
                    Depth = depth,
                    Reach = reach,
                };
                controllables.Add(entry);
                gunDepths.Add(depth);
            }
        }

        // Available layers = (gunDepths range expanded by max reach) ∩ existing network depths.
        var layers = BuildLayerInfos(networkLayers, gunDepths, controllables);
        var currentLayer = ResolveCurrentLayer(component, layers, hostDepth);

        // Point the radar at the selected layer. If a peer grid lives there we anchor on it
        // (so the radar follows shuttle motion); otherwise we anchor on the layer's map and use
        // the console's world position so the operator sees the corresponding empty-space area.
        var navState = BuildNavStateForLayer(uid, currentLayer, networkLayers);
        #endregion Pirate: multiz

        var array = controllables.ToArray();

        var state = new FireControlConsoleBoundInterfaceState(
            component.ConnectedServer != null,
            array,
            navState,
            layers, // Pirate: multiz
            currentLayer); // Pirate: multiz
        _ui.SetUiState(uid, FireControlConsoleUiKey.Key, state);
    }

    #region Pirate: multiz
    /// <summary>
    /// Describes one z-layer the console can target: the map at that depth and, when it exists,
    /// the peer grid the shuttle owns there. Grid is <c>null</c> for layers that exist in the
    /// network's <c>ZLevels</c> dict but aren't a deck of this shuttle (e.g. the empty layer
    /// above/below from the reach bonus).
    /// </summary>
    private readonly record struct ConsoleNetworkLayer(EntityUid Map, EntityUid? Grid);

    /// <summary>
    /// Returns a depth → (map, peer-grid?) map covering every layer in the console's z-network.
    /// For a non-linked grid the result is a single entry at depth 0 with the host map and grid.
    /// </summary>
    private SortedDictionary<int, ConsoleNetworkLayer> GetNetworkLayers(EntityUid? consoleGrid)
    {
        var result = new SortedDictionary<int, ConsoleNetworkLayer>();
        if (consoleGrid is null)
            return result;

        if (!TryComp<CEZLinkedGridComponent>(consoleGrid.Value, out var linked))
        {
            var soloMap = Transform(consoleGrid.Value).MapUid;
            if (soloMap is { } map)
                result[0] = new ConsoleNetworkLayer(map, consoleGrid.Value);
            return result;
        }

        // Peer grids keyed by depth so we can attach them to the matching network map below.
        var peerGridsByDepth = new Dictionary<int, EntityUid>(linked.PeerGrids)
        {
            [linked.Depth] = consoleGrid.Value,
        };

        if (!TryComp<CEZLevelsNetworkComponent>(linked.ZNetwork, out var network))
        {
            // No network component? Fall back to peer-grid-only enumeration so we still get
            // a usable selector. Empty-layer reach won't show up but at least manned decks do.
            foreach (var (depth, grid) in peerGridsByDepth)
            {
                if (!Exists(grid))
                    continue;
                var gridMap = Transform(grid).MapUid;
                if (gridMap is { } m)
                    result[depth] = new ConsoleNetworkLayer(m, grid);
            }
            return result;
        }

        foreach (var (depth, mapUidNullable) in network.ZLevels)
        {
            if (mapUidNullable is not { } mapUid || !Exists(mapUid))
                continue;

            EntityUid? gridForDepth = null;
            if (peerGridsByDepth.TryGetValue(depth, out var peerGrid) && Exists(peerGrid))
                gridForDepth = peerGrid;

            result[depth] = new ConsoleNetworkLayer(mapUid, gridForDepth);
        }

        return result;
    }

    /// <summary>
    /// Builds the layer-selector entries for the BUI. The selectable range is
    /// <c>[min_gun_depth - maxReach, max_gun_depth + maxReach]</c> intersected with depths that
    /// actually exist as maps in the network — so the operator can never target a layer with no
    /// map, but CAN target empty-space layers above/below the shuttle (within reach). If no
    /// guns exist yet, falls back to every layer in the network.
    /// </summary>
    private FireControlLayerInfo[] BuildLayerInfos(
        SortedDictionary<int, ConsoleNetworkLayer> networkLayers,
        List<int> gunDepths,
        List<FireControllableEntry> controllables)
    {
        if (networkLayers.Count == 0)
            return Array.Empty<FireControlLayerInfo>();

        int minDepth;
        int maxDepth;

        if (gunDepths.Count == 0)
        {
            minDepth = networkLayers.Keys.First();
            maxDepth = networkLayers.Keys.Last();
        }
        else
        {
            var maxReach = controllables.Max(c => c.Reach);
            minDepth = gunDepths.Min() - maxReach;
            maxDepth = gunDepths.Max() + maxReach;
        }

        return networkLayers
            .Where(kvp => kvp.Key >= minDepth && kvp.Key <= maxDepth)
            .Select(kvp =>
            {
                // Prefer the peer grid for the NetEntity reference (so the client can still
                // resolve it for any grid-specific bookkeeping); fall back to the map when the
                // layer has no peer grid.
                var anchor = kvp.Value.Grid ?? kvp.Value.Map;
                return new FireControlLayerInfo(kvp.Key, GetNetEntity(anchor));
            })
            .ToArray();
    }

    /// <summary>
    /// Picks the depth this UI push should target: the operator's preserved selection if it is
    /// still inside the layer list, otherwise the console's own depth, otherwise the first
    /// layer in the list.
    /// </summary>
    private int ResolveCurrentLayer(FireControlConsoleComponent component, FireControlLayerInfo[] layers, int hostDepth)
    {
        if (layers.Length == 0)
            return hostDepth;

        if (component.SelectedLayerDepth is { } selected && layers.Any(l => l.Depth == selected))
            return selected;

        if (layers.Any(l => l.Depth == hostDepth))
        {
            component.SelectedLayerDepth = hostDepth;
            return hostDepth;
        }

        component.SelectedLayerDepth = layers[0].Depth;
        return layers[0].Depth;
    }

    /// <summary>
    /// Builds a <see cref="NavInterfaceState"/> centred on the layer for
    /// <paramref name="currentLayer"/>. Anchors on the peer grid when one exists at that depth
    /// (so the view follows shuttle motion); otherwise anchors on the layer's map at the
    /// console's world xy so the operator sees the matching empty-space region.
    /// </summary>
    private NavInterfaceState BuildNavStateForLayer(
        EntityUid consoleUid,
        int currentLayer,
        SortedDictionary<int, ConsoleNetworkLayer> networkLayers)
    {
        var allDocks = _shuttleConsoleSystem.GetAllDocks();

        if (!networkLayers.TryGetValue(currentLayer, out var layer))
            return _shuttleConsoleSystem.GetNavState(consoleUid, allDocks);

        var consoleXform = Transform(consoleUid);

        if (layer.Grid is { } gridUid && consoleXform.GridUid == gridUid)
            return _shuttleConsoleSystem.GetNavState(consoleUid, allDocks);

        if (layer.Grid is { } peerGrid)
        {
            // Anchor on the peer grid at the console's local xy — z-synced peers share local
            // coordinate systems, so this lines the view up with the operator's deck.
            var coords = new EntityCoordinates(peerGrid, consoleXform.LocalPosition);
            return _shuttleConsoleSystem.GetNavState(consoleUid, allDocks, coords, consoleXform.LocalRotation);
        }

        // No peer grid — anchor on the empty layer's map at the console's world xy.
        var consoleWorldPos = _xform.GetWorldPosition(consoleUid);
        var mapCoords = new EntityCoordinates(layer.Map, consoleWorldPos);
        return _shuttleConsoleSystem.GetNavState(consoleUid, allDocks, mapCoords, consoleXform.LocalRotation);
    }
    #endregion Pirate: multiz
}
