/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using System.Linq;
using System.Numerics;
using Content.Client._Pirate.ZLevels.Core;
using Content.Shared._Pirate.ZLevels.Core.Components;
using Content.Shared._Pirate.ZLevels.Core.EntitySystems;
using Content.Shared.Maps;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Graphics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Client.Viewport;

public sealed partial class ScalingViewport
{
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly IEyeManager _eyeManager = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly ITileDefinitionManager _tile = default!;
    [Dependency] private readonly IOverlayManager _overlayManager = default!; // Pirate: multiz

    private CEClientZLevelsSystem? _zLevels;
    private SharedMapSystem? _mapSystem;
    // Lazily resolved so secondary z-level passes can convert the current eye between linked deck grid spaces.
    private SharedTransformSystem? _transform;

    private EntityQuery<TransformComponent>? _xformQuery;
    private EntityQuery<MapComponent>? _mapQuery;

    private IEye? _fallbackEye;

    // Cached reference to the engine's PlacementOverlay, found by type name. Pirate: multiz
    private Overlay? _cachedPlacementOverlay; // Pirate: multiz

    /// <summary>
    /// We are looking for at least one empty tile on the screen.
    /// This is used to ensure that it makes sense to draw the z-planes and that they are visible.
    /// </summary>
    public bool TryFindEmptyTiles(EntityUid mapUid)
    {
        if (_xformQuery is null || !_xformQuery.Value.TryComp(mapUid, out var xform))
            return true;

        var drawBox = GetDrawBox();
        var mapId = xform.MapID;

        var corners = new[]
        {
            _eyeManager.ScreenToMap(drawBox.BottomLeft).Position,
            _eyeManager.ScreenToMap(drawBox.BottomRight).Position,
            _eyeManager.ScreenToMap(drawBox.TopLeft).Position,
            _eyeManager.ScreenToMap(drawBox.TopRight).Position
        };

        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;

        foreach (var c in corners)
        {
            if (c.X < minX)
                minX = c.X;
            if (c.Y < minY)
                minY = c.Y;
            if (c.X > maxX)
                maxX = c.X;
            if (c.Y > maxY)
                maxY = c.Y;
        }

        var mapCoordsBottomLeft = new MapCoordinates(new Vector2(minX, minY), mapId);
        var mapCoordsTopRight = new MapCoordinates(new Vector2(maxX, maxY), mapId);

        if (!_mapManager.TryFindGridAt(mapUid, mapCoordsBottomLeft.Position, out _, out var grid))
            return true;

        var tileBottomLeft = grid.TileIndicesFor(mapCoordsBottomLeft);
        var tileTopRight = grid.TileIndicesFor(mapCoordsTopRight);

        for (var x = tileBottomLeft.X - 1; x <= tileTopRight.X + 1; x++)
        {
            for (var y = tileBottomLeft.Y - 1; y <= tileTopRight.Y + 1; y++)
            {
                var tile = grid.GetTileRef(new Vector2i(x, y));
                var tileDef = (ContentTileDefinition)_tile[tile.Tile.TypeId];
                if (tileDef.Transparent || tile.Tile.IsEmpty)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Tries to find the map for a given depth offset.
    /// Prioritizes the player's grid-specific peer links (CEZLinkedGridComponent) so that
    /// shuttles always render their own Z-levels, even when docked at or arrived at a Z-leveled station.
    /// Falls back to the map-level Z-network lookup for entities not on a linked grid.
    /// Also returns the resolved peer grid entity when available, used for correct ZEye positioning.
    /// </summary>
    private bool TryResolveZMap(EntityUid playerMapUid, EntityUid? playerGridUid, int depthOffset, out MapId mapId)
        => TryResolveZMap(playerMapUid, playerGridUid, depthOffset, out mapId, out _);

    private bool TryResolveZMap(EntityUid playerMapUid, EntityUid? playerGridUid, int depthOffset, out MapId mapId, out EntityUid? peerGridUid) // Pirate: multiz
    {
        mapId = default;
        peerGridUid = null; // Pirate: multiz

        // Primary path: grid-specific peer lookup (always shows the structure the player is on)
        if (playerGridUid != null &&
            _entityManager.TryGetComponent<CEZLinkedGridComponent>(playerGridUid.Value, out var linked))
        {
            var targetDepth = linked.Depth + depthOffset;

            if (linked.PeerGrids.TryGetValue(targetDepth, out var peerGrid))
            {
                if (_xformQuery!.Value.TryComp(peerGrid, out var peerXform) &&
                    peerXform.MapUid != null &&
                    _mapQuery!.Value.TryComp(peerXform.MapUid.Value, out var peerMapComp))
                {
                    mapId = peerMapComp.MapId;
                    peerGridUid = peerGrid; // Pirate: multiz
                    return true;
                }
            }
        }

        // Fallback: map-level Z-network lookup (for players not on a linked grid)
        if (_zLevels!.TryMapOffset(playerMapUid, depthOffset, out var targetMap))
        {
            if (_mapQuery!.Value.TryComp(targetMap.Value, out var mapComp))
            {
                mapId = mapComp.MapId;
                return true;
            }
        }

        return false;
    }

    // Reproject the current eye into the peer grid's world space so linked shuttle decks render from the correct relative viewpoint.
    private MapCoordinates GetResolvedEyePosition(TransformComponent playerXform, EntityUid? peerGridUid, MapId targetMapId)
    {
        _transform ??= _entityManager.System<SharedTransformSystem>();
        var rawEyePosition = _fallbackEye?.Position.Position ?? _eye?.Position.Position ?? _transform.GetWorldPosition(playerXform);

        if (peerGridUid is not { } peerGrid ||
            playerXform.GridUid is not { } currentGridUid)
        {
            return new MapCoordinates(rawEyePosition, targetMapId);
        }

        var currentGridMatrix = _transform.GetWorldMatrix(currentGridUid);
        var peerGridMatrix = _transform.GetWorldMatrix(peerGrid);

        if (!Matrix3x2.Invert(currentGridMatrix, out var inverseCurrentGrid))
            return new MapCoordinates(rawEyePosition, targetMapId);

        var eyeLocalToCurrentGrid = Vector2.Transform(rawEyePosition, inverseCurrentGrid);
        var targetWorldPosition = Vector2.Transform(eyeLocalToCurrentGrid, peerGridMatrix);

        return new MapCoordinates(targetWorldPosition, targetMapId);
    }

    private void RenderZLevels(IClydeViewport viewport)
    {
        if (_eye is null)
        {
            viewport.Render();
            return;
        }

        _fallbackEye = _eye;

        _xformQuery ??= _entityManager.GetEntityQuery<TransformComponent>();
        _mapQuery ??= _entityManager.GetEntityQuery<MapComponent>();
        _zLevels ??= _entityManager.System<CEClientZLevelsSystem>();
        _mapSystem ??= _entityManager.System<SharedMapSystem>();

        if (_player.LocalEntity is null)
        {
            viewport.Render();
            return;
        }

        if (!_entityManager.TryGetComponent<CEZLevelViewerComponent>(_player.LocalEntity.Value, out var zLevelViewer))
        {
            viewport.Render();
            return;
        }

        if (!_xformQuery.Value.TryComp(_player.LocalEntity, out var playerXform))
            return;

        if (playerXform.MapUid is null)
            return;

        var lookUp = zLevelViewer.LookUp ? 1 : 0;

        var lowestDepth = 0;
        for (var i = 0; i >= -CESharedZLevelsSystem.MaxZLevelsBelowRendering; i--)
        {
            if (i != 0)
            {
                if (!TryResolveZMap(playerXform.MapUid.Value, playerXform.GridUid, i, out _))
                    continue;
            }

            lowestDepth = i;
        }

        // Find the engine's PlacementOverlay once and cache it. Pirate: multiz
        // It must not render during secondary (non-depth-0) passes to avoid duplicate placement previews. Pirate: multiz
        _cachedPlacementOverlay ??= _overlayManager.AllOverlays // Pirate: multiz
            .FirstOrDefault(o => o.GetType().FullName == "Robust.Client.Placement.PlacementManager+PlacementOverlay"); // Pirate: multiz

        for (var depth = lowestDepth; depth <= lookUp; depth++)
        {
            if (depth == 0)
            {
                viewport.Eye = new ZEye(lowestDepth, 0, lookUp)
                {
                    Position = _fallbackEye.Position,
                    DrawFov = _fallbackEye.DrawFov,
                    DrawLight = _fallbackEye.DrawLight,
                    Offset = _fallbackEye.Offset,
                    Rotation = _fallbackEye.Rotation,
                    Scale = _fallbackEye.Scale,
                };
            }
            else
            {
                if (!TryResolveZMap(playerXform.MapUid.Value, playerXform.GridUid, depth, out var targetMapId, out var peerGridUid))
                    continue;

                Angle rotation = _fallbackEye.Rotation * -1;
                var offset = rotation.ToWorldVec() * CEClientZLevelsSystem.ZLevelOffset * depth;
                // Secondary passes need a peer-aware eye position; otherwise linked decks render as if they shared the root grid's world transform.
                var eyePosition = GetResolvedEyePosition(playerXform, peerGridUid, targetMapId);

                viewport.Eye = new ZEye(lowestDepth, depth, lookUp)
                {
                    Position = eyePosition,
                    DrawFov = _fallbackEye.DrawFov && depth >= 0,
                    DrawLight = _fallbackEye.DrawLight,
                    Offset = _fallbackEye.Offset + offset,
                    Rotation = _fallbackEye.Rotation,
                    Scale = _fallbackEye.Scale,
                };
            }

            viewport.ClearColor = depth == lowestDepth ? Color.Black : null;

            #region Pirate: multiz
            // Hide placement overlay on non-primary passes to prevent duplicate previews.
            var hidePlacement = depth != 0 && _cachedPlacementOverlay != null;
            if (hidePlacement)
                _overlayManager.RemoveOverlay(_cachedPlacementOverlay!);

            viewport.Render();

            if (hidePlacement)
                _overlayManager.AddOverlay(_cachedPlacementOverlay!);
            #endregion Pirate: multiz
        }

        Eye = _fallbackEye;
        viewport.Eye = Eye;
    }

    public sealed class ZEye(int lowest, int depth, int high) : Robust.Shared.Graphics.Eye
    {
        public int LowestDepth = lowest;
        public int Depth = depth;
        public int HighestDepth = high;
    }
}
