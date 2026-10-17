using DfoServer.Game.Accounts;
using System.Buffers.Binary;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class QuestGoldMailTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "quest-gold-mail"), resources: resources);
        var quest = resources.Quests.Get(3156);
        That(quest.Condition == "[reach the range]" && quest.SubType == 1 && quest.IntData.SequenceEqual(new[] { 38, 3, 445, 115, 400, 300 }) &&
            quest.Rewards.Select(row => row.Value).SequenceEqual(new[] { 0, 0 }) && quest.MinimumLevel == 17,
            "3156真实资源为已达成范围目标与公式金币奖励，不以客户端scalar代替定义");
        var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
        var repository = new CharacterRepository(fixture.Database, resources.Quests);
        ushort id;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("QuestMail", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0, 4, 17, candidate.InitialLearning with
            {
                Experience = growth.GetLevelStartExperience(17)
            }, resources.Learning);
            id = repository.Create(owner, candidate with
            {
                Level = 17, GrowType = 4, CompletedQuests = [3155], ActiveMainQuests = [new(0, 3156, 0)],
                InitialLearning = learning, Skills = learning.Trees[0].Skills.Select(row => new CharacterSkill(row.Id, row.Level)).ToArray(),
                Inventory = candidate.Inventory with { Gold = 100000001 }
            }).Id;
        }
        await fixture.StartAsync();
        uint mail;
        using (var peer = Open(fixture))
        {
            peer.Select(prepared: true);
            long beforeExperience = Convert.ToInt64(fixture.Query("SELECT experience FROM characters WHERE id=$p0", id)[0][0]);
            peer.Send(34, [34, 0, 84, 12, 255, 255, 1, 0, 255, 255]);
            peer.Expect(1, 34, [1, 84, 12, 64, 80, 11, 0, 0, 0, 0]);
            byte[] experience = peer.Expect(0, 37).Body;
            That(BinaryPrimitives.ReadUInt64LittleEndian(experience.AsSpan(1)) == (ulong)(beforeExperience + 2896) && U32(experience, 9) == 0,
                "ACK34不含未入包金币，实际2896经验与普通经验分类完整发布");
            peer.Expect(0, 21); peer.Expect(0, 356); peer.Expect(0, 99, [1, 0]);
            That(fixture.Query("SELECT gold FROM characters WHERE id=$p0", id) is [[100000001L]] &&
                fixture.Query("SELECT condition_type,gold_amount FROM character_quest_completion_receipts WHERE quest_id=3156") is [[64L, 756L]] &&
                fixture.Query("SELECT quest_id FROM character_completed_quests WHERE quest_id=3156") is [[3156L]] &&
                fixture.Query("SELECT count(*) FROM character_active_main_quests WHERE quest_id=3156") is [[0L]],
                "真实3156在超金币上限时完整发经验、清槽和完成，原金币余额不变，回执名义额756保留");
            peer.Send(96); peer.Expect(1, 96, [1, 0, 0]);
            var page = MailboxTcpTests.ReadList(peer.Expect(0, 97).Body);
            mail = page.Summaries.Single().MailId;
            That(page.Summaries.Single().Gold == 756 && page.Summaries.Single().ItemId == 0 &&
                !page.Details.Single().Text.Contains("3156", StringComparison.Ordinal) &&
                fixture.Query("SELECT expires_at,system_mail FROM mailbox_messages WHERE mail_id=$p0", mail) is [[DBNull, 1L]],
                "任务金币通过既有纯金币永久系统邮件展示，产品正文不暴露内部任务ID");
            peer.Send(34, [34, 0, 84, 12, 255, 255, 1, 0, 255, 255]); peer.Expect(1, 34, [0, 19, 0]);
            peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM mailbox_messages") is [[1L]], "重复确认不多发邮件、金币或成功增量");
            peer.Send(95, Build(w => { w.Write(1); w.Write(mail); })); peer.Expect(1, 95, [0, 10, 0]);
            peer.Barrier();
            That(fixture.Query("SELECT gold FROM mailbox_messages WHERE mail_id=$p0", mail) is [[756L]],
                "仍超携带上限时邮箱领取沿原规则拒绝，完整资格保留");
            peer.Send(3, [0]); peer.Expect(1, 3, [1]); peer.Closed();
        }
        // 仅隔离fixture模拟玩家腾出金币空间；下一连接重新读取实际存档。
        fixture.Sql("UPDATE characters SET gold=0,revision=revision+1 WHERE id=$p0", id);
        using (var peer = Open(fixture))
        {
            peer.Select(prepared: true);
            That(MailboxTcpTests.ReadList(peer.Mailbox.Single()).Summaries.Single().Gold == 756,
                "重登从存档恢复同一邮件额度与已完成主线");
            peer.Send(95, Build(w => { w.Write(1); w.Write(mail); }));
            peer.Expect(1, 95, Build(w => { w.Write((byte)1); w.Write(1); w.Write(mail); w.Write(mail); w.Write(0); }));
            byte[] delta = peer.Expect(0, 14).Body;
            That(U16(delta, 1) == 1 && U16(delta, 3) == 0 && U32(delta, 5) == 0 && U32(delta, 9) == 756,
                "腾出空间后领取发布真实Main0绝对金币，不创建物品实例");
            peer.Barrier();
            peer.Send(96); peer.Expect(1, 96, [1, 0, 0]);
            That(MailboxTcpTests.ReadList(peer.Expect(0, 97).Body).Summaries.Single().Gold == 0 &&
                fixture.Query("SELECT gold FROM characters WHERE id=$p0", id) is [[756L]] &&
                fixture.Query("SELECT gold FROM mailbox_messages WHERE mail_id=$p0", mail) is [[0L]],
                "领取一次同时更新金币和邮件状态，完成记录不重复");
            peer.Send(95, Build(w => { w.Write(1); w.Write(mail); })); peer.Expect(1, 95, [0, 21, 0]);
            peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM pragma_foreign_key_check") is [[0L]], "任务邮件与既有邮箱外键完整");
        }
    }
}
