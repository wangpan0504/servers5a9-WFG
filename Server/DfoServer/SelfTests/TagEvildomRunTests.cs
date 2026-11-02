using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.TagEvildom;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class TagEvildomRunTests
{
    internal static TagEvildomParticipants Participants()
    {
        var learning = new CharacterLearningState(0, [new(0, 321, 12, [new(7, 100, 1)]), new(1, 777, 15, [new(9, 300, 4)])]);
        var first = new Character(99, 3, "TagPrimary", 17, new(62, 0, 10, 20), null) { Level = 95, Learning = learning };
        var second = first with { Id = 103, Slot = 8, Name = "TagStandby", Learning = learning with { SelectedTree = 1 } };
        return new(first, second, 23);
    }

    internal static void Run()
    {
        RequestsAndClearCodec();
        var originalMission = new DfoServer.GameWorld.BattlePass.BattlePassMission(36, 4, 1000, 35, "", -1);
        That(DfoServer.Game.BattlePass.BattlePassTagEvildomCompletion.MatchesMission(1, originalMission),
            "魔界竞技任务保持原周36/raw35/四次/1000XP/参数-1定义");
        foreach (var changed in new[] { originalMission with { Id = 35 }, originalMission with { RawTypeCode = 34 },
            originalMission with { RawParameter = 0 }, originalMission with { RequiredCount = 3 }, originalMission with { Experience = 100 } })
            That(!DfoServer.Game.BattlePass.BattlePassTagEvildomCompletion.MatchesMission(1, changed),
                "同玩法周三次或其它任务不能改写原四次任务定义");
        That(!DfoServer.Game.BattlePass.BattlePassTagEvildomCompletion.MatchesMission(0, originalMission) &&
            !DfoServer.Game.BattlePass.BattlePassTagEvildomCompletion.MatchesMission(2, originalMission),
            "再战正式来源不借日/季任务类别");
        var participants = Participants();
        foreach (var invalid in new[] { participants with { ConfigurationRevision = 0 },
            participants with { Standby = participants.Primary },
            participants with { Primary = participants.Primary with { Level = 94 } },
            participants with { Standby = participants.Standby with { Learning = CharacterLearningState.Uninitialized } } })
            Reject<InvalidDataException>(() => new TagEvildomRunState(Guid.NewGuid(), invalid), "无完整就绪双角色不能构造生命状态");
        Reject<InvalidDataException>(() => new TagEvildomRunState(Guid.Empty, participants), "无实例身份不能构造生命状态");
        var state = new TagEvildomRunState(Guid.NewGuid(), participants);
        var initial = state.Snapshot;
        That(initial.ActiveOrdinal == 0 && initial.Slots.Length == 2 && initial.Slots.All(slot => slot == new TagEvildomLifeSlot(true, 0, null)) &&
            state.NextRevival() is null, "初始两槽存活，只有原队首活跃，不制造复活期限");
        That(state.Switch(2).Outcome == DungeonRunOutcome.Rejected && ReferenceEquals(initial, state.Snapshot), "非法ordinal不写生命状态");
        Bytes(TagEvildomRunPackets.SwitchAcknowledgement(state.Switch(0)), [1], "重复选择当前活槽可确认而不生成第二次切角");
        var switched = state.Switch(1);
        Bytes(TagEvildomRunPackets.SwitchAcknowledgement(switched), [1], "1468成功仅一字节");
        Bytes(TagEvildomRunPackets.SwitchNotification(switched), [1], "1407发队内ordinal1，不发stableId103或名册槽8");
        That(state.Snapshot.ActiveOrdinal == 1 && ReferenceEquals(participants.Primary, state.Participants.Primary) &&
            state.Participants.Standby.Learning.ActiveContext == 1 && initial.ActiveOrdinal == 0,
            "切角只推进Run状态，固定登录主角与备用资料、技能方案及旧快照不改变");
        var inactiveDeath = state.ReportDeath(0, 1_000);
        That(inactiveDeath.Outcome == DungeonRunOutcome.Accepted && inactiveDeath.SwitchTo is null &&
            inactiveDeath.Snapshot.ActiveOrdinal == 1 && !inactiveDeath.DefeatAcknowledgement,
            "本机非当前TAG Actor也可报告死亡，不将其当作越权或强制再切当前活角色");
        Reject<InvalidDataException>(() => TagEvildomRunPackets.DefeatAcknowledgement(inactiveDeath), "单角死亡绝不发送触发败北的1469成功");
        Reject<InvalidDataException>(() => TagEvildomRunPackets.SwitchAcknowledgement(inactiveDeath), "死亡结果不能冒充1468成功");
        Reject<InvalidDataException>(() => TagEvildomRunPackets.SwitchNotification(inactiveDeath), "未切角不能造1407");
        Reject<InvalidDataException>(() => TagEvildomRunPackets.Revival(inactiveDeath), "死亡不能冒充复活");
        var due = state.NextRevival()!;
        That(due.Ordinal == 0 && due.DeathSerial == 1 && due.UntilMilliseconds == 13_000 && due.InstanceId == initial.InstanceId,
            "实际死亡建立本实例、单槽serial与12秒期限");
        That(state.ReportDeath(0, 5_000).Outcome == DungeonRunOutcome.Duplicate && ReferenceEquals(inactiveDeath.Snapshot, state.Snapshot) &&
            state.NextRevival() == due && state.Switch(0).Outcome == DungeonRunOutcome.Rejected, "重复死亡不续期、不改serial，也不能切进死槽");
        foreach (var wrong in new[] { due with { InstanceId = Guid.NewGuid() }, due with { Ordinal = 1 },
            due with { DeathSerial = 2 }, due with { UntilMilliseconds = 13_001 } })
            That(state.Revive(wrong, 20_000).Outcome == DungeonRunOutcome.Rejected, "实例、槽、死亡serial与截止任一不符都不能复活");
        That(state.Revive(due, 12_999).Outcome == DungeonRunOutcome.Rejected, "截止前不提前复活");
        var revived = state.Revive(due, 13_000);
        Bytes(TagEvildomRunPackets.Revival(revived), [0, 0, 0, 0], "1676严格u32 ordinal0，不带伪造HPMP");
        That(revived.Snapshot.Slots[0] == new TagEvildomLifeSlot(true, 1, null) && state.NextRevival() is null &&
            state.Revive(due, 14_000).Outcome == DungeonRunOutcome.Rejected, "复活只确认一次且保留死亡序号");
        var activeDeath = state.ReportDeath(1, 20_000);
        Bytes(TagEvildomRunPackets.SwitchNotification(activeDeath), [0], "当前槽死亡只通知切向另一活槽");
        var dueSecond = state.NextRevival()!;
        Bytes(TagEvildomRunPackets.Revival(state.Revive(dueSecond, 32_000)), [1, 0, 0, 0], "1676 ordinal1仍为4字节");
        state.ReportDeath(0, 40_000);
        That(state.NextRevival()!.DeathSerial == 2 && state.Revive(due, 50_000).Outcome == DungeonRunOutcome.Rejected,
            "同槽第二次死亡拒绝上次serial的迟到任务");
        var terminal = state.ReportDeath(1, 41_000);
        That(terminal.DefeatAcknowledgement && terminal.Snapshot.Defeated && terminal.Snapshot.Slots.All(slot => !slot.Alive && slot.ReviveAtMilliseconds is null) &&
            state.NextRevival() is null, "双槽确实已死才进入败北终态并取消未到期复活");
        Bytes(TagEvildomRunPackets.DefeatAcknowledgement(terminal), [1], "双死1469成功触发原生败北回城");
        That(state.ReportDeath(1, 42_000).Outcome == DungeonRunOutcome.Rejected && state.Switch(0).Outcome == DungeonRunOutcome.Rejected &&
            state.Revive(dueSecond, 50_000).Outcome == DungeonRunOutcome.Rejected, "终态不再重复败北、切角或复活");
        var cancellable = new TagEvildomRunState(Guid.NewGuid(), participants);
        foreach (long badTime in new[] { -1L, long.MaxValue - 11_999 })
            That(cancellable.ReportDeath(0, badTime).Outcome == DungeonRunOutcome.Rejected, "非法时间或加12秒溢出不得改生命状态");
        cancellable.ReportDeath(0, 100);
        var cancelledDue = cancellable.NextRevival()!;
        cancellable.Cancel();
        var cancelled = cancellable.Snapshot;
        cancellable.Cancel();
        That(cancelled.Cancelled && ReferenceEquals(cancelled, cancellable.Snapshot) && cancellable.NextRevival() is null &&
            cancellable.Revive(cancelledDue, 20_000).Outcome == DungeonRunOutcome.Rejected && cancellable.Switch(1).Outcome == DungeonRunOutcome.Rejected,
            "取消幂等、清截止，并使已捕获任务失效");
    }

    static void RequestsAndClearCodec()
    {
        That(TagEvildomRunRequests.Ordinal([0]) == 0 && TagEvildomRunRequests.Ordinal([1]) == 1, "1468/1469严格读取一个队内序号");
        foreach (byte[] invalid in new byte[][] { [], [2], [255], [0, 0], [1, 0, 0, 0] })
            Reject<InvalidDataException>(() => TagEvildomRunRequests.Ordinal(invalid), "拒绝空包、越界与错用u32/尾部");
        Bytes(TagEvildomRunPackets.Failure(0x1234), [0, 0x34, 0x12], "失败仅编码已核success0加u16错误，不授予业务含义");
        // 独立字面向量：两条非零奖励行，8个空位，再count/time/构造尾0；不用被测Builder生成预期。
        byte[] expected = [0x44,0x33,0x22,0x11, 2,0,0,0, 0x88,0x77,0x66,0x55, 0x03,0x02,0x01,0,
            0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0,
            0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0, 0,0,0,0,0,0,0,0,
            2,0,0,0, 0x40,0xE2,0x01,0, 0,0,0,0];
        Bytes(TagEvildomRunPackets.ClearPresentation(new([new(0x11223344, 2), new(0x55667788, 0x010203)], 123_456)), expected,
            "1679为92B展示结构，无邮件收件人、次数或到账资格");
        var clamped = new byte[92];
        clamped[84] = 0x80; clamped[85] = 0xEE; clamped[86] = 0x36;
        Bytes(TagEvildomRunPackets.ClearPresentation(new([], 3_600_001)), clamped, "超过一小时只截断UI显示，不拒绝结果");
        Bytes(TagEvildomRunPackets.ClearPresentation(new([], long.MaxValue)), clamped, "真实耗时长整型投影不溢出u32");
        foreach (var invalid in new TagEvildomClearPresentation[] { new(default, 0),
            new(Enumerable.Repeat(new TagEvildomRewardRow(1, 1), 11).ToImmutableArray(), 0), new([new(0, 1)], 0),
            new([new(1, 0)], 0), new([], -1) })
            Reject<InvalidDataException>(() => TagEvildomRunPackets.ClearPresentation(invalid), "1679拒绝非法行、超界行数和显示值");
    }
}
