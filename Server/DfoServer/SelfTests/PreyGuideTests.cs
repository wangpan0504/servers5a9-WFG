using DfoServer.GameWorld.Raids;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Testing;
using DfoServer.Network.Builders;
using DfoServer.Game.BattlePass;
using DfoServer.GameWorld.BattlePass;
using DfoServer.Network;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using System.Net;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PreyGuideTests
{
    internal static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
        => await RunFixtureAsync(options, root, resources, false);

    internal static async Task RunBattlePassNoticeAsync(ServerOptions options, string root, TcpTestResources resources)
        => await RunFixtureAsync(options, root, resources, true);

    static async Task RunFixtureAsync(ServerOptions options, string root, TcpTestResources resources, bool battlePass)
    {
        var definition = RaidCatalog.Load(resources.Archive).Find(3)!;
        var raid = RaidResourceLoader.Load(resources.Archive, definition,
            SelfTestCatalogs.Dungeons(resources.Archive), resources.ItemResources.Items);
        That(raid.Script.Issues.IsEmpty, "普雷引导脚本完整：" + string.Join(';', raid.Script.Issues));
        That(raid.ResourcesReady, "普雷引导资源完整：" + string.Join(';', raid.Issues) + ";dgn=" + string.Join(',', raid.MissingFormalDungeons) + ";map=" + string.Join(',', raid.MissingFormalMaps) + ";item=" + string.Join(',', raid.MissingItems));
        That(raid.Script.EntryCost == new RaidCost(10309342, 1), "引导使用独立门票");
        That(raid.Script.Phases.Length == 2 && raid.Script.Phases[1].Dungeons.Any(row => row.Id == 291100397), "引导两阶段包含真实终图");
        That(raid.EntryCatalog is { Ready: true }, "引导正式进图目录完整");
        if (!battlePass) DatabaseSchemaTests.PreyGuideUpgrade(Path.Combine(root, "prey-schema"));
        await VerifyLifecycleAsync(root, resources, definition with { Resources = raid }, battlePass);
    }

    static async Task VerifyLifecycleAsync(string root, TcpTestResources resources, RaidDefinition definition, bool battlePass)
    {
        string path = Path.Combine(root, "prey-domain", "game.db");
        var repo = new CharacterRepository(path);
        var owners = new AccountOwners();
        using var owner = owners.Acquire("prey-guide-domain", () => { });
        var clock = new LukeRaidClockTests.Clock(battlePass ? DateTimeOffset.Parse("2026-11-03T00:00:00Z") : null);
        if (battlePass)
            repo.ConfigureBattlePassMissions(new BattlePassMissionPolicy("prey-notice-fixture",
                DateTimeOffset.Parse("2026-10-29T06:00:00+08:00"), BattlePassCatalog.Load(resources.Archive)));
        var point = new Location(59, 0, 445, 377);
        var created = repo.Create(owner, CharacterCreation.Prepare("PreyGuide", resources.Characters.Get(0), point, point, resources.Learning.Get)
            with { Level = 95, AcceptedStoryLevel = 95, CompletedQuests = [8935] });
        var items = resources.ItemResources;
        var inventory = new InventoryService(items.Items, items.GoldLimits, items.EpicPieces);
        InventoryService.Commit(repo, owner, created, inventory.Grant(created, [new(10309342, 2)]));
        var session = new GameSession(repo, resources.Towns, owner, entry: new(new(2, 42), 50, "[prey]"), timeProvider: clock);
        session.Select(0); session.MarkReady();
        try
        {
            var registry = new RaidRegistry(clock);
            var actor = session.CaptureRaidActor(registry)!;
            That(actor is not null, "真实95级普雷频道角色可取得团本身份");
            var party = registry.Create(actor!, 3, "引导", "guide"u8.ToArray(), 1, 1, guide: true)!;
            That(party is { Guide: true, Capacity: 1 } && party.Members[0].Squad == 1, "引导单人团自动分队且不开放邀请容量");
            var parties = new PartyService(repo);
            That(parties.SetInformation(session, 0, PartySettings.Default with { Title = "existing", Capacity = 1 }).Error == 0,
                "已有普通单人队保持合法");
            var existingParty = parties.Current(session);
            await using var timers = new LukeRaidClocks(clock, repo, (_, error) => throw new InvalidOperationException("Prey clock failed", error));
            var start = new AntonRaidStart(registry, parties, repo, resources.Towns, items.EpicPieces, clock,
                timers: timers, preyRewards: new(definition.Resources!.Script, items.Items, items.Boosters));
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "INSERT INTO character_raid_runs(character_id,account,instance_id,raid_type,source,admitted_at,state) VALUES($id,$account,$instance,3,'daily-fixture',$at,1)";
                command.Parameters.AddWithValue("$id", session.Selected!.Id);
                command.Parameters.AddWithValue("$account", owner.Account);
                command.Parameters.AddWithValue("$at", clock.GetUtcNow().ToUnixTimeSeconds());
                command.Parameters.Add("$instance", SqliteType.Text);
                for (int i = 0; i < 10; i++)
                { command.Parameters["$instance"].Value = Guid.NewGuid().ToString("N"); command.ExecuteNonQuery(); }
                var before = session.Selected;
                That(start.Start(actor!, party, definition) is { Error: 19, Reason: "prey-daily-account-enter-limit" } &&
                    ReferenceEquals(before, session.Selected) && registry.Find(actor!)?.Started is null && session.CurrentRaidBattle(actor!) is null,
                    "账号十次日上限拒绝不扣票、不安装队伍或战斗状态");
                command.CommandText = "DELETE FROM character_raid_runs WHERE source='daily-fixture'";
                command.ExecuteNonQuery();
            }
            var result = start.Start(actor!, party, definition);
            That(result.Error == 0 && result.Receipt?.Battle is not null, "普雷引导扣票与正式聚合原子启动：" + result.Reason);
            That(ReferenceEquals(existingParty, parties.Current(session)), "引导开战复用已有普通单人队，不重复建队");
            var battle = session.CurrentRaidBattle(actor!)!;
            That(!battle.AllowsPreyEntry(291100391), "引导准备倒计时内不能提前进图");
            clock.Advance(25);
            That(battle.View.Symbols["[prepare raid]"] == 1, "资源25秒准备结束后开放正式进图");
            int branch = battle.View.Symbols["[single raid type]"];
            foreach (int dungeon in branch == 0 ? new[] { 291100391, 291100393 } : new[] { 291100390, 291100392 }) Clear(dungeon);
            That(battle.HasPendingTransition && battle.View.State == AntonRaidBattleState.Fighting, "圣地完成须等待退出及阶段资格SQL屏障");
            await Settle(0);
            That(battle.View.Phase == 1 && battle.View.State == AntonRaidBattleState.Fighting, "引导第一阶段领取后进入第二阶段");
            That(!battle.AllowsPreyEntry(291100397), "未通激战地不得进入天空巢穴");
            Clear(291100394);
            That(battle.AllowsPreyEntry(291100397), "激战地一次正式清图开放天空巢穴");
            Clear(291100397);
            await Settle(1);
            if (battlePass) return;
            That(battle.View.State == AntonRaidBattleState.Completed, "引导终图一次清图完成两阶段，不扣9999次HP");
            That(repo.HasQuestDungeonClear(owner, session.Selected!, 291100397, 1), "真实引导生命周期生成6220所需的持久通关历史");
            That(!repo.HasQuestDungeonClear(owner, session.Selected!, 400000046, 1), "引导历史不能冒充普通普雷通关");
            That(!repo.HasQuestDungeonClear(owner, session.Selected!, 291100397, 2), "同一实例不重复累计隐藏任务历史");
            VerifyShip(definition.Resources!.Script, actor!);
            var packetOffer = new RaidRewardOffer(1, 0, [new(0x12345678, 3)], [])
            { PreyDisplay = [new(0, 0x12345678, 3, 0, 0, 0)] };
            That(PreyRewardPackets.Reward(0x1234, packetOffer).SequenceEqual(new byte[]
                { 1, 1, 0x34, 0x12, 0, 1, 0, 0x78, 0x56, 0x34, 0x12, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
                "普雷600独立字节预期：6字节表头与16字节物品");

            void Clear(int dungeon)
            {
                That(battle.AllowsPreyEntry(dungeon), "本分支正式目标可进入：" + dungeon);
                var identity = new DungeonRunIdentity(actor!.Character, 0, 1, Guid.NewGuid(), 1);
                using var reservation = battle.ReserveDungeon(new(battle.Instance, battle.View.Phase, 1, RaidKind.Prey),
                    checked((uint)dungeon), [(actor, identity)]);
                That(reservation is not null, "正式Run取得原实例预留");
                reservation!.Install();
                var fact = new DungeonClearFact(identity, checked((uint)dungeon), [Guid.NewGuid()])
                    { ClearMap = new DungeonMap("boss", new(0, 0), 77, "fixture/prey-boss.map") };
                int oldClears = battle.RosterClears(battle.View)!.GetValueOrDefault(actor.Character);
                That(battle.CompletePreyDungeon([(actor, fact)]) is not null, "合成权威事实验证领域推进");
                That(battle.CompletePreyDungeon([(actor, fact)]) is null, "正式Run清图重复回放幂等");
                That(battle.RosterClears(battle.View)!.GetValueOrDefault(actor.Character) == oldClears + 1,
                    "普雷通关列复用本阶段真实参与次数，重放不增计数");
                battle.ReleaseDungeon(actor, identity.InstanceId);
            }
            async Task Settle(int phase)
            {
                That(session.CommitPendingLukePhase(repo), "阶段资格与真实类型3同事务提交");
                That(battle.View.State == AntonRaidBattleState.Reward && battle.LukeEligibility(actor!.Character, phase) is { Eligible: true }, "资格提交后开放本阶段奖励");
                if (battlePass && phase == 1)
                {
                    var noticeActor = actor ?? throw new InvalidOperationException("Missing raid actor");
                    var state = repo.ReadBattlePassMissions(owner, clock.GetUtcNow())!;
                    That(state.Slots.Single(row => row.Instance.Key.Id == 59).Count == 1 &&
                        battle.LukeEligibility(noticeActor.Character, phase) is { CountsAsClear: true, Source: PreyGuideRewards.Source },
                        "原普雷引导最终计次已同事务推进59，尚未领取奖励");
                    var view = battle.View;
                    That(session.ReadBattlePassRaidCompletionState(noticeActor with { Epoch = noticeActor.Epoch + 1 }, view) is null &&
                        session.ReadBattlePassRaidCompletionState(noticeActor, view with { Instance = Guid.NewGuid() }) is null,
                        "旧选角与旧实例不能读取攻坚任务投影");
                    using var stop = new CancellationTokenSource();
                    using var stream = new MemoryStream();
                    byte[]? published = null;
                    await using var sender = new GameConnectionSender(owner, stream, stop, 0, (_, _, _, body, action) =>
                    {
                        if (action == "battle_pass_raid_completion") published = body.ToArray();
                    });
                    var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), owner, session, sender);
                    RaidCombatProjection.Notice(context, view, noticeActor);
                    await sender.DrainAsync();
                    That(published is not null && published.SequenceEqual(state.ToPayload()),
                        "普雷引导终结后由原Notice发送权威1801，不依赖领奖");
                    using var other = new AccountOwners().Acquire("different-account", () => { });
                    That(session.ReadBattlePassRaidCompletionState(noticeActor with { Owner = other }, view) is null,
                        "其他owner不能读取原成员攻坚投影");
                    session.End();
                    using var replacement = owners.Acquire("prey-guide-domain", () => { });
                    Reject<InvalidOperationException>(() => session.ReadBattlePassRaidCompletionState(noticeActor, view),
                        "旧owner generation不能继续读取攻坚投影");
                    return;
                }
                foreach (byte kind in new byte[] { 1, 0 })
                {
                    var receipt = session.ClaimRaidReward(kind, 0, repo, items.Items, items.GoldLimits, items.EpicPieces, clock.GetUtcNow());
                    That(receipt is not null && receipt.Prepared.RaidType == RaidKind.Prey, "引导独立奖池实际入库");
                    var revision = session.Selected!.Revision;
                    var replay = session.ClaimRaidReward(kind, 0, repo, items.Items, items.GoldLimits, items.EpicPieces, clock.GetUtcNow());
                    That(replay is { Commit.Duplicate: true } && session.Selected!.Revision == revision,
                        "重复领取回放原收据且不再次发放资产");
                    session.ConfirmRaidRewardPublished(receipt!);
                }
            }
        }
        finally { if (owner.IsCurrent) session.End(); }
    }

    static void VerifyShip(RaidScript script, RaidActor actor)
    {
        var branches = new HashSet<int>();
        for (int seed = 0; branches.Count < 2 && seed < 20; seed++)
        {
            var clock = new LukeRaidClockTests.Clock();
            var battle = new AntonRaidBattle(script, kind: RaidKind.Prey, clock: clock, preyRandom: new Random(seed));
            int branch = battle.View.Symbols["[single raid type]"];
            if (!branches.Add(branch)) continue;
            int[] sanctuaries = branch == 0 ? [291100391, 291100393] : [291100390, 291100392];
            That(sanctuaries.All(id => battle.View.Dungeons[id] == RaidDungeonState.Open) &&
                battle.View.Dungeons[branch == 0 ? 291100390 : 291100391] == RaidDungeonState.Hide,
                "两分支只开放对应圣地，隐藏另一分支");
            That(Enumerable.Range(1, 16).Count(slot => battle.View.Symbols[$"[area_{slot}_dgn_type]"] == 5) == 3 &&
                Enumerable.Range(1, 16).Count(slot => battle.View.Symbols[$"[area_{slot}_dgn_type]"] == 4) == 2,
                "战略地图具有真实防御及圣地区域类型");
            That(battle.Join(actor, [new("ship", [new(0, 0, [new(1, 1)], [])], new(0, 0, 0, 0, 0, 0)),
                new("ship", [new(0, 0, [new(1, 1)], [])], new(0, 1, 0, 0, 0, 0))]), "船体用例登记原角色");
            Expire(new(9000, 0));
            That(battle.AllowsPreyEntry(sanctuaries[0]), "准备后本分支允许进图");
            Expire(new(9001, 0));
            That(battle.View.Symbols["[slenikon hp]"] == 9983, "资源船体自然损耗为2秒17点本服策略");
            int defence = battle.View.Symbols["[area_2]"];
            var identity = new DungeonRunIdentity(actor.Character, 0, 1, Guid.NewGuid(), 1);
            using var defending = battle.ReserveDungeon(new(battle.Instance, 0, 1, RaidKind.Prey), checked((uint)defence), [(actor, identity)]);
            That(defending is not null, "防御Run可按真实区域预留");
            defending!.Install();
            Expire(new(1, 1000));
            That(battle.View.Symbols["[slenikon hp]"] == 9983, "正在防御的Run不按漏守重复扣船体");
            battle.ReleaseDungeon(actor, identity.InstanceId);
            That(battle.View.Symbols["[slenikon hp]"] == 7983 && battle.View.Symbols["[area_2]"] == 0,
                "放弃防御只损失资源2000点并移除区域");
            while (battle.View.Symbols["[slenikon hp]"] > 2000) Expire(new(9001, 0));
            Expire(new(1, 1000));
            Expire(new(1, 1000));
            That(!battle.AllowsPreyEntry(sanctuaries[0]) && battle.View.Timers.IsEmpty,
                "漏守耗尽船体终止实例且不续计时器、不生成通关或奖励");
            void Expire(RaidTimer timer) => battle.ExpireLukeTimer(battle.Instance, timer, battle.View.TimerGenerations[timer]);
        }
        That(branches.SetEquals([0, 1]), "两种资源分支均覆盖");
    }
}


