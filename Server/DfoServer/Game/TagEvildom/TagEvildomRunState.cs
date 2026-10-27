using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;

namespace DfoServer.Game.TagEvildom;

internal sealed record TagEvildomParticipants(Character Primary, Character Standby, long ConfigurationRevision)
{
    public void Validate(int minimumLevel = 95)
    {
        if (ConfigurationRevision <= 0 || Primary.Id == 0 || Standby.Id == 0 || Primary.Id == Standby.Id ||
            minimumLevel is not (90 or 95) || Primary.Level < minimumLevel || Standby.Level < minimumLevel || Primary.IsPvpPrivate || Standby.IsPvpPrivate ||
            !Primary.Learning.Initialized || !Standby.Learning.Initialized)
            throw new InvalidDataException("TAG参与者必须是本次完整编队的两名已就绪95级角色");
        Primary.Learning.Validate();
        Standby.Learning.Validate();
    }
}

internal readonly record struct TagEvildomLifeSlot(bool Alive, long DeathSerial, long? ReviveAtMilliseconds);
internal sealed record TagEvildomLifeSnapshot(Guid InstanceId, long Version, byte ActiveOrdinal,
    ImmutableArray<TagEvildomLifeSlot> Slots, bool Defeated, bool Cancelled);
internal enum TagEvildomLifeOperation { Switch, Death, Revival }
internal sealed record TagEvildomLifeResult(DungeonRunOutcome Outcome, TagEvildomLifeSnapshot Snapshot, TagEvildomLifeOperation Operation,
    byte? SwitchTo = null, byte? Revived = null, bool DefeatAcknowledgement = false);
internal sealed record TagEvildomRevivalTimeout(Guid InstanceId, byte Ordinal, long DeathSerial, long UntilMilliseconds);

// 由正式Run独占；只记录本机死亡报告与复活事实，不另建在线owner或连续HP权威。
internal sealed class TagEvildomRunState
{
    // 当前资源Etc/TagTournament/TagEvildomGathering.etc的[revival time]=12秒。
    public const int RevivalDelayMilliseconds = 12_000;
    public TagEvildomParticipants Participants { get; }
    public TagEvildomLifeSnapshot Snapshot { get; private set; }
    readonly bool imperialArena;
    readonly int switchCooldownMilliseconds;
    long nextSwitchAt;

    public TagEvildomRunState(Guid instanceId, TagEvildomParticipants participants, bool imperialArena = false, int switchCooldownMilliseconds = 0)
    {
        participants.Validate(imperialArena ? 90 : 95);
        if (switchCooldownMilliseconds < 0 || !imperialArena && switchCooldownMilliseconds != 0)
            throw new InvalidDataException("TAG切角冷却与玩法不一致");
        this.imperialArena = imperialArena;
        this.switchCooldownMilliseconds = switchCooldownMilliseconds;
        if (instanceId == Guid.Empty) throw new InvalidDataException("TAG生命状态必须绑定真实Run实例");
        Participants = participants;
        Snapshot = new(instanceId, 0, 0, [new(true, 0, null), new(true, 0, null)], false, false);
    }

    public TagEvildomLifeResult Switch(byte ordinal, long nowMilliseconds = 0)
    {
        if (!Available || ordinal > 1 || !Snapshot.Slots[ordinal].Alive || !Snapshot.Slots[Snapshot.ActiveOrdinal].Alive)
            return Result(DungeonRunOutcome.Rejected);
        if (ordinal == Snapshot.ActiveOrdinal) return Result(DungeonRunOutcome.Duplicate);
        if (nowMilliseconds < nextSwitchAt || nowMilliseconds > long.MaxValue - switchCooldownMilliseconds)
            return Result(DungeonRunOutcome.Rejected);
        nextSwitchAt = nowMilliseconds + switchCooldownMilliseconds;
        Snapshot = Snapshot with { Version = checked(Snapshot.Version + 1), ActiveOrdinal = ordinal };
        return Result(DungeonRunOutcome.Accepted, switchTo: ordinal);
    }

    public TagEvildomLifeResult ReportDeath(byte ordinal, long nowMilliseconds)
    {
        if (!Available || ordinal > 1 || nowMilliseconds < 0 || nowMilliseconds > long.MaxValue - RevivalDelayMilliseconds)
            return Result(DungeonRunOutcome.Rejected, operation: TagEvildomLifeOperation.Death);
        var slot = Snapshot.Slots[ordinal];
        if (!slot.Alive) return Result(DungeonRunOutcome.Duplicate, operation: TagEvildomLifeOperation.Death);
        byte other = (byte)(1 - ordinal);
        bool defeated = !Snapshot.Slots[other].Alive;
        var slots = Snapshot.Slots.SetItem(ordinal, new(false, checked(slot.DeathSerial + 1),
            defeated || imperialArena ? null : checked(nowMilliseconds + RevivalDelayMilliseconds)));
        if (defeated) slots = slots.SetItem(other, slots[other] with { ReviveAtMilliseconds = null });
        // 原生本机TAG context内的非当前Actor也可发1469，不能只准active槽；死亡备用不打断当前活角色。
        byte? switchTo = !defeated && ordinal == Snapshot.ActiveOrdinal ? other : null;
        Snapshot = Snapshot with { Version = checked(Snapshot.Version + 1), Slots = slots, Defeated = defeated,
            ActiveOrdinal = switchTo ?? Snapshot.ActiveOrdinal };
        // 本服兼容顺序：非终局不发会触败北的success，只切活槽并等待12秒复活；不是原服抓包结论。
        return Result(DungeonRunOutcome.Accepted, switchTo, defeat: defeated, operation: TagEvildomLifeOperation.Death);
    }

    public TagEvildomRevivalTimeout? NextRevival()
    {
        if (!Available) return null;
        for (byte ordinal = 0; ordinal < 2; ordinal++)
            if (Snapshot.Slots[ordinal] is { Alive: false, ReviveAtMilliseconds: { } due } slot)
                return new(Snapshot.InstanceId, ordinal, slot.DeathSerial, due);
        return null;
    }

    public TagEvildomLifeResult Revive(TagEvildomRevivalTimeout expected, long nowMilliseconds)
    {
        if (!Available || expected != NextRevival() || nowMilliseconds < expected.UntilMilliseconds)
            return Result(DungeonRunOutcome.Rejected, operation: TagEvildomLifeOperation.Revival);
        var slot = Snapshot.Slots[expected.Ordinal];
        Snapshot = Snapshot with { Version = checked(Snapshot.Version + 1),
            Slots = Snapshot.Slots.SetItem(expected.Ordinal, slot with { Alive = true, ReviveAtMilliseconds = null }) };
        return Result(DungeonRunOutcome.Accepted, revived: expected.Ordinal, operation: TagEvildomLifeOperation.Revival);
    }

    public void Cancel()
    {
        if (Snapshot.Cancelled) return;
        Snapshot = Snapshot with { Version = checked(Snapshot.Version + 1), Cancelled = true,
            Slots = Snapshot.Slots.Select(slot => slot with { ReviveAtMilliseconds = null }).ToImmutableArray() };
    }

    bool Available => !Snapshot.Cancelled && !Snapshot.Defeated && Snapshot.Version < long.MaxValue - 1;
    TagEvildomLifeResult Result(DungeonRunOutcome outcome, byte? switchTo = null, byte? revived = null, bool defeat = false,
        TagEvildomLifeOperation operation = TagEvildomLifeOperation.Switch)
        => new(outcome, Snapshot, operation, switchTo, revived, defeat);
}
