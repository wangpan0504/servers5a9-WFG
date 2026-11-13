using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DisasterDungeonTests
{
    static FormalDungeonReceipt VerifyStoneReward(QuestSceneTests.Fixture fixture, FormalDungeonReceipt receipt, int tier,
        DungeonKillExperienceCatalog kills, CharacterGrowthCatalog growth, ItemResources items, bool requireExhaustedMove = false)
    {
        var start = receipt.Snapshot.CurrentMap.Position;
        var boss = receipt.Entry.BossMap.Position;
        var previous = new Dictionary<DungeonPoint, DungeonPoint> { [start] = start };
        var queue = new Queue<DungeonPoint>();
        queue.Enqueue(start);
        while (queue.TryDequeue(out var here))
            foreach (var link in receipt.Entry.Maze.StaticTopology.Links!)
            {
                DungeonPoint? next = link.First == here ? link.Second : link.Second == here ? link.First : null;
                if (next is { } point && previous.TryAdd(point, here)) queue.Enqueue(point);
            }
        That(previous.ContainsKey(boss), "五张灾难实图均有通向Boss房的原路线");
        var path = new Stack<DungeonPoint>();
        for (var at = boss; at != start; at = previous[at]) path.Push(at);
        int exhaustedMoves = 0;
        while (path.TryPop(out var next))
        {
            ClearBlocking(fixture, receipt, kills, growth);
            bool exhausted = fixture.Read().Fatigue!.Remaining == 0;
            That(fixture.Session.MoveFormalDungeon(checked((byte)next.X), checked((byte)next.Y)) is not null,
                "按原路线进入灾难下一个房间");
            receipt = fixture.Session.CompleteFormalDungeonLoading() ?? throw new InvalidOperationException("灾难Boss路线加载失败");
            if (exhausted)
            {
                exhaustedMoves++;
                That(fixture.Read().Fatigue!.Remaining == 0, "0疲劳仍可沿路线跨房，余额保持0");
            }
        }
        That(!requireExhaustedMove || exhaustedMoves > 0, "8点入场场景明确覆盖疲劳耗0后的继续跨房");
        var stones = receipt.Monsters.Monsters.Where(monster => DisasterDungeonPolicy.IsRewardStone(receipt.Entry, monster)).ToArray();
        That(stones.Length == 5 && stones.Select(monster => monster.MonsterId).Order().SequenceEqual(Enumerable.Range(63489, 5)),
            "Boss房五档石头均绑定真实MOB路径和本实例对象键");
        var selected = stones.Single(monster => monster.MonsterId == 63489 + tier);
        var prepared = DisasterDungeonPolicy.PrepareStoneDrop(receipt.Entry, selected, items.Items.Get)!;
        That(prepared.MaximumObjects == 1 && prepared.Generate(_ => 0, DateTimeOffset.UnixEpoch).Single().Item is
            { Count: 1 } box && box.ItemId == 490012195 + tier, "每档只预留并生成对应的一盒，盒内数量不提前重抽");
        That(DisasterDungeonPolicy.PrepareStoneDrop(receipt.Entry with { DungeonId = 3 }, selected, items.Items.Get) is null &&
            DisasterDungeonPolicy.PrepareStoneDrop(receipt.Entry, selected with { SourceMap = receipt.Entry.BossMap with { Id = -1 } }, items.Items.Get) is null &&
            DisasterDungeonPolicy.PrepareStoneDrop(receipt.Entry, selected with { IsFriendly = true }, items.Items.Get) is null,
            "其他副本、非终点和友方模板不能借用时空石奖励");
        bool rollback = tier == 0;
        foreach (var monster in stones.Where(monster => monster != selected))
        {
            var observation = FormalDungeonSessionTests.Observation(receipt, monster) with { RewriteFlag = 1 };
            if (rollback)
            {
                FailGrowth();
                var before = fixture.Session.Selected;
                Reject<SqliteException>(() => fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth),
                    "清除石头晚段SQL失败整笔回滚");
                That(ReferenceEquals(before, fixture.Session.Selected), "失败不提前安装角色快照");
                fixture.Sql("DROP TRIGGER disaster_stone_fail");
                observation = observation with { RewriteFlag = 0 };
                rollback = false;
            }
            var cleared = fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth);
            That(cleared.Outcome == DungeonRunOutcome.Accepted && cleared.Drops.IsEmpty,
                "四档自动清除不产出礼盒，重试改为正常死亡也不升级奖励");
        }
        var report = FormalDungeonSessionTests.Observation(receipt, selected);
        if (tier == 0)
        {
            FailGrowth();
            Reject<SqliteException>(() => fixture.Session.ObserveFormalDungeonDeath(report, kills, growth),
                "真实破坏奖励晚段SQL失败整笔回滚");
            fixture.Sql("DROP TRIGGER disaster_stone_fail");
            report = report with { RewriteFlag = 1 };
        }
        var death = fixture.Session.ObserveFormalDungeonDeath(report, kills, growth);
        That(death.Outcome == DungeonRunOutcome.Accepted && death.Drops is [{ Core.Count: 1 }] &&
            death.Drops[0].Core.ItemId == 490012195 + tier, "唯一保留石头的正常破坏产出对应档礼盒");
        That(fixture.Session.ObserveFormalDungeonDeath(report, kills, growth).Outcome == DungeonRunOutcome.Duplicate,
            "同一石头重复报告不再生成掉落");
        var ground = death.Drops.Single();
        var pickup = fixture.Session.PickFormalDungeonItem(ground.Reservation.GroundKey, items.Items, items.EpicPieces, items.GoldLimits);
        That(pickup.Outcome == DungeonRunOutcome.Accepted &&
            fixture.Read().Inventory.Items.Where(item => item.Core.ItemId is >= 490012195 and <= 490012199).Sum(item => item.Core.Count) == 1 &&
            fixture.Session.PickFormalDungeonItem(ground.Reservation.GroundKey, items.Items, items.EpicPieces, items.GoldLimits).Outcome == DungeonRunOutcome.Rejected,
            "真实拾取落库一盒，重复领取不多发");
        return death.Receipt!;

        void FailGrowth() => fixture.Sql("CREATE TRIGGER disaster_stone_fail AFTER INSERT ON character_dungeon_growth_receipts BEGIN SELECT RAISE(ABORT,'disaster stone rollback'); END");
    }
}
