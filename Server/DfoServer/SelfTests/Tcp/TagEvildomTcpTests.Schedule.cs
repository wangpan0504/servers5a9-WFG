using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.Game.TagEvildom;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class TagEvildomTcpTests
{
    static async Task VerifyDailyScheduleAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var clock = new DailyBoosterTestClock(DateTimeOffset.Parse("2026-10-27T05:29:59+08:00"));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "tag-evildom-schedule"), resources: resources, clock: clock);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var repository = new CharacterRepository(fixture.Database);
            Character Create(string name)
            {
                var plan = CharacterCreation.Prepare(name, resources.Characters.Get(0), resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get);
                return repository.Create(owner, plan with { Level = 95, InitialLearning = SkillPointLedger.Synchronize(0, 0, 95,
                    plan.InitialLearning with { BonusSp = 0, BonusTp = 0 }, resources.Learning) });
            }
            var first = Create("TagTimeOne"); var second = Create("TagTimeTwo");
            repository.CommitTagEvildomConfiguration(owner, TagEvildomConfiguration.Empty,
                TagEvildomConfiguration.Empty with { Teams = [new(first.Id, second.Id), default, default] });
        }
        await fixture.StartAsync();
        int port = ChannelTcpTests.Endpoints(fixture)[46];
        using var peer = Open(fixture, gamePort: port, channelNumber: 46);
        peer.Select(tagEvildom: [1677, 1678, 1393, 1394]); ExitRoom(peer, republish: true);
        string Assets() => fixture.Snapshot("SELECT id,level,experience,gold,revive_coins FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid");
        string economy = Assets();
        peer.Send(15, Build(w => { w.Write(9608u); w.Write((ushort)0); }));
        peer.Expect(1, 15, [1]); peer.Expect(0, 3); peer.Expect(0, 27);
        clock.Advance(TimeSpan.FromSeconds(1));
        peer.Send(16, Selection(9608)); peer.Expect(1, 16, [0, 9, 0, 0]); peer.Barrier();
        That(fixture.Query("SELECT count(*) FROM account_tag_evildom_challenges") is [[0L]], "15跨05:30到16不得落入已关闭副本");
        ReturnToSelection(peer); peer.Select(tagEvildom: [1677, 1678, 1393, 1394]); ExitRoom(peer, republish: true);
        // 重选后沿原房间出口到中央竞技场，06点重开仍走原生15/16。
        clock.Advance(TimeSpan.FromMinutes(30));
        peer.ExpectUnusedDailyBoosterReset();
        // 新业务日重选仍按原生入城安装资料，退场本身保留已安装双角色。
        ReturnToSelection(peer);
        peer.Select(tagEvildom: [1677, 1678, 1393, 1394]);
        ExitRoom(peer, republish: true);
        EnterTag(peer, 9608);
        int ordinal = Convert.ToInt32(fixture.Query("SELECT maze_ordinal FROM account_tag_evildom_challenges")[0][0]);
        ReturnTag(peer);
        EnterTag(peer, 400000095);
        That(fixture.Query("SELECT maze_ordinal,dungeon_id FROM account_tag_evildom_challenges") is [[long same, 400000095L]] && same == ordinal,
            "实际失败退场再改难度仍使用本日同队对手");
        // 保持Loading，不发任何请求触发到期；后台任务必须自己提交回城并写出通知。
        clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(39) + TimeSpan.FromSeconds(59));
        peer.Barrier();
        clock.Advance(TimeSpan.FromSeconds(1));
        DailyReturn(peer, 4);
        That(fixture.Query("SELECT state FROM account_tag_evildom_challenges") is [[1L]] && Assets() == economy &&
            fixture.Query("SELECT count(*) FROM account_tag_evildom_clearances") is [[0L]],
            "Loading到05:40无新包也主动退场，不扣普通资产、不造通关或罚次");
        peer.Send(15, Build(w => { w.Write(9608u); w.Write((ushort)0); })); peer.Send(16, Selection(9608));
        peer.Expect(1, 15, [0, 1, 0]); peer.Expect(0, 1709); peer.Expect(1, 16, [0, 9, 0, 0]); peer.Barrier();
        clock.Advance(TimeSpan.FromMinutes(20));
        peer.ExpectUnusedDailyBoosterReset();
        ReturnToSelection(peer);
        peer.Select(tagEvildom: [1677, 1678, 1393, 1394]);
        ExitRoom(peer, republish: true);
        EnterTag(peer, 9608); FormalDungeonTcpTests.Load(peer); peer.Barrier();
        peer.Send(1469, [0]); peer.Expect(0, 1407, [1]);
        peer.Send(1469, [1]); peer.Expect(1, 1469, [1]); peer.Barrier();
        clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(40));
        DailyReturn(peer, 3);
        That(fixture.Query("SELECT count(*) FROM account_tag_evildom_challenges WHERE state=0") is [[0L]],
            "双角色全灭实例也由同一到期任务真实回城");
        clock.Advance(TimeSpan.FromMinutes(20));
        peer.ExpectUnusedDailyBoosterReset();
        ReturnToSelection(peer);
        peer.Select(tagEvildom: [1677, 1678, 1393, 1394]);
        ExitRoom(peer, republish: true);
        EnterTag(peer, 9608); ReturnTag(peer);
        ReturnToSelection(peer); peer.Select(tagEvildom: [1677, 1678, 1393, 1394]); peer.Barrier();
        clock.Advance(TimeSpan.FromDays(1)); peer.ExpectUnusedDailyBoosterReset(); peer.Barrier();
        That(Assets() == economy && fixture.Query("SELECT count(*) FROM account_tag_evildom_challenges WHERE state=0") is [[0L]],
            "已明确退场和换角取消旧截止，旧定时器不能向新选角发回城包");
    }

    static void DailyReturn(TcpPeer peer, int mode, byte[]? status = null)
    {
        peer.Expect(0, 1709); peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 0]); peer.Expect(0, 24);
        FormalDungeonTcpTests.RefreshedQuests(peer);
        peer.Expect(0, 1678, status ?? Gathering(mode)); peer.Barrier();
    }
}
