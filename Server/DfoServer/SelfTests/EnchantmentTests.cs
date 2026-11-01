using DfoServer.Game.Accounts;
using DfoServer.Persistence;
using DfoServer.Testing;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class EnchantmentTests
{
    public static void RunCore()
    {
        // 本次原客户端真实10B请求；只作为wire代表例，不限制生产物品编号或槽位。
        var source = new InventorySlot(InventoryListType.Main, 82);
        foreach (short targetSlot in new short[] { 20, 23 })
        {
            byte[] body = [0, 82, 0, 0, (byte)targetSlot, 0, 0, 0, 0, 0];
            var request = EnchantmentRequests.Read(body);
            That(request == new EnchantmentRequest(source, new(InventoryListType.Main, targetSlot), 0),
                "附魔272保留真实源/目标容器及槽位，固定10B无省略字段");
            Bytes(EnchantmentPackets.Ack(new(true, 0), request), [1, 0, (byte)targetSlot, 0],
                "成功ACK272仅返回目标u8/u16，不在ACK重复扣除宝珠");
            Bytes(EnchantmentPackets.Ack(InventoryResult.Reject(23), request), [0, 23, 0],
                "附魔失败保持公共u16拒绝结构，具体拒绝文案另待匹配客户端验收");
        }
        var worn = EnchantmentRequests.Read([0, 65, 0, 3, 14, 0, 255, 255, 255, 255]);
        That(worn.Source == new InventorySlot(InventoryListType.Main, 65) &&
            worn.Target == new InventorySlot(InventoryListType.Equipment, 14) && worn.Opaque == uint.MaxValue,
            "附魔272末尾u32完整保留，不缩成字节或解释为费用");
        Bytes(EnchantmentPackets.Ack(new(true, 0), worn), [1, 3, 14, 0], "穿戴目标按真实容器及槽位回显");
        foreach (byte[] body in new byte[][] { [], new byte[9], new byte[11],
            [0, 255, 255, 0, 20, 0, 0, 0, 0, 0], [0, 65, 0, 0, 255, 255, 0, 0, 0, 0] })
            Reject<InvalidDataException>(() => EnchantmentRequests.Read(body), "附魔结构或有符号槽位范围无效时拒绝");
        CatalogAndPlans();
        Spring2020KnownCardRarityQualificationPlans();
        OrderedRaritySubtypes();
        MultipleCardPlans();
        WeightedCardPlans();
        MultipleCardExtraQualificationPlans();
        EnchantmentResourceParsingFailures();
        ExpertJobKnownCardRarityQualificationPlans();
    }

    static void CatalogAndPlans()
    {
        const int beadId = 990101, cardId = 990102, gearId = 990103;
        const long now = 2_000_000_000;
        var files = new Dictionary<string, string>
        {
            ["stackable/bead.stk"] = $"[stackable type]\n`[enchant waste]` 0\n[monster card id]\n{cardId}\n[event enchant waste]\n",
            ["stackable/card.stk"] = """
                [stackable type]
                `[material expert job]` 1
                [string data]
                `card-image.img` `[wrist]` `[ring]`
                [/string data]
                [enchant table]
                [enchant index] 0
                [all elemental attack] 18
                [/enchant index]
                [enchant index] 2
                [all elemental attack] 20
                [/enchant index]
                [/enchant table]
                """
        };
        EnchantmentCatalog Catalog(string bead) => new(path => path == "stackable/bead.stk" ? bead : files[path],
            new Dictionary<int, string> { [beadId] = "bead.stk", [cardId] = "card.stk" });
        var definition = Catalog(files["stackable/bead.stk"]).Get(beadId)!;
        That(definition.CardItemId == cardId && definition.Upgrade == 0 && definition.Targets.SetEquals(
            new[] { (ItemKind.Equipment, (byte)20), (ItemKind.Equipment, (byte)21) }),
            "宝珠效果来自关联卡和索引零，图标项不当装备白名单，event说明不当收集箱奖励");
        var quoted = Catalog("[name]\n`[monster card id] 7 [enchant index] 2`\n" + files["stackable/bead.stk"]).Get(beadId)!;
        That(quoted.CardItemId == definition.CardItemId && quoted.Upgrade == definition.Upgrade && quoted.Targets.SetEquals(definition.Targets),
            "名称和说明内的字段文字不能覆盖真实宝珠映射或等级");
        var upgraded = Catalog(files["stackable/bead.stk"] + "[enchant index]\n2\n").Get(beadId);
        That(upgraded is { Upgrade: 2 }, "声明升级索引从宝珠读取，不接受请求末字段伪造升级");
        foreach (string invalidIndex in new[] { "0.5", "0x0", "0suffix", "0e1", "2.5" })
        {
            string invalidCard = files["stackable/card.stk"].Replace("[enchant index] " + (invalidIndex.StartsWith('2') ? "2" : "0"),
                "[enchant index] " + invalidIndex, StringComparison.Ordinal);
            var invalidCatalog = new EnchantmentCatalog(path => path == "stackable/bead.stk" ? files[path] : invalidCard,
                new Dictionary<int, string> { [beadId] = "bead.stk", [cardId] = "card.stk" });
            That(invalidCatalog.Get(beadId) is null, "卡片任何索引含小数/进制/后缀时拒绝完整资源，不把数字前缀当有效等级");
        }
        That(Catalog(files["stackable/bead.stk"] + "[enchant index]\n1\n").Get(beadId) is null &&
            Catalog("[stackable type]\n`[material expert job]` 1\n").Get(beadId) is null &&
            Catalog(files["stackable/bead.stk"] + "[usable datetime]\n1\n").Get(beadId) is null,
            "卡中不存在的索引、普通卡片和额外未闭合使用资格不当作普通宝珠消费");
        Reject<InvalidDataException>(() => Catalog(files["stackable/bead.stk"] + "[bead limited usable item]\n123 invalid\n").Get(beadId),
            "未知目标限制不能被忽略后扩大附魔范围");
        var restricted = Catalog(files["stackable/bead.stk"] + $"[bead limited usable item]\n{gearId}\n").Get(beadId)!;
        That(restricted.LimitedItems.SetEquals([gearId]), "宝珠显式目标模板限制完整保留");

        var beadTemplate = new ItemTemplate(beadId, ItemKind.Consumable, 0, 1, 0, 1000, 1, 2, 1, "[free]", ushort.MaxValue, 0, "Bead");
        var gearTemplate = new ItemTemplate(gearId, ItemKind.Equipment, 20, 90, 45, 1, 20, 0, 1, "[free]", ushort.MaxValue, 0, "Gear");
        var source = new InventoryItem(81, new(InventoryListType.Main, 65), new(beadId, ItemKind.Consumable, 2) { SortLockFlag = 1 });
        var target = new InventoryItem(82, new(InventoryListType.Main, 9), new(gearId, ItemKind.Equipment, 1234567)
        {
            Attr = 7, GenuineUpgrade = 4, AmplifyType = 2, AmplifyValue = 21, Rune = 8,
            Durability = 0, SealFlag = 1, EnchantCardId = 3175, EnchantUpgrade = 2, SortLockFlag = 1, Random0 = new(1, 2, 3)
        });
        Character role = new(1, 0, "Enchant", 0, new(0, 0, 0, 0), null)
        { Level = 15, Inventory = new([source, target], Gold: 3456) { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.Cera, 123) } };
        var request = new EnchantmentRequest(source.Position, target.Position, uint.MaxValue);
        InventoryResult Plan(Character character, EnchantmentRequest use, EnchantmentDefinition? rule = null, ItemTemplate? gear = null) =>
            InventoryEnchantmentService.Plan(character, use, now, rule ?? definition, beadTemplate, gear ?? gearTemplate);
        var result = Plan(role, request);
        var after = result.Plan!.After;
        That(result.Accepted && after.Find(source.Position) == source with { Core = source.Core with { Value = 1 } } &&
            after.Find(target.Position) == target with { Core = target.Core with { EnchantCardId = cardId, EnchantUpgrade = 0 } } &&
            after.Gold == role.Inventory.Gold && after.AccountWallet.Same(role.Inventory.AccountWallet) &&
            role.Inventory.Find(source.Position)!.Core.Count == 2,
            "附魔只扣真实源一件并覆盖Card/Upgrade，保留源/目标UID、锁、期限、强化增幅锻造及钱包");
        var last = Plan(role with { Inventory = after }, request);
        That(last.Accepted && last.Plan!.After.Find(source.Position) is null &&
            !Plan(role with { Inventory = last.Plan.After }, request).Accepted,
            "最后一件清源槽，再请求按耗尽源拒绝，无伪造冷却或固定u32令牌去重");
        That(!Plan(role, request, restricted with { LimitedItems = [gearId + 1] }).Accepted &&
            !Plan(role, request, gear: gearTemplate with { EquipmentSlot = 12 }).Accepted &&
            !Plan(role, request with { Target = source.Position }).Accepted &&
            !Plan(role, request with { Source = new(InventoryListType.PersonalCargo, 0) }).Accepted,
            "模板限制、装备部位、源目标相同及仓库源拒绝且不扣物品");
        foreach (var invalid in new[]
        {
            role with { Inventory = new([source with { Uid = 0 }, target]) },
            role with { Inventory = new([source with { Core = source.Core with { EquipmentLockId = 1 } }, target]) },
            role with { Inventory = new([source, target with { Core = target.Core with { EquipmentLockId = 1 } }]) },
            role with { Inventory = new([source with { Core = source.Core with { ExpireTime = (int)now } }, target]) },
            role with { Inventory = new([source, target with { Core = target.Core with { ExpireTime = (int)now } }]) }
        }) That(!Plan(invalid, request).Accepted, "临时UID、安全锁及准确到期的源/目标拒绝");
        var timedRole = role with { Inventory = new([source with { Core = source.Core with { ExpireTime = (int)now + 10 } }, target]) };
        var timed = Plan(timedRole, request);
        That(timed.Accepted && timed.Plan!.CharacterPrecondition!.Matches(timedRole, now + 9) &&
            !timed.Plan.CharacterPrecondition.Matches(timedRole, now + 10), "源或目标的有限期限在真实提交前再次核对");

        string root = Path.Combine(Path.GetTempPath(), "S5A9-enchant-clock-" + Guid.NewGuid().ToString("N"));
        try
        {
            string database = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(database);
            using var owner = new AccountOwners().Acquire("enchant-clock", () => { });
            var persisted = repository.Create(owner, new("ClockEnchant", role.Position, role.Return)
            { Level = role.Level, Inventory = new([
                source with { Uid = 0, Core = source.Core with { ExpireTime = (int)now + 10 } },
                target with { Uid = 0 }], Gold: role.Inventory.Gold) });
            var clock = new ConsumableUseSessionTests.FakeClock();
            clock.Advance(DateTimeOffset.FromUnixTimeSeconds(now) - clock.GetUtcNow());
            var pending = InventoryEnchantmentService.Plan(persisted, request, now, definition, beadTemplate, gearTemplate, clock: clock);
            That(pending.Accepted && pending.Plan!.CharacterPrecondition!.Matches(persisted),
                "附魔规划与真实提交前置共用注入时钟");
            string unchanged = Snapshot();
            clock.Advance(TimeSpan.FromSeconds(10));
            Reject<InventoryQualificationExpiredException>(() => InventoryService.Commit(repository, owner, persisted, pending),
                "附魔规划有效但提交恰到期时拒绝，不复用规划时间");
            That(Snapshot() == unchanged, "附魔到期提交保持源、目标完整核心、UID、金币、revision与审计序列不变");
            string Snapshot()
            {
                using var connection = new SqliteConnection("Data Source=" + database + ";Pooling=False");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT (SELECT count(*) FROM inventory_audit_log)||':'||(SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items')";
                return JsonSerializer.Serialize(repository.LoadCharacter(owner, persisted.Slot)) + ":" + command.ExecuteScalar();
            }
        }
        finally { if (Directory.Exists(root)) TestFiles.DeleteDirectory(root, recursive: true); }

        PvfTokenDocument Pool(params object[] values)
        {
            var strings = new List<string> { "[index]", "[/index]" };
            PvfToken Token(object value)
            {
                if (value is int integer) return new(0, integer);
                strings.Add((string)value);
                return new(6, strings.Count - 1);
            }
            return new([new(3, 0), .. values.Select(Token), new(3, 1)], index => strings[index]);
        }
        var pool = Pool(10015, 1, 1, 100, "[at priest]", 14, 1,
            2, 1, 0, "[at priest]", 15, 1,
            3, 2, 50, "[at priest]", 14, 2, "[at priest]", 15, 2,
            1, 1, 999, "[at priest]", 109, 2);
        string auto = $"[stackable type]\n`[auto enchant]` 0\n[string data]\n`[title name]`\n[/string data]\n" +
            $"[random title enchant]\n10015\n[bead limited usable item]\n{gearId}\n[/bead limited usable item]\n";
        var randomDefinition = new EnchantmentCatalog(_ => auto, new Dictionary<int, string> { [beadId] = "bead.stk" },
            _ => pool).Get(beadId)!;
        That(randomDefinition.RandomTitle is { TotalWeight: 150 } &&
            randomDefinition.RandomTitle.Draw(_ => 0) == 0x0001271Fu &&
            randomDefinition.RandomTitle.Draw(_ => 99) == 0x0001271Fu &&
            randomDefinition.RandomTitle.Draw(_ => 100) == 0x0003271Fu &&
            randomDefinition.RandomTitle.Draw(_ => 149) == 0x0003271Fu,
            "随机称号按完整行权重抽样，零权重不可中、重复序号首条有效，双技能共用一个序号");
        foreach (var invalid in new[] { Pool(10016, 1, 1, 1, "[at priest]", 14, 1),
            Pool(10015, 1, 1, 0, "[at priest]", 14, 1), Pool(10015, 65536, 1, 1, "[at priest]", 14, 1),
            Pool(10015, 1, 3, 1), Pool(10015, 1, 1, -1), Pool(10015, 1, 1, 1, "[at priest]", 14) })
            Reject<InvalidDataException>(() => RandomTitleEnchantmentPool.Parse(invalid, 10015),
                "奖池缺失、全零、索引越界、技能个数、负权重和截断行拒绝");
        Reject<OverflowException>(() => RandomTitleEnchantmentPool.Parse(Pool(10015,
            1, 1, int.MaxValue, "[at priest]", 14, 1, 2, 1, 1, "[at priest]", 15, 1), 10015), "权重和溢出拒绝");
        var titleTemplate = gearTemplate with { EquipmentSlot = 13 };
        int draws = 0;
        InventoryResult RandomPlan(Character character, EnchantmentDefinition? rule = null) => InventoryEnchantmentService.Plan(
            character, request, now, rule ?? randomDefinition, beadTemplate, titleTemplate, maximum => { draws++; return maximum - 1; });
        var enchanted = RandomPlan(role);
        That(enchanted.Accepted && draws == 1 && enchanted.Plan!.After.Find(target.Position) ==
            target with { Core = target.Core with { Extension97 = 0x0003271Fu } } &&
            enchanted.Plan.After.Find(source.Position)!.Core.Count == 1,
            "随机称号保留普通Card/Upgrade及完整核心UID，只改97扩展并原子扣一颗，奖池职业不收窄all使用资格");
        That(!RandomPlan(role, randomDefinition with { LimitedItems = [gearId + 1] }).Accepted && draws == 1,
            "白名单拒绝不抽奖、不消耗；请求opaque不能指定效果");
    }

    public static void RunResources(ItemResources resources, EnchantmentCatalog enchantments)
    {
        MultipleCardResourcePlans(resources, enchantments);
        WeightedCardResourcePlans(resources);
        MultipleCardExtraQualificationResources(resources, enchantments);
        Spring2020RealBeadQualificationResources(resources);
        var definition = enchantments.Get(490705314);
        That(definition is { CardItemId: 10309006, Upgrade: 0 } && definition.Targets.SetEquals(
            new[] { (ItemKind.Equipment, (byte)19), (ItemKind.Equipment, (byte)20), (ItemKind.Equipment, (byte)21) }),
            "米歇尔之魄真实Card/Index和三项首饰部位来自匹配PVF，不能取收集箱说明的攻击力12");
        foreach (int id in new[] { 22004, 24002 })
        {
            var template = resources.Items.Get(id);
            That(definition!.Targets.Contains((template.Kind, template.EquipmentSlot)), "实际用户目标模板由关联卡声明允许");
        }
        var weapon = resources.Items.Get(27308);
        That(!definition!.Targets.Contains((weapon.Kind, weapon.EquipmentSlot)), "用户强化目标不是该首饰宝珠的允许附魔部位");
        var title = enchantments.Get(490703772)!;
        That(title.RandomTitle is { Index: 10015 } && title.Targets.SetEquals([(ItemKind.Equipment, (byte)13)]) &&
            title.LimitedItems.SetEquals([400330167, 400330168, 400330169, 400330170]) &&
            title.RandomTitle.Entries.All(entry => entry.Weight > 0), "女圣职者从零宝珠读取真实奖池和四件称号白名单");
    }
}
