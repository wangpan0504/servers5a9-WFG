using DfoServer.Game.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Inventory.Models;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class AntiEvilUpgradeTests
{
    public static void RunCore()
    {
        int before = Count;
        var rules = AntiEvilUpgradeCatalog.Parse(Fixture());
        AssertWeights(rules);
        int[] expected = [42, 16, 26, 21, 0, 21];
        That(Enumerable.Range(0, 6).All(type =>
            AntiEvilUpgradeContribution.TryCalculate(rules, 3, type, 9, out int value) && value == expected[type]),
            "抗魔六项使用原生n+1，独立于费用、品级和装备评分");
        That(AntiEvilUpgradeContribution.TryCalculate(rules, 3, 0, 0, out int first) && first == 4,
            "原生索引0乘1，不擅自解释成未强化装备");
        var half = new AntiEvilUpgradeRules([1.5f], [1], 12);
        That(AntiEvilUpgradeContribution.TryCalculate(half, 0, 0, 0, out int rounded) && rounded == 1,
            "非负半值向上取整，不使用银行家舍入");
        var precision = new AntiEvilUpgradeRules([1.3f], [3], 12);
        That(AntiEvilUpgradeContribution.TryCalculate(precision, 0, 0, 4, out int single) && single == 7,
            "保持float32逐步乘除，边界值不提升为double");
        That(!AntiEvilUpgradeContribution.TryCalculate(rules, 7, 0, 0, out _) &&
            !AntiEvilUpgradeContribution.TryCalculate(rules, 0, 6, 0, out _) &&
            !AntiEvilUpgradeContribution.TryCalculate(rules with { Divisor = 0 }, 0, 0, 0, out _) &&
            !AntiEvilUpgradeContribution.TryCalculate(rules with { Divisor = -1 }, 0, 0, 0, out _) &&
            !AntiEvilUpgradeContribution.TryCalculate(new([-1f], [1], 12), 0, 0, 0, out _),
            "越界、非正除数和负结果不产生有效贡献");
        Reject<InvalidDataException>(() => AntiEvilUpgradeCatalog.Parse(Fixture(shortRarity: true)),
            "抗魔稀有度七项缺失拒绝");
        Reject<InvalidDataException>(() => AntiEvilUpgradeCatalog.Parse(Fixture(floatType: true)),
            "部位权重保留整数token合同");
        Reject<InvalidDataException>(() => AntiEvilUpgradeCatalog.Parse(Fixture(duplicate: true)),
            "重复抗魔权重块拒绝");
        int[] slots = [12, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 25];
        int[] slotValues = [42, 16, 16, 16, 16, 16, 26, 26, 26, 21, 21, 21];
        That(slots.Select((slot, index) =>
            AntiEvilUpgradeContribution.TryCalculateForEquipment(rules, 3, slot, 10, out int value) && value == slotValues[index]).All(value => value),
            "装备真实等级10转原生索引9，部位分支选择对应抗魔槽，耳环取第六项");
        That(AntiEvilUpgradeContribution.TryCalculateForEquipment(rules, 3, 12, 0, out int none) && none == 0 &&
            new[] { 0, 11, 13, 24, 26, 32, 34, 35 }.All(slot =>
                AntiEvilUpgradeContribution.TryCalculateForEquipment(rules, 3, slot, 10, out int value) && value == 0),
            "零级与不写升级抗魔的部位保留零贡献");
        var forge = AntiEvilUpgradeCatalog.ParseForge(ForgeFixture());
        That(forge.Length == 8 && forge[0].SequenceEqual([5, 5, 7, 8, 10, 5, 10]) &&
            forge[7].SequenceEqual([35, 35, 45, 54, 66, 35, 62]), "锻造八级七稀有度完整行");
        That(AntiEvilUpgradeContribution.ForgeValue(forge, 4, 8) == 66 &&
            AntiEvilUpgradeContribution.ForgeValue(forge, 6, 1) == 10 &&
            AntiEvilUpgradeContribution.ForgeValue(forge, 4, 0) == 0 &&
            AntiEvilUpgradeContribution.ForgeValue(forge, 4, 9) == 0 &&
            AntiEvilUpgradeContribution.ForgeValue(forge, 7, 1) == 0,
            "锻造按实际等级与稀有度直查，无行或越界返回零");
        Reject<InvalidDataException>(() => AntiEvilUpgradeCatalog.ParseForge(ForgeFixture(incomplete: true)),
            "锻造不完整七列拒绝");
        That(AntiEvilUpgradeContribution.EquipmentValue(480, 42, 66) == 546 &&
            AntiEvilUpgradeContribution.EquipmentValue(480, 70, 66) == 550,
            "单件基础加强化和锻造的较大者，不能双重相加");
        That(AntiEvilUpgradeCatalog.ReadBase(BaseFixture()) == 480 &&
            AntiEvilUpgradeCatalog.ReadBase(BaseFixture(nestedOnly: true)) == 0 &&
            AntiEvilUpgradeCatalog.ReadBase(BaseFixture(add: 20)) == 500,
            "基础抗魔仅取根单整数，不误取level linear ability嵌套范围");
        Reject<InvalidDataException>(() => AntiEvilUpgradeCatalog.ReadBase(BaseFixture(malformed: true)),
            "根基础抗魔拒绝范围双整数");
        var inherited = new Dictionary<string, PvfTokenDocument>
        {
            ["equipment/parent.equ"] = EquipmentFixture(480),
            ["equipment/add.equ"] = EquipmentFixture(add: 20, parent: "parent.equ"),
            ["equipment/zero.equ"] = EquipmentFixture(0, parent: "parent.equ"),
            ["equipment/loop.equ"] = EquipmentFixture(parent: "loop.equ")
        };
        string ResolveImport(int index) => index == 3 ? "parent.equ" : "loop.equ";
        That(AntiEvilUpgradeCatalog.ReadEquipmentBase(path => inherited[path], ResolveImport, "equipment/add.equ") == 500 &&
            AntiEvilUpgradeCatalog.ReadEquipmentBase(path => inherited[path], ResolveImport, "equipment/zero.equ") == 0,
            "装备基础抗魔按导入继承，局部add追加，显式零覆盖父模板");
        Reject<InvalidDataException>(() => AntiEvilUpgradeCatalog.ReadEquipmentBase(path => inherited[path], ResolveImport, "equipment/loop.equ"),
            "装备抗魔导入循环拒绝");
        That(AntiEvilUpgradeContribution.TotalValue([5000, 10, 8], [10f, 20f]) == 6523 &&
            AntiEvilUpgradeContribution.TotalValue([16777217], []) == 16777216,
            "最终抗魔整数先合计，百分比逐项加到1f，再一次float32乘法截断");
        var effect = new RecoveryConsumableDefinition(100, 1, 0, 1)
            { StatChanges = [new("+", 10, "antievil"), new("+", 90, "strength")] };
        ConsumableState[] states = [new(ConsumableStateKind.Effect, 100, 2000, 7, 1),
            new(ConsumableStateKind.Effect, 101, 1000, 7, 1), new(ConsumableStateKind.Cooldown, 102, 2000, 7, 1)];
        That(AntiEvilUpgradeContribution.ConsumableFlatValue(states, 1000, id =>
            id == 100 ? effect : throw new InvalidOperationException("过期及冷却不读取效果")) == 10,
            "复用现有活跃Effect状态，仅累加资源antievil平值，不计过期、冷却或其他属性");
        EquipmentBaseTailCore();
        MemberSetListCompatibility();
        MemberSetListProviderCore();
        ProviderCore();
        Console.WriteLine($"PASS {Count - before} 抗魔升级权重与纯贡献公式");
    }

    static void EquipmentBaseTailCore()
    {
        string[] names = ["[anti evil]", "[then]", "[/then]", "[impossible contents]", "[/impossible contents]",
            "disjoint", "gift", "event", "[import script]", "parent.equ", "[add anti evil]"];
        string Resolve(int index) => names[index];
        PvfToken[] Tail(params PvfToken[] body) => [.. body, new(3, 3), new(6, 5), new(6, 6), new(3, 4), new(6, 7)];
        PvfTokenDocument Read(PvfToken[] source) => AntiEvilUpgradeCatalog.ReadEquipmentBaseDocument(source, Resolve);
        var tokens = Tail(new(3, 0), new(0, 525), new(3, 1), new(3, 0), new(0, 999), new(3, 2));
        That(AntiEvilUpgradeCatalog.ReadEquipmentBase(_ => Read(tokens), Resolve, "equipment/tail.equ") == 525,
            "真实成长武器已闭impossible contents后的孤立event不应阻止读取根抗魔525，条件内999不合入");
        var inherited = new Dictionary<string, PvfToken[]>
        {
            ["equipment/parent.equ"] = Tail(new(3, 0), new(0, 480)),
            ["equipment/add.equ"] = Tail(new(3, 8), new(6, 9), new(3, 10), new(0, 20)),
            ["equipment/zero.equ"] = Tail(new(3, 8), new(6, 9), new(3, 0), new(0, 0)),
            ["equipment/override.equ"] = Tail(new(3, 8), new(6, 9), new(3, 0), new(0, 525), new(3, 10), new(0, 5))
        };
        int Value(string path) => AntiEvilUpgradeCatalog.ReadEquipmentBase(source => Read(inherited[source]), Resolve, path);
        That(Value("equipment/add.equ") == 500 && Value("equipment/zero.equ") == 0 && Value("equipment/override.equ") == 530,
            "导入链逐文件兼容合法尾串，继承、显式零、覆盖和追加仍按原抗魔顺序计算");
        Reject<InvalidDataException>(() => AntiEvilUpgradeCatalog.ReadEquipmentBase(
                _ => Read(Tail(new(3, 0), new(0, 525), new(0, 526))), Resolve, "equipment/bad-anti.equ"),
            "忽略无关尾串不能掩盖实际生效抗魔声明的双整数坏值");
        Reject<InvalidDataException>(() => Read([.. tokens[..^1], new(3, 4), new(6, 7)]),
            "只有完全闭合且无诊断的前缀允许尾串兼容，多余关闭不能借截尾通过");
        Reject<InvalidDataException>(() => Read([.. tokens[..^1], new(0, 1)]),
            "已闭块后的游离数值不在已证单个字符串兼容形状内");
        Reject<InvalidDataException>(() => AntiEvilUpgradeCatalog.ReadEquipmentBase(
                _ => Read([new(3, 0), new(0, 525), new(6, 7)]), Resolve, "equipment/bad-anti-string.equ"),
            "抗魔字段自身多出的字符串不能被当作已闭块尾串丢掉");
    }

    public static void RunEquipmentBaseTailResources(DfoServer.Infrastructure.ItemResources resources)
    {
        int before = Count;
        foreach (int id in new[] { 408000001, 408010001, 408020001, 408030001, 408040001 })
            That(resources.Items.Get(id).AntiEvil == 525, "实际成长武器孤立尾串不阻止读取根抗魔525：" + id);
        Console.WriteLine($"PASS {Count - before} 首期基础抗魔局部兼容5件成长武器末尾孤立字符串");
    }

    static void MemberSetListCompatibility()
    {
        That(AntiEvilSetCatalog.ReadMemberSetIds(new PvfDocument(
                "[usable job]\n`[at gunner]`\n[/usable job]\n[avatar func filter]\n0\n[/usable job]\n[part set index]\n2\n")).SequenceEqual([2]),
            "真实105520410的无关多余usable job关闭不使完整part set成员2失效");
        That(AntiEvilSetCatalog.ReadMemberSetIds(new PvfDocument(
                "[part set index]\n2\n[usable job]\n`[mage]`\n[/usable job]\n[grade]\n2\n[part set index]\n2\n")).SequenceEqual([2]),
            "真实106501565完整同值套装声明重复按最后一列加载");
        That(AntiEvilSetCatalog.ReadMemberSetIds(new PvfDocument("[part set index]\n-1 0 30125 30125\n")).SequenceEqual([-1, 0, 30125]),
            "原生signed集合保留负值和零并去重，provider只计正套装成员");
        That(AntiEvilSetCatalog.ReadMemberSetIds(new PvfDocument(
                "[part set index]\n2 30123\n[part set index]\n30125 30123\n")).SequenceEqual([30123, 30125]),
            "原生每次清容器后逐整数插入，后声明替换整列表而不是合并或截取一项");
        That(AntiEvilSetCatalog.ReadMemberSetIds(new PvfDocument("[name]\n`no set`\n")).IsEmpty &&
            AntiEvilSetCatalog.ReadMemberSetIds(new PvfDocument("[part set index]\n")).IsEmpty,
            "缺席与显式空列表不伪造套装编号");
        Reject<InvalidDataException>(() => AntiEvilSetCatalog.ReadMemberSetIds(new PvfDocument("[part set index]\n30125 invalid\n")),
            "实际生效声明中的坏token不能被数字提取器跳过或吞异常");
        Reject<InvalidDataException>(() => AntiEvilSetCatalog.ReadMemberSetIds(new PvfDocument("[part set index]\n2147483648\n")),
            "套装成员列表仍保持signed int32范围，不截断溢出整数");
    }

    public static void RunMemberSetCompatibilityResources(DfoServer.Infrastructure.ItemResources resources)
    {
        int before = Count;
        foreach (int id in new[] { 105520410, 106501565 })
            That(resources.Items.Get(id).PartSetIds.SequenceEqual([2]), "完整装备模板恢复套装成员列表2：" + id);
        foreach (var (id, antiEvil) in new[] { (2680004, 96), (400340147, 272), (400340160, 256) })
            That(resources.Items.Get(id).PartSetIds.SequenceEqual([-1]) && resources.Items.Get(id).AntiEvil == antiEvil,
                "实际-1套装旧装备恢复加载且原抗魔保持：" + id);
        Console.WriteLine($"PASS {Count - before} 首期套装成员局部兼容5件真实旧装备");
    }

    public static void Run(PvfArchive archive)
    {
        int before = Count;
        var catalog = new AntiEvilUpgradeCatalog(archive);
        AssertWeights(catalog.Reinforce);
        AssertWeights(catalog.Amplify);
        That(catalog.Forge.Length == 8 && catalog.Forge[7][4] == 66, "真实锻造资源抗魔表");
        var items = new ItemCatalog(archive, PvfScript.List(archive.ReadText("equipment/equipment.lst")),
            PvfScript.List(archive.ReadText("stackable/stackable.lst")), 0);
        That(items.Get(2747476).AntiEvil == 480, "真实装备模板携带根基础抗魔480");
        var consumables = new RecoveryConsumableCatalog(archive,
            PvfScript.List(archive.ReadText("stackable/stackable.lst")), items);
        That(consumables.TryGet(10008141, out var potion) &&
            potion.StatChanges.Contains(new ConsumableStatChange("+", 10, "antievil")),
            "真实抗魔药剂保留加10抗魔声明供现有持续Effect状态计算");
        var now = DateTimeOffset.FromUnixTimeSeconds(2_000_000_000);
        var sets = new AntiEvilSetCatalog(archive);
        var provider = new CharacterAntiEvil(items, catalog, consumables, new FixedTime(now), sets: sets);
        InventoryItem Equipped(int id, short slot, byte upgrade = 0, byte forge = 0) =>
            new(id, new(InventoryListType.Equipment, slot), new(id, ItemKind.Equipment, 0)
            { Durability = items.Get(id).Durability, Attr = upgrade, GenuineUpgrade = forge });
        InventoryItem[] worn = [Equipped(2747476, 12, 10, 8), Equipped(10400, 14, 10),
            Equipped(12400, 16), Equipped(16400, 18), Equipped(18400, 17)];
        Character role = new(1, 0, "AntiEvil", 0, new(1, 0, 0, 0), null)
        {
            Level = 95, Job = 0, Inventory = new([.. worn,
                worn[0] with { Uid = 99, Position = new(InventoryListType.Main, 9) }])
            { ConsumableStates = [new(ConsumableStateKind.Effect, 10008141, now.ToUnixTimeMilliseconds() + 1000, 7, 1)] }
        };
        That(provider.Compute(role) == 562,
            "真实武器480加锻造54、衣服2加强化10、其余三件各2、活跃药剂10合计562，背包副本不计");
        That(provider.Compute(role with { Inventory = role.Inventory with
            { Items = [.. worn.Select((item, index) => index == 1 ? item with { Core = item.Core with { Durability = 0 } } : item)] } }) == 550,
            "破损有最大耐久装备不贡献抗魔，其他穿戴与药剂保留");
        That(provider.Compute(role with { Inventory = role.Inventory with
            { Items = [.. worn.Select((item, index) => index == 0 ? item with { Core = item.Core with { ExpireTime = 1 } } : item)] } }) == 28,
            "过期普通武器不贡献基础、升级和锻造");
        That(provider.Compute(role with { Job = 1 }) == 28,
            "真实武器职业不符不贡献，通用衣服和药剂仍计入");
        That(provider.Compute(role with { Inventory = role.Inventory with
            { ConsumableStates = [new(ConsumableStateKind.Effect, 10008141, now.ToUnixTimeMilliseconds(), 7, 1)] } }) == 552,
            "药剂在Effect截止后不再进入权威总值");
        That(items.Get(63430).UsableExpired && !items.Get(2747476).UsableExpired,
            "真实宠物63430携带空体usable expired item，普通武器未声明时不推定可用");
        Character blackhole = role with { Inventory = new([Equipped(400300070, 19),
            Equipped(400310070, 20), Equipped(400320074, 21)]) };
        That(provider.Compute(blackhole) == 1608,
            "真实黑洞湮灭三件各516，加套装30125三件阶梯抗魔60，总1608而非1548");
        That(provider.Compute(blackhole with { Inventory = new([.. blackhole.Inventory.Items.Take(2)]) }) == 1032 &&
            provider.Compute(blackhole with { Inventory = new([.. blackhole.Inventory.Items.Select((item, index) =>
                index == 2 ? item with { Core = item.Core with { ExpireTime = 1 } } : item)]) }) == 1032,
            "套装只数有效穿戴，缺件或成员过期不产生三件属性");
        That(sets.Get(30073).SequenceEqual([new AntiEvilSetTier(3, 16), new(5, 30)]) &&
            new[] { 30044, 30045, 30046, 30074, 30123, 30125 }.SelectMany(id => sets.Get(id))
                .Select(tier => tier.AntiEvil).SequenceEqual([15, 15, 20, 30, 60, 60]),
            "七组真实抗魔套装保留各阶梯原始平值，不执行其他技能或条件属性");
        Console.WriteLine($"PASS {Count - before} 真实强化与增幅抗魔权重");
    }

    sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    static void AssertWeights(AntiEvilUpgradeRules rules) => That(
        rules.RarityWeights.SequenceEqual([1f, 1f, 1.3f, 1.56f, 1.9f, 1f, 1.8f]) &&
        rules.TypeWeights.SequenceEqual([8, 3, 5, 4, 0, 4]) && rules.Divisor == 12,
        "抗魔嵌套权重保持7个float、6个int、整数除数，不混入费用同名域");

    static PvfTokenDocument BaseFixture(bool nestedOnly = false, int add = 0, bool malformed = false)
    {
        string[] tags = ["[level linear ability]", "[anti evil]", "[/level linear ability]", "[add anti evil]"];
        var tokens = new List<PvfToken> { new(3, 0), new(3, 1), new(0, 9), new(0, 19), new(3, 2) };
        if (!nestedOnly) tokens.AddRange([new(3, 1), new(0, 480)]);
        if (malformed) tokens.Add(new(0, 481));
        if (add != 0) tokens.AddRange([new(3, 3), new(0, add)]);
        return new(tokens, index => tags[index]);
    }

    static PvfTokenDocument ForgeFixture(bool incomplete = false)
    {
        int[] values = [5,5,7,8,10,5,10, 11,11,14,17,20,11,19, 19,19,24,29,35,19,34,
            21,21,28,33,41,21,38, 27,27,35,42,51,27,48, 29,29,38,46,56,29,53,
            32,32,42,50,61,32,58, 35,35,45,54,66,35,62];
        var tokens = new List<PvfToken> { new(3, 0) };
        tokens.AddRange(values.Take(values.Length - (incomplete ? 1 : 0)).Select(value => new PvfToken(0, value)));
        tokens.Add(new(3, 1));
        return new(tokens, index => index == 0 ? "[anti evil upgrade]" : "[/anti evil upgrade]");
    }

    static PvfTokenDocument EquipmentFixture(int? basis = null, int? add = null, string? parent = null)
    {
        string[] strings = ["[anti evil]", "[add anti evil]", "[import script]", "parent.equ", "loop.equ"];
        var tokens = new List<PvfToken>();
        if (basis is int value) tokens.AddRange([new(3, 0), new(0, value)]);
        if (add is int extra) tokens.AddRange([new(3, 1), new(0, extra)]);
        if (parent is not null) tokens.AddRange([new(3, 2), new(6, parent == "loop.equ" ? 4 : 3)]);
        return new(tokens, index => strings[index]);
    }

    static PvfTokenDocument Fixture(bool shortRarity = false, bool floatType = false, bool duplicate = false,
        float firstRarity = 1f)
    {
        var tokens = new List<PvfToken>();
        var strings = new List<string>();
        Tag("cost weights by rarity");
        tokens.Add(new(0, 99));
        Block();
        if (duplicate) Block();
        return new(tokens, index => strings[index]);

        void Block()
        {
            Tag("anti evil upgrade");
            Tag("rarity weights");
            foreach (float value in (shortRarity ? new[] { 1f } : [firstRarity, 1f, 1.3f, 1.56f, 1.9f, 1f, 1.8f]))
                tokens.Add(new(2, BitConverter.SingleToInt32Bits(value)));
            Tag("type weights");
            foreach (int value in new[] { 8, 3, 5, 4, 0, 4 })
                tokens.Add(floatType ? new(2, BitConverter.SingleToInt32Bits(value)) : new(0, value));
            Tag("div weights");
            tokens.Add(new(0, 12));
            Tag("/anti evil upgrade");
        }

        void Tag(string text)
        {
            tokens.Add(new(3, strings.Count));
            strings.Add("[" + text + "]");
        }
    }
}
