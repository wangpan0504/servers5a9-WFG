using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EvildomWarGuideTests
{
    internal static void Run(TcpTestResources resources, EvildomWarCatalog catalog, string root)
    {
        int before = Count;
        string path = Path.Combine(root, "war-guide.db");
        var repository = new CharacterRepository(path, resources.Quests);
        new AuthenticationService(new AuthenticationRepository(path)).Create("warguide", "fixture-password");
        var owners = new AccountOwners();
        using var owner = owners.Acquire("warguide", () => { });
        var clock = new ConsumableUseSessionTests.FakeClock();
        clock.Advance(DateTimeOffset.Parse("2026-10-29T05:59:59+08:00") - clock.GetUtcNow());
        var parties = new PartyService(repository, clock, guideStore: repository);
        var service = new PartyDungeonService(parties, repository, guideStore: repository);
        var guide = PartySettings.Default with { Kind = 5, KindArgument = 2 };
        var sessions = new List<GameSession>();
        var roles = Enumerable.Range(0, 4).Select(index =>
        {
            var candidate = CharacterCreation.Prepare("WarGuide" + index, resources.Characters.Get(0), catalog.WaitingArea,
                catalog.WaitingArea, resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning);
            return repository.Create(owner, candidate with { Level = 95, AcceptedStoryLevel = 95, InitialLearning = learning });
        }).ToArray();
        try
        {
            var first = Session(0);
            Sql("CREATE TRIGGER reject_guide BEFORE INSERT ON account_evildom_war_guides BEGIN SELECT RAISE(ABORT,'guide failure'); END");
            Reject<SqliteException>(() => parties.SetInformation(first, 0, guide), "绑定SQL失败不安装引导队伍");
            That(parties.Current(first) is null && Scalar("SELECT count(*) FROM account_evildom_war_guides") == 0 &&
                repository.LoadCharacter(owner, roles[0].Slot).Revision == first.Selected!.Revision,
                "失败不造队伍、不占名额、不修改角色资产revision");
            Sql("DROP TRIGGER reject_guide");
            That(parties.SetInformation(first, 0, guide).Accepted && parties.SetInformation(first, 1, guide).Accepted &&
                Scalar("SELECT count(*) FROM account_evildom_war_guides") == 1, "创建引导队伍即绑定，重复改名不重复占用");
            parties.Detach(first); first.End();
            var second = Session(1);
            That(parties.SetInformation(second, 0, guide).Accepted && Scalar("SELECT count(*) FROM account_evildom_war_guides") == 2,
                "同账号第二个角色可以绑定");
            parties.Detach(second); second.End();
            repository.Delete(owner, repository.LoadDeletionTarget(owner, roles[0].Slot));
            That(Scalar("SELECT count(*) FROM account_evildom_war_guides") == 2,
                "删除已绑定角色不返还本周名额，不阻碍正常删角事务");
            var third = Session(2);
            That(!parties.SetInformation(third, 0, guide).Accepted && parties.Current(third) is null &&
                Scalar("SELECT count(*) FROM account_evildom_war_guides") == 2, "第三个角色不能创建引导队伍，不留下幽灵名册");
            foreach (ushort mode in new ushort[] { 0, 1 })
            {
                That(parties.SetInformation(third, 0, guide with { KindArgument = mode }).Accepted &&
                    service.StartEvildomWar(third, catalog) is null, "绑定上限不限制普通困难开战");
                parties.Detach(third); third.End();
                third = Session(2);
            }
            // 旧路径或恢复的名册也要在开战处复验，不能绕过建队绑定。
            var oldParties = new PartyService(repository, clock);
            var legacy = Session(3, oldParties);
            That(oldParties.SetInformation(legacy, 0, guide).Accepted &&
                new PartyDungeonService(oldParties, repository, guideStore: repository).StartEvildomWar(legacy, catalog) == "guide-account-limit",
                "旧名册不能绕过账号引导上限");
            oldParties.Detach(legacy); legacy.End();
            var oldWeek = DungeonIncountPolicy.Period(clock.GetUtcNow()).Week;
            clock.Advance(TimeSpan.FromSeconds(1));
            Sql("CREATE TRIGGER reject_guide_reset BEFORE INSERT ON account_evildom_war_guides BEGIN SELECT RAISE(ABORT,'guide reset rollback'); END");
            Reject<SqliteException>(() => parties.SetInformation(third, 0, guide), "跨周绑定故障同时回滚旧周清理");
            That(parties.Current(third) is null && Scalar($"SELECT count(*) FROM account_evildom_war_guides WHERE week={oldWeek}") == 2,
                "跨周SQL失败保留完整旧账本，不造未持久绑定的引导队伍");
            Sql("DROP TRIGGER reject_guide_reset");
            That(DungeonIncountPolicy.Period(clock.GetUtcNow()).Week != oldWeek && parties.SetInformation(third, 0, guide).Accepted &&
                Scalar("SELECT count(*) FROM account_evildom_war_guides") == 1, "周四06:00按业务周惰性清除旧绑定，第三角色获得本周名额");
            parties.Detach(third); third.End();
            repository = new CharacterRepository(path, resources.Quests);
            var reopened = Session(2);
            That(parties.SetInformation(reopened, 0, guide).Accepted && Scalar("SELECT count(*) FROM account_evildom_war_guides") == 1,
                "重开仓储保留本周绑定且幂等");
            var replacement = owners.Acquire(owner.Account, () => { });
            using (replacement)
                Reject<InvalidOperationException>(() => repository.TryBindEvildomWarGuide(owner, roles[0], oldWeek), "旧owner不能占用或清空新账号绑定");
        }
        finally { foreach (var session in sessions) session.End(); }
        Console.WriteLine($"PASS evildom war guide {Count - before}");

        GameSession Session(int index, PartyService? selectedParties = null)
        {
            var group = selectedParties ?? parties;
            var session = new GameSession(repository, resources.Towns, owner, questCatalog: resources.Quests,
                entry: new(new(2, 41), 54, "[evildom_war]"), timeProvider: clock,
                incounts: DungeonIncountCatalog.Load(resources.Archive));
            sessions.Add(session); session.Select(roles[index].Slot, resumeTown: true); session.MarkReady();
            session.CurrentParty = () => group.Current(session);
            return session;
        }
        void Sql(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        long Scalar(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return (long)command.ExecuteScalar()!; }
        SqliteConnection Open() { var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open(); return db; }
    }
}
