using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class EnchantmentTests
{
    static void WeightedCardPlans()
    {
        const int beadId = 990121, first = 990122, second = 990123, targetId = 990124;
        const string card = """
            [stackable type]
            `[material expert job]` 1
            [string data]
            `card.img` `[title name]`
            [/string data]
            [enchant table]
            [enchant index] 0
            [all elemental attack] 10
            [/enchant index]
            [/enchant table]
            """;
        string Bead(string rows) => """
            [stackable type]
            `[enchant waste]` 0
            [monster card id drop prob]
            """ + "\n" + rows + "\n" + """
            [/monster card id drop prob]
            [action usable place]
            `[village]`
            [/action usable place]
            [bead limited usable item]
            990124
            [/bead limited usable item]
            """;
        EnchantmentDefinition? Load(string text) => new EnchantmentCatalog(
            path => path == "stackable/bead.stk" ? text : card,
            new Dictionary<int, string> { [beadId] = "bead.stk", [first] = "first.stk", [second] = "second.stk" }).Get(beadId);
        var definition = Load(Bead("990122 2 990123 0 990123 3 990122 1")) ??
            throw new InvalidDataException("合法二列权重宝珠定义缺失");
        That(definition.VillageOnly && definition.RandomTitle is null && definition.RandomCards is not null &&
            definition.LimitedItems.SetEquals([targetId]), "权重宝珠沿普通卡核心、白名单与城镇地点，不当称号97技能池");
        int[] expected = [first, first, second, second, second, first];
        var bead = new ItemTemplate(beadId, ItemKind.Consumable, 0, 1, 0, 1000, 1, 2, 1, "[free]", ushort.MaxValue, 0, "Bead");
        var title = new ItemTemplate(targetId, ItemKind.Equipment, 13, 1, 0, 1, 1, 2, 1, "[free]", ushort.MaxValue, 0, "Title");
        var source = new InventoryItem(91, new(InventoryListType.Main, 82), new(beadId, ItemKind.Consumable, 2));
        var target = new InventoryItem(92, new(InventoryListType.Main, 9), new(targetId, ItemKind.Equipment, 0)
        { EnchantCardId = 3175, EnchantUpgrade = 2, Extension97 = 0x00012711, Attr = 7, GenuineUpgrade = 4, SortLockFlag = 1 });
        Character role = new(1, 0, "WeightBead", 0, new(0, 0, 0, 0), null)
        { Level = 95, Inventory = new([source, target], Gold: 1234) };
        var request = new EnchantmentRequest(source.Position, target.Position, uint.MaxValue);
        for (int index = 0; index < expected.Length; index++)
        {
            int draws = 0;
            var result = InventoryEnchantmentService.Plan(role, request, 2_000_000_000, definition, bead, title,
                maximum => { That(maximum == 6, "抽样域为实际总权重，不固定百分比或展开重复卡"); draws++; return index; });
            That(result.Accepted && draws == 1 && result.Plan!.After.Find(target.Position) == target with
                { Core = target.Core with { EnchantCardId = expected[index], EnchantUpgrade = 0 } } &&
                result.Plan.After.Find(source.Position)!.Core.Count == 1 && result.Plan.After.Gold == 1234 &&
                role.Inventory.Find(target.Position) == target,
                "每个权重区间准确到达，零权重不抽中，计划不更改原状态且完整保留其他核心");
        }
        Reject<InvalidDataException>(() => definition.RandomCards!.Draw(_ => -1), "负随机位置明确拒绝");
        Reject<InvalidDataException>(() => definition.RandomCards!.Draw(maximum => maximum), "随机位置到总权重上界明确拒绝");
        bool Rejected(string text)
        {
            try { return Load(text) is null; }
            catch (Exception error) when (error is InvalidDataException or OverflowException) { return true; }
        }
        foreach (string rows in new[] { "", "990122", "0 1", "-1 1", "990122 -1", "990122 0", "990122 1.5",
            "990122 2147483647 990123 1", "990999 1" })
            That(Rejected(Bead(rows)), "坏二列池、缺卡、负权重、全零和总和溢出不能消费：" + rows);
        string valid = Bead("990122 100");
        foreach (string text in new[]
        {
            valid + "\n[monster card id]\n990123\n[/monster card id]\n",
            valid + "\n[monster card id drop prob]\n990123 1\n[/monster card id drop prob]\n",
            valid + "\n[usable expired item]\n",
            valid.Replace("[/monster card id drop prob]", ""),
            valid.Replace("[bead limited usable item]\n990124\n[/bead limited usable item]", "")
        }) That(Rejected(text), "冲突奖池、重复、未闭合、未知过期标记及缺白名单不能放行");
        That(!Rejected(valid.Replace("`[village]`", "`[dungeon]`")), "地点不改变权重宝珠的身份和奖池");
    }

    static void WeightedCardResourcePlans(ItemResources resources)
    {
        var enchantments = resources.Enchantments;
        foreach (var (id, maximum) in new[] { (10304051, 100), (10310134, 500), (10311584, 99), (10311590, 600) })
        {
            var definition = enchantments.Get(id) ?? throw new InvalidDataException("真实权重宝珠定义缺失：" + id);
            That(definition.RandomCards is not null && definition.VillageOnly && !definition.LimitedItems.IsEmpty,
                "真实权重宝珠保留白名单、城镇和独立卡池：" + id);
            var title = resources.Items.Get(definition.LimitedItems.Order().First());
            var bead = resources.Items.Get(id);
            var source = new InventoryItem(81, new(InventoryListType.Main, 82), InventoryRewardGrantService.Create(bead, 2));
            var target = new InventoryItem(82, new(InventoryListType.Main, 9), InventoryRewardGrantService.Create(title) with
                { EnchantCardId = 3175, EnchantUpgrade = 2, Extension97 = 0x00012711, SortLockFlag = 1 });
            Character role = new(1, 0, "RealWeight", 0, new(0, 0, 0, 0), null)
            { Level = 95, Inventory = new([source, target], Gold: 1234) };
            foreach (int position in new[] { 0, maximum - 1 })
            {
                var result = InventoryEnchantmentService.Plan(role, new(source.Position, target.Position, 0),
                    2_000_000_000, definition, bead, title, size => { That(size == maximum, "真实池总权重完整保留"); return position; });
                That(result.Accepted && result.Plan!.After.Find(source.Position)!.Core.Count == 1 &&
                    result.Plan.After.Find(target.Position)!.Core is var core && definition.RandomCards!.Positions.Contains(core.EnchantCardId) &&
                    core == target.Core with { EnchantCardId = core.EnchantCardId, EnchantUpgrade = 0 },
                    "真实权重总和代表完整消费一颗并只改附魔卡字段：" + id);
            }
        }
        foreach (int id in new[] { 10158322, 10158324, 10160673, 10160674 })
        {
            bool rejected;
            try { rejected = enchantments.Get(id) is null; }
            catch (InvalidDataException) { rejected = true; }
            That(rejected, "独立过期使用标记仍未放行：" + id);
        }
    }
}
