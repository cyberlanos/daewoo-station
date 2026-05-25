// SPDX-FileCopyrightText: 2023 DrSmugleaf <DrSmugleaf@users.noreply.github.com>
// SPDX-FileCopyrightText: 2024 Jake Huxell <JakeHuxell@pm.me>
// SPDX-FileCopyrightText: 2024 metalgearsloth <31366439+metalgearsloth@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 Aiden <28298836+Aidenkrz@users.noreply.github.com>
//
// SPDX-License-Identifier: MIT

using Content.Shared._Pirate.ZLevels.Core.Components; // Pirate: multiz
using Content.Shared.Shuttles.Components;
using JetBrains.Annotations;

namespace Content.Shared.Shuttles.Systems;

public abstract partial class SharedShuttleSystem
{
    /*
     * Handles the label visibility on radar controls. This can be hiding the label or applying other effects.
     */

    protected virtual void UpdateIFFInterfaces(EntityUid gridUid, IFFComponent component) {}

    public Color GetIFFColor(EntityUid gridUid, bool self = false, IFFComponent? component = null)
    {
        if (self)
        {
            return IFFComponent.SelfColor;
        }

        if (!Resolve(gridUid, ref component, false))
        {
            return IFFComponent.IFFColor;
        }

        return component.Color;
    }

    public string? GetIFFLabel(EntityUid gridUid, bool self = false, IFFComponent? component = null)
    {
        // Pirate: multiz — for z-linked grids, the canonical name lives on the lowest-depth peer
        // (typically the manned deck that actually got renamed). Reading the focused deck's own
        // EntityName produces "Unknown" / default placeholder for upper decks; redirect so every
        // peer in the network reports the same name on radar.
        var nameSource = ResolveLinkedGridForLabel(gridUid);
        // Pirate: multiz — ResolveLinkedGridForLabel may hand back a peer-deck uid; tolerate missing metadata gracefully.
        var entName = TryComp<MetaDataComponent>(nameSource, out var nameMeta) ? nameMeta.EntityName : string.Empty;

        if (self)
        {
            return entName;
        }

        if (Resolve(gridUid, ref component, false) && (component.Flags & (IFFFlags.HideLabel | IFFFlags.Hide)) != 0x0)
        {
            return null;
        }

        return string.IsNullOrEmpty(entName) ? Loc.GetString("shuttle-console-unknown") : entName;
    }

    #region Pirate: multiz
    /// <summary>
    /// For a grid that is part of a <see cref="CEZLinkedGridComponent"/> network, returns the
    /// peer with the lowest depth (the bottom deck). For non-linked grids, returns the grid
    /// itself. Used to canonicalise display names across linked-grid networks.
    /// </summary>
    private EntityUid ResolveLinkedGridForLabel(EntityUid gridUid)
    {
        if (!TryComp<CEZLinkedGridComponent>(gridUid, out var linked))
            return gridUid;

        var bestUid = gridUid;
        var bestDepth = linked.Depth;
        foreach (var (depth, peer) in linked.PeerGrids)
        {
            if (depth < bestDepth && Exists(peer))
            {
                bestDepth = depth;
                bestUid = peer;
            }
        }
        return bestUid;
    }
    #endregion Pirate: multiz

    /// <summary>
    /// Sets the color for this grid to appear as on radar.
    /// </summary>
    [PublicAPI]
    public void SetIFFColor(EntityUid gridUid, Color color, IFFComponent? component = null)
    {
        component ??= EnsureComp<IFFComponent>(gridUid);

        if (component.Color.Equals(color))
            return;

        component.Color = color;
        Dirty(gridUid, component);
        UpdateIFFInterfaces(gridUid, component);
    }

    [PublicAPI]
    public void AddIFFFlag(EntityUid gridUid, IFFFlags flags, IFFComponent? component = null)
    {
        component ??= EnsureComp<IFFComponent>(gridUid);

        if ((component.Flags & flags) == flags)
            return;

        component.Flags |= flags;
        Dirty(gridUid, component);
        UpdateIFFInterfaces(gridUid, component);
    }

    [PublicAPI]
    public void RemoveIFFFlag(EntityUid gridUid, IFFFlags flags, IFFComponent? component = null)
    {
        if (!Resolve(gridUid, ref component, false))
            return;

        if ((component.Flags & flags) == 0x0)
            return;

        component.Flags &= ~flags;
        Dirty(gridUid, component);
        UpdateIFFInterfaces(gridUid, component);
    }
}