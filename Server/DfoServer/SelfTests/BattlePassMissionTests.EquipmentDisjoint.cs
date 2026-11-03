using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class BattlePassMissionTests
{
    static void EquipmentDisjointPersistence()
    {
        // 不依赖PVF，直接覆盖图鉴writer与战令来源校验的同事务边界。
        string root = Path.Combine(Path.GetTempPath(), "S5A9-disjoint-1032-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool succeeded = false;
        try
        {
            string path = Path.Combine(root, "game.db");
            var clock = new MissionClock(Start.AddHours(1));
            var repository = new CharacterRepository(path);
            repository.ConfigureBattlePassMissions(new("disjoint-1032", Start, Catalog(disjoint: true)), clock);
            using var owner = new AccountOwners().Acquire("disjoint-1032", () => { });
            var location = new Location(1, 0, 10, 10);
            var sources = Enumerable.Range(3, 8).Select(slot => new InventoryItem(0,
                new(InventoryListType.Main, (short)slot), new(10400, ItemKind.Equipment, 999999998)
                { Durability = 100, Marker16 = slot })).ToImmutableArray();
            var tool = new InventoryItem(0, new(InventoryListType.Main, 65), new(2600468, ItemKind.Consumable, 2));
            var current = repository.Create(owner, new NewCharacter("DisjointCore", location, location)
                { Level = 95, Inventory = new([.. sources, tool], Gold: 1000) });
            int committedCount = 0, pieceCount = 0;
            foreach (var mode in new[] { DisjointMode.Ordinary, DisjointMode.Ordinary, DisjointMode.OwnMachine, DisjointMode.OtherMachine })
            foreach (bool pieces in new[] { true, false })
            {
                var source = current.Inventory.Items.First(item => item.Core.Kind == ItemKind.Equipment);
                var portable = committedCount is 2 or 3 ? current.Inventory.Find(tool.Position) : null;
                var request = new DisjointRequest(source.Position, portable?.Position.Index ?? -1, 0);
                var paid = current.Inventory with { Items = current.Inventory.Items.Remove(source) };
                if (portable is not null)
                    paid = paid with { Items = portable.Core.Count == 1 ? paid.Items.Remove(portable) :
                        paid.Items.Replace(portable, portable with { Core = portable.Core with { Value = portable.Core.Value - 1 } }) };
                if (mode is DisjointMode.OwnMachine or DisjointMode.OtherMachine) paid = paid with { Gold = paid.Gold - 10 };
                var after = CurrencyWallet.Set(paid, CurrencyKind.ClearCube, CurrencyWallet.Get(paid, CurrencyKind.ClearCube) + 2);
                if (pieces) after = after with { EpicPieces = new(after.EpicPieces.Revision, new string('A', 64), [pieceCount + 100]) };
                var receipt = new DisjointReceipt(request, pieces ? [new(354, 3037, 2), new(-1, 9001, 100)] : [new(354, 3037, 2)]);
                string reason = mode == DisjointMode.Ordinary ? "equipment_disjoint" : "expert_job_machine_disjoint";
                var fact = EquipmentDisjointFact.Capture(current.Inventory, paid, after, request, receipt, mode, reason,
                    source, portable, clock.GetUtcNow(), clock);
                var plan = new InventoryPlan(current.Inventory, after, reason) { EquipmentDisjoint = fact };
                if (mode == DisjointMode.OtherMachine)
                {
                    var tradeAfter = after with { Gold = after.Gold - 7 };
                    plan = new(current.Inventory, tradeAfter, "expert_job_trade_customer")
                        { EquipmentDisjoint = fact.ForTrade(plan, current.Inventory, tradeAfter, 7) };
                }
                if (committedCount == 0)
                {
                    string before = Snapshot();
                    Sql("CREATE TRIGGER fail_disjoint_1032 AFTER INSERT ON account_battle_pass_source_events " +
                        "BEGIN SELECT RAISE(ABORT,'disjoint-1032-late'); END");
                    Reject<SqliteException>(() => repository.CommitInventory(owner, current, plan), "战令来源写入失败向上传播");
                    Sql("DROP TRIGGER fail_disjoint_1032");
                    That(Snapshot() == before, "首次图鉴、源装备、钱包、版本及审计随战令失败整笔回滚");
                }
                var previous = current;
                current = repository.CommitInventory(owner, previous, plan);
                committedCount++;
                if (pieces) pieceCount += 100;
                That(current.Inventory.EpicPieces.Revision == (committedCount + 1) / 2 &&
                    current.Inventory.EpicPieces.Counts[0] == pieceCount && current.Inventory.Find(source.Position) is null &&
                    CurrencyWallet.Get(current.Inventory, CurrencyKind.ClearCube) == committedCount * 2,
                    "普通、便携及双机器连续提交保留完整产物，图鉴只在有碎片时递增版本");
                var state = Read(repository, owner, clock.GetUtcNow());
                That(Find(state, 0, 18).Count == Math.Min(3, committedCount) && Find(state, 1, 42).Count == committedCount,
                    "每件实际装备只计一次，日任务满额后周任务继续推进");
                string saved = Snapshot();
                Reject<InvalidOperationException>(() => repository.CommitInventory(owner, previous, plan), "旧分解计划不能重复提交");
                That(Snapshot() == saved, "重放不重复产物、来源或任务进度");
            }
            var restored = new CharacterRepository(path).LoadCharacter(owner, current.Slot);
            That(restored.Inventory.EpicPieces.Same(current.Inventory.EpicPieces) &&
                restored.Inventory.Find(tool.Position) is null && restored.Inventory.Gold == 946 &&
                restored.Inventory.Items.All(item => item.Core.Kind != ItemKind.Equipment),
                "重开仓储恢复全部图鉴、工具耗尽、机器及摊位费用和源删除");
            succeeded = true;

            void Sql(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            SqliteConnection Open()
            {
                var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
                db.Open(); return db;
            }
            string Snapshot()
            {
                using var db = Open();
                return JsonSerializer.Serialize(new[] { "characters", "character_inventory_items", "account_epic_books",
                    "account_currency_balances", "inventory_audit_log", "currency_audit_log", "epic_piece_audit_log",
                    "account_battle_pass_source_events", "account_battle_pass_mission_progress", "account_battle_pass_seasons" }
                    .Select(table => { using var command = db.CreateCommand(); command.CommandText = "SELECT * FROM " + table;
                        using var reader = command.ExecuteReader(); var rows = new List<object[]>();
                        while (reader.Read()) { var values = new object[reader.FieldCount]; reader.GetValues(values); rows.Add(values); }
                        return rows; }).ToArray());
            }
        }
        finally
        {
            if (succeeded) TestFiles.DeleteDirectory(root, recursive: true);
            else Console.Error.WriteLine($"分解事务回归失败，现场保留于：{root}");
        }
    }
}
