using DfoServer.Game.Raids;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    RaidRegistry? raidRegistry;
    public Action<RaidChange, RaidActor>? RaidNoticeArrived { get; set; }
    public Action<RaidParty, RaidActor>? RaidRecordNoticeArrived { get; set; }
    public Action<RaidActor, RaidActor, uint, bool>? RaidPeerNoticeArrived { get; set; }
    public Action<RaidDirectoryChange, RaidActor>? RaidDirectoryNoticeArrived { get; set; }
    public Action<RaidActor>? RaidPeerRefused { get; set; }
    public Action<RaidStartReceipt, RaidActor>? RaidStartArrived { get; set; }
    public Action<RaidCostStatus[], RaidActor>? RaidCostArrived { get; set; }

    // 正式开战身份归原Raid聚合，供进图轨读取；不以诊断对象代替实例。
    public RaidStarted? StartedRaid => raidRegistry?.FindCurrent(this, SelectionEpoch)?.Started;

    internal Characters.Character? CaptureRaidStart(RaidActor actor)
    {
        try { return owner.Execute(() => actor.IsCurrent && CanChangeTownParty ? Selected : null); }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return null; }
    }
    internal bool MatchesRaidStart(RaidStartReservation reservation) => reservation.Actor.IsCurrent && CanChangeTownParty &&
        ReferenceEquals(Selected, reservation.Expected);
    internal void InstallRaidStart(RaidStartReservation reservation, Characters.Character committed)
    {
        if (!MatchesRaidStart(reservation)) throw new InvalidOperationException("攻坚开战提交后原会话已变化");
        Selected = committed;
    }

    public RaidActor? CaptureRaidActor(RaidRegistry registry)
    {
        try
        {
            return owner.Execute(() =>
            {
                if (!CanChangeTownParty || Selected is not { } character || ChannelEntry is null ||
                    RaidAdmissionPlan.Rejection(ChannelEntry, character) is not null) return null;
                if (raidRegistry is not null && !ReferenceEquals(raidRegistry, registry))
                    throw new InvalidOperationException("会话不能绑定另一套攻坚队目录");
                raidRegistry = registry;
                var actor = new RaidActor(this, owner, SelectionEpoch, character.Id, character.Name, ChannelEntry.Identity);
                registry.Observe(actor);
                return actor;
            });
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return null; }
    }

    public bool CanPublishRaid(RaidActor actor, RaidParty party) => actor.IsCurrent && raidRegistry?.IsCurrent(party) == true;
    public bool HasCurrentRaid(RaidActor actor) => actor.IsCurrent && raidRegistry?.Find(actor) is not null;
    public bool HasRaidKey(RaidActor actor, uint key) => actor.IsCurrent && raidRegistry?.Find(actor)?.Key == key;
    public bool CanPublishRaidDirectory(RaidActor actor, RaidDirectoryChange change) => raidRegistry?.CanPublishDirectory(actor, change) == true;
    public RaidWaiting[] RaidWaitingSnapshot(RaidActor actor, byte type) => raidRegistry?.Waiting(actor.Channel, type) ?? [];
    public bool CanPublishRaidWaiting(RaidActor actor, byte type, RaidWaiting[] rows) => actor.IsCurrent &&
        raidRegistry?.IsWaitingSnapshot(actor.Channel, type, rows) == true;

    void InvalidateRaids()
    {
        var registry = raidRegistry;
        raidRegistry = null;
        registry?.RemoveSession(this, SelectionEpoch);
    }
}
