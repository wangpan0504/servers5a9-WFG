using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class EnchantmentTests
{
    static void MultipleCardPlans()
    {
        const int beadId = 990111, first = 990112, second = 990113, third = 990114, titleId = 990115;
        const long now = 2_000_000_000;
        const string card = """
            [stackable type]
            `[material expert job]` 1
            [string data]
            `card-image.img` `[title]`
            [/string data]
            [enchant table]
            [enchant index] 0
            [all elemental attack] 10
            [/enchant index]
            [/enchant table]
            """;
        const string bead = """
            [stackable type]
            `[enchant waste]` 0
            [monster card id]
            990112 990112 990113 990112 990114 990114
            [/monster card id]
            [bead limited usable item]
            990115
            [/bead limited usable item]
            """;
        EnchantmentCatalog Catalog(string body, Action<Dictionary<int, string>, Dictionary<string, string>>? alter = null)
        {
            var index = new Dictionary<int, string>
            { [beadId] = "bead.stk", [first] = "first.stk", [second] = "second.stk", [third] = "third.stk" };
            var files = new Dictionary<string, string>
            { ["stackable/bead.stk"] = body, ["stackable/first.stk"] = card,
                ["stackable/second.stk"] = card, ["stackable/third.stk"] = card };
            alter?.Invoke(index, files);
            return new EnchantmentCatalog(path => files[path], index);
        }
        EnchantmentDefinition? Load(string body, Action<Dictionary<int, string>, Dictionary<string, string>>? alter = null) =>
            Catalog(body, alter).Get(beadId);

        var definition = Load(bead) ?? throw new InvalidDataException("合法多卡宝珠定义缺失");
        That(definition.RandomTitle is null && definition.CardItemId == 0,
            "普通多卡列表是随机卡池，不把首卡冒充固定卡，也不当97扩展技能池");
        var beadTemplate = new ItemTemplate(beadId, ItemKind.Consumable, 0, 1, 0, 1000, 1, 2, 1, "[free]", ushort.MaxValue, 0, "Bead");
        var titleTemplate = new ItemTemplate(titleId, ItemKind.Equipment, 13, 1, 0, 1, 1, 2, 1, "[free]", ushort.MaxValue, 0, "Title");
        var source = new InventoryItem(91, new(InventoryListType.Main, 82), new(beadId, ItemKind.Consumable, 2));
        var target = new InventoryItem(92, new(InventoryListType.Main, 9), new(titleId, ItemKind.Equipment, 1234567)
        { Attr = 7, GenuineUpgrade = 4, AmplifyType = 2, AmplifyValue = 21, Rune = 8, SealFlag = 1,
            EnchantCardId = 3175, EnchantUpgrade = 2, Extension97 = 0x00012711, SortLockFlag = 1, Random0 = new(1, 2, 3) });
        Character role = new(1, 0, "MultiBead", 0, new(0, 0, 0, 0), null)
        { Level = 25, Inventory = new([source, target], Gold: 1234) };
        var request = new EnchantmentRequest(source.Position, target.Position, uint.MaxValue);
        int draws = 0;
        InventoryResult Plan(Character character, int position, EnchantmentDefinition? rule = null,
            EnchantmentRequest? use = null, ItemTemplate? gear = null) => InventoryEnchantmentService.Plan(
                character, use ?? request, now, rule ?? definition, beadTemplate, gear ?? titleTemplate, maximum =>
                { That(maximum == 6, "抽样域为完整六个位置，重复卡不去重"); draws++; return position; });
        int[] expected = [first, first, second, first, third, third];
        var observed = new List<int>();
        for (int position = 0; position < expected.Length; position++)
        {
            int beforeDraws = draws;
            var result = Plan(role, position);
            That(result.Accepted && draws == beforeDraws + 1 &&
                result.Plan!.After.Find(source.Position) == source with { Core = source.Core with { Value = 1 } } &&
                result.Plan.After.Find(target.Position) == target with
                { Core = target.Core with { EnchantCardId = expected[position], EnchantUpgrade = 0 } } &&
                result.Plan.After.Gold == 1234 && role.Inventory.Find(source.Position) == source,
                "每个合法原位置只抽一次并原子扣一珠，只覆盖Card/Upgrade，保留UID、97扩展与完整核心");
            observed.Add(result.Plan!.After.Find(target.Position)!.Core.EnchantCardId);
        }
        That(observed.Count(value => value == first) == 3 && observed.Count(value => value == second) == 1 &&
            observed.Count(value => value == third) == 2, "重复非连续卡位保留3:1:2权重，所有位置均可达");
        int noDraws = draws;
        foreach (var rejected in new[]
        {
            Plan(role, 0, definition with { LimitedItems = [titleId + 1] }),
            Plan(role, 0, gear: titleTemplate with { EquipmentSlot = 20 }),
            Plan(role, 0, use: request with { Target = source.Position }),
            Plan(role, 0, use: request with { Source = new(InventoryListType.PersonalCargo, 0) }),
            Plan(role with { Level = 0 }, 0),
            Plan(role with { Inventory = new([source with { Core = source.Core with { EquipmentLockId = 1 } }, target]) }, 0),
            Plan(role with { Inventory = new([source, target with { Core = target.Core with { ExpireTime = (int)now } }]) }, 0)
        }) That(!rejected.Accepted && rejected.Plan is null, "多卡池资格、容器、安全锁及期限拒绝时无扣源计划");
        That(draws == noDraws && role.Inventory.Find(source.Position) == source && role.Inventory.Find(target.Position) == target,
            "全部拒绝均不抽样、不扣源、不改变原角色快照");
        var lastRole = role with { Inventory = new([source with { Core = source.Core with { Value = 1 } }, target]) };
        var last = Plan(lastRole, 5);
        noDraws = draws;
        That(last.Accepted && last.Plan!.After.Find(source.Position) is null &&
            !Plan(lastRole with { Inventory = last.Plan.After }, 0).Accepted && draws == noDraws,
            "最后一珠清源槽，耗尽重发不重抽、不重复安装");

        foreach (string invalid in new[] { bead.Replace("990113", "missing"), bead.Replace("990113", "999999"),
            bead + "\n[monster card id]\n990112\n", bead.Replace("990113", "0") })
            That(Refuses(invalid), "列表非法token、缺卡、重复字段或零ID不能截成首个合法卡");
        That(Refuses(bead, (index, _) => index.Remove(third)) &&
            Refuses(bead, (_, files) => files["stackable/third.stk"] = card.Replace("[material expert job]", "[enchant waste]")) &&
            Refuses(bead, (_, files) => files["stackable/third.stk"] = card.Replace("[enchant index] 0", "[enchant index] 1")),
            "完整校验全部候选映射、卡类型及附魔等级，不能只验证首卡");
        var templates = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        templates.PrefillTemplates(new Dictionary<int, ItemTemplate> { [beadId] = beadTemplate, [titleId] = titleTemplate });
        noDraws = draws;
        foreach (string extra in new[] { "[action usable place]\n`[unknown place]`\n[/action usable place]",
            "[item category]\n`restricted`\n[/item category]", "[usable expired item]\n" })
        {
            var pending = Catalog(bead + "\n" + extra);
            var service = new InventoryEnchantmentService(templates, pending, random: _ => { draws++; return 0; });
            var rejected = service.Plan(role, request, now);
            That(pending.Get(beadId) is null && !rejected.Accepted && rejected.Plan is null && draws == noDraws &&
                role.Inventory.Find(source.Position) == source && role.Inventory.Find(target.Position) == target,
                "多卡地点、分类或材料声明未闭合时，真实服务拒绝且不抽样、不扣源、不改目标");
        }
        EnchantmentDefinition? mixed = null;
        try { mixed = Load(bead, (_, files) => files["stackable/third.stk"] = card.Replace("[title]", "[wrist]")); }
        catch (InvalidDataException) { }
        noDraws = draws;
        That(mixed is null || !Plan(role, 0, mixed).Accepted, "任一候选不支持目标时不能随机装入不适用卡");
        That(draws == noDraws, "候选目标资格在抽样前完整确定");
        MultipleCardRollback(role, request, definition, beadTemplate, titleTemplate, now);

        bool Refuses(string body, Action<Dictionary<int, string>, Dictionary<string, string>>? alter = null)
        {
            try { return Load(body, alter) is null; }
            catch (InvalidDataException) { return true; }
        }
    }

    static void MultipleCardRollback(Character role, EnchantmentRequest request, EnchantmentDefinition definition,
        ItemTemplate beadTemplate, ItemTemplate titleTemplate, long now)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-multi-card-" + Guid.NewGuid().ToString("N"));
        try
        {
            string database = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(database);
            using var owner = new AccountOwners().Acquire("multi-card-bead", () => { });
            var persisted = repository.Create(owner, new("MultiSave", role.Position, role.Return)
            { Level = role.Level, Inventory = role.Inventory with { Items = [.. role.Inventory.Items.Select(item => item with { Uid = 0 })] } });
            int draws = 0;
            var result = InventoryEnchantmentService.Plan(persisted, request, now, definition, beadTemplate, titleTemplate,
                _ => { draws++; return 4; });
            That(result.Accepted && draws == 1, "多卡事务开始前只生成一个实际结果");
            Sql("CREATE TRIGGER fail_multi_card BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='enchant_by_bead' BEGIN SELECT RAISE(ABORT,'multi card rollback'); END");
            string before = Snapshot();
            Reject<SqliteException>(() => InventoryService.Commit(repository, owner, persisted, result),
                "多卡附魔审计末段失败，选中卡与源扣除同笔回滚");
            That(Snapshot() == before && draws == 1, "回滚保留完整核心、UID、金币、revision、审计和序列，不重抽");
            Sql("DROP TRIGGER fail_multi_card");
            var committed = InventoryService.Commit(repository, owner, persisted, result);
            var reopened = new CharacterRepository(database).LoadCharacter(owner, persisted.Slot);
            That(committed.Revision == persisted.Revision + 1 && reopened.Revision == committed.Revision && draws == 1 &&
                reopened.Inventory.Find(request.Source)!.Core.Count == 1 &&
                reopened.Inventory.Find(request.Target) == persisted.Inventory.Find(request.Target)! with
                { Core = persisted.Inventory.Find(request.Target)!.Core with { EnchantCardId = 990114, EnchantUpgrade = 0 } },
                "提交与重开库保存同一选中卡和完整实例，不重新抽奖或改97扩展");
            Reject<InvalidOperationException>(() => InventoryService.Commit(repository, owner, persisted, result),
                "旧revision的随机计划不可再次扣源或覆盖效果");

            void Sql(string sql)
            {
                using var connection = new SqliteConnection("Data Source=" + database + ";Pooling=False");
                connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
            }
            string Snapshot()
            {
                using var connection = new SqliteConnection("Data Source=" + database + ";Pooling=False");
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "SELECT (SELECT count(*) FROM inventory_audit_log)||':'||" +
                    "(SELECT group_concat(name||':'||seq,';') FROM (SELECT name,seq FROM sqlite_sequence ORDER BY name))";
                return JsonSerializer.Serialize(repository.LoadCharacter(owner, persisted.Slot)) + ":" + command.ExecuteScalar();
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    static void MultipleCardResourcePlans(ItemResources resources, EnchantmentCatalog enchantments)
    {
        int[] sea = [.. Enumerable.Repeat(490701142, 30), .. Enumerable.Repeat(490701143, 5),
            .. Enumerable.Repeat(490701144, 25), .. Enumerable.Repeat(490701145, 5),
            .. Enumerable.Repeat(490701146, 30), .. Enumerable.Repeat(490701147, 5)];
        int[] summer = [.. Enumerable.Repeat(490702344, 5), .. Enumerable.Repeat(490702345, 5),
            .. Enumerable.Repeat(490702346, 5), .. Enumerable.Repeat(490702347, 5),
            .. Enumerable.Repeat(490702348, 2), .. Enumerable.Repeat(490702349, 2),
            .. Enumerable.Repeat(490702350, 2), .. Enumerable.Repeat(490702351, 2),
            .. Enumerable.Repeat(490702352, 4), .. Enumerable.Repeat(490702353, 4), .. Enumerable.Repeat(490702354, 4)];
        foreach (var (beadId, expected, allowed) in new (int, int[], int[])[]
        {
            (490701137, sea, [400330097,400330098,400330099]),
            (490701256, sea, [400330097,400330098,400330099]),
            (490702314, summer, [400330118,400330119,400330120,400330121]),
            (490702319, summer, [400330118,400330119,400330120,400330121]),
            (490702378, summer, [400330118,400330119,400330120,400330121])
        })
        {
            var definition = enchantments.Get(beadId) ?? throw new InvalidDataException("真实多卡宝珠定义缺失: " + beadId);
            That(definition.CardItemId == 0 && definition.Upgrade == 0 && definition.RandomTitle is null &&
                definition.LimitedItems.SetEquals(allowed), "真实普通多卡宝珠保留完整白名单且不冒固定卡：" + beadId);
            var beadTemplate = resources.Items.Get(beadId);
            var targetTemplate = resources.Items.Get(allowed[0]);
            var source = new InventoryItem(71, new(InventoryListType.Main, 82), InventoryRewardGrantService.Create(beadTemplate, 2));
            var target = new InventoryItem(72, new(InventoryListType.Main, 9), InventoryRewardGrantService.Create(targetTemplate) with
            { EnchantCardId = 3175, EnchantUpgrade = 2, Extension97 = 0x00012711, SortLockFlag = 1 });
            Character role = new(1, 0, "RealMulti", 0, new(0, 0, 0, 0), null)
            { Level = 95, ExpertJobType = 1, Inventory = new([source, target], Gold: 1234) };
            var request = new EnchantmentRequest(source.Position, target.Position, uint.MaxValue);
            for (int position = 0; position < expected.Length; position++)
            {
                int draws = 0;
                var result = InventoryEnchantmentService.Plan(role, request, 2_000_000_000, definition, beadTemplate, targetTemplate,
                    maximum => { That(maximum == expected.Length, "真实重复卡位完整进入抽样域"); draws++; return position; });
                That(result.Accepted && draws == 1 && result.Plan!.After.Find(target.Position) == target with
                    { Core = target.Core with { EnchantCardId = expected[position], EnchantUpgrade = 0 } } &&
                    result.Plan.After.Find(source.Position)!.Core.Count == 1,
                    "真实五种珠逐位置覆盖原权重与完整卡核心：" + beadId + ":" + position);
            }
            int sharedDraws = 0;
            var service = new InventoryEnchantmentService(resources.Items, enchantments,
                random: maximum => { sharedDraws++; return maximum - 1; });
            int expectedExperience = role.ExpertJobExperience + resources.ExpertJobs.MonsterCardExp(1,
                resources.ExpertJobs.Level(1, role.ExpertJobExperience)).Min;
            That(ExpertJobCraft.TryPlanEnchant(role, service, resources.ExpertJobs, 0, 9, 0, 82, 2_000_000_000,
                out var shared, out int experience, out _) && sharedDraws == 1 &&
                shared.After.Find(target.Position)!.Core == target.Core with { EnchantCardId = expected[^1], EnchantUpgrade = 0 } &&
                shared.After.Find(source.Position)!.Core.Count == 1 && experience == expectedExperience,
                "CMD601本人宝珠fallback复用同一随机计划，完整扣源并保留副职业结算：" + beadId);
        }
    }
}
