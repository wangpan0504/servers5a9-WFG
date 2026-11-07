using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Network;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
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
        await GuideAutomaticAsync(root, resources, definition);

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

    // 复用上方已加载目录，不再启动Runtime；明确区分真实SQL/发送泵故障与TCP、实机验收。
    static async Task GuideAutomaticAsync(string root, TcpTestResources resources, RaidDefinition definition)
    {
        int before = Count;
        string path = Path.Combine(root, "prey-guide-auto-reward", "game.db");
        var repository = new CharacterRepository(path);
        var owners = new AccountOwners();
        var online = new OnlineSessionDirectory();
        var clock = new LukeRaidClockTests.Clock();
        var raids = new RaidRegistry(clock);
        var parties = new PartyService(repository);
        var items = resources.ItemResources;
        var script = definition.Resources!.Script;
        var automatic = new LukeAutomaticRewards(repository, owners, online, repository.LoadCharacterById,
            items.Items, items.GoldLimits, items.EpicPieces, clock);
        var errors = new List<(string Stage, Exception Error)>();
        await using var timers = new LukeRaidClocks(clock, repository, (stage, error) => errors.Add((stage, error)),
            settleLukeRewards: automatic.SettleAsync);
        using var owner = owners.Acquire("prey-guide-auto", static () => { });
        var point = new Location(59, 0, 445, 377);
        var created = repository.Create(owner, new("GuideAuto", point, point)
        { Level = 95, AcceptedStoryLevel = 95, CompletedQuests = [8935], Inventory = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0)) });
        var inventory = new InventoryService(items.Items, items.GoldLimits, items.EpicPieces);
        InventoryService.Commit(repository, owner, created, inventory.Grant(created, [new(script.EntryCost!.ItemId, 2)]));
        var session = new GameSession(repository, resources.Towns, owner, entry: new(new(2, 42), 50, "[prey]"), timeProvider: clock);
        session.CurrentParty = () => parties.Current(session);
        session.Select(0); session.MarkReady();
        online.Register(owner, session);
        try
        {
            var actor = session.CaptureRaidActor(raids)!;
            var party = raids.Create(actor, 3, "GuideAuto", "GuideAuto"u8.ToArray(), 1, guide: true)!;
            var start = new AntonRaidStart(raids, parties, repository, resources.Towns, items.EpicPieces, clock, timers: timers,
                preyRewards: new(script, items.Items, items.Boosters));
            That(start.Start(actor, party, definition) is { Receipt: not null, Error: 0 }, "引导自动夹具沿真实single配方扣票开战");
            var battle = session.CurrentRaidBattle(actor)!;
            That(battle.PreyGuide && battle.HasPreyAutomaticRewards && !battle.HasPreyTeamAutomaticRewards,
                "引导自动资格使用真实single来源，不伪装TeamSource或普通重返窗口");
            clock.Advance(25);
            int branch = battle.View.Symbols["[single raid type]"];
            foreach (int dungeon in branch == 0 ? new[] { 291100391, 291100393 } : new[] { 291100390, 291100392 }) Clear(dungeon);
            That(session.CommitPendingLukePhase(repository), "引导真实首阶段资格先经SQLite提交");
            var first = battle.LukeEligibility(actor.Character, 0)!;
            string firstFrozen = JsonSerializer.Serialize(first);
            That(first is { Source: PreyGuideRewards.Source, Phase: 0, Eligible: true } &&
                battle.View is { State: AntonRaidBattleState.Reward, Phase: 0 } && battle.DueLukeRewards().Length == 0,
                "single真实phase0等待本阶段演出，不能提前自动交付或写成phase1");
            var materialBefore = session.Selected!.Inventory.AccountWallet;
            Fill(first);
            var movie = battle.View;
            using var stop = new CancellationTokenSource();
            using var stream = new GuideResultFailureStream();
            var writtenGate = new object();
            var written = new List<(byte Kind, ushort Opcode, byte[] Body)>();
            TaskCompletionSource? movieWritten = null;
            await using var sender = new GameConnectionSender(owner, stream, stop, 0, (kind, opcode, _, body, _) =>
            {
                byte[] bytes = body.ToArray();
                lock (writtenGate) written.Add((kind, opcode, bytes));
                if (kind == 0 && opcode == 587 && bytes is [3, 1]) Volatile.Read(ref movieWritten)?.TrySetResult();
            });
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), owner, session, sender);
            var town = new TownProjection(resources.Towns, new CharacterStatComputer(resources.Characters), null, items.EpicPieces, clock: clock);
            await SceneReturn(context, queued: true);
            // 独立连接展示身份复用同一会话、发送泵及只读目录，不直接修改RaidDisplay凑重复场景。
            context = new(new(2, IPAddress.Loopback, stream, stop), owner, session, sender);
            await SceneReturn(context, queued: false);
            var movieRequest = session.RaidMovieSnapshot()!.Value;
            var movieIdentity = context.RaidDisplay;
            context.RaidDisplay = null;
            That(RaidCombatProjection.Movie(context, actor, battle, movieRequest.View, movieRequest.Event, true) &&
                !battle.PreyMovieCardsReady(actor, battle.View), "尚未实际展示电影时拒绝旧确认，不提前写电影事实");
            context.RaidDisplay = movieIdentity;
            That(RaidCombatProjection.Movie(context, actor, battle, movieRequest.View, movieRequest.Event, true) &&
                RaidCombatProjection.Movie(context, actor, battle, movieRequest.View, movieRequest.Event, true) &&
                !battle.CompletePreyMovie(actor, movie), "首阶段电影确认经原Session/Projection只记一次，重复请求可ACK，不注入660领奖");
            await sender.DrainAsync();
            lock (writtenGate) That(written.Count(row => row is { Kind: 1, Opcode: 659, Body: [1] }) == 2,
                "原电影请求与重复请求均写出659成功ACK，不能只记Handled而静默吞确认");
            That(session.CommandGate.Wait(0), "真实占用引导在线命令门禁");
            try
            {
                clock.Advance(1);
                That(Receipts(0) == 0 && battle.View.Phase == 0 &&
                    clock.PendingDeadlines.Count(row => row.Instance == battle.Instance && row.Due == clock.GetTimestamp() + 1000) == 1,
                    "引导自动交付遇在线门禁忙时不抢占、不跳阶段，由原时钟一秒重排");
            }
            finally { session.CommandGate.Release(); }
            var selected = session.Selected;
            string beforeSql = ClientSettingsSnapshots.Capture(path, true);
            // 各offer独立原子提交；首类材料可能直接进入账号材料栏，不能假设填满实体背包必走邮箱。
            Sql("CREATE TRIGGER guide_auto_receipt_failure BEFORE INSERT ON character_raid_reward_receipts BEGIN SELECT RAISE(ABORT,'guide automatic receipt tail'); END");
            bool reached = false;
            try
            {
                try { await automatic.SettleAsync(battle); }
                catch (SqliteException error) when (error.SqliteErrorCode == 19 && error.Message == "SQLite Error 19: 'guide automatic receipt tail'.")
                { reached = true; }
                That(reached && ClientSettingsSnapshots.Capture(path, true) == beforeSql && ReferenceEquals(selected, session.Selected) &&
                    Receipts(0) == 0 && !battle.HasConfirmedLukeReward(actor.Character, 0, 1) && !battle.HasConfirmedLukeReward(actor.Character, 0, 0),
                    "必须命中首笔引导收据尾段marker；同笔失败回滚全部资产、UID、邮件、收据，保留原资格与Selected");
            }
            finally { Sql("DROP TRIGGER guide_auto_receipt_failure"); }
            var publishedOffer = first.Offers[0];
            var published = session.ClaimRaidReward(publishedOffer.Kind, publishedOffer.Card, repository,
                items.Items, items.GoldLimits, items.EpicPieces, clock.GetUtcNow());
            That(published is { Commit.Duplicate: false }, "竞态前首类真实资产提交成功");
            await RaidCombatProjection.PublishPreyGuideResultsAsync(context, published!);
            session.ConfirmRaidRewardPublished(published!);
            var lastOffer = first.Offers[1];
            That(session.CommandGate.Wait(0), "模拟原手动命令持有门禁直到末份结果发送完成");
            string recorded;
            try
            {
                var last = session.ClaimRaidReward(lastOffer.Kind, lastOffer.Card, repository,
                    items.Items, items.GoldLimits, items.EpicPieces, clock.GetUtcNow());
                That(last is { Commit.Duplicate: false } && Receipts(0) == 2 &&
                    battle.SubmittedLukeReward(actor.Character, 0, lastOffer.Kind) == last.Prepared &&
                    battle.HasConfirmedLukeReward(actor.Character, 0, publishedOffer.Kind) &&
                    !battle.HasConfirmedLukeReward(actor.Character, 0, lastOffer.Kind),
                    "末份SQL与Battle原回执均已登记，但真实结果尚未发送和确认");
                recorded = ClientSettingsSnapshots.Capture(path, true);
                await automatic.SettleAsync(battle);
                That(battle.View is { State: AntonRaidBattleState.Reward, Phase: 0 } &&
                    !battle.HasConfirmedLukeReward(actor.Character, 0, lastOffer.Kind) &&
                    ClientSettingsSnapshots.Capture(path, true) == recorded,
                    "原命令仍持门禁时自动路径不得借Submitted快路确认末份、推进阶段或改资产");
            }
            finally { session.CommandGate.Release(); }
            int publications = 0;
            session.AutomaticRaidRewardArrived = receipt =>
            {
                publications++;
                That(receipt.Prepared is { Source: PreyGuideRewards.Source, Phase: 0 } && receipt.Commit.Duplicate,
                    "释放门禁后自动路径仅重放引导原phase0末份已提交回执");
                return RaidCombatProjection.PublishPreyGuideResultsAsync(context, receipt);
            };
            stream.FailResults = true;
            LukeRewardPublicationException? failure = null;
            var settling = automatic.SettleAsync(battle);
            try
            {
                await stream.ResultWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                That(!settling.IsCompleted && battle.View is { State: AntonRaidBattleState.Reward, Phase: 0 } &&
                    !battle.HasConfirmedLukeReward(actor.Character, 0, lastOffer.Kind) &&
                    ClientSettingsSnapshots.Capture(path, true) == recorded,
                    "生产600发送泵被明确持住时Drain尚未结束，不提前确认、推进或重复资产");
            }
            finally
            {
                stream.ReleaseResultWrite.TrySetResult();
                try { await settling; }
                catch (LukeRewardPublicationException error) { failure = error; }
            }
            // 等待原发送泵退出后再检查队列释放，避免把Drain异常唤醒与泵finally之间的调度差当产品故障。
            await sender.DisposeAsync();
            session.RaidBattleNoticeArrived = null;
            That(failure is { InnerExceptions.Count: 1 } && failure.InnerExceptions[0] is IOException { Message: GuideResultFailureStream.Marker } &&
                stream.Failures == 1 && publications == 1 && sender.PendingBytes == 0 && Receipts(0) == 2 &&
                battle.View is { State: AntonRaidBattleState.Fighting, Phase: 1 },
                "真实引导600经生产投影及发送泵写失败后，已提交两类权益不回滚或重发，阶段义务照常确认");
            That(JsonSerializer.Serialize(battle.LukeEligibility(actor.Character, 0)) == firstFrozen && Receipts(1) == 0,
                "发送失败及phase1开放不改phase0冻结产物，也不提前制造phase1收据");
            string afterSendFailure = ClientSettingsSnapshots.Capture(path, true);
            await automatic.SettleAsync(battle);
            That(ClientSettingsSnapshots.Capture(path, true) == afterSendFailure && publications == 1,
                "已提交未展示结果的重复自动收尾不重复发奖或再次使用已失效发送泵");
            Clear(291100394); Clear(291100397);
            That(session.CommitPendingLukePhase(repository), "引导终图同一实例提交真实phase1资格");
            var second = battle.LukeEligibility(actor.Character, 1)!;
            string secondFrozen = JsonSerializer.Serialize(second);
            That(second is { Source: PreyGuideRewards.Source, Phase: 1, Eligible: true } && battle.DueLukeRewards().Length == 0 &&
                !battle.PreyMovieCardsReady(actor, battle.View), "phase0演出确认不能解锁phase1演出、开窗或自动资产");
            Fill(second);
            session.End(); parties.Detach(session); online.Remove(owner.Account, session); owner.Dispose();
            int fallback = checked(script.Headers.Single(row => row.Tag == "[movie end time]").Values.Max(row => row.Number) + 120);
            using (var busy = owners.Acquire("prey-guide-auto", static () => { }))
            {
                clock.Advance(fallback - 1);
                That(Receipts(1) == 0 && battle.View is { State: AntonRaidBattleState.Reward, Phase: 1 } && battle.DueLukeRewards().Length == 0,
                    "引导断线不伪造第二阶段659，原电影最大值加120秒前不提前兑现");
                clock.Advance(1);
                That(Receipts(1) == 0 && battle.PreyRewardDue &&
                    clock.PendingDeadlines.Count(row => row.Instance == battle.Instance && row.Due == clock.GetTimestamp() + 1000) == 1,
                    "原期限恰到后离线账号有新owner时不接管，保留义务并按原秒tick重排");
            }
            clock.Advance(1);
            That(battle.View is { State: AntonRaidBattleState.Completed, Phase: 1 } && Receipts(0) == 2 && Receipts(1) == 2 &&
                JsonSerializer.Serialize(battle.LukeEligibility(actor.Character, 1)) == secondFrozen && errors.Count == 0,
                "新owner释放后原时钟按single冻结权益兑现离线两类奖励，准确完成真实第二阶段");
            using var recoveredOwner = owners.Acquire("prey-guide-auto", static () => { });
            var reopened = new CharacterRepository(path);
            var current = reopened.LoadCharacterById(recoveredOwner, actor.Character)!;
            var mailIds = new HashSet<uint>();
            foreach (var eligibility in new[] { first, second })
                foreach (var offer in eligibility.Offers)
                {
                    var target = new LukeRewardRecoveryTarget(actor.Character, battle.Instance, eligibility.Phase, offer.Kind, offer.Card) { RaidType = RaidKind.Prey };
                    var snapshot = reopened.ReadLukeRewardRecovery(recoveredOwner, current, target);
                    bool physical = offer.Grants.Any(grant => items.Items.Get(grant.ItemId).AccountMaterial is null);
                    That(snapshot.Claimed && (!physical || !snapshot.MailIds.IsEmpty) && snapshot.Prepared.Source == PreyGuideRewards.Source &&
                        snapshot.Event == eligibility.Event && snapshot.Anchor == eligibility.Anchor && snapshot.Prepared.Grants.SequenceEqual(offer.Grants),
                        "重开仓储读取精确single来源、阶段、事件、锚点、冻结产物；实体奖励保留原永久邮件回执");
                    foreach (uint id in snapshot.MailIds) mailIds.Add(id);
                    string committed = ClientSettingsSnapshots.Capture(path, true);
                    var replay = LukeRewardRecovery.Claim(recoveredOwner, current, target, eligibility.Event, eligibility.Anchor, reopened,
                        () => throw new InvalidOperationException("重复已交付权益不得加载资源或重新抽奖"), clock.GetUtcNow());
                    That(replay.Duplicate && replay.MailIds.SequenceEqual(snapshot.MailIds) && ClientSettingsSnapshots.Capture(path, true) == committed,
                        "发送失败或离线恢复后的重放只返回原MailIds，不新增资产、UID、邮件或收据");
                }
            foreach (var group in new[] { first, second }.SelectMany(row => row.Offers).SelectMany(offer => offer.Grants)
                .Where(grant => items.Items.Get(grant.ItemId).AccountMaterial is not null).GroupBy(grant => grant.ItemId))
                That(current.Inventory.AccountWallet.Material(group.Key).Balance == materialBefore.Material(group.Key).Balance + group.Sum(grant => grant.Count),
                    "两阶段账号材料按原钱包真实入账，不因填满实体背包改成邮件或重复兑现");
            That(mailIds.Count > 0 && Scalar("SELECT count(*) FROM mailbox_messages WHERE expires_at IS NULL") == mailIds.Count &&
                Scalar("SELECT count(*) FROM mailbox_messages") == mailIds.Count,
                "引导满包两阶段所有实际奖励邮件均为永久，不回落30天或另造补偿邮件");
            Console.WriteLine($"PASS {Count - before} 普雷引导自动揭示资格、发送泵失败、永久邮件与离线SQLite断言（非TCP）");

            async Task SceneReturn(GameConnectionContext scene, bool queued)
            {
                string mode = queued ? "排队" : "直接";
                int began;
                lock (writtenGate) began = written.Count;
                var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref movieWritten, shown);
                session.RaidBattleNoticeArrived = (view, currentActor) => RaidCombatProjection.Notice(scene, view, currentActor);
                await sender.Send(0, 28, new byte[10], "fixture_guide_scene_enter_skipped", () => false);
                That(!sender.DungeonSceneWritten, mode + "：守卫失效未写出的28不能标记客户端已进图");
                // 此28正文与下方返城回执是明确合成的场景输入，仅覆盖生产发送屏障，不冒充正式Run入图。
                await sender.Send(0, 28, new byte[10], "fixture_guide_scene_enter");
                That(sender.DungeonSceneWritten && session.Phase == SessionPhase.Town,
                    mode + "：实际28写出后即使服务端已在Town，也保留客户端副本场景事实");
                await sender.Send(1, 24, [1], "fixture_guide_scene_reply");
                That(sender.DungeonSceneWritten && !Frames().Any(row => row is { Kind: 0, Opcode: 24 }),
                    mode + "：同opcode的Kind1成功写出也不能清除副本场景或冒充真实Kind0返城");
                await sender.Send(0, 24, [], "fixture_guide_scene_return_skipped", () => false);
                That(sender.DungeonSceneWritten && !Frames().Any(row => row is { Kind: 0, Opcode: 24 }),
                    mode + "：守卫失效未写出的24不能提前清除客户端副本场景或冒充真实Kind0返城");
                battle.Publish(movie);
                await sender.DrainAsync();
                That(!Frames().Any(row => row is { Kind: 0, Opcode: 601 } or { Kind: 0, Opcode: 587, Body: [3, 1] }),
                    mode + "：客户端副本场景尚在时，真实Reward通知不得提前发601或587演出");
                var receipt = new TownTravelReceipt(session.Selected!, owner.Generation, session.SelectionEpoch)
                    { RaidBattleChange = new(movie, movie) };
                stream.HoldTownReturn();
                Task returning;
                if (queued)
                {
                    using (sender.QueuePublication(() => session.CanPublishTravel(receipt)))
                        returning = town.ReturnFromFormalDungeonAsync(scene, receipt, "fixture_guide_scene_return");
                }
                else returning = town.ReturnFromFormalDungeonAsync(scene, receipt, "fixture_guide_scene_return");
                try
                {
                    await stream.TownWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    That(queued ? returning.IsCompletedSuccessfully : !returning.IsCompleted,
                        mode + "：排队返城不等待网络，直接返城仍等待真实24完成");
                    battle.Publish(movie);
                    battle.Publish(movie);
                    That(sender.DungeonSceneWritten && !shown.Task.IsCompleted &&
                        !Frames().Any(row => row is { Kind: 0, Opcode: 24 or 601 } or { Kind: 0, Opcode: 587, Body: [3, 1] }),
                        mode + "：生产24写入被明确信号持住时，重复Battle通知仍不得提前启动演出");
                }
                finally
                {
                    stream.ReleaseTownWrite.TrySetResult();
                    await returning;
                }
                await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await sender.DrainAsync();
                var frames = Frames();
                int returned = Array.FindIndex(frames, row => row is { Kind: 0, Opcode: 24 });
                int result = Array.FindIndex(frames, row => row is { Kind: 0, Opcode: 601 });
                int played = Array.FindIndex(frames, row => row is { Kind: 0, Opcode: 587, Body: [3, 1] });
                That(!sender.DungeonSceneWritten && returned >= 0 && result > returned && played > result &&
                    frames.Count(row => row is { Kind: 0, Opcode: 24 }) == 1 && frames.Count(row => row is { Kind: 0, Opcode: 601 }) == 1 &&
                    frames.Count(row => row is { Kind: 0, Opcode: 587, Body: [3, 1] }) == 1 && frames[result].Body[1] == 0,
                    mode + "：真实24成功写出后唯一601真实phase0及587显示phase1顺序成立");
                battle.Publish(movie);
                await sender.DrainAsync();
                That(Frames().Count(row => row is { Kind: 0, Opcode: 601 }) == 1 &&
                    Frames().Count(row => row is { Kind: 0, Opcode: 587, Body: [3, 1] }) == 1,
                    mode + "：24回调与后续领域重放不重复创建同一演出");

                (byte Kind, ushort Opcode, byte[] Body)[] Frames()
                { lock (writtenGate) return written.Skip(began).ToArray(); }
            }
            void Clear(int dungeon)
            {
                var identity = new DungeonRunIdentity(actor.Character, 0, 1, Guid.NewGuid(), 1);
                using var reserved = battle.ReserveDungeon(new(battle.Instance, battle.View.Phase, 1, RaidKind.Prey), checked((uint)dungeon), [(actor, identity)]);
                That(reserved is not null, "引导使用真实当前阶段及单人Run预留：" + dungeon);
                reserved!.Install();
                That(battle.CompletePreyDungeon([(actor, new DungeonClearFact(identity, checked((uint)dungeon), [Guid.NewGuid()])
                    { ClearMap = new DungeonMap("boss", new(0, 0), 77, "fixture/prey-guide-auto.map") })]) is not null,
                    "引导清图事实经原领域推进，不直接手工设置奖励阶段");
                battle.ReleaseDungeon(actor, identity.InstanceId);
            }
            void Fill(RaidPhaseEligibility eligibility)
            {
                var templates = eligibility.Offers.SelectMany(offer => offer.Grants).Select(grant => items.Items.Get(grant.ItemId))
                    .Where(template => template.AccountMaterial is null)
                    .DistinctBy(template => template.Kind).ToArray();
                var filled = session.MutateInventory(role =>
                {
                    var packed = role.Inventory.Items;
                    foreach (var template in templates)
                    {
                        var range = ItemSlotBoundService.Range(template.Kind, role.Inventory);
                        var list = ItemSlotBoundService.List(template.Kind);
                        packed = packed.Where(row => row.Position.List != list || row.Position.Index < range.Start || row.Position.Index > range.End).ToImmutableArray();
                        for (short slot = range.Start; slot <= range.End; slot++)
                            packed = packed.Add(new(0, new(list, slot), InventoryRewardGrantService.Create(template, template.StackLimit)));
                    }
                    return new(true, 0, new InventoryPlan(role.Inventory, role.Inventory with { Items = packed }, "fixture_guide_full", new(role)));
                });
                That(filled.Accepted, "原在线库存事务填满该阶段实际奖励类别，后续发奖必须走真实邮件");
            }
        }
        finally { if (owner.IsCurrent) session.End(); }

        long Receipts(int phase) => Scalar("SELECT count(*) FROM character_raid_reward_receipts WHERE raid_type=3 AND phase=" + phase);
        long Scalar(string sql) { using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False"); db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
        void Sql(string sql) { using var db = new SqliteConnection($"Data Source={path};Pooling=False"); db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    }

    sealed class GuideResultFailureStream : MemoryStream
    {
        internal const string Marker = "guide result write failure";
        internal bool FailResults;
        internal int Failures;
        internal readonly TaskCompletionSource ResultWriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseResultWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool holdTownReturn;
        internal TaskCompletionSource TownWriteEntered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseTownWrite { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void HoldTownReturn()
        {
            TownWriteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ReleaseTownWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
            holdTownReturn = true;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (holdTownReturn && buffer.Length >= 3 && BinaryPrimitives.ReadUInt16LittleEndian(buffer.Span[1..]) == 24)
            {
                holdTownReturn = false;
                TownWriteEntered.TrySetResult();
                await ReleaseTownWrite.Task.WaitAsync(cancellationToken);
            }
            if (FailResults && buffer.Length >= 3 && BinaryPrimitives.ReadUInt16LittleEndian(buffer.Span[1..]) == 600)
            {
                FailResults = false;
                ResultWriteEntered.TrySetResult();
                await ReleaseResultWrite.Task.WaitAsync(cancellationToken);
                Failures++;
                throw new IOException(Marker);
            }
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}
