using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Caching;
using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PvpTotalRatingSettlementTests
{
    public static void RunCore()
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-total-rating-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            using var memory = new GameCache();
            var owners = new AccountOwners(memory);
            using var first = owners.Acquire("total-rating-one", () => { });
            using var second = owners.Acquire("total-rating-two", () => { });
            var point = new Location(10, 4, 10, 10);
            Character[] a = Enumerable.Range(0, 3).Select(i => repository.Create(first, new("TotalA" + i, point, point))).ToArray();
            Character[] b = Enumerable.Range(0, 3).Select(i => repository.Create(second, new("TotalB" + i, point, point))).ToArray();
            var teamA = Save(first, "总力甲", a);
            var teamB = Save(second, "总力乙", b);
            var calculator = new PvpRatingCalculator(PvpRatingTests.Fixture());
            var characters = new PvpRatingSettlement(repository, calculator);
            var totals = new PvpTotalRatingSettlement(repository, characters, calculator);
            var secondRating = new PvpRatingSnapshot(0, calculator.Initial());
            That(totals.Capture(first, teamA.Id) == new PvpRatingSnapshot(0, calculator.Initial()) &&
                repository.ReadPvpTotalRating(first, teamA.Id) is null, "总力初始队伍分只读，不造来源");
            Reject<InvalidOperationException>(() => totals.Capture(first, teamB.Id), "总力分查询必须属于原账号队伍");
            Reject<ArgumentOutOfRangeException>(() => repository.ReadPendingPvpTotalRatings(first, 33), "总力恢复有界");

            var match = Result();
            Reject<InvalidDataException>(() => (match with { Participants = match.Participants.SetItem(0,
                match.Participants[0] with { TotalTeam = teamA with { Characters = [a[0].Id, 0, a[2].Id] } }) }).Validate(),
                "冻结队伍不能包含零或哨兵角色");
            Sql("CREATE TRIGGER fail_total_source BEFORE INSERT ON account_pvp_total_rating_claims WHEN NEW.account='total-rating-two' BEGIN SELECT RAISE(ABORT,'Injected total source failure'); END");
            Reject<SqliteException>(() => repository.CommitPvpArenaResult(first, match), "对方队伍来源失败整体回滚");
            That(repository.ReadPvpArenaResult(first, match.MatchId) is null &&
                Scalar("SELECT count(*) FROM character_pvp_rating_claims") is 0L &&
                Scalar("SELECT count(*) FROM account_pvp_total_rating_claims") is 0L, "不遗留整局、六角色或半份队伍来源");
            Sql("DROP TRIGGER fail_total_source");
            repository.CommitPvpArenaResult(first, match);
            repository.CommitPvpArenaResult(first, match);
            That(Scalar("SELECT count(*) FROM character_pvp_rating_claims") is 6L &&
                Scalar("SELECT count(*) FROM account_pvp_total_rating_claims") is 2L, "整局一次冻结六角色及两个独立队伍来源，重复不增加");
            var claim = repository.ReadPvpTotalRatingClaim(first, match.MatchId)!;
            var change = calculator.Calculate(claim.Before.State, PvpRatingMode.Total, claim.Outcome, claim.Opponent);
            var plan = new PvpTotalRatingPlan(claim, change, a.Select(row => characters.Prepare(first, row.Id, match.MatchId)!).ToImmutableArray());
            Sql("CREATE TRIGGER fail_total_receipt BEFORE UPDATE OF receipt_json ON account_pvp_total_rating_claims BEGIN SELECT RAISE(ABORT,'Injected total receipt failure'); END");
            Reject<SqliteException>(() => totals.Settle(first, match.MatchId), "末段队伍回执失败传播");
            That(repository.ReadPvpTotalRating(first, teamA.Id) is null && a.All(row =>
                repository.ReadPvpRating(first, row.Id) is null && repository.ReadPvpRatingClaim(first, row.Id, match.MatchId)!.Receipt is null),
                "三角色经验、个人版本、队伍分及来源消费同事务全部回滚");
            Sql("DROP TRIGGER fail_total_receipt");
            var receipt = totals.Settle(first, match.MatchId);
            That(receipt is { Revision: 1, BeforeRevision: 0, Characters.Length: 3 } && receipt.Change == change &&
                a.All(row => repository.ReadPvpRating(first, row.Id) is { Revision: 1 } rating &&
                    rating.State == calculator.CalculateTotalCharacterExperience(calculator.Initial(), PvpArenaOutcome.Win).After) &&
                repository.ReadPvpTotalRating(second, teamB.Id) is null && b.All(row => repository.ReadPvpRating(second, row.Id) is null),
                "本人独立队伍分与三角色总力经验到账，个人匹配战绩不变，不写对方账号");
            That(JsonSerializer.Serialize(totals.Settle(first, match.MatchId)) == JsonSerializer.Serialize(receipt) &&
                JsonSerializer.Serialize(repository.CommitPvpTotalRating(first, plan)) == JsonSerializer.Serialize(receipt) &&
                repository.ReadPvpTotalRating(first, teamA.Id)!.Revision == 1, "总力回放复用回执，不再次推进分数或经验");
            Reject<InvalidOperationException>(() => repository.CommitPvpTotalRating(first,
                plan with { Change = change with { After = change.After with { Rating = change.After.Rating + 1 } } }), "总力冲突回放拒绝");
            Reject<InvalidOperationException>(() => repository.CommitPvpTotalRating(second, plan), "本人队伍规划不能在对方owner下提交");

            var reopened = new CharacterRepository(path);
            var recovered = new PvpTotalRatingSettlement(reopened, new(reopened, calculator), calculator);
            That(JsonSerializer.Serialize(recovered.Settle(first, match.MatchId)) == JsonSerializer.Serialize(receipt), "重开仓储回放原局，不依赖原内存");
            using var replacement = owners.Acquire(second.Account, () => { });
            Reject<InvalidOperationException>(() => totals.Recover(second), "接管后旧owner不能消费队伍来源");
            characters.Settle(replacement, b[0].Id, match.MatchId);
            That(recovered.Recover(replacement) && repository.ReadPvpTotalRating(replacement, teamB.Id)!.State ==
                calculator.Calculate(calculator.Initial(), PvpRatingMode.Total, PvpArenaOutcome.Lose, calculator.Initial()).After &&
                b.All(row => repository.ReadPvpRating(replacement, row.Id)!.Revision == 1),
                "新owner按对方冻结旧分恢复，已消费一个角色只读复用，所有角色仅消费一次");
            secondRating = totals.Capture(replacement, teamB.Id);

            var ordinary = new PvpArenaResultRecord(Guid.NewGuid(), Guid.NewGuid(), 296, PvpArenaMode.Individual,
                [new(first.Account, a[0].Id, 0, 0, PvpArenaOutcome.Win), new(replacement.Account, b[0].Id, 1, 1, PvpArenaOutcome.Lose)], false, 1);
            repository.CommitPvpArenaResult(first, ordinary);
            var next = Result();
            repository.CommitPvpArenaResult(first, next);
            Reject<InvalidOperationException>(() => totals.Settle(first, next.MatchId), "总力结算不能越过更早个人来源");
            That(recovered.Recover(first) && repository.ReadPvpTotalRating(first, teamA.Id)!.Revision == 2 &&
                repository.ReadPvpRating(first, a[0].Id)!.Revision == 3 && repository.ReadPvpRating(first, a[1].Id)!.Revision == 2,
                "总力恢复先消费更早普通来源，再共同提交本局三角色经验及队伍分");
            That(characters.Recover(replacement, b[0].Id) && recovered.Recover(replacement), "普通个人恢复与队伍恢复共同兼容已消费角色");
            secondRating = totals.Capture(replacement, teamB.Id);

            var deleted = Result();
            repository.CommitPvpArenaResult(first, deleted);
            repository.Delete(first, repository.LoadDeletionTarget(first, a[1].Slot));
            var rebuilt = repository.Create(first, new("TotalNew", point, point));
            That(rebuilt.Slot == a[1].Slot && rebuilt.Id != a[1].Id && recovered.Recover(first) &&
                repository.ReadPvpTotalRatingClaim(first, deleted.MatchId)!.Receipt!.Characters.Length == 2 &&
                repository.ReadPvpRating(first, rebuilt.Id) is null,
                "删角后的缺员队伍仍恢复原局剩余角色及队伍分，同槽替身不继承经验");
            That(repository.LoadCharacter(first, a[0].Slot).Revision == a[0].Revision &&
                repository.LoadCharacter(first, a[0].Slot).Inventory.Gold == a[0].Inventory.Gold,
                "评分账本不改角色资产版本、钱包或普通经验");

            var cancelled = Result();
            cancelled = cancelled with { Participants = cancelled.Participants.Select(row => row with
                { Outcome = PvpArenaOutcome.Cancelled }).ToImmutableArray() };
            repository.CommitPvpArenaResult(first, cancelled);
            var unchanged = totals.Settle(first, cancelled.MatchId);
            That(unchanged.Change.Before == unchanged.Change.After && unchanged.Characters.All(row => row.Change.Before == row.Change.After),
                "取消总力局只留消费回执，队伍分和各角色经验不变化");
            var earlier = Result();
            var later = Result();
            repository.CommitPvpArenaResult(first, earlier);
            repository.CommitPvpArenaResult(first, later);
            Reject<InvalidOperationException>(() => totals.Settle(first, later.MatchId), "队伍后局不能越过更早队伍来源");
            totals.Settle(first, earlier.MatchId);
            Reject<InvalidOperationException>(() => totals.Settle(first, later.MatchId), "冻结旧队伍分不能覆盖前局推进后的新版本");

            PvpTotalTeam Save(AccountOwners.Lease owner, string name, Character[] rows)
            {
                var team = new PvpTotalTeam(Guid.NewGuid(), 0, name, rows.Select(row => (ushort?)row.Id).ToImmutableArray());
                return repository.SavePvpTotalTeam(owner, null, team, repository.ListRoster(owner).ToImmutableArray())!;
            }
            PvpArenaResultRecord Result() => new(Guid.NewGuid(), Guid.NewGuid(), 296, PvpArenaMode.Individual,
                [new(first.Account, a[0].Id, 0, 0, PvpArenaOutcome.Win) { TotalTeam = teamA, Rating = totals.Capture(first, teamA.Id) },
                 new(second.Account, b[0].Id, 1, 1, PvpArenaOutcome.Lose) { TotalTeam = teamB,
                     Rating = secondRating }], false, 0) { RatingMode = PvpRatingMode.Total };
            object? Scalar(string sql)
            {
                using var db = new SqliteGameDatabase(path).Open();
                using var command = db.CreateCommand(); command.CommandText = sql;
                return command.ExecuteScalar();
            }
            void Sql(string sql) => _ = Scalar(sql);
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {Count - before} 总力独立队伍评分、三角色经验、原子回滚及恢复断言");
    }
}
