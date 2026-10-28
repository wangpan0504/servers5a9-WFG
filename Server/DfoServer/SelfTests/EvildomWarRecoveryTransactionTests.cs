using System.Collections.Immutable;
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

internal static class EvildomWarRecoveryTransactionTests
{
    internal static void Run(TcpTestResources resources, EvildomWarCatalog catalog, string root)
    {
        int before = Count;
        foreach (string scenario in new[] { "resume", "takeover", "cleanup-failure", "refund", "cross-day", "completed" })
        {
            string path = Path.Combine(root, "war-recovery-" + scenario + ".db");
            var repository = new CharacterRepository(path, resources.Quests);
            var owners = new AccountOwners();
            var leases = new List<AccountOwners.Lease>();
            var sessions = new List<GameSession>();
            var clock = new ConsumableUseSessionTests.FakeClock();
            var owner = owners.Acquire("warrecover" + scenario, () => { }); leases.Add(owner);
            var candidate = CharacterCreation.Prepare("WarRecovery", resources.Characters.Get(0), catalog.WaitingArea,
                catalog.WaitingArea, resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning);
            repository.Create(owner, candidate with { Level = 95, AcceptedStoryLevel = 95, InitialLearning = learning });
            var parties = new PartyService(repository, clock);
            var service = new PartyDungeonService(parties, repository, repository, hiddenDraw: maximum => maximum - 1);
            var session = NewSession(owner, parties);
            try
            {
                That(parties.SetInformation(session, 0, PartySettings.Default with { Kind = 5, KindArgument = 0 }).Accepted &&
                    service.StartEvildomWar(session, catalog) is null, "恢复夹具创建真实单人队伍并开战");
                var war = session.ReadEvildomWar()!;
                uint dungeon = checked((uint)war.Dungeons[0].Id);
                That(service.BeginSelection(session, dungeon).Length == 1, "恢复夹具真实选择首图");
                var original = session.Selected!;
                Sql("CREATE TRIGGER reject_war_seat BEFORE INSERT ON character_evildom_war_seats BEGIN SELECT RAISE(ABORT,'seat failure'); END");
                Reject<SqliteException>(() => Enter(service, session, dungeon), "席位写入失败拒绝整次入场");
                That(ReferenceEquals(session.Selected, original) && Scalar("SELECT count(*) FROM evildom_war_battles") is 0L &&
                    Scalar("SELECT count(*) FROM character_dungeon_incount") is 0L && Scalar("SELECT count(*) FROM character_weekly_dungeon") is 0L,
                    "席位故障同时回滚疲劳、次数、周统计、战役与尝试号");
                Sql("DROP TRIGGER reject_war_seat");
                That(Enter(service, session, dungeon).Any(n => n.Kind == PartyDungeonNoticeKind.Entry), "首图费用与席位同事务提交");
                var saved = repository.LoadEvildomWarRecovery(owner, session.Selected!.Id, clock.GetUtcNow())!;
                That(saved.Checkpoint.View.BattleId == war.Battle.View.BattleId && saved.Payment.RemainingConsumed == 30 &&
                    saved.Payment.UsedAdded == 30 && saved.Payment.Weekly?.Slot == 7 && saved.Payment.FirstRun.TaskAttempt == 1 &&
                    saved.Checkpoint.View.Dungeons[0].State == 1, "持久凭据冻结真实消耗差值、原周期、原尝试与战况");
                if (scenario is "resume" or "completed")
                {
                    // 本层只验证持久检查点；真实怪物及终点事实另由完整五图TCP覆盖。
                    var identity = session.CurrentFormalDungeonScene()!.Snapshot.Identity;
                    var facts = new[] { new DungeonClearFact(identity, dungeon, [Guid.NewGuid()]) };
                    Sql("CREATE TRIGGER reject_war_checkpoint BEFORE UPDATE ON evildom_war_battles BEGIN SELECT RAISE(ABORT,'checkpoint failure'); END");
                    var previous = war.Battle.View;
                    Reject<SqliteException>(() => war.Battle.Complete(facts, clock.GetUtcNow(), session.PersistEvildomWarCheckpoint),
                        "检查点SQL失败不能提前安装内存战况");
                    That(ReferenceEquals(war.Battle.View, previous), "检查点失败保留原版本及活动Run，可原事实重试");
                    Sql("DROP TRIGGER reject_war_checkpoint");
                    That(war.Battle.Complete(facts, clock.GetUtcNow(), session.PersistEvildomWarCheckpoint) is not null,
                        "同一检查点在SQL恢复后提交一次");
                }
                if (scenario == "completed")
                {
                    // 终态SQL合同测试不伪造客户端通关验收，也不发奖。
                    var previous = war.Battle.View;
                    var finished = previous with { Revision = previous.Revision + 1,
                        Dungeons = previous.Dungeons.Select(row => row with { State = 2 }).ToImmutableArray() };
                    var completion = new EvildomWarCompletion(previous.BattleId, previous.Mode, clock.GetUtcNow(),
                        [new(session.CurrentFormalDungeonScene()!.Snapshot.Identity, (uint)finished.Dungeons.Single(row => row.Kind == 1).Id, [Guid.NewGuid()])]);
                    repository.CommitEvildomWarCheckpoint(owner, session.Selected.Id, war.BindingToken, previous, new(finished, completion));
                    clock.Advance(TimeSpan.FromSeconds(catalog.LimitTime + 1));
                    var refund = repository.RefundExpiredEvildomWarSeats(owner, session.Selected, clock.GetUtcNow());
                    That(refund.Seats == 0 && Scalar("SELECT state FROM character_evildom_war_seats") is 2L,
                        "持久完成终态排除超时退款，即使尚未冻结奖励");
                    That(repository.LoadUnrewardedEvildomWarCompletions(owner, session.Selected.Id).Length == 1,
                        "原Run结束前崩溃仍能读取持久完成事实，不依赖内存奖励计划");
                    var policy = new EvildomWarRewardPolicy(catalog, resources.ItemResources.Items, resources.ItemResources.Boosters);
                    var choice = policy.Prepare(completion.Mode, session.Selected, _ => 0);
                    // 旧版已冻结奖励包含固定第1组，升级后必须继续交付原产物，不重写成v2。
                    choice = choice with { Items = choice.Items.AddRange(policy.PrepareHidden(completion.Mode).Items) };
                    var pending = new EvildomWarRewardSnapshot(completion, choice, policy.Materialize(choice, completion.Created))
                        { Rule = EvildomWarRewardPolicy.LegacyRule };
                    pending.Validate();
                    repository.FreezeRecoveredEvildomWarReward(owner, session.Selected.Id, pending);
                    string frozen = (string)Scalar("SELECT reward_json FROM character_evildom_war_rewards")!;
                    repository.FreezeRecoveredEvildomWarReward(owner, session.Selected.Id, pending with { Items = pending.Items.Reverse().ToImmutableArray() });
                    That(Scalar("SELECT reward_json FROM character_evildom_war_rewards") as string == frozen &&
                        repository.LoadUnrewardedEvildomWarCompletions(owner, session.Selected.Id).IsEmpty,
                        "恢复奖励冻结一次后不覆盖首次产物，重复规划不改变存档");
                    var delivered = repository.RecoverEvildomWarRewards(owner, session.Selected.Id);
                    That(!delivered.IsEmpty && repository.RecoverEvildomWarRewards(owner, session.Selected.Id).IsEmpty,
                        "持久完成事实恢复出的全部奖励通过原邮件事务交付一次");
                    continue;
                }
                if (scenario != "takeover")
                {
                    if (scenario == "cleanup-failure")
                    {
                        int cleanupErrors = 0;
                        session.EvildomWarCleanupFailed = _ => cleanupErrors++;
                        Sql("CREATE TRIGGER reject_war_disconnect BEFORE UPDATE ON character_evildom_war_seats BEGIN SELECT RAISE(ABORT,'disconnect failure'); END");
                        session.End();
                        That(cleanupErrors == 1 && session.Selected is null && !session.Ready && session.CurrentFormalDungeonScene() is null,
                            "断线写入故障仍完成内存失效，不残留在线角色或旧Run，故障单独记录");
                        Sql("DROP TRIGGER reject_war_disconnect");
                    }
                    else
                    {
                        session.DisconnectEvildomWar();
                        saved = repository.LoadEvildomWarRecovery(owner, session.Selected.Id, clock.GetUtcNow())!;
                        That(saved.ReconnectUntil == clock.GetUtcNow().AddSeconds(300), "断线席位使用资源300秒窗口并受战役截止限制");
                        parties.Detach(session); session.End();
                    }
                }
                if (scenario is "resume" or "takeover" or "cleanup-failure")
                {
                    var replacementOwner = owners.Acquire(owner.Account, () => { }); leases.Add(replacementOwner);
                    var newParties = scenario == "resume" ? new PartyService(repository, clock) : parties;
                    var next = NewSession(replacementOwner, newParties);
                    var nextService = new PartyDungeonService(newParties, repository, repository, hiddenDraw: maximum => maximum - 1);
                    nextService.RecoverEvildomWarAsync(next, catalog).GetAwaiter().GetResult();
                    var restored = next.ReadEvildomWar()!;
                    That(restored.Battle.View.BattleId == saved.Checkpoint.View.BattleId && restored.Deadline == saved.Checkpoint.View.Deadline &&
                        restored.Dungeons[0].State == (scenario == "resume" ? 2 : 0) && restored.Charged && next.Phase == SessionPhase.Town,
                        "新会话重建真实一人队伍，保留已清图、原截止及支付状态");
                    That(!repository.ClaimEvildomWarSeat(replacementOwner, next.Selected!.Id, saved, Guid.NewGuid(), clock.GetUtcNow()),
                        "旧token不能重复认领已交接席位");
                    Reject<InvalidOperationException>(() => repository.DisconnectEvildomWarSeat(owner, next.Selected.Id,
                        restored.Battle.View.BattleId, war.BindingToken, clock.GetUtcNow()), "旧generation不能更新新席位");
                    repository.DisconnectEvildomWarSeat(replacementOwner, next.Selected.Id, restored.Battle.View.BattleId,
                        war.BindingToken, clock.GetUtcNow());
                    That(Scalar("SELECT state FROM character_evildom_war_seats") is 0L, "当前owner携带旧token的迟到清理也不能覆盖新绑定");
                    uint second = (uint)restored.Dungeons[1].Id;
                    That(nextService.BeginSelection(next, second).Length == 1 && Enter(nextService, next, second).Any(n => n.Kind == PartyDungeonNoticeKind.Entry) &&
                        next.Selected.Fatigue!.Used == 30 && Scalar("SELECT used FROM character_weekly_dungeon") is 1L &&
                        Scalar("SELECT daily_used FROM character_dungeon_incount") is 1L, "重连后的下一图沿用原费用，整场次数及周统计不重复");
                    next.DisconnectEvildomWar(); newParties.Detach(next); next.End();
                    repository = new CharacterRepository(path, resources.Quests);
                    var finalOwner = owners.Acquire(owner.Account, () => { }); leases.Add(finalOwner);
                    var finalParties = new PartyService(repository, clock);
                    var finalSession = NewSession(finalOwner, finalParties);
                    var finalService = new PartyDungeonService(finalParties, repository, repository, hiddenDraw: maximum => maximum - 1);
                    finalService.RecoverEvildomWarAsync(finalSession, catalog).GetAwaiter().GetResult();
                    That(finalSession.ReadEvildomWar() is { Charged: true } finalWar && finalWar.Dungeons[0].State == (scenario == "resume" ? 2 : 0) &&
                        finalWar.Dungeons[1].State == 0 && finalWar.Deadline == restored.Deadline &&
                        finalService.BeginSelection(finalSession, second).Length == 1 && Enter(finalService, finalSession, second).Any(n => n.Kind == PartyDungeonNoticeKind.Entry) &&
                        finalSession.Selected!.Fatigue!.Used == 30,
                        "中途图断线并重开仓储后回到等待区检查点，只重开未完成图且不再次计费");
                    continue;
                }
                if (scenario == "cross-day")
                {
                    clock.Advance(TimeSpan.FromDays(1));
                    Sql($"UPDATE character_dungeon_incount SET day=day+1,daily_used=1; UPDATE character_fatigue SET used=86,remaining=70");
                }
                else
                {
                    clock.Advance(TimeSpan.FromSeconds(301));
                    Sql("UPDATE character_fatigue SET used=37,remaining=119");
                }
                var current = repository.LoadCharacter(owner, 0);
                Sql("CREATE TRIGGER reject_war_refund BEFORE UPDATE OF state ON character_evildom_war_seats BEGIN SELECT RAISE(ABORT,'refund failure'); END");
                Reject<SqliteException>(() => repository.RefundExpiredEvildomWarSeats(owner, current, clock.GetUtcNow()), "退款回执故障整体回滚");
                That(Scalar("SELECT weekly_used FROM character_dungeon_incount") is 1L && Scalar("SELECT used FROM character_weekly_dungeon") is 1L &&
                    repository.LoadCharacter(owner, 0).Fatigue == current.Fatigue, "退款失败不先退次数或疲劳");
                Sql("DROP TRIGGER reject_war_refund");
                var result = repository.RefundExpiredEvildomWarSeats(owner, current, clock.GetUtcNow());
                That(result.Seats == 1 && Scalar("SELECT weekly_used FROM character_dungeon_incount") is 0L &&
                    Scalar("SELECT used FROM character_weekly_dungeon") is 0L && Scalar("SELECT state FROM character_evildom_war_seats") is 3L,
                    "超时原周计次与slot7同事务只退一次");
                That(scenario == "cross-day" ? result.Character.Fatigue == current.Fatigue && Scalar("SELECT daily_used FROM character_dungeon_incount") is 1L :
                    result.Character.Fatigue is { Used: 7, Remaining: 149 } && Scalar("SELECT daily_used FROM character_dungeon_incount") is 0L,
                    "跨日保留新日次数及新疲劳池，同日只退原30点并保留之后的7点消费");
                That(repository.RefundExpiredEvildomWarSeats(owner, result.Character, clock.GetUtcNow()).Seats == 0,
                    "退款重试由独立回执排除，不增加资产");
            }
            finally
            {
                foreach (var active in sessions.AsEnumerable().Reverse()) active.End();
                foreach (var lease in leases) lease.Dispose();
            }

            GameSession NewSession(AccountOwners.Lease lease, PartyService partyService)
            {
                var result = new GameSession(repository, resources.Towns, lease, questCatalog: resources.Quests,
                    entry: new(new(2, 41), 54, "[evildom_war]"), timeProvider: clock, incounts: DungeonIncountCatalog.Load(resources.Archive));
                sessions.Add(result); result.Select(0, resumeTown: true); result.MarkReady(); result.CurrentParty = () => partyService.Current(result);
                return result;
            }
            PartyDungeonNotice[] Enter(PartyDungeonService service, GameSession member, uint id) => service.Enter(member, id, 2,
                SelfTestCatalogs.Dungeons(resources.Archive), SelfTestCatalogs.Rooms(resources.Archive), null, null, null, null);
            void Sql(string sql) { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
            object? Scalar(string sql) { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; return cmd.ExecuteScalar(); }
            SqliteConnection Open() { var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True"); db.Open(); return db; }
        }
        Console.WriteLine($"PASS evildom war recovery transactions {Count - before}");
    }
}
