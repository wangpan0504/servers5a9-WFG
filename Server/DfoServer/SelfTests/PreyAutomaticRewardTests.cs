using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 真实开团、阶段资格、库存/UID/邮件与收据使用SQLite；战场清图事实为明确合成输入，不代替TCP或客户端验收。
internal static class PreyAutomaticRewardTests
{
    internal static async Task RunAsync(string root, TcpTestResources resources)
    {
        int before = Count;
        string path = Path.Combine(root, "prey-team-auto-reward", "game.db");
        var repository = new CharacterRepository(path);
        var owners = new AccountOwners();
        var online = new OnlineSessionDirectory();
        var clock = new LukeRaidClockTests.Clock();
        var candidates = new AntonRaidRejoin(false, lukeCandidatesEnabled: true, clock: clock);
        var raids = new RaidRegistry(clock);
        raids.BindRejoin(candidates);
        var parties = new PartyService(repository);
        var recovery = new LukeRaidRejoin(raids, parties, candidates);
        var itemResources = resources.ItemResources;
        var definition = RaidCatalog.Load(resources.Archive).Find(3)!;
        var loaded = RaidResourceLoader.Load(resources.Archive, definition,
            SelfTestCatalogs.Dungeons(resources.Archive), itemResources.Items);
        definition = definition with { Resources = loaded };
        var script = loaded.PreyTeam!.Script;
        var start = new AntonRaidStart(raids, parties, repository, resources.Towns, itemResources.EpicPieces, clock,
            preyTeamRewards: new PreyGuideRewards(script, itemResources.Items, itemResources.Boosters));
        var leases = new List<AccountOwners.Lease>();
        var sessions = new List<GameSession>();
        try
        {
            var actors = Enumerable.Range(0, 2).Select(index =>
            {
                var owner = owners.Acquire("prey-auto-" + index, static () => { });
                leases.Add(owner);
                var point = new Location(59, 0, 445, 377);
                var character = repository.Create(owner, new("PreyAuto" + index, point, point)
                {
                    Level = 95, AcceptedStoryLevel = 95, CompletedQuests = [8935],
                    Inventory = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0))
                });
                var inventory = new InventoryService(itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces);
                InventoryService.Commit(repository, owner, character, inventory.Grant(character, [new(script.EntryCost!.ItemId, 2)]));
                var session = new GameSession(repository, resources.Towns, owner,
                    entry: new(new(2, 42), 50, "[prey]"), timeProvider: clock);
                sessions.Add(session);
                session.CurrentParty = () => parties.Current(session);
                session.Select(0); session.MarkReady();
                online.Register(owner, session);
                return session.CaptureRaidActor(raids)!;
            }).ToArray();
            That(raids.Create(actors[0], 3, "PreyAuto", "PreyAuto"u8.ToArray(), 12) is not null,
                "自动奖励夹具沿真实目录创建普通普雷团");
            raids.Invite(actors[0], actors[1], 0);
            That(raids.Respond(actors[1], actors[0].Character, true).Accepted, "第二名角色真实加入同一普通团");
            for (int index = 0; index < actors.Length; index++)
                That(raids.AssignPrey(actors[0], actors[index].Character, checked((byte)((index + 1) * 10 + 1))) is not null,
                    "自动奖励夹具在开团前为每名成员明确分配攻坚标记");
            var started = start.Start(actors[0], raids.Find(actors[0])!, definition);
            That(started.Receipt is not null && started.Error == 0, "自动奖励夹具按原资源门票真实开战：" + started.Reason);
            var party = raids.Find(actors[0])!;
            var battle = sessions[0].CurrentRaidBattle(actors[0])!;
            That(battle.HasPreyTeamAutomaticRewards && battle.LukeRejoinWindow > TimeSpan.Zero,
                "普通TeamSource实例有自动交付义务及原PVF重返窗口");
            That(battle.ExpireLukeTimer(battle.Instance, new(9000, 0), battle.View.TimerGenerations[new(9000, 0)]) is not null,
                "合成战场先完成原普通准备期");
            var sanctuaries = script.Phases[0].Dungeons.Where(dungeon => dungeon.Declarations.Any(field =>
                field.Tag == "[type]" && field.Values is [{ Text: "sanctuary" }])).ToArray();
            That(sanctuaries.Length == 4, "沿原普通资源四个圣地制造明确的领域通关事实");
            DungeonRunIdentity? last = null;
            for (int index = 0; index < sanctuaries.Length; index++)
            {
                var actor = actors[index % actors.Length];
                var identity = new DungeonRunIdentity(actor.Character, 0, 1, Guid.NewGuid(), 1);
                using var reserved = battle.ReserveDungeon(new(battle.Instance, 0, 0, RaidKind.Prey)
                    { PreyGuide = false, PreySoloCharacter = actor.Character }, checked((uint)sanctuaries[index].Id), [(actor, identity)]);
                That(reserved is not null, "合成Run仍保留原普通Battle、角色与唯一正式Run身份");
                reserved!.Install();
                That(battle.CompletePreyDungeon([(actor, new DungeonClearFact(identity, checked((uint)sanctuaries[index].Id), [Guid.NewGuid()])
                    { ClearMap = new DungeonMap("boss", new(0, 0), 77, "fixture/prey-auto.map") })]) is not null,
                    "原普通领域消费每人两次明确清图事实，不直接写资格");
                if (index + 1 != sanctuaries.Length) battle.ReleaseDungeon(actor, identity.InstanceId);
                else last = identity;
            }
            var pending = battle.PendingLukeSettlement()!;
            That(pending is { Members.Length: 2 } && pending.Members.All(member => member.Present && member.CommittedRuns == 2),
                "一阶段资格按两名原成员各两次领域通关密封");
            var committed = repository.CommitLukePhase(pending);
            battle.AcceptLukeSettlement(pending, committed);
            battle.ReleaseDungeon(actors[^1], last!.InstanceId);
            That(battle.View is { State: AntonRaidBattleState.Reward, Phase: 0 } &&
                committed.Members.All(eligibility => eligibility.Eligible && eligibility.Source == PreyGuideRewards.TeamSource &&
                    eligibility.Offers.Select(offer => offer.Kind).SequenceEqual(new byte[] { 1, 0 }) && eligibility.Offers.All(offer => offer.Card == 0)),
                "真实SQLite冻结每人原TeamSource两类Card0产物，原Run退出后才开放Reward");
            string frozen = JsonSerializer.Serialize(committed);

