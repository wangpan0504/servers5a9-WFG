using System.Buffers.Binary;
using DfoServer.Game.Marriage;
using DfoServer.Network.Builders;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class MarriageFailureTests
{
    public static void RunCore()
    {
        int checks = Count;
        int[] lengths = [12, 13, 5, 1, 6, 5, 5, 5, 5, 9, 5, 23, 1, 6, 5, 1, 5, 5, 5, 5, 5, 5, 5];
        for (ushort command = 1034; command <= 1056; command++)
        {
            byte[] body = MarriageFailurePackets.Body(command, -7, 123, 2, 1, 3, 4, 0x12345678, true);
            That(body.Length == lengths[command - 1034], "婚姻每条失败回复按对应原生消费者保留读宽 " + command);
            if (command is 1037 or 1046 or 1049)
                Bytes(body, [0], "无结果消费者的失败不伪装为成功 " + command);
            else That(body[0] == 1 && BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(1)) == -7,
                "原生业务结果封装不遗漏i32，即使某回调先读取结果再检查ACK标志 " + command);
        }
        byte[] reply = MarriageFailurePackets.Body(1035, -9, 123, 2, 0, 0x34, 0x12);
        That(BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(5)) == 123 && reply[9] == 2 &&
            reply[10] == 0x34 && reply[11] == 0x12 && reply[12] == 0,
            "失败答复保留目标、邀请类型、保留字和原回应，不改变等待框的匹配上下文");
        Reject<ArgumentException>(() => MarriageFailurePackets.Body(1034, 0), "不允许通用失败入口制造成功结果");
        Reject<ArgumentOutOfRangeException>(() => MarriageFailurePackets.Body(1057, -1), "未知命令不借用通用婚姻ACK");

        var catalog = new MarriageResourceTests.Source().Parse();
        var relation = MarriageWeddingRules.CreateEngagement(Guid.NewGuid(), 1, 1, 2, 1000);
        var married = relation with { State = 4 };
        Reason(() => MarriageWeddingRules.UpgradeRing(relation, catalog, 1, 1, 201), MarriageFailureReason.NotMarried);
        Reason(() => MarriageWeddingRules.UpgradeRing(married, catalog, 1, 5, 201), MarriageFailureReason.InvalidRingLevel);
        Reason(() => MarriageWeddingRules.UpgradeRing(married, catalog, 1, 1, 999), MarriageFailureReason.MissingItem);
        Reason(() => MarriageWeddingRules.UseLovePointItem(married with
            { Members = married.Members.SetItem(1, new() { LovePoints = catalog.LovePoints.Max }) }, catalog, 1, 701), MarriageFailureReason.LovePointLimit);
        Reason(() => MarriageWeddingRules.Reserve(relation, catalog, Guid.NewGuid(), 1, 0, 0, 1001, 1300, false),
            MarriageFailureReason.TasksIncomplete);
        That(MarriageFailurePackets.Rule(1038, MarriageFailureReason.NotMarried) == -4 &&
            MarriageFailurePackets.Rule(1038, MarriageFailureReason.InvalidRingLevel) == -2 &&
            MarriageFailurePackets.Rule(1038, MarriageFailureReason.MissingItem) == -7 &&
            MarriageFailurePackets.Rule(1039, MarriageFailureReason.LovePointLimit) == -2 &&
            MarriageFailurePackets.Rule(1035, MarriageFailureReason.TasksIncomplete, 2) == -9,
            "相同业务原因按命令映射，婚戒、心意上限和预约任务失败不混用");
        foreach (var (command, reason, operation, expected) in new (ushort, MarriageFailureReason, byte, int)[]
        {
            (1035, MarriageFailureReason.AlreadyReserved, 2, -8),
            (1035, MarriageFailureReason.WeddingDailyLimit, 2, -11),
            (1035, MarriageFailureReason.HallUnavailable, 2, -3),
            (1036, MarriageFailureReason.GiftClosed, 0, -4),
            (1036, MarriageFailureReason.AlreadyGifted, 0, -3),
            (1036, MarriageFailureReason.GiftTooSmall, 0, -6),
            (1036, MarriageFailureReason.GiftTooLarge, 0, -7),
            (1040, MarriageFailureReason.NoInvitations, 0, -4),
            (1040, MarriageFailureReason.DuplicateInvitation, 0, -5),
            (1040, MarriageFailureReason.InvalidInvitationTarget, 0, -6)
        })
            That(MarriageFailurePackets.Rule(command, reason, operation) == expected, "原生预约、礼金及请帖分支 " + reason);
        var reserved = relation with { State = 2, Wedding = new() { Id = Guid.NewGuid(), State = 1, Type = 0,
            PayerId = 1, ReservedAt = 1000, StartsAt = 1300, EndsAt = 1360 } };
        var gift = new MarriageGuestGift { Id = Guid.NewGuid(), CharacterId = 3, Gold = 10000, Text = "祝福" };
        Reason(() => MarriageWeddingRules.AddGift(reserved, catalog, gift with { Gold = 0 }, 1001), MarriageFailureReason.GiftTooSmall);
        Reason(() => MarriageWeddingRules.AddGift(reserved, catalog, gift, 1300), MarriageFailureReason.GiftClosed);
        Reason(() => MarriageWeddingRules.SendInvitation(reserved, 1, 3, "Guest", 1001), MarriageFailureReason.NoInvitations);

        var tree = married with { Tree = new() { Seed = 7, Expiry = 2000, WaterDay = 1, Growth = 0 } };
        int seed = catalog.Seeds.Keys.First();
        tree = tree with { Tree = tree.Tree! with { Seed = seed } };
        int water = catalog.Trees[catalog.Seeds[seed]].GrowPoints.Keys.First();
        That(MarriageCoupleRules.Water(tree, 1, 1, water, 2000, 1, catalog).Result == -11 &&
            MarriageCoupleRules.Harvest(tree, 1, 2000, catalog, _ => 0).Result == -7 &&
            MarriageCoupleRules.Harvest(tree, 1, 1001, catalog, _ => throw new Exception()).Result == -6,
            "枯萎、成长不足和无树分别返回各自分支，不把所有树拒绝当成关系丢失");
        That(MarriageCoupleRules.MakeCapsule(married, 1, 1, catalog).Result == -4 &&
            MarriageCoupleRules.MakeCapsule(married with { Members = married.Members.SetItem(1,
                new() { CapsuleGauge = catalog.GrowthCapsule.MaxGage, CapsulesOwned = catalog.GrowthCapsule.MaxOwnCount }) }, 1, 1, catalog).Result == -5 &&
            MarriageCoupleRules.TakeCapsule(married with { Members = married.Members.SetItem(1,
                new() { CapsuleDay = 1, CapsulesTaken = catalog.GrowthCapsule.MaxTakenCount, CapsulesPending = 1 }) }, 1, 1, catalog).Result == -7,
            "胶囊能量不足、持有上限和每日领取额度分别映射原生结果");
        Console.WriteLine($"PASS {Count - checks} 结婚系统全命令失败封装及错误分类断言");

        void Reason(Action action, MarriageFailureReason expected)
        {
            try { action(); }
            catch (MarriageRuleException error) { That(error.Reason == expected, "领域拒绝保留明确原因 " + expected); return; }
            throw new InvalidOperationException("预期婚姻规则拒绝：" + expected);
        }
    }
}
