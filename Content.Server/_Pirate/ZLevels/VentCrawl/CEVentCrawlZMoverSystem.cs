using Content.Server._Pirate.ZLevels.Atmos.Piping;
using Content.Shared._Pirate.ZLevels.Core.Components;
using Content.Shared._Pirate.ZLevels.Ghost;
using Content.Shared._Starlight.VentCrawling;
using Content.Shared._Starlight.VentCrawling.Components;
using Content.Shared.Actions;
using Content.Shared.Actions.Components;
using Content.Shared.VentCrawler.Tube.Components;
using Robust.Shared.Containers;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;

namespace Content.Server._Pirate.ZLevels.VentCrawl;

/// <summary>
/// Grants a vent-crawling mob z-level up/down actions while it sits in a multi-z pipe adapter that
/// has a matching adapter above/below, and relocates its holder to that peer adapter when used.
/// </summary>
public sealed class CEVentCrawlZMoverSystem : EntitySystem
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(0.25);
    private static readonly TimeSpan MoveCooldown = TimeSpan.FromSeconds(1);

    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedVentCrawableSystem _ventCraw = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private TimeSpan _nextUpdate;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CEVentCrawlZMoverComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<CEVentCrawlZMoverComponent, CEZLevelActionUp>(OnZLevelUp);
        SubscribeLocalEvent<CEVentCrawlZMoverComponent, CEZLevelActionDown>(OnZLevelDown);
        SubscribeLocalEvent<BeingVentCrawlerComponent, ComponentShutdown>(OnCrawlerShutdown);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_timing.CurTime < _nextUpdate)
            return;

        _nextUpdate = _timing.CurTime + UpdateInterval;

        var query = EntityQueryEnumerator<BeingVentCrawlerComponent>();
        while (query.MoveNext(out var uid, out var crawler))
        {
            UpdateActions(uid, crawler);
        }
    }

    private void UpdateActions(EntityUid uid, BeingVentCrawlerComponent crawler)
    {
        if (!TryGetAdapterTube(crawler, out var tube))
        {
            RemCompDeferred<CEVentCrawlZMoverComponent>(uid);
            return;
        }

        var hasUp = FindPeerAdapterTube(tube, 1) != null;
        var hasDown = FindPeerAdapterTube(tube, -1) != null;

        if (!hasUp && !hasDown)
        {
            RemCompDeferred<CEVentCrawlZMoverComponent>(uid);
            return;
        }

        var mover = EnsureComp<CEVentCrawlZMoverComponent>(uid);

        if (hasUp)
            EnsureAction(uid, mover, true);
        else
            RemoveAction(uid, mover, true);

        if (hasDown)
            EnsureAction(uid, mover, false);
        else
            RemoveAction(uid, mover, false);
    }

    private void OnZLevelUp(Entity<CEVentCrawlZMoverComponent> ent, ref CEZLevelActionUp args)
    {
        if (args.Handled)
            return;

        args.Handled = TryTraverse(ent, ent.Comp, 1);
    }

    private void OnZLevelDown(Entity<CEVentCrawlZMoverComponent> ent, ref CEZLevelActionDown args)
    {
        if (args.Handled)
            return;

        args.Handled = TryTraverse(ent, ent.Comp, -1);
    }

    private bool TryTraverse(EntityUid uid, CEVentCrawlZMoverComponent mover, int offset)
    {
        if (_timing.CurTime < mover.NextMove)
            return false;

        if (!TryComp<BeingVentCrawlerComponent>(uid, out var crawler) ||
            !TryGetAdapterTube(crawler, out var currentTube) ||
            !TryComp<VentCrawlerHolderComponent>(crawler.Holder, out var holder))
        {
            return false;
        }

        if (FindPeerAdapterTube(currentTube, offset) is not { } peerTube)
            return false;

        var holderUid = crawler.Holder;

        // Detach the holder from the current tube before re-inserting into the peer, mirroring the crawl Update loop.
        if (TryComp<VentCrawlerTubeComponent>(currentTube, out var tubeComp) && tubeComp.Contents != null)
            _container.Remove(holderUid, tubeComp.Contents, reparent: false, force: true);

        holder.NextTube = null;

        if (!_ventCraw.EnterTube(holderUid, peerTube, holder))
            return false;

        StartCooldown(mover);
        return true;
    }

    /// <summary>
    /// True if the crawler's holder is currently resting in a multi-z pipe adapter, outputting that tube.
    /// </summary>
    private bool TryGetAdapterTube(BeingVentCrawlerComponent crawler, out EntityUid tube)
    {
        tube = default;

        if (!TryComp<VentCrawlerHolderComponent>(crawler.Holder, out var holder) ||
            holder.CurrentTube is not { } current ||
            !HasComp<CEMultizAtmosPipeAdapterComponent>(current))
        {
            return false;
        }

        tube = current;
        return true;
    }

    /// <summary>
    /// Finds a connected adapter tube on the linked peer grid <paramref name="offset"/> levels away,
    /// sitting at the same world tile. Mirrors <see cref="CEMultizAtmosPipeAdapterNode"/>'s peer lookup.
    /// </summary>
    private EntityUid? FindPeerAdapterTube(EntityUid tube, int offset)
    {
        var xform = Transform(tube);

        if (xform.GridUid is not { } gridUid ||
            !TryComp<MapGridComponent>(gridUid, out _) ||
            !TryComp<CEZLinkedGridComponent>(gridUid, out var linked))
        {
            return null;
        }

        if (!linked.PeerGrids.TryGetValue(linked.Depth + offset, out var peerGridUid) ||
            !TryComp<MapGridComponent>(peerGridUid, out var peerGrid))
        {
            return null;
        }

        // Source tile won't line up on peer decks with a different transform; reproject via world pos.
        var worldPos = _transform.GetWorldPosition(xform);
        var peerTile = _map.WorldToTile(peerGridUid, peerGrid, worldPos);

        foreach (var ent in _map.GetAnchoredEntities(peerGridUid, peerGrid, peerTile))
        {
            if (HasComp<CEMultizAtmosPipeAdapterComponent>(ent) &&
                TryComp<VentCrawlerTubeComponent>(ent, out var peerTube) &&
                peerTube.Connected)
            {
                return ent;
            }
        }

        return null;
    }

    private void EnsureAction(EntityUid uid, CEVentCrawlZMoverComponent mover, bool up)
    {
        ref var actionEntity = ref (up ? ref mover.ZLevelUpActionEntity : ref mover.ZLevelDownActionEntity);

        if (actionEntity is { } existing &&
            TryComp<ActionComponent>(existing, out var action) &&
            action.AttachedEntity == uid)
        {
            return;
        }

        if (actionEntity is { } invalid && !Exists(invalid))
            actionEntity = null;

        _actions.AddAction(uid, ref actionEntity, up ? mover.UpActionProto : mover.DownActionProto);
    }

    private void RemoveAction(EntityUid uid, CEVentCrawlZMoverComponent mover, bool up)
    {
        ref var actionEntity = ref (up ? ref mover.ZLevelUpActionEntity : ref mover.ZLevelDownActionEntity);

        if (actionEntity is not { } action)
            return;

        _actions.RemoveAction(uid, action);
        actionEntity = null;
    }

    private void StartCooldown(CEVentCrawlZMoverComponent mover)
    {
        var start = _timing.CurTime;
        mover.NextMove = start + MoveCooldown;

        _actions.SetCooldown(mover.ZLevelUpActionEntity, start, mover.NextMove);
        _actions.SetCooldown(mover.ZLevelDownActionEntity, start, mover.NextMove);
    }

    private void OnShutdown(Entity<CEVentCrawlZMoverComponent> ent, ref ComponentShutdown args)
    {
        RemoveAction(ent, ent.Comp, true);
        RemoveAction(ent, ent.Comp, false);
    }

    private void OnCrawlerShutdown(Entity<BeingVentCrawlerComponent> ent, ref ComponentShutdown args)
        => RemCompDeferred<CEVentCrawlZMoverComponent>(ent);
}
