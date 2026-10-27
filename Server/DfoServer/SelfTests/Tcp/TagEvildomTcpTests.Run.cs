using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class TagEvildomTcpTests
{
    static byte[] EnterTag(TcpPeer peer, uint dungeon)
    {
        // 原生15发完同栈紧跟16，测试不等待15 ACK才开始第二步。
        peer.Send(15, Build(w => { w.Write(dungeon); w.Write((ushort)0); }));
        peer.Send(16, Selection(dungeon));
        peer.Expect(1, 15, [1]); peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]);
        peer.Expect(0, 27, new byte[42]);
        var information = peer.Expect(0, 28).Body;
        That(information.Length == 36 && U32(information, 0) == dungeon && information[7] < 5 &&
            information[8] == 4 && information[9] == 0, "TAG真实36B信息保持已冻结候选ordinal及显式Boss4,0");
        var map = peer.Expect(0, 29).Body;
        That(map[0] == 0 && map[1] == 0, "TAG真实起始房0,0，不发空地图或success stub");
        return map;
    }

    static void ReturnTag(TcpPeer peer)
    {
        peer.Send(42); peer.Expect(1, 42, [1]);
        peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 0]); peer.Expect(0, 24);
        FormalDungeonTcpTests.RefreshedQuests(peer);
        // 真正刷新原登录队首，而不是冒用切换后的备用成为Selected；随后的1394才有可靠primary来源。
        peer.Expect(0, 2); peer.Expect(0, 2); peer.Expect(0, 19);
        peer.Expect(0, 1677); peer.Expect(0, 1678); peer.Expect(0, 1393); peer.Expect(0, 1394); peer.Barrier();
    }

    static string Economy(TcpTestFixture fixture) => fixture.Snapshot("SELECT id,level,experience,gold,revive_coins FROM characters ORDER BY id") +
        fixture.Snapshot("SELECT * FROM character_fatigue ORDER BY character_id") +
        // 邮件附件复用同一物品表，合法通关会新增未入背包的UID；普通经济隔离比较实际槽位中的资产。
        fixture.Snapshot("SELECT i.* FROM character_inventory_items i JOIN character_inventory_slots s USING(item_uid) ORDER BY i.item_uid") +
        fixture.Snapshot("SELECT * FROM character_skill_points ORDER BY character_id,context") +
        fixture.Snapshot("SELECT character_id,capsule_experience,proficiency_level,proficiency_experience FROM character_dungeon_state ORDER BY character_id");

    static string RewardState(TcpTestFixture fixture) => string.Concat(new[]
    {
        "SELECT * FROM characters ORDER BY id", "SELECT * FROM character_fatigue ORDER BY character_id",
        "SELECT * FROM character_inventory_items ORDER BY item_uid", "SELECT * FROM character_inventory_slots ORDER BY item_uid",
        "SELECT * FROM character_skill_points ORDER BY character_id,context", "SELECT * FROM character_dungeon_state ORDER BY character_id",
        "SELECT * FROM account_wallets ORDER BY account", "SELECT * FROM account_currency_balances ORDER BY account,currency_kind",
        "SELECT * FROM account_tag_evildom_configuration ORDER BY account", "SELECT * FROM account_tag_evildom_runs ORDER BY account,instance_id",
        "SELECT * FROM account_tag_evildom_challenges ORDER BY account,day,team_index",
        "SELECT * FROM account_tag_evildom_clearances ORDER BY account,receipt", "SELECT * FROM mailbox_messages ORDER BY mail_id",
        "SELECT * FROM mailbox_attachments ORDER BY attachment_id", "SELECT * FROM mailbox_audit_log ORDER BY audit_id"
    }.Select(sql => fixture.Snapshot(sql)));

    static async Task VerifyClearAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var clock = new DailyBoosterTestClock(DateTimeOffset.Parse("2026-10-27T00:00:00Z"));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "tag-evildom-clear"), resources: resources, clock: clock);
        Character first, second;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var repository = new CharacterRepository(fixture.Database);
            Character Create(string name)
            {
                var plan = CharacterCreation.Prepare(name, resources.Characters.Get(0), resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get);
                return repository.Create(owner, plan with { Level = 95, InitialLearning = SkillPointLedger.Synchronize(0, 0, 95,
                    plan.InitialLearning with { BonusSp = 0, BonusTp = 0 }, resources.Learning) });
            }
            first = Create("TagWinOne"); second = Create("TagWinTwo");
        }
        await fixture.StartAsync();
        int port = ChannelTcpTests.Endpoints(fixture)[46];
        byte[] finalReport = [];
        (uint Id, uint Count)[] shown = [];
        uint deliveredMail = 0;
        long initialMailCount = 0;
        using (var peer = Open(fixture, gamePort: port, channelNumber: 46))
        {
            peer.Select(tagEvildom: [1677, 1678]);
            peer.Send(1662, Setting(0, 1)); peer.Expect(1, 1662, Accepted(Teams(0, 1)));
            ReadConfiguration(peer, Teams(0, 1), Gathering()); ReturnToSelection(peer);
            peer.Select(tagEvildom: [1677, 1678, 1393, 1394]); ExitRoom(peer);
            // 保留正常任务邀请邮件；本次通关只允许在已存在邮箱上新增一封，不把其它业务邮件算成重复奖励。
            initialMailCount = (long)fixture.Query("SELECT count(*) FROM mailbox_messages")[0][0]!;
            string economy = Economy(fixture);
            peer.Send(16, Selection(9608)); peer.Expect(1,16,[0,9,0,0]); peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM account_tag_evildom_runs") is [[0L]], "只有真实双角色仍不能绕过专用15许可直发16");
            var map = EnterTag(peer, 9608);
            for (int room = 0; room < 5; room++)
            {
                FormalDungeonTcpTests.Load(peer); peer.Barrier();
                clock.Advance(TimeSpan.FromSeconds(25));
                if (room == 0)
                {
                    peer.Send(1468, [1]); peer.Expect(1, 1468, [1]); peer.Expect(0, 1407, [1]);
                    That(peer.Id == first.Id, "active1不改变连接登录队首/最终邮件收件角色");
                    peer.Send(39, FormalDungeonTcpTests.Death(65535, peer.Id, 95)); peer.Barrier();
                    That(fixture.Query("SELECT count(*) FROM account_tag_evildom_clearances") is [[0L]], "未知怪物不能制造TAG通关");
                }
                var rows = Enumerable.Range(0, map[18]).Select(index => 19 + 22 * index)
                    .Where(at => map[at + 13] != 9 && map[at + 21] == 0).OrderBy(at => map[at + 13] is 3 or 8).ToArray();
                foreach (int at in rows)
                {
                    finalReport = FormalDungeonTcpTests.Death(U16(map, at + 6), peer.Id, map[at + 12]);
                    peer.Send(39, finalReport); var death = peer.Expect(0, 38).Body;
                    That(U16(death, 0) == U16(map, at + 6) && U16(death, 2) == 0, "TAG怪死确认真实key且无普通经验/地面掉落");
                    if (room == 4 && map[at + 13] is 3 or 8) break;
                    peer.Barrier();
                }
                if (room < 4)
                {
                    var move = new byte[100]; move[0] = checked((byte)(room + 1));
                    peer.Send(45, move); peer.Expect(1, 45, [1]); map = peer.Expect(0, 29).Body;
                }
            }
            var result = peer.Expect(0, 1679).Body;
            That(result.Length == 92 && U32(result, 80) is 2 or 3 && U32(result, 84) == 125_000 && U32(result, 88) == 0,
                "1679只在真实五房终点提交后展示，使用首加载到末Boss的125秒单调耗时");
            shown = Enumerable.Range(0, checked((int)U32(result, 80))).Select(index => (U32(result, index * 8), U32(result, index * 8 + 4))).ToArray();
            var counts = peer.Expect(0, 1678).Body; peer.Expect(0, 99); peer.Barrier();
            That(U32(counts, 0) == 1 && counts.AsSpan(16, 10).SequenceEqual(new byte[] { 2,0,0,0,0,2,1,0,0,0 }),
                "周使用一次、两名真实原名册角色同时保护");
            That(fixture.Query("SELECT first,second,mail_id FROM account_tag_evildom_runs WHERE clearance_receipt IS NOT NULL") is
                [[long primary, long standby, long mail]] && primary == first.Id && standby == second.Id && mail > 0 &&
                fixture.Query("SELECT recipient_id FROM mailbox_messages WHERE mail_id=$p0", mail) is [[long recipient]] && recipient == first.Id &&
                fixture.Query("SELECT count(*) FROM mailbox_messages") is [[long totalMail]] && totalMail == initialMailCount + 1,
                "活动槽1胜利仍只寄给登录队员1，一封邮件与两角色clear同事务");
            deliveredMail = checked((uint)(long)fixture.Query("SELECT mail_id FROM account_tag_evildom_runs WHERE clearance_receipt IS NOT NULL")[0][0]!);
            string committed = RewardState(fixture);
            peer.Send(39, finalReport); peer.Expect(0, 1678); peer.Expect(0, 99); peer.Barrier();
            That(RewardState(fixture) == committed && Economy(fixture) == economy,
                "重复终点不重抽、不重发1679、不增邮件/次数/普通经验；回放counts/alarm允许幂等");
            peer.Send(46, new byte[4]); peer.Barrier();
            That(RewardState(fixture) == committed, "TAG结算不能借普通翻牌产生第二份资产");
            clock.Advance(DateTimeOffset.Parse("2026-10-28T05:40:00+08:00") - clock.GetUtcNow());
            DailyReturn(peer, 3, counts);
            That(fixture.Query("SELECT state FROM account_tag_evildom_challenges") is [[2L]] &&
                fixture.Query("SELECT count(*) FROM account_tag_evildom_clearances") is [[1L]],
                "已提交结果页05:40也主动退场，保持真实完成及奖励，不转成失败罚次");
            peer.Send(15, Build(w => { w.Write(9608u); w.Write((ushort)0); })); peer.Send(16, Selection(9608));
            peer.Expect(1, 15, [0,1,0]); peer.Expect(0,1709); peer.Expect(1,16,[0,9,0,0]); peer.Barrier();
            peer.Send(1663); peer.Expect(1,1663,Accepted(Teams(0,1)));
            peer.Expect(0,1677,Teams(0,1)); peer.Expect(0,1678,counts); peer.Barrier();
            That(fixture.Query("SELECT first0,second0 FROM account_tag_evildom_configuration") is [[long keptFirst,long keptSecond]] &&
                keptFirst == first.Id && keptSecond == second.Id, "真实通关后初始化保留当日受保护双角色，不靠重置清除额度");
        }
        using (var peer = Open(fixture, gamePort: port, channelNumber: 46))
        {
            peer.Select(tagEvildom: [1677,1678,1393,1394]);
            That(fixture.Query("SELECT count(*) FROM mailbox_messages") is [[long totalMail]] && totalMail == initialMailCount + 1 &&
                fixture.Query("SELECT count(*) FROM account_tag_evildom_clearances") is [[1L]],
                "真实重连仅恢复持久邮件/保护，不把遗留admitted或旧终点当新通关");
            var inbox = MailboxTcpTests.ReadList(peer.Mailbox.Single());
            var grants = inbox.Summaries.Where(row => row.MailId == deliveredMail).ToArray();
            That(grants.Select(row => ((uint)row.ItemId, U32(row.Core, 6))).SequenceEqual(shown),
                "真实邮箱各附件完整核心与1679展示同源，结算定义卡不成为可领取物品");
            peer.Send(95, Build(w => { w.Write(1); w.Write(deliveredMail); }));
            peer.Expect(1, 95, Build(w => { w.Write((byte)1); w.Write(grants.Length);
                foreach (var grant in grants) { w.Write(grant.MailId); w.Write(grant.ClaimId); w.Write(0); } }));
            peer.Expect(0, 14); peer.Barrier();
            foreach (var item in shown)
                That(fixture.Query("SELECT sum(json_extract(i.item_core,'$.Value')) FROM character_inventory_items i " +
                    "JOIN character_inventory_slots s ON s.item_uid=i.item_uid WHERE s.character_id=$p0 AND json_extract(i.item_core,'$.ItemId')=$p1",
                    first.Id, item.Id) is [[long count]] && count == item.Count, "通关奖励经真实邮件领取进入队首背包且数量准确");
            string claimed = RewardState(fixture);
            peer.Send(95, Build(w => { w.Write(1); w.Write(deliveredMail); })); peer.Expect(1, 95, [0,21,0]); peer.Barrier();
            That(RewardState(fixture) == claimed, "重复领取原通关邮件不会再发物品，也不清除两角色通关额度");
        }
    }
}
