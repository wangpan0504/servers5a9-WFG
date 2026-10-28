using System.Collections.Immutable;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonAltarSessionTests
{
    public static void Run(TcpTestResources resources)
    {
        int before = Count;
        var archive = resources.Archive;
        var dungeons = SelfTestCatalogs.Dungeons(archive);
        var rooms = SelfTestCatalogs.Rooms(archive);
        var growth = SelfTestCatalogs.Growth(archive);
        var items = resources.ItemResources;
        var policy = new DungeonAltarRewardPolicy(DfoServer.GameWorld.Dungeons.DungeonAltarRewardCatalog.Load(archive),
            DfoServer.GameWorld.Dungeons.DungeonAltarClearItemCatalog.Load(archive), items.DungeonAltarItems,
            SelfTestCatalogs.KillExperience(archive), items.Items, items.GoldLimits, items.EpicPieces);
        var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 11006));
        var done = area.Quests.Concat(area.AnyQuests.Take(1)).Concat(area.Default.Bounds.SelectMany(row => row.Quests))
            .Concat(area.Default.DungeonGates.SelectMany(row => row.Quests)).Distinct().ToArray();
        var bounds = area.Map(done.ToHashSet()).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)),
            checked((ushort)(bounds.Y + bounds.Height / 2)));
        // 原生选图会自动携带关联支线任务；不能只测手工清零任务号的请求。
        foreach (var (dungeonId, questId, prerequisite) in new[] { (11006u, (ushort)5650, 63), (11007u, (ushort)7340, 7339) })
        {
            using var linked = New(new Clock(), done.Append(prerequisite).Distinct().ToArray());
            var accepted = linked.Session.ChangeQuest(questId, DfoServer.Game.Quests.QuestLifecycleAction.Accept,
                resources.Quests, items.Items, items.GoldLimits, items.EpicPieces);
            That(accepted?.Commit is not null, "合法接取祭坛关联支线：" + questId);
            That(linked.Session.BeginDungeonSelection() is not null, "关联任务祭坛建立原选图上下文");
            That(linked.Session.EnterFormalDungeon(dungeonId, (uint)(questId + 1), 0, dungeons, rooms, pieces: items.EpicPieces) is null &&
                linked.Session.EnterFormalDungeon(dungeonId == 11006 ? 11007u : 11006u, questId, 0, dungeons, rooms, pieces: items.EpicPieces) is null &&
                linked.Session.CaptureDungeonEnterCounts(dungeons.EnterCounts)!.Counts.All(row => row.Daily == 3) &&
                linked.Session.Selected!.Inventory.Gold == 500000 && linked.Session.DungeonSnapshot is null,
                "未接取或目标属于另一祭坛的任务仍拒绝，不扣费扣次或安装实例");
            var entered = linked.Session.EnterFormalDungeon(dungeonId, questId, 0, dungeons, rooms, pieces: items.EpicPieces);
            That(entered is not null, $"关联任务不阻断祭坛准入：{questId}/{linked.Session.LastFormalDungeonRejection}");
            That(entered!.Monsters.Altar is not null && entered.Entry.QuestId == questId && entered.Entry.RouteQuestId == 0 &&
                entered.Entry.QuestAcceptanceId == linked.Session.Selected!.ActiveMainQuests.Single(row => row.QuestId == questId).AcceptanceId,
                "祭坛复用普通任务路线资格并冻结原接取，仍使用专用波次与次数事务");
        }
        foreach (uint dungeonId in new uint[] { 11006, 11007 })
        {
            var clock = new Clock();
            using var fixture = New(clock);
            var session = fixture.Session;
            if (dungeonId == 11006)
            {
                int fee = dungeons.AltarAdmission.BloodFee(session.Selected!.Level, 0);
                That(session.MutateInventory(c => CurrencyService.SetGold(c, checked((uint)(fee - 1)))).Accepted,
                    "准备有次数但少一金币的无尽角色");
                var unpaid = session.Selected!;
                That(session.BeginDungeonSelection() is not null &&
                    session.EnterFormalDungeon(11006, 0, 0, dungeons, rooms, pieces: items.EpicPieces) is null &&
                    session.LastFormalDungeonRejection == "altar-entry-insufficient-gold" &&
                    ReferenceEquals(session.Selected, unpaid) && fixture.Read().Inventory.Gold == fee - 1 &&
                    session.CaptureDungeonEnterCounts(dungeons.EnterCounts)!.Counts.Single(row => row.Dungeon == 11006).Daily == 3,
                    "无尽次数不是免费资格，费用不足保留选图及全部次数，不安装Run");
                That(session.DungeonSnapshot is null && session.ReturnFromDungeonSelection() is not null &&
                    session.MutateInventory(c => CurrencyService.SetGold(c, 500000)).Accepted,
                    "失败后可退出选图补足费用，不残留半个实例");
            }
            var paid = session.Selected!;
            var countsBefore = session.CaptureDungeonEnterCounts(dungeons.EnterCounts)!;
            That(countsBefore.Counts.All(row => row.Daily == 3 && row.Weekly == 0) &&
                session.CanPublishDungeonEnterCounts(countsBefore), "祭坛空账本投影两条3次，回执绑定当前选角");
            fixture.Sql("CREATE TRIGGER fail_count_entry BEFORE INSERT ON dungeon_special_entry_receipts BEGIN SELECT RAISE(ABORT,'count rollback'); END");
            try
            {
                Reject<Microsoft.Data.Sqlite.SqliteException>(() => Enter(session, dungeonId), "实际入场回执失败回滚计数和扣费");
                That(session.CaptureDungeonEnterCounts(dungeons.EnterCounts)!.Counts.All(row => row.Daily == 3) &&
                    session.Selected!.Inventory.Gold == paid.Inventory.Gold && session.CanPublishDungeonEnterCounts(countsBefore),
                    "SQL回滚后仍是原角色快照与3次，不投影未提交的扣次");
            }
            finally { fixture.Sql("DROP TRIGGER fail_count_entry"); }
            var entered = Enter(session, dungeonId);
            That(!session.CanPublishDungeonEnterCounts(countsBefore) &&
                session.CaptureDungeonEnterCounts(dungeons.EnterCounts)!.Counts.Single(row => row.Dungeon == dungeonId).Daily == 2,
                "原扣费事务提交后投影2次，旧角色快照不能再发布3次");
            That(entered.Monsters.Altar is not null && entered.SpecialEntry is not null &&
                entered.Character.Inventory.Gold == paid.Inventory.Gold - entered.SpecialEntry.Gold &&
                entered.Character.Fatigue == paid.Fatigue, "实际祭坛准入使用专用演员，扣费与次数共提交而不扣疲劳");
            int roomsCleared = 0, actorsKilled = 0;
            while (true)
            {
                var loaded = session.CompleteFormalDungeonLoading()!;
                That(session.StartDungeonAltar(loaded) is null && session.PrepareDungeonAltarUi(loaded),
                    "实际房间必须完成UI准备才能启动波次");
                var current = session.StartDungeonAltar(loaded)!;
                var nextRooms = entered.Entry.Maze.StaticTopology.Links!.SelectMany(link =>
                    new[] { (From:link.First,To:link.Second), (From:link.Second,To:link.First) })
                    .Where(edge => edge.From == loaded.Snapshot.CurrentMap.Position && edge.To.X > edge.From.X)
                    .Select(edge => edge.To).ToArray();
                var firstActor = loaded.Monsters.Altar!.Actors[0];
                That(session.ObserveDungeonAltarDeath(Observation(firstActor.ObjectKey)) is null,
                    "两祭坛39不能确认已分配但尚未计时发布的对象");
                while (!current.State.Completed)
                {
                    if (current.State.WaitingForDifficulty)
                        current = session.SelectDungeonAltarDifficulty(current.State.Choices.Last())!;
                    clock.AdvanceTo(current.Due ?? throw new InvalidOperationException("祭坛尚未完成但没有后续计时"));
                    current = session.TickDungeonAltar(current) ?? throw new InvalidOperationException("实际波次到期没有生成");
                    if (current.Batch is { } batch)
                    {
                        actorsKilled += batch.Actors.Length;
                        var keys = batch.Actors.Select(row => row.ObjectKey).ToImmutableArray();
                        if (dungeonId == 11007 || current.State.Round == current.State.Plan.Definition.RoundCount - 1 && current.State.NextDue is null)
                        {
                            if (nextRooms is [var pendingRoom])
                                That(session.MoveFormalDungeon(checked((byte)pendingRoom.X), checked((byte)pendingRoom.Y)) is null,
                                    "无尽末批已经发布但尾怪尚未确认死亡时仍不能提前换房");
                            foreach (var actor in batch.Actors)
                            {
                                var observation = Observation(actor.ObjectKey);
                                var zero = new NativeDungeonScriptedDeathObservation(actor.ObjectKey,1,true,new string('C',64));
                                That(session.ObserveDungeonAltarScriptedDeath(zero with { ChecksumMatches = false }) is null &&
                                    session.ObserveDungeonAltarScriptedDeath(zero with { MonsterKey = uint.MaxValue }) is null &&
                                    session.ObserveDungeonAltarScriptedDeath(zero with { MonsterKey = ushort.MaxValue }) is null,
                                    "祭坛零参与者39仍拒绝错误摘要、溢出键和未发布对象");
                                That(session.ObserveDungeonAltarDeath(observation with { ChecksumMatches = false }) is null &&
                                    session.ObserveDungeonAltarDeath(observation with { MonsterKey = uint.MaxValue }) is null &&
                                    session.ObserveDungeonAltarDeath(observation with { MonsterKey = ushort.MaxValue }) is null,
                                    "祭坛39拒绝错误校验和、溢出键与未知对象，仍可处理后续合法击杀");
                                current = session.ObserveDungeonAltarDeath(observation)
                                    ?? throw new InvalidOperationException("祭坛实际39死亡未推进");
                                var deathReplay = session.ObserveDungeonAltarDeath(observation);
                                var zeroReplay = session.ObserveDungeonAltarScriptedDeath(zero);
                                That(deathReplay is { RoundAdvanced: false } && deathReplay.Round == current.Round &&
                                    zeroReplay is { RoundAdvanced: false } && zeroReplay.Round == current.Round &&
                                    ReferenceEquals(session.Selected, entered.Character),
                                    "祭坛重复39只补发退场资格，不重复推进或写普通成长/掉落");
                            }
                            That(session.ObserveDungeonAltarDeaths(keys, new string('B',64)) is null,
                                "39之后重复255共用原死亡表，不推进另一回合");
                        }
                        else
                        {
                            current = session.ObserveDungeonAltarDeaths(keys, new string('A',64))
                                ?? throw new InvalidOperationException("实际祭坛对象死亡未推进");
                            That(session.ObserveDungeonAltarDeath(Observation(keys[0])) is { RoundAdvanced: false },
                                "255之后重报39可补发退场资格，不因上报协议不同重复推进");
                        }
                    }
                }
                roomsCleared++;
                if (session.Phase == SessionPhase.Settlement) break;
                var next = nextRooms.Single();
                That(session.MoveFormalDungeon(checked((byte)next.X),checked((byte)next.Y)) is not null,
                    "无尽必须清完本房五回合才能沿实际地图连接换房");
                That(!session.CanPublishDungeonAltar(current) && session.TickDungeonAltar(current) is null &&
                    session.ObserveDungeonAltarDeath(Observation(current.State.Plan.Actors[^1].ObjectKey)) is null,
                    "换房后旧房间的计时、尾怪重报及发布资格全部失效，不阻断新房加载");
            }
            var result = session.BeginDungeonAltarSettlement(policy,growth)!;
            That(result.Result is { Stage: 0 } && result.Result.Pending.Fact.CompletedRounds == (dungeonId == 11006 ? 15 : 7) &&
                roomsCleared == (dungeonId == 11006 ? 3 : 1) && actorsKilled > 0,
                "实际十五或七回合只进入专用结算，不走普通翻牌");
            long revision = fixture.Read().Revision;
            var replay = session.BeginDungeonAltarSettlement(policy,growth)!;
            That(replay.Result!.Commit.Duplicate == result.Result!.Commit.Duplicate && fixture.Read().Revision == revision &&
                ReferenceEquals(replay.Result.Pending,result.Result.Pending), "重复结算复用冻结奖励，不重抽或二次发奖");
            That(!session.MutateInventory(c => CurrencyService.SetGold(c,1), requireTown:false).Accepted,
                "结算演出前不能并发修改已提交但待投影资产");
            for (int stage = 1; stage <= 2; stage++)
            {
                That(session.TickDungeonAltar(result)?.Result?.Stage == stage-1,
                    "结算计时提前唤醒只续等，不推进演出阶段");
                clock.AdvanceTo(result.Due!.Value);
                result = session.TickDungeonAltar(result)!;
                That(result.Result?.Stage == stage, "祭坛奖励及到账按八秒阶段推进");
            }
            clock.AdvanceTo(result.Due!.Value);
            That(session.TickDungeonAltar(result)?.Returned is not null && session.Phase == SessionPhase.Town &&
                fixture.Read().Revision >= revision, "专用结算结束后回原城镇并保留已到账资产");
            Enter(session, dungeonId);
            var reloaded = session.CompleteFormalDungeonLoading()!;
            That(session.PrepareDungeonAltarUi(reloaded), "再次挑战使用新实例的UI准备身份");
            var abandoned = session.StartDungeonAltar(reloaded)!;
            That(session.ReturnFromFormalDungeon() is not null && !session.CanPublishDungeonAltar(abandoned) &&
                session.TickDungeonAltar(abandoned) is null && session.DungeonSnapshot is null &&
                session.ObserveDungeonAltarDeath(Observation(entered.Monsters.Altar!.Actors[0].ObjectKey)) is null &&
                session.CaptureDungeonEnterCounts(dungeons.EnterCounts)!.Counts.Single(row => row.Dungeon == dungeonId).Daily == 1,
                "祭坛已启动波次退出后旧计时不能发布或复活实例，已提交的两次入场不退款");
            session.End();
            That(!session.CanPublishDungeonAltar(abandoned) && session.TickDungeonAltar(abandoned) is null,
                "会话结束后原祭坛回调继续失效");
        }
        {
            var clock = new Clock();
            using var fixture = New(clock);
            var session = fixture.Session;
            session.MutateInventory(c => new(true,0,new(c.Inventory,c.Inventory with { ReviveCoins = 20 },"fixture_coins")));
            Enter(session,11007);
            var loaded = session.CompleteFormalDungeonLoading()!;
            That(session.CurrentDungeonAltarResult() is null, "尚未提交祭坛结算时不能手动完成结果页");
            session.PrepareDungeonAltarUi(loaded);
            var active = session.StartDungeonAltar(loaded)!;
            DungeonAltarReceipt? old = null;
            for (int count = 0; count < 7; count++)
            {
                That(session.ReportFormalCharacterDeath() is not null,"祭坛实际死亡进入复活窗口");
                var waiting = session.WaitDungeonAltarRevival()!;
                That(session.BeginDungeonAltarSettlement(policy,growth) is null && !session.CanFinishDungeonAltarDeath(waiting),
                    "十秒前不能通过死亡或255重发提前结算");
                That(session.ReviveFormalCharacter(session.Selected!.Id)?.Accepted == true &&
                    session.ResumeDungeonAltar() is not null && !session.CanFinishDungeonAltarDeath(waiting),
                    "扣币成功恢复原祭坛，旧死亡回调不能结算新生命");
                old = waiting;
            }
            session.ReportFormalCharacterDeath();
            var final = session.WaitDungeonAltarRevival()!;
            That(session.ReviveFormalCharacter(session.Selected!.Id)?.Accepted == false &&
                session.Selected.Inventory.ReviveCoins == 13, "当前PVF极限七币上限生效，第八次不扣币");
            clock.AdvanceTo(final.Due!.Value);
            That(!session.CanFinishDungeonAltarDeath(old!) && session.CanFinishDungeonAltarDeath(final),
                "同实例早先死亡序号失效，只有本次十秒截止能失败结算");
            var result = session.BeginDungeonAltarSettlement(policy,growth)!;
            That(result.Result?.Pending.Fact is { Cleared:false, CompletedRounds:0 },
                "十秒未复活才冻结零回合失败，没有虚构通关");
            long revision = fixture.Read().Revision;
            var stage0 = session.CurrentDungeonAltarResult()!;
            var stage1 = session.AdvanceDungeonAltarResult(stage0)!;
            That(stage0.Result?.Stage == 0 && stage1.Result?.Stage == 1 &&
                !session.CanPublishDungeonAltar(stage0) && session.AdvanceDungeonAltarResult(stage0) is null,
                "手动回城立即推进排名阶段，旧回执不能重复推进或发布");
            var stage2 = session.AdvanceDungeonAltarResult(stage1)!;
            That(stage2.Result?.Stage == 2 && session.CurrentDungeonAltarResult()?.Result?.Stage == 2 &&
                !session.CanPublishDungeonAltar(stage1) && session.AdvanceDungeonAltarResult(stage1) is null &&
                session.AdvanceDungeonAltarResult(stage2) is null && fixture.Read().Revision == revision,
                "手动回城依次推进奖励和资产投影，重放不重复写入奖励");
            That(session.ReturnFromFormalDungeon() is not null && session.Phase == SessionPhase.Town &&
                session.CurrentDungeonAltarResult() is null && !session.CanPublishDungeonAltar(stage2) &&
                session.TickDungeonAltar(stage2) is null && fixture.Read().Revision >= revision,
                "手动回城提交后旧结算和自动计时失效");
        }
        Console.WriteLine($"PASS {Count-before} 实际祭坛入口、全波次、专用结算、复活窗口和币上限断言");

        QuestSceneTests.Fixture New(Clock clock, int[]? completed = null)
        {
            var fixture = new QuestSceneTests.Fixture(resources.Towns,resources.Characters,resources.Learning,growth,
                resources.Quests,false,70,completed ?? done,position:position,timeProvider:clock);
            That(fixture.Session.MutateInventory(c => CurrencyService.SetGold(c,500000)).Accepted,"准备隔离角色祭坛费用");
            return fixture;
        }
        FormalDungeonReceipt Enter(GameSession session,uint id)
        {
            That(session.BeginDungeonSelection() is not null,"实际亡者峡谷建立选图上下文");
            return session.EnterFormalDungeon(id,0,0,dungeons,rooms,items: null,draw:_=>0,pieces:items.EpicPieces)
                ?? throw new InvalidOperationException("祭坛实际准入失败："+session.LastFormalDungeonRejection);
        }

        NativeDungeonDeathObservation Observation(ushort key) => new(key, 1, 0x111, 1, 600, 1, 0, 70, 0, 650, 1,
            true, new string('A',64));
    }

    sealed class Clock : TimeProvider
    {
        long elapsed;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => elapsed;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(1792756800).AddMilliseconds(elapsed);
        public void AdvanceTo(long value) => elapsed = Math.Max(elapsed,value);
    }
}
