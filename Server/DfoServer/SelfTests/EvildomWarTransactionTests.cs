using System.Text.Json;
using DfoServer.Game.Accounts;
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

// 真实SQLite故障恢复由领域直接复核，不把断开网络后的测试重连冒充原PendingDeath恢复。
internal static class EvildomWarTransactionTests
{
    internal static void Run(TcpTestResources resources, EvildomWarCatalog catalog, string root)
    {
        int before = Count;
        string path = Path.Combine(root, "evildom-transactions.db");
        var repository = new CharacterRepository(path, resources.Quests);
        var owners = new AccountOwners();
        var leases = new List<AccountOwners.Lease>();
        var all = new List<GameSession>();
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        var rooms = SelfTestCatalogs.Rooms(resources.Archive);
        var kills = SelfTestCatalogs.KillExperience(resources.Archive);
        var incount = DungeonIncountCatalog.Load(resources.Archive);
        try
        {
            foreach (int failureIndex in new[] { 0, 1, 3, 4 })
            {
                var clock = new ConsumableUseSessionTests.FakeClock();
                var parties = new PartyService(repository, clock);
                var store = new EntryStore(repository);
                var service = new PartyDungeonService(parties, store, hiddenDraw: maximum => maximum - 1);
                var sessions = Enumerable.Range(0, 4).Select(index =>
                {
                    var owner = owners.Acquire($"wartransaction{failureIndex}-{index}", () => { });
                    leases.Add(owner);
                    var candidate = CharacterCreation.Prepare($"WarTx{failureIndex}{index}", resources.Characters.Get(0),
                        catalog.WaitingArea, catalog.WaitingArea, resources.Learning.Get);
                    var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                        { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning);
                    repository.Create(owner, candidate with { Level = 95, AcceptedStoryLevel = 95, InitialLearning = learning });
                    var session = new GameSession(repository, resources.Towns, owner, questCatalog: resources.Quests,
                        entry: new(new(2, 41), 54, "[evildom_war]"), timeProvider: clock, incounts: incount);
                    all.Add(session); session.Select(0, resumeTown: true); session.MarkReady(); session.CurrentParty = () => parties.Current(session);
                    return session;
                }).ToArray();
                That(parties.SetInformation(sessions[0], 0, PartySettings.Default with { Kind = 5, KindArgument = 1 }).Accepted,
                    "事务夹具创建真实魔界队伍");
                foreach (var session in sessions.Skip(1))
                {
                    That(parties.Invite(sessions[0], session, 0).Accepted &&
                        parties.Respond(session, sessions[0].Selected!.Id, 0, true).Accepted, "事务夹具真实邀请队员");
                }
                var startRejection = service.StartEvildomWar(sessions[0], catalog);
                That(startRejection is null, "事务夹具按正式服务开战: " + startRejection);
                var battle = sessions[0].ReadEvildomWar()!.Battle;
                var initialView = battle.View;
                uint dungeon = checked((uint)initialView.Dungeons[0].Id);
                That(service.BeginSelection(sessions[0], dungeon).Length == 4, "事务夹具全队正式选图");
                string State() => JsonSerializer.Serialize(sessions.Select(session => session.Selected).ToArray());
                var initial = State();
                PartyDungeonNotice[] Enter() => service.Enter(sessions[0], dungeon, 2, dungeons, rooms, null, null, null, null);
                if (failureIndex == 4)
                {
                    store.AfterEntry = () => clock.Advance(TimeSpan.FromSeconds(catalog.LimitTime));
                    var crossing = Enter();
                    That(crossing.Count(notice => notice.Kind == PartyDungeonNoticeKind.Entry) == 4 &&
                        crossing.Count(notice => notice.Kind == PartyDungeonNoticeKind.EvildomWarUpdated) == 4 &&
                        battle.View.Dungeons[0].State == 1 && sessions.All(session => session.PartyDungeon is not null),
                        "真实入场事务跨截止仍用SQL前冻结许可安装原Run，不遗留已扣费却未绑定的入场");
                    That(sessions.All(session => session.ReadEvildomWar() is null && !session.CanPublishEvildomWar(battle.View)) &&
                        sessions.All(session => session.Selected!.Fatigue!.Used == catalog.NeedFatigue),
                        "跨截止提交只扣一次费用，过期战役不再接受新资格或发布");
                    var replacement = owners.Acquire("wartransaction4-0", () => { });
                    leases.Add(replacement);
                    foreach (var session in sessions[..^1]) session.End();
                    That(battle.View.Dungeons[0].State == 1, "接管和旧Session结束仅释放各自原Run，仍有成员在场不重开图");
                    sessions[^1].End();
                    That(battle.View.Dungeons[0].State == 0, "过期及旧generation收尾仍释放精确原Run，不遗留活动占用");
                    continue;
                }
                Sql($"CREATE TRIGGER reject_war_entry BEFORE UPDATE ON character_dungeon_state WHEN NEW.character_id={sessions[failureIndex].Selected!.Id} BEGIN SELECT RAISE(ABORT,'war entry failure'); END");
                Reject<SqliteException>(() => Enter(), "首/中/末成员入场SQL失败");
                That(State() == initial && ReferenceEquals(initialView, battle.View) && sessions.All(session => session.PartyDungeon is null),
                    "全员入场失败没有费用或战况提前变化，预留被释放");
                Sql("DROP TRIGGER reject_war_entry");
                var entered = Enter();
                That(entered.Count(notice => notice.Kind == PartyDungeonNoticeKind.Entry) == 4 &&
                    entered.Count(notice => notice.Kind == PartyDungeonNoticeKind.EvildomWarUpdated) == 4 &&
                    !sessions[0].CanPublishEvildomWar(initialView), "成功入场全队安装一次战况，旧视图不能发布");
                var instance = sessions[0].PartyDungeon!;
                var plan = entered[0].Scene!.Entry;
                var route = QuestStoryRouteTests.Path(plan.Maze.StaticTopology, plan.StartMap.Position, plan.BossMap.Position);
                bool injected = false;
                foreach (var point in route)
                {
                    foreach (var session in sessions) instance.CompleteLoading(session);
                    var scene = sessions[0].CurrentFormalDungeonScene()!;
                    var monsters = scene.Monsters.Monsters.Where(monster => monster.IsBlocking && monster.CanReportDeath && !monster.IsFriendly)
                        .OrderBy(monster => monster.IsBoss).ToArray();
                    for (int index = 0; index < monsters.Length; index++)
                    {
                        var monster = monsters[index];
                        var observation = FormalDungeonSessionTests.Observation(scene, monster);
                        PartyDungeonNotice[] Kill() => instance.ObserveDeath(sessions[0], monster.ObjectKey,
                            session => session.ObserveFormalDungeonDeath(observation, kills, resources.Growth));
                        if (point == route[^1] && index == monsters.Length - 1)
                        {
                            var fighting = battle.View;
                            Sql($"CREATE TRIGGER reject_war_clear AFTER INSERT ON character_dungeon_growth_receipts WHEN NEW.character_id={sessions[failureIndex].Selected!.Id} BEGIN SELECT RAISE(ABORT,'war clear failure'); END");
                            Reject<SqliteException>(() => Kill(), "首/中/末成员末怪成长SQL失败");
                            That(ReferenceEquals(fighting, battle.View) && fighting.Dungeons[0].State == 1,
                                "未全员确认的通关事实不能提前完成图或解锁王座");
                            Sql("DROP TRIGGER reject_war_clear");
                            var restored = Kill();
                            That(restored.Count(notice => notice.Kind == PartyDungeonNoticeKind.Death) == 4 &&
                                restored.Count(notice => notice.Kind == PartyDungeonNoticeKind.EvildomWarUpdated) == 4 &&
                                battle.View.Revision == fighting.Revision + 1 && battle.View.Dungeons[0].State == 2 &&
                                !sessions[0].CanPublishEvildomWar(fighting), "原PendingDeath恢复后只推进一次，旧战斗中视图失效");
                            var cleared = battle.View;
                            That(Kill().Length == 0 && ReferenceEquals(cleared, battle.View), "重复死亡不再次推进或通知");
                            injected = true;
                        }
                        else Kill();
                    }
                    if (point == route[^1]) break;
                    var next = route[Array.IndexOf(route, point) + 1];
                    That(instance.Move(sessions[0], (byte)next.X, (byte)next.Y, 0).Length == 4, "事务回归沿正式地图通路过房");
                }
                That(injected, "真实末房末怪必须触发SQL失败恢复用例");
                var latest = battle.View;
                sessions[0].End();
                That(!sessions[0].CanPublishEvildomWar(latest), "结束选角后原战役通知失效");
                foreach (var session in sessions.Skip(1)) session.End();

            }
        }
        finally { foreach (var session in all) session.End(); foreach (var lease in leases) lease.Dispose(); }
        Console.WriteLine($"PASS evildom war transactions {Count - before}");
        void Sql(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
    }

    sealed class EntryStore(IPartyDungeonStore store) : IPartyDungeonStore
    {
        public Character[] CommitBeastDungeonLoading(IReadOnlyList<BeastDungeonLoadingReservation> rows) => store.CommitBeastDungeonLoading(rows);
        public Action? AfterEntry { get; set; }
        public PartyDungeonEntryCommit[] CommitPartyDungeonEntry(IReadOnlyList<PartyDungeonReservation> reservations)
        {
            var committed = store.CommitPartyDungeonEntry(reservations);
            AfterEntry?.Invoke();
            return committed;
        }
        public Character[] CommitPartyDungeonExit(IReadOnlyList<PartyDungeonExitReservation> reservations) => store.CommitPartyDungeonExit(reservations);
    }
}
