using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using DfoServer.Testing;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EvildomWarHiddenTransactionTests
{
    internal static void Run(TcpTestResources resources, EvildomWarCatalog catalog, string root)
    {
        int before = Count;
        var dungeons = SelfTestCatalogs.Dungeons(resources.Archive);
        var rooms = SelfTestCatalogs.Rooms(resources.Archive);
        var kills = SelfTestCatalogs.KillExperience(resources.Archive);
        var policy = new EvildomWarRewardPolicy(catalog, resources.ItemResources.Items, resources.ItemResources.Boosters);
        foreach (byte mode in new byte[] { 0, 1, 2 })
        foreach (int count in mode == 2 ? new[] { 1 } : new[] { 1, 4 })
        {
            string path = Path.Combine(root, $"war-hidden-{mode}-{count}.db");
            var repository = new CharacterRepository(path, resources.Quests);
            var owners = new AccountOwners();
            var leases = new List<AccountOwners.Lease>();
            var all = new List<GameSession>();
            var clock = new ConsumableUseSessionTests.FakeClock();
            var parties = new PartyService(repository, clock);
            int draws = 0;
            var service = new PartyDungeonService(parties, repository, repository, hiddenDraw: _ => { draws++; return 0; });
            var sessions = Enumerable.Range(0, count).Select(index =>
            {
                var owner = owners.Acquire($"warhidden{mode}-{count}-{index}", () => { }); leases.Add(owner);
                var candidate = CharacterCreation.Prepare($"WarHidden{index}", resources.Characters.Get(0), catalog.WaitingArea,
                    catalog.WaitingArea, resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                    { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning);
                repository.Create(owner, candidate with { Level = 95, AcceptedStoryLevel = 95, InitialLearning = learning });
                return Session(owner);
            }).ToArray();
            try
            {
                That(parties.SetInformation(sessions[0], 0, PartySettings.Default with { Kind = 5, KindArgument = mode }).Accepted,
                    "隐藏夹具使用本模式真实队伍");
                foreach (var session in sessions.Skip(1))
                    That(parties.Invite(sessions[0], session, 0).Accepted &&
                        parties.Respond(session, sessions[0].Selected!.Id, 0, true).Accepted, "隐藏夹具真实邀请");
                That(service.StartEvildomWar(sessions[0], catalog) is null && draws == 2, "每场只抽一次隐藏路线");
                var originalView = sessions[0].ReadEvildomWar()!.Battle.View;
                var hidden = originalView.Hidden!;
                int hiddenIndex = Enumerable.Range(0, originalView.Dungeons.Length).Single(index => originalView.Dungeons[index].Id == hidden.StageDungeon);
                That(hidden.Dungeon == catalog.HiddenDungeons.Single(row => row.Mode == mode).Id && originalView.Dungeons.Length == 5,
                    "隐藏副本对应真实模式且不增加面板行");
                That(service.BeginSelection(sessions[0], hidden.StageDungeon).Length == count, "选择原面板前置图");
                Sql("CREATE TRIGGER reject_hidden_entry BEFORE INSERT ON character_evildom_war_seats BEGIN SELECT RAISE(ABORT,'hidden entry rollback'); END");
                Reject<SqliteException>(() => Enter(), "隐藏入场SQL故障整体回滚");
                That(sessions.All(session => session.Selected!.Fatigue!.Used == 0 && session.PartyDungeon is null) &&
                    Scalar("SELECT count(*) FROM evildom_war_battles") == 0, "隐藏入场失败不提前扣费、持久化战况或安装实例");
                Sql("DROP TRIGGER reject_hidden_entry");
                CheckEntry(Enter());
                foreach (var session in sessions) { parties.Detach(session); session.End(); }
                parties = new PartyService(repository, clock);
                service = new PartyDungeonService(parties, repository, repository,
                    hiddenDraw: _ => throw new InvalidOperationException("Recovery must not draw"));
                sessions = leases.Take(count).ToArray().Select(previous =>
                {
                    var owner = owners.Acquire(previous.Account, () => { }); leases.Add(owner);
                    var session = Session(owner);
                    service.RecoverEvildomWarAsync(session, catalog).GetAwaiter().GetResult();
                    return session;
                }).ToArray();
                That(sessions.All(session => session.ReadEvildomWar()!.Battle.View.Hidden == hidden) && draws == 2 &&
                    sessions[0].ReadEvildomWar()!.Battle.View.Dungeons[hiddenIndex].State == 0,
                    "全员断线后重建真实队伍，原隐藏路线不重抽且未清图恢复可选");
                That(service.BeginSelection(sessions[0], hidden.StageDungeon).Length == count, "重连后仍选择原面板图");
                CheckEntry(Enter());
                var battle = sessions[0].ReadEvildomWar()!.Battle;
                That(sessions.All(session => session.CommitEvildomWarHiddenReward(policy, repository) is null &&
                    repository.LoadUnrewardedEvildomWarHiddenCompletions(leases[count + Array.IndexOf(sessions, session)], session.Selected!.Id).IsEmpty),
                    "隐藏真实物件终点之前不能领奖");
                var instance = sessions[0].PartyDungeon!;
                var plan = sessions[0].CurrentFormalDungeonScene()!.Entry;
                var route = QuestStoryRouteTests.Path(plan.Maze.StaticTopology, plan.StartMap.Position, plan.BossMap.Position);
                foreach (var point in route)
                {
                    foreach (var session in sessions) instance.CompleteLoading(session);
                    var scene = sessions[0].CurrentFormalDungeonScene()!;
                    foreach (var monster in scene.Monsters.Monsters.Where(row => row.IsBlocking && row.CanReportDeath && !row.IsFriendly)
                        .OrderBy(row => row.IsBoss).ToArray())
                    {
                        var observation = FormalDungeonSessionTests.Observation(scene, monster);
                        var notices = instance.ObserveDeath(sessions[0], monster.ObjectKey,
                            session => session.ObserveFormalDungeonDeath(observation, kills, resources.Growth));
                        That(notices.All(notice => notice.Kind != PartyDungeonNoticeKind.EvildomWarUpdated), "隐藏怪物不能代替物件通关");
                    }
                    if (point == route[^1]) break;
                    var next = route[Array.IndexOf(route, point) + 1];
                    That(instance.Move(sessions[0], (byte)next.X, (byte)next.Y, 0).Length == count, "隐藏真实迷宫全队过房");
                }
                var target = plan.Maze.ClearConditions.Single();
                That(target is { Type: "destroy object", Count: 1 } && battle.View.Dungeons[hiddenIndex].State == 1 &&
                    sessions.All(session => session.Phase == SessionPhase.Dungeon), "真实物件缺失时仍在战斗且不开放结算");
                string fingerprint = new('A', 64);
                PartyDungeonNotice[] Observe() => instance.ObservePassive(sessions[0], (uint)target.TargetId, fingerprint,
                    session => session.ObserveFormalPassiveDeath((uint)target.TargetId, true, fingerprint, rooms));
                Sql($"CREATE TRIGGER reject_hidden_clear AFTER INSERT ON character_dungeon_scene_clear_receipts WHEN NEW.character_id={sessions[^1].Selected!.Id} BEGIN SELECT RAISE(ABORT,'hidden clear rollback'); END");
                Reject<SqliteException>(() => Observe(), "最后成员物件事务失败不提前推进全队战况");
                That(battle.View.Dungeons[hiddenIndex].State == 1, "部分成员物件事实不能完成原面板图");
                Sql("DROP TRIGGER reject_hidden_clear");
                Sql("CREATE TRIGGER reject_hidden_checkpoint BEFORE UPDATE ON evildom_war_battles BEGIN SELECT RAISE(ABORT,'hidden checkpoint rollback'); END");
                Reject<SqliteException>(() => Observe(), "物件全员提交后检查点失败仍可重试");
                That(battle.View.Dungeons[hiddenIndex].State == 1 && battle.HiddenCompletion is null &&
                    Scalar("SELECT count(*) FROM character_dungeon_scene_clear_receipts") == count,
                    "检查点失败保留原战况与全员真实物件回执");
                Sql("DROP TRIGGER reject_hidden_checkpoint");
                var completed = Observe();
                That(completed.Count(notice => notice.Kind == PartyDungeonNoticeKind.EvildomWarUpdated) == count &&
                    battle.View.Dungeons[hiddenIndex].State == 2 && battle.View.Dungeons[^1].State == 3 &&
                    sessions.All(session => session.CurrentFormalDungeonScene()!.Entry.DungeonId == hidden.Dungeon &&
                        session.Phase == SessionPhase.Settlement), "物件重试只推进原前置图一次，真实Run可结算且王座仍封锁");
                long revision = battle.View.Revision;
                That(Observe().Count(notice => notice.Kind == PartyDungeonNoticeKind.EvildomWarUpdated) == count &&
                    battle.View.Revision == revision && Scalar("SELECT count(*) FROM character_dungeon_scene_clear_receipts") == count &&
                    Scalar("SELECT count(*) FROM character_evildom_war_rewards") == 0 &&
                    sessions.All(session => session.Selected!.Fatigue!.Used == 30), "重复物件仅重同步原隐藏事实，不推进战况、发王座奖或再扣费");
                int amount = mode == 2 ? 9 : 15;
                var reward = policy.PrepareHidden(mode);
                That(reward.Items is [{ Group: 1, ItemId: 10310786 }] && reward.Items[0].Count == amount &&
                    policy.Prepare(mode, sessions[0].Selected!, _ => 0).Items.All(row => row.Group != 1),
                    "资源固定符文原石仅为本服隐藏额外奖，王座不重复包含该组");
                var hiddenSnapshot = new EvildomWarRewardSnapshot(battle.HiddenCompletion!, reward,
                    policy.Materialize(reward, battle.HiddenCompletion!.Created)) { Rule = EvildomWarRewardPolicy.HiddenRule };
                Reject<InvalidOperationException>(() => repository.CommitEvildomWarHiddenReward(leases[^1], sessions[0].Selected!.Id,
                    hiddenSnapshot with { Completion = hiddenSnapshot.Completion with { BattleId = Guid.NewGuid() } }),
                    "隐藏奖励拒绝其他账号或伪造战役");
                var reopened = new CharacterRepository(path, resources.Quests);
                Sql("CREATE TRIGGER reject_hidden_mail BEFORE INSERT ON mailbox_messages BEGIN SELECT RAISE(ABORT,'hidden mail rollback'); END");
                Reject<SqliteException>(() => sessions[0].CommitEvildomWarHiddenReward(policy, reopened), "隐藏邮件故障可重试");
                That(Scalar("SELECT count(*) FROM character_evildom_war_hidden_rewards") == 0 &&
                    Scalar("SELECT count(*) FROM mailbox_messages") == 0 && battle.HiddenCompletion is not null,
                    "隐藏邮件和独立回执整批回滚，原持久物件事实保留");
                Sql("DROP TRIGGER reject_hidden_mail");
                Sql("CREATE TRIGGER reject_hidden_receipt BEFORE INSERT ON character_evildom_war_hidden_rewards BEGIN SELECT RAISE(ABORT,'hidden receipt rollback'); END");
                Reject<SqliteException>(() => sessions[0].CommitEvildomWarHiddenReward(policy, reopened), "隐藏末回执失败回滚已建邮件");
                That(Scalar("SELECT count(*) FROM mailbox_messages") == 0, "独立回执失败不能遗留邮件资产");
                Sql("DROP TRIGGER reject_hidden_receipt");
                var firstReward = sessions[0].CommitEvildomWarHiddenReward(policy, reopened)!;
                That(firstReward.Commit.MailIds.Length == 1 && sessions[0].CanPublishEvildomWarHiddenReward(firstReward) &&
                    sessions[0].CommitEvildomWarHiddenReward(policy, reopened)!.Commit.MailIds.SequenceEqual(firstReward.Commit.MailIds) &&
                    reopened.CommitEvildomWarHiddenReward(leases[count], sessions[0].Selected!.Id, hiddenSnapshot).Duplicate,
                    "真实物件失败重试后只发一封永久邮件，重建仓储仍幂等");
                foreach (var session in sessions.Skip(1).Take(Math.Max(0, count - 2)))
                    That(session.RecoverEvildomWarRewards(reopened, policy).Length == 1 &&
                        session.RecoverEvildomWarRewards(reopened, policy).IsEmpty, "Ready恢复每位成员独立隐藏奖励且只补一次");
                That(Scalar("SELECT count(*) FROM character_evildom_war_hidden_rewards") == (count == 4 ? 3 : 1) &&
                    Scalar("SELECT count(*) FROM mailbox_messages WHERE expires_at IS NULL") == (count == 4 ? 3 : 1),
                    "隐藏奖励逐成员永久投递，未领取成员保留独立资格");
                foreach (var session in sessions)
                {
                    var enabled = session.EnableFormalDungeonSettlement();
                    That(enabled is not null, "隐藏真实物件完成后开放原结算许可");
                    session.ConfirmFormalDungeonSettlementEnabled(enabled!.Value.Receipt);
                    var memberIds = sessions.Select(member => member.Selected!.Id).ToArray();
                    byte[] report = DungeonSettlementProtocolTests.Report(memberIds.Select(id => (id, (byte)99)).ToArray());
                    That(DungeonSettlementRequests.TryParsePlayResult(report, out var result), "隐藏结算沿正式46报告");
                    var settled = session.SettleFormalDungeon(session.Selected!.Id, result!.RankPoint, result.MaximumRankPoint,
                        report, dungeons, SelfTestCatalogs.ClearExperience(resources.Archive), resources.Growth, reportMembers: memberIds);
                    That(settled.Outcome == DungeonRunOutcome.Accepted && settled.Receipt is not null &&
                        session.SettleFormalDungeon(session.Selected!.Id, result.RankPoint, result.MaximumRankPoint,
                            report, dungeons, SelfTestCatalogs.ClearExperience(resources.Archive), resources.Growth, reportMembers: memberIds).Outcome == DungeonRunOutcome.Duplicate,
                        "隐藏物件持久来源允许正式结算，重复46不重复结算");
                }
                That(instance.ResultReturn(sessions[0]).Count(notice => notice.Kind == PartyDungeonNoticeKind.ResultReturn) == count,
                    "隐藏正式结算后全员返回等待区");
                uint nextDungeon = (uint)battle.View.Dungeons.First(row => row.Kind == 0 && row.State == 0).Id;
                That(service.BeginSelection(sessions[0], nextDungeon).Length == count &&
                    service.Enter(sessions[0], nextDungeon, catalog.Dungeons.First(row => row.Mode == mode).Difficulty,
                        dungeons, rooms, null, null, null, null).Any(notice => notice.Kind == PartyDungeonNoticeKind.Entry),
                    "隐藏完成后真实进入下一副本");
                var afterEntry = JsonSerializer.Deserialize<EvildomWarCheckpoint>((string)ScalarText("SELECT checkpoint_json FROM evildom_war_battles")!)!;
                That(afterEntry.HiddenCompletion is not null && JsonSerializer.Serialize(afterEntry.HiddenCompletion) ==
                    JsonSerializer.Serialize(hiddenSnapshot.Completion), "后续进图事务保留原隐藏真实终点凭据");
                if (count == 4)
                {
                    var last = sessions[^1];
                    var previous = leases[count + count - 1];
                    ushort id = last.Selected!.Id;
                    parties.Detach(last); last.End();
                    clock.Advance(TimeSpan.FromMinutes(51));
                    var nextOwner = owners.Acquire(previous.Account, () => { }); leases.Add(nextOwner);
                    var reconnected = Session(nextOwner);
                    That(reconnected.RefundEvildomWar(reopened) == 1 &&
                        reconnected.RecoverEvildomWarRewards(reopened, policy).Length == 1 &&
                        reconnected.RecoverEvildomWarRewards(reopened, policy).IsEmpty &&
                        reopened.LoadUnrewardedEvildomWarHiddenCompletions(nextOwner, id).IsEmpty,
                        "跨副本后重开会话且战役超时退款，已完成隐藏奖励仍独立补发一次");
                    Reject<InvalidOperationException>(() => reopened.CommitEvildomWarHiddenReward(previous, id, hiddenSnapshot),
                        "旧owner不能领取隐藏奖励");
                }
            }
            finally { foreach (var session in all) session.End(); foreach (var owner in leases) owner.Dispose(); }

            GameSession Session(AccountOwners.Lease owner)
            {
                var session = new GameSession(repository, resources.Towns, owner, questCatalog: resources.Quests,
                    entry: new(new(2, 41), 54, "[evildom_war]"), timeProvider: clock,
                    incounts: DungeonIncountCatalog.Load(resources.Archive));
                all.Add(session); session.Select(0, resumeTown: true); session.MarkReady();
                session.CurrentParty = () => parties.Current(session);
                return session;
            }
            PartyDungeonNotice[] Enter() => service.Enter(sessions[0], sessions[0].ReadEvildomWar()!.Battle.View.Hidden!.StageDungeon,
                catalog.Dungeons.First(row => row.Mode == mode).Difficulty, dungeons, rooms, null, null, null, null);
            void CheckEntry(PartyDungeonNotice[] notices)
            {
                That(notices.Count(notice => notice.Kind == PartyDungeonNoticeKind.Entry) == count &&
                    notices.Where(notice => notice.Kind == PartyDungeonNoticeKind.Entry).All(notice =>
                        notice.Scene!.Entry.DungeonId == catalog.HiddenDungeons.Single(row => row.Mode == mode).Id),
                    "普通困难一人或四人、引导一人实际进入本模式隐藏副本");
            }
            void Sql(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
                using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
            }
            long Scalar(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
                using var command = db.CreateCommand(); command.CommandText = sql; return (long)command.ExecuteScalar()!;
            }
            object? ScalarText(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
                using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar();
            }
        }
        Console.WriteLine($"PASS evildom war hidden transactions {Count - before}");
    }
}
