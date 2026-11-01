using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing.Inventory;

internal static class WeeklyItemExpirationTests
{
    public static void RunCore()
    {
        int before = Count;
        var sunday = new DateTimeOffset(2027, 1, 3, 6, 0, 0, TimeSpan.FromHours(8));
        That(sunday.DayOfWeek == DayOfWeek.Sunday, "测试边界确为周日六点");
        for (int day = 0; day <= 6; day++)
        {
            var boundary = sunday.AddDays(day);
            var weekly = ItemExpiration.Parse(new PvfDocument($"[weekly delete item]\n{day}\n"));
            That(weekly.WeeklyDeleteDay == day && weekly.SupportsCreation &&
                weekly.Resolve(boundary.AddSeconds(-1)) == boundary.ToUnixTimeSeconds() &&
                weekly.Resolve(boundary) == boundary.AddDays(7).ToUnixTimeSeconds() &&
                weekly.Resolve(boundary.AddSeconds(1)) == boundary.AddDays(7).ToUnixTimeSeconds(),
                "0周日及1..6星期枚举在06前/恰06/后严格取下一有效边界");
            foreach (bool absolute in new[] { false, true })
            foreach (bool relative in new[] { false, true })
                That(weekly.Resolve(boundary.AddSeconds(-1), absolute, relative) == boundary.ToUnixTimeSeconds(),
                    "商城普通期限忽略不取消每周删除");
        }
        var zero = new ItemExpiration(WeeklyDeleteDay: 0);
        That(zero.Resolve(sunday.AddDays(-2)) == sunday.ToUnixTimeSeconds() &&
            zero.Resolve(sunday.AddDays(-3).ToOffset(TimeSpan.Zero)) == sunday.ToUnixTimeSeconds(),
            "跨年/跨周及传入UTC表示仍使用本服UTC+8指定星期的06:00");
        That(ItemExpiration.Parse(new PvfDocument("")).WeeklyDeleteDay is null && new ItemExpiration().Resolve(sunday) == 0,
            "缺字段与明确0不同，原无期限物品保持永久");
        var now = sunday.AddDays(-2);
        That((zero with { Days = 1 }).Resolve(now) == now.AddDays(1).ToUnixTimeSeconds() &&
            (zero with { Days = 10 }).Resolve(now) == sunday.ToUnixTimeSeconds() &&
            (zero with { AbsoluteTime = now.AddHours(2).ToUnixTimeSeconds() }).Resolve(now) == now.AddHours(2).ToUnixTimeSeconds() &&
            (zero with { DailyDelete = 1 }).Resolve(now) == now.AddDays(1).ToUnixTimeSeconds(),
            "usable/absolute/daily与weekly合并只取原普通规则和各周期的最早期限");
        That((zero with { AbsoluteTime = sunday.AddDays(-1).ToUnixTimeSeconds() }).Resolve(DateTimeOffset.MaxValue) == sunday.AddDays(-1).ToUnixTimeSeconds() &&
            zero.Resolve(DateTimeOffset.MaxValue) == ItemExpiration.MaximumUnixTime,
            "远期钳位不溢出、不把已有更早绝对截止延后");
        foreach (int day in new[] { -1, 7, int.MaxValue })
        {
            var unknown = ItemExpiration.Parse(new PvfDocument($"[weekly delete item]\n{day}\n"));
            That(unknown.WeeklyDeleteDay == day && !unknown.SupportsCreation, "未知星期保原整数且禁止新建，不降为永久");
            Reject<InvalidDataException>(() => unknown.Resolve(now), "未知周删除不能发放");
        }
        foreach (string text in new[] { "", "0 1", "`0`", "0\n[weekly delete item]\n4", "0\n[weekly delete item", "0\n[/weekly delete item]" })
            Reject<InvalidDataException>(() => ItemExpiration.Parse(new PvfDocument("[weekly delete item]\n" + text)),
                "空值/多值/字符串/冲突重复/残缺周声明拒绝");
        Reject<InvalidDataException>(() => ItemExpiration.Parse(new PvfDocument("[outer]\n[weekly delete item]\n0\n[/outer]")),
            "嵌套周声明不得被当作缺字段永久化");
        foreach (int day in new[] { 0, 4 })
        {
            var duplicate = new PvfDocument($"[weekly delete item]\n{day}\n[weekly delete item]\n{day}\n");
            That(ItemExpiration.Parse(duplicate).WeeklyDeleteDay == day,
                "当前PVF已存在的同层完整同值周声明兼容，0仍是周日");
            Reject<InvalidDataException>(() => duplicate.ReadRootInt32("weekly delete item"),
                "通用根整数默认仍严格唯一，只有weekly调用明确放行同值重复");
        }
        Reject<InvalidDataException>(() => ItemExpiration.Parse(new PvfDocument("[daily delete item]\n1\n[daily delete item]\n1\n")),
            "每日删除没有扩展重复语义");
        var scripts = new Dictionary<string, string>
        {
            ["equipment/base.equ"] = "[weekly delete item]\n6\n[weekly delete item]\n6\n",
            ["equipment/child.equ"] = "[import script]\n`base.equ`\n"
        };
        ItemExpiration Imported() => ItemExpiration.Parse(EquipmentFile.ReadScript(path => scripts[path], "equipment/child.equ"));
        That(Imported().WeeklyDeleteDay == 6, "装备缺weekly声明按原通用import继承父星期");
        scripts["equipment/child.equ"] += "[weekly delete item]\n0\n[weekly delete item]\n0\n";
        That(Imported().WeeklyDeleteDay == 0, "子模板显式周日0覆盖父周六，不当作缺声明");
        scripts["equipment/child.equ"] = "[import script]\n`base.equ`\n[weekly delete item]\n";
        Reject<InvalidDataException>(() => Imported(), "本地空weekly不能回退父字段掩盖非法声明");
        scripts["equipment/child.equ"] = "[import script]\n`base.equ`\n";
        scripts["equipment/base.equ"] = "";
        That(Imported().WeeklyDeleteDay is null, "整条import链缺weekly保持未启用，不能默认0");
        var catalog = new ItemCatalog(null!, new Dictionary<int,string>(), new Dictionary<int,string>(), 0);
        var ordinary = new ItemTemplate(9100, ItemKind.Consumable, 0, 1, 0, 100, 0, 0, 0,
            "[trade]", ushort.MaxValue, 0, "period") { Expiration = new(Days: 7) };
        var legacy = new InventoryItem(1, new(InventoryListType.Main, 65), new(9100, ItemKind.Consumable, 1));
        foreach (var expiration in new[] { new ItemExpiration(Days: 7, WeeklyDeleteDay: 0),
            new ItemExpiration(AbsoluteTime: 12345, WeeklyDeleteDay: 6) })
        {
            catalog.PrefillTemplates(new Dictionary<int,ItemTemplate> { [9100] = ordinary with { Expiration = expiration } });
            That(InventoryExpiryService.RepairZeroPeriod(new([legacy]), catalog).Plan!.After.Items.Single() == legacy,
                "旧零截止周模板即使叠加usable/absolute，也不被历史修复永久化或续周");
        }
        catalog.PrefillTemplates(new Dictionary<int,ItemTemplate> { [9100] = ordinary });
        That(InventoryExpiryService.RepairZeroPeriod(new([legacy]), catalog).Plan!.After.Items.Single().ExpireTime == int.MaxValue,
            "无weekly旧usable实例保持原历史修复行为");
        Console.WriteLine($"PASS {Count-before} 每周删除解析、星期与创建期限边界断言");
    }

