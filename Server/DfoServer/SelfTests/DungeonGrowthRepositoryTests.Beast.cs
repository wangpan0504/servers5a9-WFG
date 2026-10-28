using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonGrowthRepositoryTests
{
    static void RunBeastStageRewards(AccountOwners.Lease owner, NewCharacter fixture, CharacterGrowthCatalog growth, string root)
    {
        const int rewardId = 10303240, fillerId = 9001, questId = 9002;
        var catalog = new ItemCatalog(null!, null!, null!, 0);
        catalog.PrefillTemplates(new Dictionary<int, ItemTemplate>
        {
            [rewardId] = new(rewardId, ItemKind.Consumable, 0, 1, 0, 99, 0, 0, 0, "", 0, 0, "魔兽地下城奖励袖珍罐"),
            [fillerId] = new(fillerId, ItemKind.Consumable, 0, 1, 0, 99, 0, 0, 0, "", 0, 0, "fixture filler"),
            [questId] = new(questId, ItemKind.Material, 0, 1, 0, 99, 0, 0, 0, "", 0, 0, "fixture quest drop")
        });
        var limits = new GoldLimitCatalog("[gold limit from level]\n0 1000000000\n[/gold limit from level]");
        var pieces = new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]");
        var at = DateTimeOffset.FromUnixTimeSeconds(1793000000);
        foreach (bool overflow in new[] { false, true })
        {
            string path = Path.Combine(root, overflow ? "beast-mail.db" : "beast-bag.db");
            var repository = new CharacterRepository(path);
            var inventory = InventorySnapshot.Empty;
            if (overflow)
            {
                var range = ItemSlotBoundService.Range(ItemKind.Consumable, inventory);
                inventory = inventory with { Items = Enumerable.Range(range.Start, range.End - range.Start + 1)
                    .Select(slot => new InventoryItem(0, new(InventoryListType.Main, checked((short)slot)),
                        new(fillerId, ItemKind.Consumable, 1))).ToImmutableArray() };
            }
            var before = repository.Create(owner, fixture with { Name = overflow ? "BeastMail" : "BeastBag", Inventory = inventory });
            var (run, authority) = CreateRun(before, beast: true);
            var first = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity, 20, 1)).Pending!;
            That(first.BeastRoomClear is null, "魔兽尚有阻挡怪时不能生成阶段清房事实");
            var current = repository.CommitDungeonGrowth(owner, before, run, first, Reward(first), growth).Character;
            run.ConfirmCommittedDeath(first);
            var pending = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity, 21, 2)).Pending!;
            That(pending.BeastRoomClear is not null && pending.Result.ClearFact is not null,
                "魔兽终末房也生成独立阶段清房事实，不依赖中途任务房事实");
            var questGrant = InventoryRewardGrantService.TryGrantBatch(current, [new(questId, 1)], catalog, limits, pieces, at);
            var quest = new QuestDropPlan(current, pending.Death.EventId, pending.Death.Identity,
                pending.Result.Snapshot.CurrentMap.Id, pending.Monster.MonsterId, pending.Monster.EnemyType, true, at, [], questGrant.Plan!);
            var stage = BeastStageRewardPlan.Create(current, run, pending, 0, quest, catalog, limits, pieces, at);
            pending.FreezeBeastStageReward(stage);
            Reject<InvalidOperationException>(() => pending.FreezeBeastStageReward(null), "阶段候选一经冻结不能改为无奖励");
            Reject<InvalidOperationException>(() => pending.FreezeBeastStageReward(
                BeastStageRewardPlan.Create(current, run, pending, 0, quest, catalog, limits, pieces, at)),
                "同一死亡不能重新生成替换阶段奖励规划");
            long receipts = Scalar("SELECT COUNT(*) FROM character_dungeon_growth_receipts");
            string snapshot = Snapshot();
            Execute("CREATE TRIGGER beast_growth_abort AFTER INSERT ON character_dungeon_growth_receipts BEGIN SELECT RAISE(ABORT,'beast rollback'); END");
            Reject<SqliteException>(() => repository.CommitDungeonGrowth(owner, current, run, pending, Reward(pending), growth,
                questDrops: quest), "阶段库存提交后的成长回执故障回滚整笔击杀");
            That(Snapshot() == snapshot && Scalar("SELECT COUNT(*) FROM character_dungeon_growth_receipts") == receipts &&
                run.IsPendingDeath(pending), "故障不留下经验、revision、物品、邮件、托管资产、审计或提前确认死亡");
            Execute("DROP TRIGGER beast_growth_abort");
            var committed = repository.CommitDungeonGrowth(owner, current, run, pending, Reward(pending), growth, questDrops: quest);
            That(committed.Character.Revision == current.Revision + 1 && !committed.Duplicate &&
                committed.Character.Inventory.Items.Where(item => item.Core.ItemId == questId).Sum(item => item.Core.Count) == 1,
                "阶段与任务库存仅提交一次，击杀成长只推进一次revision");
            That(overflow ? Scalar("SELECT COUNT(*) FROM mailbox_messages") == 1 && stage.Inventory.OverflowItems.Length == 1
                    : committed.Character.Inventory.Items.Where(item => item.Core.ItemId == rewardId).Sum(item => item.Core.Count) == 1 &&
                      committed.BeastRewardSlots.Length == 1,
                "有空位恰好发一罐，满包恰好生成一封溢出邮件");
            string committedSnapshot = Snapshot();
            var replay = repository.CommitDungeonGrowth(owner, current, run, pending, Reward(pending), growth, questDrops: quest);
            That(replay.Duplicate && Snapshot() == committedSnapshot && replay.Character.Revision == committed.Character.Revision &&
                replay.BeastRewardSlots.SequenceEqual(committed.BeastRewardSlots), "模拟提交返回丢失：原死亡重放不再发罐、邮件或任务物品");
            That(run.ConfirmCommittedDeath(pending).Outcome == DungeonRunOutcome.Accepted,
                "数据库成功后才确认魔兽阶段死亡");

            SqliteConnection Open()
            {
                var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open();
                return db;
            }
            void Execute(string sql)
            {
                using var db = Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
            long Scalar(string sql)
            {
                using var db = Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar());
            }
            string Snapshot()
            {
                using var db = Open();
                using var command = db.CreateCommand();
                command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
                var tables = new List<string>();
                using (var reader = command.ExecuteReader()) while (reader.Read()) tables.Add(reader.GetString(0));
                var rows = new List<object>();
                foreach (string table in tables)
                {
                    command.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"") + "\" ORDER BY rowid";
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) rows.Add(new { Table = table, Values = Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue).ToArray() });
                }
                return System.Text.Json.JsonSerializer.Serialize(rows);
            }
        }
        static DungeonExperienceReward Reward(PreparedDungeonDeath pending) => new("fixture-only", DungeonKillExperienceCatalog.ResourcePath,
            pending.Monster.MonsterId, pending.Monster.CandidateLevel, 1);
    }
}
