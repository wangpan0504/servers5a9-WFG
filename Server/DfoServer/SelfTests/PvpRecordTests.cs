using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PvpRecordTests
{
    internal static void RunCore()
    {
        int count = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-pvp-record-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var owners = new AccountOwners();
            using var first = owners.Acquire("record-first", () => { });
            using var second = owners.Acquire("record-second", () => { });
            var location = new Location(10, 0, 10, 10);
            var a = repository.Create(first, new NewCharacter("RecordA", location, location));
            var b = repository.Create(second, new NewCharacter("RecordB", location, location));
            var ratings = new PvpRatingSettlement(repository, new PvpRatingCalculator(PvpRatingTests.Fixture()));
            That(repository.ReadPvpRecord(first, a.Id) == PvpRecordStatistics.Empty, "新角色查询返回真实空战绩");
            Reject<InvalidOperationException>(() => repository.ReadPvpRecord(first, b.Id), "战绩存储禁止借本人owner读取对方角色");
            foreach (var outcome in new[] { PvpArenaOutcome.Win, PvpArenaOutcome.Win, PvpArenaOutcome.Lose,
                PvpArenaOutcome.Draw, PvpArenaOutcome.Win, PvpArenaOutcome.Win, PvpArenaOutcome.Win,
                PvpArenaOutcome.Lose, PvpArenaOutcome.Win, PvpArenaOutcome.Lose, PvpArenaOutcome.Draw,
                PvpArenaOutcome.Win, PvpArenaOutcome.Win })
                Match(PvpRatingMode.Matching, outcome, true);
            var expected = new PvpRecordStatistics(8, 3, 2, 6, 2, 2, 2, 3);
            That(repository.ReadPvpRecord(first, a.Id) == expected,
                "十三局真实结算统计胜负平、最近十局、当前及最高连胜，不把较早胜局算入近期");
            Match(PvpRatingMode.Ordinary, PvpArenaOutcome.Win, true);
            Match(PvpRatingMode.Matching, PvpArenaOutcome.Cancelled, true);
            Match(PvpRatingMode.Matching, PvpArenaOutcome.Win, false);
            var reopened = new CharacterRepository(Path.Combine(root, "game.db"));
            That(reopened.ReadPvpRecord(first, a.Id) == expected,
                "普通局、取消及未到账匹配不污染战绩，重开仓储仍读取真实已结算记录");
            var map = new TownMap("record-fixture.map", [new(new(0, 0, 100, 100), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(10, 0)] = new(10, 0, 1, [], [], [], location, map, [], []) },
                location, location, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var session = new GameSession(repository, towns, first);
            session.Select(0); session.MarkReady();
            var actor = session.CaptureSocialActor()!;
            var records = new PvpRecordQuery(reopened, reopened);
            That(records.Capture(actor, a.Id, 6, true, -1)?.Statistics == expected &&
                records.Capture(actor, a.Id, 5, true, -1)?.Statistics == PvpRecordStatistics.Empty,
                "当前页查询真实记录，旧赛季页不复制当前数据");
            That(records.Capture(actor, b.Id, 6, false, -1) is null,
                "目录定位与身份捕获之间换角时，不能把另一角色的有效身份当成原请求目标");
            session.ReturnToRoster(); session.Select(0); session.MarkReady();
            That(records.Capture(actor, a.Id, 6, true, -1) is null, "小退重选后旧选角身份不能查询或发布");
            actor = session.CaptureSocialActor()!;
            That(records.Capture(actor, a.Id, 6, true, 3) is null && records.Capture(actor, a.Id, 1, true, -1) is null,
                "领域边界拒绝非法页面及成员索引，不依赖网络解析器兜底");
            using var replacement = owners.Acquire(first.Account, () => { });
            Reject<InvalidOperationException>(() => reopened.ReadPvpRecord(first, a.Id), "被接管owner不能继续读取战绩");
            That(records.Capture(actor, a.Id, 6, true, -1) is null, "战绩用例将接管失效转为拒绝，不抛出到连接循环");
            That(reopened.ReadPvpRecord(replacement, a.Id) == expected, "新owner读取相同持久战绩");

            void Match(PvpRatingMode mode, PvpArenaOutcome outcome, bool settle)
            {
                var other = outcome == PvpArenaOutcome.Win ? PvpArenaOutcome.Lose :
                    outcome == PvpArenaOutcome.Lose ? PvpArenaOutcome.Win : outcome;
                var result = new PvpArenaResultRecord(Guid.NewGuid(), Guid.NewGuid(), 47, PvpArenaMode.Individual,
                    [new(first.Account, a.Id, 0, 0, outcome)
                        { Rating = mode == PvpRatingMode.Matching ? ratings.Capture(first, a.Id) : null },
                     new(second.Account, b.Id, 1, 1, other)
                        { Rating = mode == PvpRatingMode.Matching ? ratings.Capture(second, b.Id) : null }],
                    false, 0) { RatingMode = mode };
                repository.CommitPvpArenaResult(first, result);
                if (!settle) return;
                ratings.Settle(first, a.Id, result.MatchId);
                ratings.Settle(second, b.Id, result.MatchId);
            }
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - count} 决斗战绩只读聚合与持久来源断言");
    }
}
