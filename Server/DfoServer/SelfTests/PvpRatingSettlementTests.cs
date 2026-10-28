using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Caching;
using DfoServer.Game.Pvp;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PvpRatingSettlementTests
{
    public static void RunCore()
    {
        int beforeCount = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-pvp-rating-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            using var memory = new GameCache();
            var owners = new AccountOwners(memory);
            using var firstOwner = owners.Acquire("rating-first", () => { });
            using var secondOwner = owners.Acquire("rating-second", () => { });
            var point = new Location(10, 4, 10, 10);
            var first = repository.Create(firstOwner, new NewCharacter("RatingOne", point, point));
            var second = repository.Create(secondOwner, new NewCharacter("RatingTwo", point, point));
            var calculator = new PvpRatingCalculator(PvpRatingTests.Fixture());
            var settlements = new PvpRatingSettlement(repository, calculator);
            That(repository.ReadPvpRating(firstOwner, first.Id) is null &&
                repository.ReadPendingPvpRatings(firstOwner, first.Id, 32).Length == 0,
                "新角色评分缺行只读，不自动补造经验或历史来源");
            Reject<InvalidOperationException>(() => repository.ReadPvpRating(firstOwner, second.Id), "评分查询核账号角色归属");
            Reject<ArgumentOutOfRangeException>(() => repository.ReadPendingPvpRatings(firstOwner, first.Id, 33),
                "评分恢复查询有界，不能请求无界历史");

            var result = Result();
            Sql("CREATE TRIGGER fail_rating_source BEFORE INSERT ON character_pvp_rating_claims WHEN NEW.account='rating-second' BEGIN SELECT RAISE(ABORT,'Injected source failure'); END");
            Reject<SqliteException>(() => repository.CommitPvpArenaResult(firstOwner, result),
                "第二成员来源失败须回滚第一成员来源和整局事实");
            That(repository.ReadPvpArenaResult(firstOwner, result.MatchId) is null &&
                Scalar("SELECT count(*) FROM character_pvp_rating_claims") is 0L, "无半份整局结算来源");
            Sql("DROP TRIGGER fail_rating_source");
            repository.CommitPvpArenaResult(firstOwner, result);
            That(Scalar("SELECT count(*) FROM character_pvp_rating_claims") is 2L,
                "新整局事实和双方来源共同提交");
            repository.CommitPvpArenaResult(firstOwner, result);
            That(Scalar("SELECT count(*) FROM character_pvp_rating_claims") is 2L, "事实回放不重复创建来源");
            Reject<InvalidOperationException>(() => settlements.Settle(firstOwner, second.Id, result.MatchId),
                "报告人的owner不能消费对方经验来源");
            var firstClaim = repository.ReadPvpRatingClaim(firstOwner, first.Id, result.MatchId)!;
            var initial = new PvpRatingSnapshot(0, calculator.Initial());
            var firstChange = calculator.Calculate(initial.State, PvpRatingMode.Ordinary, PvpArenaOutcome.Win);
            var firstPlan = new PvpRatingPlan(firstClaim, initial, firstChange);
            Sql("CREATE TRIGGER fail_rating_receipt BEFORE UPDATE OF receipt_json ON character_pvp_rating_claims BEGIN SELECT RAISE(ABORT,'Injected receipt failure'); END");
            Reject<SqliteException>(() => repository.CommitPvpRating(firstOwner, firstPlan),
                "评分写入后回执失败须整体回滚");
            That(repository.ReadPvpRating(firstOwner, first.Id) is null &&
                repository.ReadPvpRatingClaim(firstOwner, first.Id, result.MatchId)!.Receipt is null,
                "失败不留评分、版本或已消费来源");
            Sql("DROP TRIGGER fail_rating_receipt");
            var receipt = settlements.Settle(firstOwner, first.Id, result.MatchId);
            That(receipt is { BeforeRevision: 0, Revision: 1, Change.ExperienceDelta: 450, Change.RatingDelta: 0 } &&
                receipt.RuleVersion == PvpRatingCalculator.RuleVersion && repository.ReadPvpRating(secondOwner, second.Id) is null,
                "普通胜方实际到账450且保留个人匹配分，不在本账号锁里改写对手");
            That(settlements.Settle(firstOwner, first.Id, result.MatchId) == receipt &&
                repository.CommitPvpRating(firstOwner, firstPlan) == receipt &&
                repository.ReadPvpRating(firstOwner, first.Id)!.Revision == 1, "重复报告回放原回执，不再次加经验或推进版本");
            Reject<InvalidOperationException>(() => repository.CommitPvpRating(firstOwner,
                firstPlan with { Change = firstChange with { After = firstChange.After with { Experience = 451 } } }),
                "同局冲突额度不能覆盖已提交回执");
            var reopened = new CharacterRepository(path);
            var recovered = new PvpRatingSettlement(reopened, calculator);
            That(recovered.Settle(firstOwner, first.Id, result.MatchId) == receipt,
                "重开仓储从持久来源回放，不依赖原服务内存");

            var earlier = Result();
            var later = Result();
            repository.CommitPvpArenaResult(firstOwner, earlier);
            repository.CommitPvpArenaResult(firstOwner, later);
            var old = repository.ReadPvpRating(firstOwner, first.Id)!;
            var stalePlan = new PvpRatingPlan(repository.ReadPvpRatingClaim(firstOwner, first.Id, later.MatchId)!, old,
                calculator.Calculate(old.State, PvpRatingMode.Ordinary, PvpArenaOutcome.Win));
            Reject<InvalidOperationException>(() => repository.CommitPvpRating(firstOwner, stalePlan),
                "后局不能越过更早的持久来源");
            settlements.Settle(firstOwner, first.Id, earlier.MatchId);
            Reject<InvalidOperationException>(() => repository.CommitPvpRating(firstOwner, stalePlan),
                "前局到账后旧评分快照不能覆盖新状态");
            That(recovered.Recover(firstOwner, first.Id) &&
                repository.ReadPvpRating(firstOwner, first.Id) is { Revision: 3, State.Experience: 1350 } &&
                settlements.Settle(firstOwner, first.Id, result.MatchId) == receipt,
                "按来源顺序恢复剩余局，后续状态推进不改变旧回执");

            That(recovered.Recover(secondOwner, second.Id), "冻结匹配赛前分之前先恢复对手的更早普通来源");
            var frozenFirst = recovered.Capture(firstOwner, first.Id);
            var frozenSecond = recovered.Capture(secondOwner, second.Id);
            var matching = Result(PvpRatingMode.Matching);
            matching = matching with { Participants = matching.Participants.Select(row => row with
                { Rating = row.CharacterId == first.Id ? frozenFirst : frozenSecond }).ToImmutableArray() };
            Reject<InvalidDataException>(() => repository.CommitPvpArenaResult(firstOwner,
                matching with { Participants = matching.Participants.Select(row => row with { Rating = null }).ToImmutableArray() }),
                "匹配事实缺少任一赛前冻结快照时不能生成评分来源");
            repository.CommitPvpArenaResult(firstOwner, matching);
            Reject<InvalidOperationException>(() => settlements.Settle(firstOwner, first.Id, matching.MatchId, PvpRatingMode.Ordinary),
                "匹配来源不能误按普通45%结算");
            Sql("CREATE TRIGGER fail_matching_receipt BEFORE UPDATE OF receipt_json ON character_pvp_rating_claims WHEN NEW.rating_mode=1 BEGIN SELECT RAISE(ABORT,'Injected matching receipt failure'); END");
            Reject<SqliteException>(() => recovered.Recover(firstOwner, first.Id), "匹配经验、定位积分及回执末段失败整体回滚");
            That(repository.ReadPvpRating(firstOwner, first.Id) == frozenFirst &&
                repository.ReadPvpRatingClaim(firstOwner, first.Id, matching.MatchId)!.Receipt is null,
                "匹配写失败保留原普通经验、RP、定位场次和未消费来源");
            Sql("DROP TRIGGER fail_matching_receipt");
            That(recovered.Recover(firstOwner, first.Id) &&
                repository.ReadPvpRating(firstOwner, first.Id) is { State.Experience: 2350, State.Rating: 1050, State.Played: 1 } &&
                repository.ReadPvpRating(secondOwner, second.Id) == frozenSecond,
                "通用恢复按冻结双方赛前分给胜方定位积分，对方仍保持未消费的原状态");
            var cancelled = Result(outcome: PvpArenaOutcome.Cancelled);
            repository.CommitPvpArenaResult(firstOwner, cancelled);
            var cancelReceipt = recovered.Settle(firstOwner, first.Id, cancelled.MatchId);
            That(cancelReceipt.Change.Before == cancelReceipt.Change.After, "取消来源只留消费回执，不改变评分或战绩");

            using var replacement = owners.Acquire(secondOwner.Account, () => { });
            Reject<InvalidOperationException>(() => recovered.Recover(secondOwner, second.Id),
                "接管后的旧owner不能消费待结算经验");
            That(recovered.Recover(replacement, second.Id) &&
                repository.ReadPvpRating(replacement, second.Id) is { State.Experience: 0, State.Rating: 950, State.Played: 1 },
                "有效新owner用原胜方900快照计算负方950，不能改用胜方已到账1050再算一次");
            var unchanged = repository.LoadCharacter(firstOwner, 0);
            That(unchanged.Revision == first.Revision && unchanged.Inventory.Gold == first.Inventory.Gold &&
                unchanged.Position == first.Position, "独立决斗经验不改角色普通经验、钱包、位置或资产revision");
            repository.Delete(firstOwner, repository.LoadDeletionTarget(firstOwner, 0));
            That(Scalar("SELECT count(*) FROM character_pvp_ratings WHERE account='rating-first'") is 0L &&
                Scalar("SELECT count(*) FROM character_pvp_rating_claims WHERE account='rating-first'") is 0L &&
                repository.ReadPvpArenaResult(replacement, result.MatchId) is not null,
                "删角清理本角色评分及来源，保留对方可读的整局事实");
            var rebuilt = repository.Create(firstOwner, new NewCharacter("RatingNew", point, point));
            That(repository.ReadPvpRating(firstOwner, rebuilt.Id) is null &&
                repository.ReadPvpRatingClaim(firstOwner, rebuilt.Id, result.MatchId) is null,
                "同槽重建角色不能继承被删角色历史来源");

            PvpArenaResultRecord Result(PvpRatingMode mode = PvpRatingMode.Ordinary, PvpArenaOutcome outcome = PvpArenaOutcome.Win) =>
                new(Guid.NewGuid(), Guid.NewGuid(), 296, PvpArenaMode.Individual,
                    [new(firstOwner.Account, first.Id, 0, 0, outcome),
                     new(secondOwner.Account, second.Id, 1, 1, outcome == PvpArenaOutcome.Win ? PvpArenaOutcome.Lose : outcome)],
                    false, 0) { RatingMode = mode };
            object? Scalar(string sql)
            {
                using var db = new SqliteGameDatabase(path).Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
            void Sql(string sql) => _ = Scalar(sql);
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - beforeCount} 决斗评分持久来源、原子到账、重放及接管恢复断言");
    }
}
