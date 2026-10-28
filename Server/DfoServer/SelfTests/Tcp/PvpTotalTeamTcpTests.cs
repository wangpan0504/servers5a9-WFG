using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Pvp;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class PvpTotalTeamTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var f = new TcpTestFixture(source, Path.Combine(root, "pvp-total-team"), resources: resources);
        var repository = new CharacterRepository(f.Database, resources.Quests);
        var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
        ushort first, second, third;
        using (var owner = new AccountOwners().Acquire("total-tcp", () => { }))
        {
            first = Seed(owner, "TotalFirst", 1).Id;
            var hole = Seed(owner, "TotalHole", 2);
            second = Seed(owner, "TotalSecond", 2).Id;
            third = Seed(owner, "TotalThird", 3).Id;
            repository.Delete(owner, repository.LoadDeletionTarget(owner, hole.Slot));
        }
        using (var owner = new AccountOwners().Acquire("total-tcp-other", () => { }))
            for (byte grow = 1; grow <= 3; grow++) Seed(owner, "TotalOther" + grow, grow);
        await f.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(f);
        byte pvp = ChannelTcpTests.PvpChannelNumber(f);
        using (var peer = Open(f, "total-tcp-other", gamePort: endpoints[pvp], channelNumber: pvp))
        {
            peer.Send(874, NameBody("编队甲")); peer.Expect(1, 874, [1]);
            That(f.Query("SELECT count(*) FROM account_pvp_total_teams") is [[0L]], "874名称可用不预留名称或创建空队");
        }
        string saved;
        using (var peer = Open(f, "total-tcp", gamePort: endpoints[47], channelNumber: 47))
        {
            peer.Send(873, TeamBody("编队甲", 2, 0, 1)); peer.Expect(1, 873, [0, 2, 0]);
            peer.Select(prepared: true); peer.Barrier();
            peer.Send(1426); peer.SendBarrier(); peer.Batch((0, 23), (0, 24), (1, 1347));
            That(f.Query("SELECT town,area FROM characters WHERE id=$p0", first) is [[10L, 0L]],
                "无双频道普通角色离开共享房间后实际进入决斗地区10/0");
            string assets = f.Snapshot();
            peer.Send(874, NameBody("编队甲")); peer.Expect(1, 874, [1]);
            peer.Send(873, TeamBody("", 2, 0, 1)); peer.Expect(1, 873, [0, 4, 0]);
            peer.Send(873, TeamBody("bad!", 2, 0, 1)); peer.Expect(1, 873, [0, 159, 0]);
            peer.Send(873, TeamBody("编队甲", 2, 0, 0)); peer.Expect(1, 873, [0, 2, 0]);
            peer.Send(873, TeamBody("编队甲", 2, 0, 3)); peer.Expect(1, 873, [0, 2, 0]);
            peer.Send(873, TeamBody("编队甲", 2, 0, 1)); peer.Expect(1, 873, [1, 2, 0, 1]);
            peer.Barrier();
            That(f.Query("SELECT revision,name,first_id,second_id,third_id FROM account_pvp_total_teams WHERE account='total-tcp'")
                is [[1L, "编队甲", long a, long b, long c]] && a == third && b == first && c == second,
                "873回传原列表索引，有持久槽空洞时仍按已发布顺序保存稳定ID");
            saved = State();
            peer.Send(873, TeamBody("编队甲", 2, 0, 1)); peer.Expect(1, 873, [1, 2, 0, 1]);
            peer.Send(874, NameBody("编队甲")); peer.Expect(1, 874, [1]);
            peer.Send(873, TeamBody("编队乙", 2, 0, 1)); peer.Expect(1, 873, [0, 159, 0]);
            peer.Barrier();
            That(State() == saved && f.Snapshot() == assets, "重复登记不推进队伍版本，改名不能绕过费用且编队不改角色资产");
            peer.Send(873, TeamBody("编队甲", 1, 2, 0)); peer.Expect(1, 873, [1, 1, 2, 0]); peer.Barrier();
            That(f.Query("SELECT revision,first_id,second_id,third_id FROM account_pvp_total_teams WHERE account='total-tcp'")
                is [[2L, long d, long e, long g]] && d == second && e == third && g == first,
                "873重排使用同一队伍身份并推进自己的版本");
            saved = State();
            f.Sql("CREATE TRIGGER total_team_wire_fail AFTER UPDATE ON account_pvp_total_teams BEGIN SELECT RAISE(ABORT,'Injected total team failure'); END");
            peer.Send(873, TeamBody("编队甲", 2, 0, 1)); peer.Closed();
            That(State() == saved && f.Snapshot() == assets, "873末段写失败关闭连接，阵容、队名及版本原子回滚");
            f.Sql("DROP TRIGGER total_team_wire_fail");
        }
        using (var peer = Open(f, "total-tcp-other", gamePort: endpoints[pvp], channelNumber: pvp))
        {
            peer.Select(prepared: true); peer.Barrier();
            peer.Send(874, NameBody("编队甲")); peer.Expect(1, 874, [0, 3, 0]);
            peer.Send(874, NameBody("bad!")); peer.Expect(1, 874, [0, 159, 0]);
            peer.Send(873, TeamBody("编队甲", 0, 1, 2)); peer.Expect(1, 873, [0, 1, 0]); peer.Barrier();
            That(f.Query("SELECT count(*) FROM account_pvp_total_teams WHERE account='total-tcp-other'") is [[0L]] &&
                State() == saved, "另一账号虽曾名称预检通过，真正登记仍拒绝重名，不改原队伍");
        }
        await f.StopAsync(); await f.StartAsync("restart");
        endpoints = ChannelTcpTests.Endpoints(f);
        using (var peer = Open(f, "total-tcp", gamePort: endpoints[47], channelNumber: 47))
        {
            That(peer.LoginEndpoint[3] == 8, "总力编队重登无双频道保持真实类型8");
            peer.Select(prepared: true, pvpTotalTeam: TeamBody("编队甲", 1, 2, 0)); peer.Barrier();
            peer.Send(873, TeamBody("编队甲", 1, 2, 0)); peer.Expect(1, 873, [1, 1, 2, 0]); peer.Barrier();
            That(State() == saved, "真实重启及重登回放原队伍和阵容，不另建身份或增加版本");
            peer.Send(875, RenameBody("编队甲", ushort.MaxValue)); peer.Expect(1, 875, [1, ..NameBody("编队甲")]);
            peer.Barrier();
            string renameAssets = f.Snapshot();
            foreach (var (name, slot) in new[] { ("bad!", (ushort)121), ("更名甲", ushort.MaxValue), ("更名甲", (ushort)122) })
            {
                peer.Send(875, RenameBody(name, slot)); peer.Expect(1, 875, [0, 0, 0]); peer.Barrier();
                That(f.Snapshot() == renameAssets && State() == saved, "875拒绝非法名称、查找失败与空源格，零资产变化");
            }
            f.Sql("CREATE TRIGGER total_rename_fail AFTER UPDATE ON account_pvp_total_teams BEGIN SELECT RAISE(ABORT,'Injected rename failure'); END");
            peer.Send(875, RenameBody("更名甲", 121)); peer.Closed();
            That(f.Snapshot() == renameAssets && State() == saved, "875改名末段失败时队名与扣券审计一起回滚");
            f.Sql("DROP TRIGGER total_rename_fail");
        }
        using (var peer = Open(f, "total-tcp", gamePort: endpoints[47], channelNumber: 47))
        {
            peer.Select(prepared: true, pvpTotalTeam: TeamBody("编队甲", 1, 2, 0)); peer.Barrier();
            peer.Send(875, RenameBody("更名甲", 121)); peer.Expect(1, 875, [1, ..NameBody("更名甲")]);
            peer.Expect(0, 14); peer.Barrier();
            using var reader = new AccountOwners().Acquire("total-tcp", () => { });
            var committed = repository.LoadCharacter(reader, 0);
            That(committed.Inventory.Find(new(InventoryListType.Main, 121))?.Core.Count == 1 &&
                repository.ReadPvpTotalTeam(reader) is { Name: "更名甲", Revision: 3 } team &&
                team.Characters.SequenceEqual(new ushort?[] { second, third, first }),
                "875实际ACK后以14投影剩余券，保留队伍及三角色顺序");
            string once = f.Snapshot(); string teamOnce = State();
            peer.Send(875, RenameBody("更名甲", ushort.MaxValue)); peer.Expect(1, 875, [1, ..NameBody("更名甲")]);
            peer.Barrier();
            That(f.Snapshot() == once && State() == teamOnce, "875同名重放不重复扣券或发布库存变化");
            peer.Send(875, RenameBody("更名乙", 121)); peer.Expect(1, 875, [1, ..NameBody("更名乙")]);
            peer.Expect(0, 14); peer.Barrier();
            That(repository.LoadCharacter(reader, 0).Inventory.Find(new(InventoryListType.Main, 121)) is null &&
                repository.ReadPvpTotalTeam(reader) is { Name: "更名乙", Revision: 4 }, "875最后一张删槽并提交第二次更名");
            string exhausted = f.Snapshot();
            peer.Send(875, RenameBody("更名丙", 121)); peer.Expect(1, 875, [0, 0, 0]); peer.Barrier();
            That(f.Snapshot() == exhausted, "875耗尽后不能凭旧槽继续免费改名");
        }

        string State() => f.Snapshot("SELECT * FROM account_pvp_total_teams WHERE account='total-tcp'");
        Character Seed(AccountOwners.Lease owner, string name, byte grow)
        {
            var seed = CharacterCreation.Prepare(name, resources.Characters.Get(0), resources.Towns.InitialReturn,
                resources.Towns.InitialReturn, resources.Learning.Get);
            var inventory = name == "TotalFirst" ? seed.Inventory with
            { Items = seed.Inventory.Items.Add(new(0, new(InventoryListType.Main, 121), new(PvpTotalTeamRename.CardId, ItemKind.Material, 2))) } : seed.Inventory;
            return repository.Create(owner, seed with { Level = 85, GrowType = grow, AcceptedStoryLevel = 85, Inventory = inventory,
                InitialLearning = SkillPointLedger.Synchronize(0, grow, 85, seed.InitialLearning with
                { Experience = growth.GetLevelStartExperience(85) }, resources.Learning) });
        }
    }

    static byte[] NameBody(string name) => Build(writer =>
    {
        byte[] bytes = CharacterName.Encoding.GetBytes(name);
        writer.Write((uint)bytes.Length); writer.Write(bytes);
    });
    static byte[] TeamBody(string name, byte first, byte second, byte third) =>
        [..NameBody(name), first, second, third];
    static byte[] RenameBody(string name, ushort slot) => [..NameBody(name), (byte)slot, (byte)(slot >> 8)];
}
