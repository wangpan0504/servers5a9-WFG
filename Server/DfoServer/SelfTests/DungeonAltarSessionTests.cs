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
        foreach (uint dungeonId in new uint[] { 11006, 11007 })
        {
            var clock = new Clock();
            using var fixture = New(clock);
            var session = fixture.Session;
            var paid = session.Selected!;
            var entered = Enter(session, dungeonId);
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
                while (!current.State.Completed)
                {
                    if (current.State.WaitingForDifficulty)
                        current = session.SelectDungeonAltarDifficulty(current.State.Choices.Last())!;
                    clock.AdvanceTo(current.Due ?? throw new InvalidOperationException("祭坛尚未完成但没有后续计时"));
                    current = session.TickDungeonAltar(current) ?? throw new InvalidOperationException("实际波次到期没有生成");
                    if (current.Batch is { } batch)
                    {
                        actorsKilled += batch.Actors.Length;
                        current = session.ObserveDungeonAltarDeaths(batch.Actors.Select(row => row.ObjectKey).ToImmutableArray(),
                            new string('A',64)) ?? throw new InvalidOperationException("实际祭坛对象死亡未推进");
                    }
                }
                roomsCleared++;
                if (session.Phase == SessionPhase.Settlement) break;
                var next = entered.Entry.Maze.StaticTopology.Links!.SelectMany(link =>
                    new[] { (From:link.First,To:link.Second), (From:link.Second,To:link.First) })
                    .Where(edge => edge.From == loaded.Snapshot.CurrentMap.Position && edge.To.X > edge.From.X)
                    .Select(edge => edge.To).Single();
                That(session.MoveFormalDungeon(checked((byte)next.X),checked((byte)next.Y)) is not null,
                    "无尽必须清完本房五回合才能沿实际地图连接换房");
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
        }
        {
            var clock = new Clock();
            using var fixture = New(clock);
            var session = fixture.Session;
            session.MutateInventory(c => new(true,0,new(c.Inventory,c.Inventory with { ReviveCoins = 20 },"fixture_coins")));
            Enter(session,11007);
            var loaded = session.CompleteFormalDungeonLoading()!;
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
            That(session.BeginDungeonAltarSettlement(policy,growth)?.Result?.Pending.Fact is
                { Cleared:false, CompletedRounds:0 }, "十秒未复活才冻结零回合失败，没有虚构通关");
        }
        Console.WriteLine($"PASS {Count-before} 实际祭坛入口、全波次、专用结算、复活窗口和币上限断言");

        QuestSceneTests.Fixture New(Clock clock)
        {
            var fixture = new QuestSceneTests.Fixture(resources.Towns,resources.Characters,resources.Learning,growth,
                resources.Quests,false,70,done,position:position,timeProvider:clock);
            That(fixture.Session.MutateInventory(c => CurrencyService.SetGold(c,500000)).Accepted,"准备隔离角色祭坛费用");
            return fixture;
        }
        FormalDungeonReceipt Enter(GameSession session,uint id)
        {
            That(session.BeginDungeonSelection() is not null,"实际亡者峡谷建立选图上下文");
            return session.EnterFormalDungeon(id,0,0,dungeons,rooms,items: null,draw:_=>0,pieces:items.EpicPieces)
                ?? throw new InvalidOperationException("祭坛实际准入失败："+session.LastFormalDungeonRejection);
        }
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