            var first = sessions[0].ClaimRaidReward(1, 0, repository, itemResources.Items,
                itemResources.GoldLimits, itemResources.EpicPieces, clock.GetUtcNow());
            That(first is not null && !first.Commit.Duplicate && !battle.HasConfirmedLukeReward(actors[0].Character, 0, 1) &&
                battle.SubmittedLukeReward(actors[0].Character, 0, 1) == first.Prepared,
                "模拟SQL成功但ACK丢失：原Session登记精确已提交产物，尚未确认领奖义务");

            var mailEligibility = committed.Members.Single(member => member.Character == actors[1].Character);
            var mailOffer = mailEligibility.Offers.Single(offer => offer.Kind == 0);
            var template = itemResources.Items.Get(mailOffer.Grants[0].ItemId);
            var filled = sessions[1].MutateInventory(character =>
            {
                var range = ItemSlotBoundService.Range(template.Kind, character.Inventory);
                var list = ItemSlotBoundService.List(template.Kind);
                var items = character.Inventory.Items.Where(item => item.Position.List != list ||
                    item.Position.Index < range.Start || item.Position.Index > range.End).ToImmutableArray();
                for (short slot = range.Start; slot <= range.End; slot++)
                    items = items.Add(new(0, new(list, slot), InventoryRewardGrantService.Create(template, template.StackLimit)));
                return new(true, 0, new InventoryPlan(character.Inventory, character.Inventory with { Items = items }, "fixture_full", new(character)));
            });
            That(filled.Accepted, "邮件回归沿原在线库存提交填满奖励所属背包类别");
            var fullBefore = sessions[1].Selected;
            string beforeFailure = ClientSettingsSnapshots.Capture(path, true);
            Sql("CREATE TRIGGER prey_auto_mail_failure BEFORE INSERT ON mailbox_messages BEGIN SELECT RAISE(ABORT,'prey automatic overflow failure'); END");
            try
            {
                Reject<SqliteException>(() => sessions[1].ClaimRaidReward(0, 0, repository, itemResources.Items,
                    itemResources.GoldLimits, itemResources.EpicPieces, clock.GetUtcNow()), "满包奖励的真实邮件INSERT故障传播");
                That(ClientSettingsSnapshots.Capture(path, true) == beforeFailure && ReferenceEquals(sessions[1].Selected, fullBefore) &&
                    !battle.HasConfirmedLukeReward(actors[1].Character, 0, 0),
                    "邮件失败回滚全部资产、物理UID、邮件、资格及领奖收据，Selected与确认保持原值");
            }
            finally { Sql("DROP TRIGGER prey_auto_mail_failure"); }
            var mailed = sessions[1].ClaimRaidReward(0, 0, repository, itemResources.Items,
                itemResources.GoldLimits, itemResources.EpicPieces, clock.GetUtcNow());
            That(mailed is { Commit.Duplicate: false } && !mailed.Commit.MailIds.IsEmpty && mailed.Prepared.Grants.SequenceEqual(mailOffer.Grants),
                "故障解除后原冻结计划成功提交一次，真实溢出邮件与收据同事务落库");
            var target = new LukeRewardRecoveryTarget(actors[1].Character, battle.Instance, 0, 0, 0) { RaidType = RaidKind.Prey };
            string afterMail = ClientSettingsSnapshots.Capture(path, true);
            var replay = LukeRewardRecovery.Claim(actors[1].Owner, sessions[1].Selected!, target, mailEligibility.Event, mailEligibility.Anchor,
                repository, () => throw new InvalidOperationException("已提交回执不得再次加载资源或抽奖"), clock.GetUtcNow());
            That(replay.Duplicate && replay.MailIds.SequenceEqual(mailed!.Commit.MailIds) && ClientSettingsSnapshots.Capture(path, true) == afterMail,
                "精确持久回执重放原MailIds，资源不可用也不重新规划、不重发资产");

