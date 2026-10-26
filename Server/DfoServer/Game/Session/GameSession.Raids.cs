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
