using static DfoServer.Testing.TestCheck;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Inventory.Models;
using System.Text.Json;
using System.Collections.Immutable;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer.Testing.Inventory;

internal static class DailyBoosterUsageTests
{
    internal const string Retained = "[stackable type]\n`[booster]` 0\n[booster info]\n[etc]\n1 490704522 1000 10\n[/etc]\n[/booster info]\n[total usable count]\n1\n[impossible contents]\n`delete booster`\n[/impossible contents]\n";

    public static void RunCore()
    {
        That(BoosterCatalog.Parse(Retained).Available, "每日保留普通盒接受完整奖池及明确删除禁令");
        That(!BoosterCatalog.Parse(Retained + "[unknown count group").Available,
            "未闭合未知根字段不能被正则静默忽略");
        foreach (string count in new[] { "", "0", "-1", "256", "1 2", "`1`", "1\n[total usable count]\n1" })
            That(!BoosterCatalog.Parse(Retained.Replace("[total usable count]\n1", "[total usable count]\n" + count)).Available,
                "每日保留次数拒绝缺失、非法、溢出及重复值 " + count);
        foreach (string extra in new[] { "[unknown count group]\n1", "[total weekly usable count]\n1", "[action usable place]\n`[village]`\n[/action usable place]" })
            That(!BoosterCatalog.Parse(Retained + extra).Available, "未知共享组、周次数及地点声明保持Pending");
        That(!BoosterCatalog.Parse(Retained.Replace("`delete booster`", "`not delete booster`")).Available &&
            !BoosterCatalog.Parse(Retained.Replace("[/impossible contents]", "")).Available,
            "保留源必须来自唯一完整块的精确token");
        foreach (string bad in new[] { Retained + "[impossible contents]\n`delete booster`\n[/impossible contents]",
            Retained.Replace("[total usable count]\n1", "[booster info]\n[total usable count]\n1\n[/booster info]"),
            Retained.Replace("`delete booster`", "`delete booster` `another policy`") })
            That(!BoosterCatalog.Parse(bad).Available, "重复块、嵌套计数字段及未知禁令不误放行");
        That(BoosterCatalog.Parse(Retained.Replace("[total usable count]\n1", "[total usable count]\n255")).DailyRetainedLimit == 255,
            "次数255合法但不绕过独立每包255条限制");
        var counted = JsonSerializer.Deserialize<InventorySnapshot>("{\"Items\":[],\"DailyBoosterUses\":[{\"ItemId\":123,\"Day\":1,\"Used\":1}]}")!;
        That(new InventoryPlan(InventorySnapshot.Empty, counted, "daily_usage").Changed,
            "仅次数变化也是库存真实变化，不能静默丢弃独立账本");
        var boundary = new DateTimeOffset(2026, 12, 31, 22, 0, 0, TimeSpan.Zero);
        int today = ShopPurchaseCount.Today(boundary);
        That(ShopPurchaseCount.Today(boundary.AddSeconds(-1)) == today - 1 &&
            DailyBoosterUsage.NextReset(boundary.AddSeconds(-1)) == boundary &&
            DailyBoosterUsage.NextReset(boundary) == boundary.AddDays(1), "UTC+8六点跨年边界与下一次重置严格前进");
        var both = ImmutableArray<DailyBoosterUsage>.Empty;
        That(DailyBoosterUsage.TryConsume([], 123, 1, 1, boundary, out var once) &&
            !DailyBoosterUsage.TryConsume(once, 123, 1, 1, boundary, out _) &&
            DailyBoosterUsage.TryConsume(once, 124, 255, 255, boundary, out both) &&
            DailyBoosterUsage.Same(both, both.Reverse().ToImmutableArray()), "模板共额度、异模板独立、255边界与反序语义比较");
        That(!DailyBoosterUsage.TryConsume([], 123, 256, 255, boundary, out _) &&
            !DailyBoosterUsage.TryConsume([], 123, 0, 1, boundary, out _) &&
            !DailyBoosterUsage.TryConsume(once, 123, 1, 1, boundary.AddSeconds(-1), out _), "批量越界与时钟回拨拒绝，不能恢复额度");
        That(DailyBoosterUsage.TryConsume(both, 123, 1, 1, boundary.AddDays(1), out var next) && next.Length == 1 &&
            DailyBoosterUsage.Effective(both, 123, today + 1) == 0, "正常跨日成功清理旧行，读取不改历史账本");
        foreach (ImmutableArray<DailyBoosterUsage> invalid in new ImmutableArray<DailyBoosterUsage>[]
        {
            default, [new(0, today, 1)], [new(1, -1, 1)], [new(1, today, 0)], [new(1, today, 1), new(1, today, 2)], [null!]
        })
        {
            bool rejected = false;
            try { (InventorySnapshot.Empty with { DailyBoosterUses = invalid }).Validate(); }
            catch (InvalidDataException) { rejected = true; }
            That(rejected, "无效账本不能被快照接受或静默归零");
        }
        string root = Path.Combine(Path.GetTempPath(), "daily-booster-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            _ = new CharacterRepository(path);
            using var db = new SqliteConnection("Data Source=" + path);
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT count(*) FROM pragma_table_info('characters') WHERE name='daily_booster_usage' AND dflt_value='''[]''' AND \"notnull\"=1";
            That((long)command.ExecuteScalar()! == 1, "当前schema包含独立每日礼盒非空数组列");
            command.CommandText = "INSERT INTO characters(account,slot,name,revision,town,area,x,y,direction,gold) VALUES('daily-old',0,'DailyOld',7,38,1,450,234,0,77); ALTER TABLE characters DROP COLUMN daily_booster_usage; PRAGMA user_version=55";
            command.ExecuteNonQuery();
            _ = new CharacterRepository(path);
            command.CommandText = "SELECT revision||':'||gold||':'||daily_booster_usage FROM characters WHERE name='DailyOld'";
            That((string)command.ExecuteScalar()! == "7:77:[]", "55升级56只新增空数组，不改旧资产或revision");
            command.CommandText = "PRAGMA user_version";
            That((long)command.ExecuteScalar()! == 56, "迁移采用fresh核验的下一连续版本56");
            command.CommandText = "UPDATE characters SET daily_booster_usage='{}'";
            bool rejected = false;
            try { command.ExecuteNonQuery(); }
            catch (SqliteException) { rejected = true; }
            That(rejected, "SQL约束拒绝非数组JSON，不能静默清次数");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