            That(battle.DueLukeRewards().Length == 2 && battle.PreyTeamRewardDue,
                "普通一阶段已密封且无活跃Run时立即交付原权益，不等待不存在的客户端翻牌或144秒兜底");
            foreach (var actor in actors.Reverse())
            {
                var fact = recovery.TryCaptureForDisconnect(actor.Session, out string reason);
                That(fact is not null && reason == "captured", "密封资格后仍从原重返服务捕获断线预约");
                actor.Session.End(); parties.Detach(actor.Session);
                online.Remove(actor.Owner.Account, actor.Session); actor.Owner.Dispose();
                That(recovery.TryRecordDisconnected(fact!, out reason) && reason == "recorded", "原owner释放后提交同一断线预约");
            }
            int movie = script.Headers.Single(row => row.Tag == "[movie end time]").Values.Max(value => value.Number);
            clock.Advance(checked((int)Math.Max(battle.LukeRejoinWindow.TotalSeconds, movie + 120) + 1));
            That(actors.All(actor => candidates.Peek(actor.Character) is null) && raids.Find(party.Key, party.Channel) is null &&
                battle.DueLukeRewards().Length == 2 && battle.View.State == AntonRaidBattleState.Reward,
                "全团超过原PVF重返窗后原团预约过期，已密封的两人领奖义务仍保留");
            var automatic = new LukeAutomaticRewards(repository, owners, online, repository.LoadCharacterById,
                itemResources.Items, itemResources.GoldLimits, itemResources.EpicPieces, clock);
            using (var busy = owners.Acquire(actors[0].Owner.Account, static () => { }))
            {
                await automatic.SettleAsync(battle);
                That(Scalar("SELECT count(*) FROM character_raid_reward_receipts WHERE character_id=" + actors[0].Character) == 1 &&
                    battle.View is { State: AntonRaidBattleState.Reward, Phase: 0 },
                    "离线账号已被新owner占用时TryAcquire拒绝，不接管账号或跳过剩余权益推进阶段");
            }
            await automatic.SettleAsync(battle);
            That(battle.View is { State: AntonRaidBattleState.Fighting, Phase: 1 } &&
                Scalar("SELECT count(*) FROM character_raid_reward_receipts WHERE raid_type=3 AND phase=0") == 4 &&
                Scalar("SELECT count(*) FROM character_raid_reward_receipts WHERE raid_type=3 AND phase=1") == 0,
                "空闲后按冻结产物补齐剩余两类原权益，完成全团一阶段并真正进入二阶段");
            foreach (var eligibility in committed.Members)
            {
                using var owner = owners.Acquire(actors.Single(actor => actor.Character == eligibility.Character).Owner.Account, static () => { });
                var current = repository.LoadCharacterById(owner, eligibility.Character)!;
                foreach (var offer in eligibility.Offers)
                {
                    var saved = repository.ReadLukeRewardRecovery(owner, current,
                        new(current.Id, eligibility.Instance, 0, offer.Kind, offer.Card) { RaidType = RaidKind.Prey });
                    That(saved.Claimed && saved.Event == eligibility.Event && saved.Anchor == eligibility.Anchor &&
                        saved.Prepared.Source == eligibility.Source && saved.Prepared.Week == eligibility.Week && saved.Prepared.Grants.SequenceEqual(offer.Grants) &&
                        battle.HasConfirmedLukeReward(current.Id, 0, offer.Kind),
                        "离线自动交付精确匹配原instance/event/anchor/周期和冻结产物，不重抽或替换奖池");
                }
            }
            That(JsonSerializer.Serialize(committed) == frozen, "自动交付及断线超时不改已密封资格或冻结Offers");
            string final = ClientSettingsSnapshots.Capture(path, true);
            await automatic.SettleAsync(battle);
            That(ClientSettingsSnapshots.Capture(path, true) == final && battle.View.Phase == 1,
                "重复自动结算保持全部资产、UID、邮件、资格及收据不变");
            Console.WriteLine($"PASS {Count - before} 普雷普通离线奖励领域与SQLite事务断言");
        }
        finally
        {
            foreach (var session in sessions) session.End();
            foreach (var lease in leases) lease.Dispose();
        }

        void Sql(string sql)
        {
            using var db = new SqliteConnection($"Data Source={path};Pooling=False"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        long Scalar(string sql)
        {
            using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar());
        }
    }
}
