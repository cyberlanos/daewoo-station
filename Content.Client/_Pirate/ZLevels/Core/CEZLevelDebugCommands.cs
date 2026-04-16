/*
 * This file is sublicensed under MIT License
 * https://github.com/space-wizards/space-station-14/blob/master/LICENSE.TXT
 */

using System.Globalization;
using System.Linq;
using Content.Shared._Pirate.ZLevels.Core.Components;
using Content.Shared._Pirate.ZLevels.Core.EntitySystems;
using Robust.Client.Player;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;

namespace Content.Client._Pirate.ZLevels.Core;

public sealed class CEZDebugSelfCommand : LocalizedCommands
{
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IEntityManager _entityManager = default!;

    public override string Command => "cezdebugself";
    public override string Description => "Prints local player z-level state and high-ground entities on the current tile.";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (_player.LocalEntity is not { } uid)
        {
            shell.WriteLine("No local controlled entity.");
            return;
        }

        var xformSystem = _entityManager.System<SharedTransformSystem>();
        var mapSystem = _entityManager.System<SharedMapSystem>();
        var zLevels = _entityManager.System<CESharedZLevelsSystem>();

        if (!_entityManager.TryGetComponent(uid, out TransformComponent? xform) ||
            !_entityManager.TryGetComponent(uid, out MetaDataComponent? meta))
        {
            shell.WriteLine($"Failed to resolve transform/metadata for local entity {uid}.");
            return;
        }

        shell.WriteLine($"Entity: {meta.EntityName} proto={meta.EntityPrototype?.ID ?? "<none>"} uid={uid}");
        shell.WriteLine($"Transform: parent={xform.ParentUid} grid={xform.GridUid?.ToString() ?? "null"} map={xform.MapUid?.ToString() ?? "null"} anchored={xform.Anchored}");
        shell.WriteLine($"Position: world={xformSystem.GetWorldPosition(uid)} tile={xformSystem.GetGridOrMapTilePosition(uid)}");

        if (_entityManager.TryGetComponent(uid, out CEZPhysicsComponent? zPhys))
        {
            shell.WriteLine(
                $"CEZPhysics: yes active={_entityManager.HasComponent<CEActiveZPhysicsComponent>(uid)} local={Fmt(zPhys.LocalPosition)} vel={Fmt(zPhys.Velocity)} ground={Fmt(zPhys.CurrentGroundHeight)} sticky={zPhys.CurrentStickyGround} gravity={Fmt(zPhys.GravityMultiplier)} autoStep={zPhys.AutoStep}");
        }
        else
        {
            shell.WriteLine($"CEZPhysics: no active={_entityManager.HasComponent<CEActiveZPhysicsComponent>(uid)}");
        }

        if (xform.MapUid is { } mapUid &&
            _entityManager.TryGetComponent(mapUid, out CEZLevelMapComponent? zMap))
        {
            shell.WriteLine($"ZMap: depth={zMap.Depth} hasTileAbove={zLevels.HasTileAbove(uid)}");
            shell.WriteLine($"ZNetwork: hasUp={zLevels.TryMapUp((mapUid, zMap), out var upMap)} hasDown={zLevels.TryMapDown((mapUid, zMap), out var downMap)}");

            if (upMap != null)
                shell.WriteLine($"MapUp: uid={upMap.Value.Owner} depth={upMap.Value.Comp.Depth}");

            if (downMap != null)
                shell.WriteLine($"MapDown: uid={downMap.Value.Owner} depth={downMap.Value.Comp.Depth}");
        }
        else
        {
            shell.WriteLine("ZMap: current map is not in a z-level network.");
        }

        if (xform.GridUid is not { } gridUid ||
            !_entityManager.TryGetComponent(gridUid, out MapGridComponent? grid))
        {
            shell.WriteLine("Current tile entities: local entity is not on a grid.");
            return;
        }

        var tile = xformSystem.GetGridOrMapTilePosition(uid);
        shell.WriteLine($"Current tile entities at {tile}:");

        var found = false;
        var enumerator = mapSystem.GetAnchoredEntitiesEnumerator(gridUid, grid, tile);
        while (enumerator.MoveNext(out var anchoredNullable))
        {
            var anchored = anchoredNullable.Value;
            found = true;

            var anchoredMeta = _entityManager.GetComponent<MetaDataComponent>(anchored);
            var proto = anchoredMeta.EntityPrototype?.ID ?? "<none>";
            var line = $"- {anchoredMeta.EntityName} proto={proto} uid={anchored}";

            if (_entityManager.TryGetComponent(anchored, out CEZLevelHighGroundComponent? highGround))
            {
                var curve = string.Join(", ", highGround.HeightCurve.Select(Fmt));
                var rot = xformSystem.GetWorldRotation(anchored);
                line += $" highGround=true stick={highGround.Stick} corner={highGround.Corner} rot={rot} curve=[{curve}]";
            }

            shell.WriteLine(line);
        }

        if (!found)
            shell.WriteLine("- none");
    }

    private static string Fmt(float value)
    {
        return value.ToString("0.00", CultureInfo.InvariantCulture);
    }
}
