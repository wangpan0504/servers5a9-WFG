using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Caching;
using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestPvpProgressTests
{
    public static void Run()
    {
        int checks = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-quest-pvp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            int nextId = 29000;
            var definitions = new[]
            {
                Quest("[pvp match]", -1, [2]), Quest("[pvp win]", 0, [2]), Quest("[pvp lose]", 0, [2]),
                Quest("[pvp quest]", 0, [4, 0]), Quest("[pvp quest]", 1, [2, 2]),
                Quest("[pvp quest]", 2, [2, -1, 2]), Quest("[pvp quest]", 8, [1])
            };
            var catalog = new QuestCatalog(definitions.ToDictionary(row => row.Id));
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path, catalog);
            using var cache = new GameCache();
            var owners = new AccountOwners(cache);
            using var owner = owners.Acquire("quest-pvp-first", () => { });
            using var otherOwner = owners.Acquire("quest-pvp-second", () => { });
            var point = new DfoServer.GameWorld.Towns.Location(10, 4, 10, 10);
            var character = repository.Create(owner, new NewCharacter("QuestPvpOne", point, point));
            var other = repository.Create(otherOwner, new NewCharacter("QuestPvpTwo", point, point));
            var active = definitions.Select((quest, index) => new ActiveMainQuest((byte)index, (ushort)quest.Id,
                QuestPvpProgress.Initial(quest)) { AcceptanceId = Guid.NewGuid() }).ToImmutableArray();
            var unrelated = QuestEligibilityTests.Quest(29990);
            var bindingCatalog = new QuestCatalog(catalog.All.Append(unrelated).ToDictionary(row => row.Id));
            var withLegacy = active.Append(new(8, (ushort)unrelated.Id, 1) { AcceptanceId = Guid.NewGuid() })
                .Append(active[0] with { Slot = 9, AcceptanceId = Guid.Empty }).ToArray();
            That(QuestPvpProgress.Bindings(withLegacy, bindingCatalog).SequenceEqual(active) &&
                QuestPvpProgress.Bindings(withLegacy, null).IsEmpty && withLegacy[^1].AcceptanceId == Guid.Empty,
                "只冻结本次可推进的决斗接取，不复制无关任务或因旧无身份槽阻止决斗");
            using (var db = new SqliteGameDatabase(path).Open())
            foreach (var row in active)
            {
                using var query = db.CreateCommand();
                query.CommandText = "INSERT INTO character_active_main_quests(character_id,slot,quest_id,condition_state,acceptance_id) VALUES($id,$slot,$quest,$state,$acceptance)";
                query.Parameters.AddWithValue("$id", character.Id); query.Parameters.AddWithValue("$slot", row.Slot);
                query.Parameters.AddWithValue("$quest", row.QuestId); query.Parameters.AddWithValue("$state", (long)row.ConditionState);
                query.Parameters.AddWithValue("$acceptance", row.AcceptanceId.ToString("N")); query.ExecuteNonQuery();
            }
            var result = new PvpArenaResultRecord(Guid.NewGuid(), Guid.NewGuid(), 296, PvpArenaMode.Individual,
                [new(owner.Account, character.Id, 0, 0, PvpArenaOutcome.Win) { Kills = 2, QuestBindings = active },
                 new(otherOwner.Account, other.Id, 1, 1, PvpArenaOutcome.Lose)], false, 0);
            That(QuestPvpProgress.Apply(active, catalog, result, result.Participants[0], 1).Select(row => row.ConditionState)
                .SequenceEqual(new uint[] { 1, 1, 2, 2, 2, 1, 0 }), "按真实模式、胜负、击杀及段位分别推进，不把所有PVP子型当作获胜");
            foreach (var changed in new[] { result with { RatingMode = PvpRatingMode.Total }, result with
                { Participants = result.Participants.SetItem(0, result.Participants[0] with { Outcome = PvpArenaOutcome.Cancelled }) } })
                That(QuestPvpProgress.Apply(active, catalog, changed, changed.Participants[0], 10).SequenceEqual(active),
                    "取消及独立整体赛不伪造普通决斗任务");
            var reaccepted = active.Select(row => row with { AcceptanceId = Guid.NewGuid() }).ToImmutableArray();
            That(QuestPvpProgress.Apply(reaccepted, catalog, result, result.Participants[0], 10).SequenceEqual(reaccepted),
                "原房间的任务绑定不能推进放弃重接后的新身份");
            That(QuestPvpProgress.Apply(active, catalog, result, result.Participants[1], 10).SequenceEqual(active),
                "另一成员没有本角色接取身份，不能串用任务");
            repository.CommitPvpArenaResult(owner, result);
            var calculator = new PvpRatingCalculator(PvpRatingTests.Fixture());
            var settlement = new PvpRatingSettlement(repository, calculator);
            Sql("CREATE TRIGGER quest_pvp_abort BEFORE UPDATE OF receipt_json ON character_pvp_rating_claims BEGIN SELECT RAISE(ABORT,'quest pvp rollback'); END");
            Reject<SqliteException>(() => settlement.Settle(owner, character.Id, result.MatchId), "评分回执末段失败必须回滚同事务的任务进度");
            That(repository.LoadCharacter(owner, character.Slot).ActiveMainQuests.SequenceEqual(active) &&
                repository.ReadPvpRating(owner, character.Id) is null, "失败不保留任务、角色版本或评分的半份结算");
            Sql("DROP TRIGGER quest_pvp_abort");
            var receipt = settlement.Settle(owner, character.Id, result.MatchId);
            var saved = repository.LoadCharacter(owner, character.Slot);
            That(receipt.QuestAdvance is { } advance && advance.Revision == saved.Revision &&
                advance.After.SequenceEqual(saved.ActiveMainQuests) && saved.ActiveMainQuests[0].ConditionState == 1 &&
                repository.LoadCharacter(otherOwner, other.Slot).Revision == other.Revision,
                "结算回执携带实际任务与角色版本，不修改另一账号");
            var reopened = new CharacterRepository(path, catalog);
            var replay = new PvpRatingSettlement(reopened, calculator).Settle(owner, character.Id, result.MatchId);
            That(replay.QuestAdvance!.After.SequenceEqual(saved.ActiveMainQuests) &&
                reopened.LoadCharacter(owner, character.Slot).Revision == saved.Revision,
                "重开仓储回放原任务结算，不重复减计数或推进版本");
            var old = result with { MatchId = Guid.NewGuid(), RoomInstance = Guid.NewGuid(), Participants =
                result.Participants.SetItem(0, result.Participants[0] with { QuestBindings = null }) };
            That(!System.Text.Json.JsonSerializer.Serialize(old).Contains("QuestBindings", StringComparison.Ordinal),
                "没有任务绑定的历史整局JSON保持原格式，不因新增字段而冲突");
            reopened.CommitPvpArenaResult(owner, old);
            That(new PvpRatingSettlement(reopened, calculator).Recover(owner, character.Id) &&
                reopened.LoadCharacter(owner, character.Slot).Revision == saved.Revision,
                "未绑定任务的旧整局来源仍正常恢复评分，但不补发历史任务进度");

            void Sql(string text)
            {
                using var db = new SqliteGameDatabase(path).Open(); using var query = db.CreateCommand();
                query.CommandText = text; query.ExecuteNonQuery();
            }
            StoryQuest Quest(string condition, int subtype, int[] data) => QuestEligibilityTests.Quest(nextId++) with
                { Grade = "[side]", Condition = condition, SubType = subtype, IntData = data };
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {Count - checks} 决斗任务模式、接取隔离、SQL回滚与重放断言");
    }
}
