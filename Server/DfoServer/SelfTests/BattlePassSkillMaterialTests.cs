using DfoServer.Game.BattlePass;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.BattlePass;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 本地 SQLite/Session 回归；本组不开 TCP 连接，不代表实机客户端验收。
internal static class BattlePassSkillMaterialTests
{
    public static void Run(TcpTestResources resources)
    {
        int before = Count;
        var start = new DateTimeOffset(2026, 10, 22, 6, 0, 0, TimeSpan.FromHours(8));
        var policy = new BattlePassMissionPolicy("skill-material-operation", start, BattlePassCatalog.Load(resources.Archive));
        var at = Enumerable.Range(0, policy.Days).Select(day => start.AddDays(day).AddHours(1))
            .First(now => policy.Active(now).Any(row => row.Key.Category == 0 && row.Key.Id == 15));
        var clock = new Clock(at);
        var items = resources.ItemResources;
        var inventory = new InventoryService(items.Items, items.GoldLimits, items.EpicPieces, clock: clock);
        using var fixture = new QuestSceneTests.Fixture(resources.Towns, resources.Characters, resources.Learning,
            SelfTestCatalogs.Growth(resources.Archive, resources.Learning), resources.Quests, false, 95,
            timeProvider: clock, inventorySetup: character => inventory.Grant(character, [new(3037, 100), new(3036, 20)]).Plan!);
        fixture.Repository.ConfigureBattlePassMissions(policy);
        That(Progress() == 0 && Balance() == 100, "skill-material fixture starts with active daily 15 and 100 real clear cubes");

        // 原生 DELETE_ITEM 正文：Main / flag0 / Type2 / 虚拟槽358 / 物品3037 / 数量5。
        var request = InventoryRequests.Delete(Convert.FromHexString("1000000010001A0A080210E60218DD172005200001"));
        That(fixture.Session.DeleteInventory(request, inventory).Accepted && Balance() == 95,
            "native Type2 operation really pays five clear cubes through original Session inventory writer");
        That(Progress() == 1,
            "daily15 counts one paid Type2 operation, not five material units");

        That(fixture.Session.DeleteInventory(request, inventory).Accepted && Balance() == 90 && Progress() == 2,
            "identical native body is a second legitimate operation, not a replay key");
        for (int operation = 3; operation <= 6; operation++)
            That(fixture.Session.DeleteInventory(request, inventory).Accepted && Balance() == 100 - 5 * operation &&
                Progress() == Math.Min(5, operation), "daily15 caps at five operations while actual payments continue: " + operation);
        That(fixture.Repository.ReadBattlePassMissions(fixture.Owner, at)!.Season.TotalExperience == 0,
            "material operations never grant mission XP automatically");
        VerifyBoundaries();
        VerifyWriterDay();
        BattlePassSkillMaterialBoundaryTests.Run(resources);
        Console.WriteLine($"PASS battle-pass-skill-material local SQLite/Session checks={Count - before}; no TCP/client validation");

        long Progress() => fixture.Repository.ReadBattlePassMissions(fixture.Owner, clock.GetUtcNow())!.Slots
            .Single(row => row.Instance.Key.Category == 0 && row.Instance.Key.Id == 15).Count;
        long Balance() => fixture.Read().Inventory.AccountWallet.Get(CurrencyKind.ClearCube);

        void VerifyBoundaries()
        {
            using var boundary = new QuestSceneTests.Fixture(resources.Towns, resources.Characters, resources.Learning,
                SelfTestCatalogs.Growth(resources.Archive, resources.Learning), resources.Quests, false, 95,
                timeProvider: clock, inventorySetup: character => inventory.Grant(character,
                    [new(3037, 100), new(3036, 20), new(1000, 6)]).Plan!);
            boundary.Repository.ConfigureBattlePassMissions(policy);
            That(ProgressOf() == 0, "boundary fixture starts from a separate zero-count mission instance");

            foreach (byte type in new byte[] { 0, 1 })
            {
                var entity = boundary.Session.Selected!.Inventory.Items.First(row => row.Core.ItemId == 1000);
                var ordinary = request with { Items = [new(type, entity.Position.Index, entity.Core.ItemId, 1)] };
                int oldCount = boundary.Read().Inventory.Items.Where(row => row.Core.ItemId == 1000).Sum(row => row.Core.Count);
                That(boundary.Session.DeleteInventory(ordinary, inventory).Accepted &&
                    boundary.Read().Inventory.Items.Where(row => row.Core.ItemId == 1000).Sum(row => row.Core.Count) == oldCount - 1 &&
                    ProgressOf() == 0, "real ordinary Type" + type + " inventory deletion does not count as a skill operation");
            }
            var equipment = request with { Items = [request.Items[0] with { Type = 3 }] };
            That(boundary.Session.DeleteInventory(equipment, inventory).Accepted && BalanceOf() == 95 && ProgressOf() == 0,
                "real Type3 equipment-script clear-cube payment does not count");
            var colored = request with { Items = [new(2, 357, 3036, 5)] };
            That(boundary.Session.DeleteInventory(colored, inventory).Accepted &&
                boundary.Read().Inventory.AccountWallet.Get(CurrencyKind.BlueCube) == 15 && ProgressOf() == 0,
                "Type2 colored-cube payment does not count for daily15 item3037");

            string preserved = Snapshot(boundary);
            That(boundary.Session.PlanAndCommitInventory(character => inventory.Delete(character, request, preserveMaterials: true)).Accepted &&
                Snapshot(boundary) == preserved && ProgressOf() == 0,
                "preserved material operation has no actual payment and writes neither assets nor battle-pass state");
            foreach (var invalid in new[]
            {
                request with { Flag = 1 },
                request with { List = InventoryListType.PersonalCargo },
                request with { Items = [request.Items[0] with { Count = 96 }] },
                request with { Items = [request.Items[0] with { Count = 0 }] },
                request with { Items = [request.Items[0] with { Type = 4 }] }
            })
            {
                string unchanged = Snapshot(boundary);
                That(!boundary.Session.DeleteInventory(invalid, inventory).Accepted && Snapshot(boundary) == unchanged,
                    "wrong flag/list/type, zero payment, or insufficient balance rejects without any write");
            }

            var staleCharacter = boundary.Session.Selected!;
            var stale = inventory.Delete(staleCharacter, request);
            That(boundary.Session.DeleteInventory(request, inventory).Accepted && BalanceOf() == 90 && ProgressOf() == 1,
                "first actual paid skill operation counts after all non-target operations");
            string once = Snapshot(boundary);
            bool rejected = false;
            try { InventoryService.Commit(boundary.Repository, boundary.Owner, staleCharacter, stale); }
            catch (InvalidOperationException) { rejected = true; }
            That(rejected && Snapshot(boundary) == once, "old inventory plan cannot repay or count through the original owner/revision writer");

            const string marker = "skill material late transaction reached";
            boundary.Sql($"""
                CREATE TRIGGER fail_skill_material_mission AFTER UPDATE OF revision ON account_battle_pass_seasons
                BEGIN
                  SELECT CASE WHEN
                    (SELECT balance FROM account_currency_balances WHERE currency_kind=7)=85
                    AND (SELECT count(*) FROM account_battle_pass_source_events)=2
                    AND (SELECT current_count FROM account_battle_pass_mission_progress WHERE category=0 AND mission_id=15)=2
                    AND (SELECT count(*) FROM currency_audit_log WHERE action_name='delete_skill_material')=3
                  THEN RAISE(ABORT,'{marker}') ELSE RAISE(ABORT,'skill material late prerequisites missing') END;
                END;
                """);
            var selected = boundary.Session.Selected;
            string beforeFault = Snapshot(boundary);
            bool late = false;
            try { boundary.Session.DeleteInventory(request, inventory); }
            catch (SqliteException error) when (error.SqliteErrorCode == 19 && error.Message.Contains(marker, StringComparison.Ordinal)) { late = true; }
            That(late && Snapshot(boundary) == beforeFault && ReferenceEquals(boundary.Session.Selected, selected),
                "exact late marker sees payment, currency audit, source and progress before rolling back every row and Session publication");
            boundary.Sql("DROP TRIGGER fail_skill_material_mission");
            That(boundary.Session.DeleteInventory(request, inventory).Accepted && BalanceOf() == 85 && ProgressOf() == 2,
                "same native body retries after late rollback and counts exactly one new operation");

            string path = (string)boundary.Query("SELECT file FROM pragma_database_list WHERE name='main'")!;
            var reopened = new CharacterRepository(path);
            reopened.ConfigureBattlePassMissions(policy);
            var restored = new GameSession(reopened, resources.Towns, boundary.Owner, timeProvider: clock);
            try
            {
                restored.Select(0); restored.MarkReady();
                That(restored.Selected!.Inventory.AccountWallet.Get(CurrencyKind.ClearCube) == 85 &&
                    reopened.ReadBattlePassMissions(boundary.Owner, at)!.Slots.Single(row => row.Instance.Key.Category == 0 && row.Instance.Key.Id == 15).Count == 2,
                    "new Repository and Session restore the committed wallet and daily15 operation count");
            }
            finally { restored.End(); }

            long ProgressOf() => boundary.Repository.ReadBattlePassMissions(boundary.Owner, clock.GetUtcNow())!.Slots
                .Single(row => row.Instance.Key.Category == 0 && row.Instance.Key.Id == 15).Count;
            long BalanceOf() => boundary.Read().Inventory.AccountWallet.Get(CurrencyKind.ClearCube);
        }

        void VerifyWriterDay()
        {
            // 选择真实排期中启用日15且前一日未启用日15的日期。
            var writerAt = Enumerable.Range(0, policy.Days).Select(day => start.AddDays(day).AddMinutes(1))
                .First(now => policy.Active(now).Any(row => row.Key.Category == 0 && row.Key.Id == 15) &&
                    !policy.Active(now.AddMinutes(-2)).Any(row => row.Key.Category == 0 && row.Key.Id == 15));
            clock.Now = writerAt.AddMinutes(-2);
            try
            {
                using var crossing = new QuestSceneTests.Fixture(resources.Towns, resources.Characters, resources.Learning,
                    SelfTestCatalogs.Growth(resources.Archive, resources.Learning), resources.Quests, false, 95,
                    timeProvider: clock, inventorySetup: character => inventory.Grant(character, [new(3037, 20)]).Plan!);
                crossing.Repository.ConfigureBattlePassMissions(policy);
                _ = crossing.Repository.ReadBattlePassMissions(crossing.Owner, clock.Now);
                var character = crossing.Session.Selected!;
                var plannedBeforeSix = inventory.Delete(character, request);
                That(plannedBeforeSix.Accepted && crossing.Read().Inventory.AccountWallet.Get(CurrencyKind.ClearCube) == 20,
                    "05:59 preparation does not pay materials or mutate mission progress");
                clock.Now = writerAt;
                InventoryService.Commit(crossing.Repository, crossing.Owner, character, plannedBeforeSix);
                That(crossing.Read().Inventory.AccountWallet.Get(CurrencyKind.ClearCube) == 15 &&
                    crossing.Repository.ReadBattlePassMissions(crossing.Owner, writerAt)!.Slots
                        .Single(row => row.Instance.Key.Category == 0 && row.Instance.Key.Id == 15).Count == 1 &&
                    Convert.ToInt64(crossing.Query("SELECT occurred_at FROM account_battle_pass_source_events")) == writerAt.ToUnixTimeSeconds(),
                    "05:59 plan paid at 06:01 counts in the actual writer day and records writer time");
            }
            finally { clock.Now = at; }
        }
    }

    static string Snapshot(QuestSceneTests.Fixture fixture)
    {
        string state = JsonSerializer.Serialize(fixture.Read());
        foreach (string table in new[] { "account_wallets", "account_currency_balances", "currency_audit_log",
            "account_battle_pass_source_events", "account_battle_pass_mission_progress", "account_battle_pass_seasons",
            "account_battle_pass_mission_claims" })
        {
            string columns = (string)fixture.Query($"SELECT group_concat('quote(\"' || name || '\")', ' || char(44) || ') FROM pragma_table_info('{table}')")!;
            state += "\n" + table + ":" + fixture.Query($"SELECT group_concat(row_value, char(10)) FROM (SELECT {columns} AS row_value FROM {table} ORDER BY row_value)");
        }
        return state;
    }

    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