    public static void RunResources(TownCatalog towns, ItemResources resources)
    {
        int before = Count;
        var boundary = new DateTimeOffset(2027, 1, 3, 6, 0, 0, TimeSpan.FromHours(8));
        var now = boundary.AddSeconds(-1);
        var duplicateTemplate = resources.Items.Get(490013622);
        That(duplicateTemplate.Expiration.WeeklyDeleteDay == 4 &&
            boundary.AddDays(4).DayOfWeek == DayOfWeek.Thursday &&
            InventoryRewardGrantService.Create(duplicateTemplate, utcNow: now).ExpireTime == boundary.AddDays(4).ToUnixTimeSeconds(),
            "真实强化器两次同值weekly4按原模板创建有限周四截止，不按ID补永久或忽略字段");
        var template = resources.Items.Get(10159649);
        That(template.Expiration.WeeklyDeleteDay == 0, "当前PVF卢克门票明确每周日删除，不混每日/通关计数");
        var core = InventoryRewardGrantService.Create(template, utcNow: now);
        short first = ItemSlotBoundService.Range(template.Kind, InventorySnapshot.Empty).Start;
        var currentSlot = new InventorySlot(ItemSlotBoundService.List(template.Kind), first);
        var legacySlot = currentSlot with { Index = checked((short)(first + 1)) };
        That(core.ExpireTime == boundary.ToUnixTimeSeconds(), "真实门票统一Create冻结本次周日截止");
        byte[] row = Build(writer => ItemListProtocolWriter.Write(writer, first, core));
        That(BitConverter.ToInt32(row, 56) == core.ExpireTime, "沿既有123B offset56下发绝对截止，无新协议字段");
        That(!InventoryStackRuleService.CanShareStack(core, core with { ExpireTime = 0 }) &&
            !InventoryStackRuleService.CanShareStack(core, InventoryRewardGrantService.Create(template, utcNow: boundary)),
            "历史永久或跨周新实例不能合堆给旧票续期");
        string root = Path.Combine(Path.GetTempPath(), "weekly-item-expiry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool completed = false;
        using var owner = new AccountOwners().Acquire("weekly-expiry", () => { });
        GameSession? session = null;
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var created = repository.Create(owner, new("WeeklyLife", towns.Room, towns.InitialReturn) { Inventory = new([
                new(0, currentSlot, core),
                new(0, legacySlot, core with { ExpireTime = 0 })]) });
            var clock = new DailyBoosterTestClock(now);
            session = new GameSession(repository, towns, owner, timeProvider: clock, items: resources.Items);
            session.Select(0);
            That(session.Selected!.Inventory.Find(currentSlot) == created.Inventory.Find(currentSlot),
                "实际选角在到期前保原UID/截止，不重新Resolve");
            session.ReturnToRoster();
            var restored = new CharacterRepository(path).List(owner).Single();
            That(restored.Inventory.Items.Select(item => item.Core.ExpireTime).Order().SequenceEqual(new[] { 0, core.ExpireTime }),
                "重开仓储保原截止和历史零期限，不补下一周");
            clock.Advance(TimeSpan.FromSeconds(1));
            string saved = JsonSerializer.Serialize(repository.List(owner));
            Sql("CREATE TRIGGER fail_weekly_expiry BEFORE DELETE ON character_inventory_items BEGIN SELECT RAISE(ABORT,'weekly expiry rollback'); END");
            Reject<SqliteException>(() => session.Select(0), "到期选角清理SQL故障");
            That(JsonSerializer.Serialize(repository.List(owner)) == saved && session.Selected is null,
                "清理失败完整库存/UID/revision不变，不安装部分角色");
            Sql("DROP TRIGGER fail_weekly_expiry");
            session.Select(0);
            var remaining = session.Selected!.Inventory.Items.Single();
            That(remaining.Position == legacySlot && remaining.Core.ExpireTime == 0 &&
                remaining.Uid == created.Inventory.Find(legacySlot)!.Uid,
                "恰到期沿原选角清理删除新票，历史零期限不擅自补算或删除");
            session.ReturnToRoster(); clock.Advance(TimeSpan.FromDays(7)); session.Select(0);
            That(session.Selected!.Inventory.Items.Single() == remaining, "跨周再选角不为旧实例重算或续期");
            completed = true;
            void Sql(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path); db.Open();
                using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
            }
        }
        finally
        {
            session?.End(); SqliteConnection.ClearAllPools();
            if (completed) TestFiles.DeleteDirectory(root, true);
            else Console.WriteLine("Weekly expiry失败现场保留：" + root);
        }
        Console.WriteLine($"PASS {Count-before} 真实卢克门票期限、原wire、选角持久化与SQL回滚断言");
    }
}
