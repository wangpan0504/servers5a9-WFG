using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class EnchantmentTests
{
    static void MultipleCardExtraQualificationPlans()
    {
        const int beadId = 990121, first = 990122, second = 990123, gearId = 990124, materialId = 990125;
        const long now = 2_000_000_000;
        const string bead = """
            [stackable type]
            `[enchant waste]` 0
            [monster card id]
            990122 990122 990123
            [/monster card id]
            [action usable place]
            `[village]`
            [/action usable place]
            """;
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
        const string category = "\n[item category]\n`event`\n[/item category]\n";
        const string price = "\n[price]\n17\n[add price]\n3\n[value]\n1000\n" +
            "[need material]\n990125 600\n[/need material]\n";
        EnchantmentCatalog Catalog(string body) => new(path => path == "stackable/bead.stk" ? body : card,
            new Dictionary<int, string> { [beadId] = "bead.stk", [first] = "first.stk", [second] = "second.stk" });
        var beadTemplate = new ItemTemplate(beadId, ItemKind.Consumable, 0, 1, 0, 1000, 1, 2, 1,
            "[free]", ushort.MaxValue, 0, "Bead");
        var gearTemplate = new ItemTemplate(gearId, ItemKind.Equipment, 13, 1, 0, 1, 1, 2, 1,
            "[free]", ushort.MaxValue, 0, "Title");
        var source = new InventoryItem(91, new(InventoryListType.Main, 82), new(beadId, ItemKind.Consumable, 2));
        var target = new InventoryItem(92, new(InventoryListType.Main, 9), new(gearId, ItemKind.Equipment, 1234567)
        { EnchantCardId = 3175, EnchantUpgrade = 2, Extension97 = 0x00012711, SortLockFlag = 1, Random0 = new(1, 2, 3) });
        var material = new InventoryItem(93, new(InventoryListType.Main, 121), new(materialId, ItemKind.Material, 1000));
        Character role = new(1, 0, "BeadPlace", 0, new(1, 0, 10, 10), null)
        {
            Level = 25,
            Inventory = new([source, target, material], Gold: 1234)
            { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.Cera, 123) }
        };
        var request = new EnchantmentRequest(source.Position, target.Position, uint.MaxValue);
        int draws = 0;
        foreach (string extra in new[] { "", category, price })
        {
            string body = bead + extra;
            var definition = Catalog(body).Get(beadId);
            That(definition is { CardItemId: 0, RandomCards: not null } &&
                definition.RandomCards.Positions.SequenceEqual(new[] { first, first, second }),
                "村庄多卡宝珠允许已证event分类和NPC兑换价格，保留完整重复卡位");
            var template = beadTemplate with { ShopPrice = ItemShopPrice.Parse(new PvfDocument(body), false, 0) };
            if (extra == price)
                That(template.ShopPrice is { Gold: 20, SellGold: 200 } &&
                    template.ShopPrice.Materials.SequenceEqual(new[] { new ItemMaterialCost(materialId, 600) }),
                    "need material完整保留为NPC购买价，不改写模板价格合同");
            foreach (var candidate in new[] { role, role with { Inventory = new([source, target]) } })
            {
                int beforeDraws = draws;
                var planned = InventoryEnchantmentService.Plan(candidate, request, now, definition!, template, gearTemplate,
                    maximum => { That(maximum == 3, "额外声明不改变原位置抽样域"); draws++; return 2; });
                That(planned.Accepted && draws == beforeDraws + 1 &&
                    planned.Plan!.After.Find(source.Position) == source with { Core = source.Core with { Value = 1 } } &&
                    planned.Plan.After.Find(target.Position) == target with
                    { Core = target.Core with { EnchantCardId = second, EnchantUpgrade = 0 } } &&
                    planned.Plan.After.Find(material.Position) == candidate.Inventory.Find(material.Position) &&
                    (planned.Plan.After with { Items = candidate.Inventory.Items }) == candidate.Inventory,
                    "有无NPC兑换材料及金币均可使用；只扣一珠，完整保留目标、材料、钱包与其它库存状态");
                That(planned.Plan!.CharacterPrecondition is { } qualification && qualification.Matches(candidate, now) &&
                    !qualification.Matches(candidate with { Position = candidate.Position with { X = 11 } }, now),
                    "村庄多卡计划绑定原位置，不能在规划后移动时复用地点资格");
            }
        }
        var quoted = Catalog("[name]\n`[item category] restricted [/item category]`\n" + bead + category).Get(beadId);
        That(quoted?.RandomCards?.Positions.SequenceEqual(new[] { first, first, second }) == true,
            "说明中的分类字样不能覆盖根级完整event字面量");
        var templates = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        templates.PrefillTemplates([new(beadId, beadTemplate), new(gearId, gearTemplate)]);
        foreach (string invalid in new[]
        {
            bead + category.Replace("`event`", "`event bonus`"),
            bead + category.Replace("`event`", "`not event`"),
            bead + category.Replace("`event`", "`event` `restricted`"),
            bead + category + category,
            bead + category.Replace("[/item category]", ""),
            bead + "\n[nested]\n" + category + "[/nested]\n",
            bead + "\n[usable expired item]\n",
            bead + category + "\n[item category"
        })
        {
            int beforeDraws = draws;
            var catalog = Catalog(invalid);
            bool refused;
            try
            {
                var rejected = new InventoryEnchantmentService(templates, catalog, random: _ => { draws++; return 0; })
                    .Plan(role, request, now);
                refused = catalog.Get(beadId) is null && !rejected.Accepted && rejected.Plan is null;
            }
            catch (InvalidDataException) { refused = true; }
            That(refused && draws == beforeDraws && role.Inventory.Find(source.Position) == source &&
                role.Inventory.Find(target.Position) == target && role.Inventory.Find(material.Position) == material,
                "未知/残缺/重复/嵌套分类及未证过期标记不抽样、不扣源、不改目标");
        }
        That(Catalog(bead.Replace("`[village]`", "`[unknown place]`")).Get(beadId) is not null,
            "未知地点不改变宝珠身份，实际地点由公共使用规则判断");
    }

    static void MultipleCardExtraQualificationResources(ItemResources resources, EnchantmentCatalog enchantments)
    {
        const long now = 2_000_000_000;
        // 仅冻结本批已保存原文的完整卡位与白名单；不在自测内重扫STK分母。
        static int[] Positions(params (int Card, int Count)[] runs) =>
            runs.SelectMany(run => Enumerable.Repeat(run.Card, run.Count)).ToArray();
        (int Id, int[] Cards, int[] Allowed, ItemMaterialCost[] Price)[] cases =
        [
            (2683770, Positions((10087953, 22), (10087956, 22), (10087959, 22), (10087962, 22), (10087965, 22), (10087968, 22), (10087954, 8), (10087957, 8), (10087960, 8), (10087963, 8), (10087966, 8), (10087969, 8), (10087955, 1), (10087958, 1), (10087961, 1), (10087964, 1), (10087967, 1), (10087970, 1)), [100330959, 100330960, 100330961], [new(10015553, 1)]),
            (10004522, Positions((10004681, 50), (10004682, 30), (10004683, 10), (10004684, 5), (10004685, 3), (10004686, 1)), [], [new(10004516, 150)]),
            (10004530, Positions((10004675, 50), (10004676, 30), (10004677, 10), (10004678, 5), (10004679, 3), (10004680, 2)), [], [new(10004516, 150)]),
            (10005039, Positions((10005067, 35), (10005068, 49), (10005069, 13), (10005070, 2), (10005071, 1)), [], [new(10005031, 10)]),
            (10005040, Positions((10005072, 35), (10005073, 49), (10005074, 13), (10005075, 2), (10005076, 1)), [], [new(10005031, 10)]),
            (10005041, Positions((10005077, 35), (10005078, 49), (10005079, 13), (10005080, 2), (10005081, 1)), [], [new(10005031, 10)]),
            (10005042, Positions((10005082, 35), (10005083, 49), (10005084, 13), (10005085, 2), (10005086, 1)), [], [new(10005031, 10)]),
            (10006583, Positions((10098372, 50), (10098373, 30), (10098374, 10), (10098375, 5), (10098376, 3), (10098377, 2)), [], [new(10006594, 15)]),
            (10006584, Positions((10098378, 50), (10098379, 30), (10098380, 10), (10098381, 5), (10098382, 3), (10098383, 2)), [], [new(10006594, 15)]),
            (10006585, Positions((10006664, 50), (10006665, 30), (10006666, 10), (10006667, 5), (10006668, 3), (10006669, 2)), [], [new(10006594, 15)]),
            (10006586, Positions((10098384, 90), (10098385, 5), (10098386, 3), (10098387, 2)), [], [new(10006594, 15)]),
            (10006587, Positions((10098388, 90), (10098389, 5), (10098390, 3), (10098391, 2)), [], [new(10006594, 15)]),
            (10006588, Positions((10098396, 90), (10098397, 5), (10098398, 3), (10098399, 2)), [], [new(10006594, 15)]),
            (10006589, Positions((10098392, 90), (10098393, 5), (10098394, 3), (10098395, 2)), [], [new(10006594, 15)]),
            (10088989, Positions((10089516, 4), (10089517, 3), (10089518, 2), (10089519, 1)), [100331272, 100331273, 100331274, 100331275, 100331276, 100331277, 100331278, 100331279, 100331280, 100331281, 100331282, 100331283], [new(10015553, 1)]),
            (10088990, Positions((10089520, 4), (10089521, 3), (10089522, 2), (10089523, 1)), [100331272, 100331273, 100331274, 100331275, 100331276, 100331277, 100331278, 100331279, 100331280, 100331281, 100331282, 100331283], [new(10015553, 1)]),
            (10088991, Positions((10089524, 4), (10089525, 3), (10089526, 2), (10089527, 1)), [100331272, 100331273, 100331274, 100331275, 100331276, 100331277, 100331278, 100331279, 100331280, 100331281, 100331282, 100331283], [new(10015553, 1)]),
            (10088992, Positions((10089528, 4), (10089529, 3), (10089530, 2), (10089531, 1)), [100331272, 100331273, 100331274, 100331275, 100331276, 100331277, 100331278, 100331279, 100331280, 100331281, 100331282, 100331283], [new(10015553, 1)]),
            (10098365, Positions((10098372, 50), (10098373, 30), (10098374, 10), (10098375, 5), (10098376, 3), (10098377, 2)), [], [new(10098358, 10)]),
            (10098366, Positions((10098378, 50), (10098379, 30), (10098380, 10), (10098381, 5), (10098382, 3), (10098383, 2)), [], [new(10098358, 10)]),
            (10098367, Positions((10098384, 90), (10098385, 5), (10098386, 3), (10098387, 2)), [], [new(10098358, 10)]),
            (10098368, Positions((10098388, 90), (10098389, 5), (10098390, 3), (10098391, 2)), [], [new(10098358, 10)]),
            (10098369, Positions((10098392, 90), (10098393, 5), (10098394, 3), (10098395, 2)), [], [new(10098358, 10)]),
            (10098370, Positions((10098396, 100), (10098397, 5), (10098398, 3), (10098399, 2)), [], [new(10098358, 10)]),
            (10099668, Positions((10087953, 22), (10087956, 22), (10087959, 22), (10087962, 22), (10087965, 22), (10087968, 22), (10087954, 8), (10087957, 8), (10087960, 8), (10087963, 8), (10087966, 8), (10087969, 8), (10087955, 1), (10087958, 1), (10087961, 1), (10087964, 1), (10087967, 1), (10087970, 1)), [100331372, 100331373, 100331374, 100331375, 100331376, 100331377, 100331378, 100331379, 100331380, 100331381, 100331382, 100331383], []),
            (10101842, Positions((10087953, 22), (10087956, 22), (10087959, 22), (10087962, 22), (10087965, 22), (10087968, 22), (10087954, 8), (10087957, 8), (10087960, 8), (10087963, 8), (10087966, 8), (10087969, 8), (10087955, 1), (10087958, 1), (10087961, 1), (10087964, 1), (10087967, 1), (10087970, 1)), [100331372, 100331373, 100331374, 100331375, 100331376, 100331377, 100331378, 100331379, 100331380, 100331381, 100331382, 100331383], []),
            (10147586, Positions((10087953, 22), (10087956, 22), (10087959, 22), (10087962, 22), (10087965, 22), (10087968, 22), (10087954, 8), (10087957, 8), (10087960, 8), (10087963, 8), (10087966, 8), (10087969, 8), (10087955, 1), (10087958, 1), (10087961, 1), (10087964, 1), (10087967, 1), (10087970, 1)), [100331372, 100331373, 100331374, 100331375, 100331376, 100331377, 100331378, 100331379, 100331380, 100331381, 100331382, 100331383], []),
            (10148273, Positions((10098372, 50), (10098373, 35), (10098374, 9), (10098375, 3), (10098376, 2), (10098377, 1)), [], []),
            (10148274, Positions((10098378, 50), (10098379, 35), (10098380, 9), (10098381, 3), (10098382, 2), (10098383, 1)), [], []),
            (10148275, Positions((10098384, 60), (10098385, 30), (10098386, 8), (10098387, 2)), [], []),
            (10148276, Positions((10098388, 60), (10098389, 30), (10098390, 8), (10098391, 2)), [], []),
            (10148277, Positions((10098392, 60), (10098393, 30), (10098394, 8), (10098395, 2)), [], []),
            (10148278, Positions((10098396, 60), (10098397, 30), (10098398, 8), (10098399, 2)), [], []),
            (10155733, Positions((10098372, 12), (10155748, 7), (10098377, 1)), [], [new(10155739, 10)]),
            (10155734, Positions((10098378, 12), (10155749, 7), (10098383, 1)), [], [new(10155739, 10)]),
            (10155735, Positions((10098396, 100), (10098397, 5), (10098398, 3), (10098399, 2)), [], [new(10098358, 10)]),
            (10155736, Positions((10098384, 90), (10098385, 5), (10098386, 3), (10098387, 2)), [], [new(10098358, 10)]),
            (10155737, Positions((10098388, 90), (10098389, 5), (10098390, 3), (10098391, 2)), [], [new(10098358, 10)]),
            (10155738, Positions((10098392, 90), (10098393, 5), (10098394, 3), (10098395, 2)), [], [new(10098358, 10)]),
            (490000140, Positions((490000191, 25), (490000192, 20), (490000193, 16), (490000194, 14), (490000195, 14), (490000196, 5), (490000197, 3), (490000198, 2), (490000200, 1)), [400300035, 400310037, 400320036], [new(10006594, 15)]),
            (490000141, Positions((490000207, 25), (490000208, 20), (490000209, 16), (490000210, 14), (490000211, 14), (490000212, 5), (490000213, 3), (490000214, 2), (490000216, 1)), [400300035, 400310037, 400320036], [new(10006594, 15)]),
            (490000142, Positions((490000223, 25), (490000224, 20), (490000225, 16), (490000226, 14), (490000227, 14), (490000228, 5), (490000229, 3), (490000230, 2), (490000232, 1)), [400300035, 400310037, 400320036], [new(10006594, 15)]),
            (490000143, Positions((490000239, 25), (490000240, 20), (490000241, 16), (490000242, 14), (490000243, 14), (490000244, 5), (490000306, 3), (490000307, 2), (490000309, 1)), [400300035, 400310037, 400320036], [new(10006594, 15)]),
            (490000144, Positions((490000185, 45), (490000186, 45), (490000187, 40), (490000188, 35), (490000189, 25), (490000190, 15), (490000191, 15), (490000192, 10), (490000193, 5), (490000194, 5), (490000195, 4), (490000196, 3), (490000197, 1), (490000198, 1), (490000200, 1)), [400300035, 400310037, 400320036], [new(10006594, 15)]),
            (490000145, Positions((490000201, 45), (490000202, 45), (490000203, 40), (490000204, 35), (490000205, 25), (490000206, 15), (490000207, 15), (490000208, 10), (490000209, 5), (490000210, 5), (490000211, 4), (490000212, 3), (490000213, 1), (490000214, 1), (490000216, 1)), [400300035, 400310037, 400320036], [new(10006594, 15)]),
            (490000146, Positions((490000217, 45), (490000218, 45), (490000219, 40), (490000220, 35), (490000221, 25), (490000222, 15), (490000223, 15), (490000224, 10), (490000225, 5), (490000226, 5), (490000227, 4), (490000228, 3), (490000229, 1), (490000230, 1), (490000232, 1)), [400300035, 400310037, 400320036], [new(10006594, 15)]),
            (490000147, Positions((490000233, 45), (490000234, 45), (490000235, 40), (490000236, 35), (490000237, 25), (490000238, 15), (490000239, 15), (490000240, 10), (490000241, 5), (490000242, 5), (490000243, 4), (490000244, 3), (490000306, 1), (490000307, 1), (490000309, 1)), [400300035, 400310037, 400320036], [new(10006594, 15)]),
            (490002767, Positions((490002834, 50), (490002835, 30), (490002836, 10), (490002837, 5), (490002838, 3), (490002839, 2)), [104000518, 104020502, 104040522, 104010504, 104030506, 102010546, 102030454, 102000482, 102020452, 102040517, 101040451, 101030655, 101010784, 101020596, 101000627, 109000442, 109010409, 109030301, 109040278, 106020500, 106010438, 106040411, 106030532, 106000409, 108030439, 108040393, 108000435, 108010405, 108020387, 414000023, 414010023, 104000520, 104020504, 104040524, 104010506, 104030508, 102010548, 102030456, 102000484, 102020454, 102040519, 101040453, 101030657, 101010786, 101020598, 101000629, 109000444, 109010411, 109030303, 109040280, 106020504, 106010440, 106040413, 106030536, 106000411, 108030441, 108040395, 108000437, 108010407, 108020389, 414000024, 414010024, 104000521, 104020505, 104040525, 104010507, 104030509, 102010549, 102030457, 102000485, 102020455, 102040520, 101040454, 101030658, 101010787, 101020599, 101000630, 109000445, 109010412, 109030304, 109040281, 106020505, 106010441, 106040414, 106030537, 106000412, 108030442, 108040396, 108000438, 108010408, 108020390, 414000025, 414010025, 104000522, 104020506, 104040526, 104010508, 104030510, 102010550, 102030458, 102000486, 102020456, 102040521, 101040455, 101030659, 101010788, 101020600, 101000631, 109000446, 109010413, 109030305, 109040282, 106020506, 106010442, 106040415, 106030538, 106000413, 108030443, 108040397, 108000439, 108010409, 108020391, 414000026, 414010026], [new(490002769, 16)]),
            (490002768, Positions((490002840, 50), (490002841, 30), (490002842, 10), (490002843, 5), (490002844, 3), (490002845, 2)), [104000518, 104020502, 104040522, 104010504, 104030506, 102010546, 102030454, 102000482, 102020452, 102040517, 101040451, 101030655, 101010784, 101020596, 101000627, 109000442, 109010409, 109030301, 109040278, 106020500, 106010438, 106040411, 106030532, 106000409, 108030439, 108040393, 108000435, 108010405, 108020387, 414000023, 414010023, 104000520, 104020504, 104040524, 104010506, 104030508, 102010548, 102030456, 102000484, 102020454, 102040519, 101040453, 101030657, 101010786, 101020598, 101000629, 109000444, 109010411, 109030303, 109040280, 106020504, 106010440, 106040413, 106030536, 106000411, 108030441, 108040395, 108000437, 108010407, 108020389, 414000024, 414010024, 104000521, 104020505, 104040525, 104010507, 104030509, 102010549, 102030457, 102000485, 102020455, 102040520, 101040454, 101030658, 101010787, 101020599, 101000630, 109000445, 109010412, 109030304, 109040281, 106020505, 106010441, 106040414, 106030537, 106000412, 108030442, 108040396, 108000438, 108010408, 108020390, 414000025, 414010025, 104000522, 104020506, 104040526, 104010508, 104030510, 102010550, 102030458, 102000486, 102020456, 102040521, 101040455, 101030659, 101010788, 101020600, 101000631, 109000446, 109010413, 109030305, 109040282, 106020506, 106010442, 106040415, 106030538, 106000413, 108030443, 108040397, 108000439, 108010409, 108020391, 414000026, 414010026], [new(490002769, 16)]),
            (490002951, Positions((10098372, 50), (10098373, 30), (10098374, 10), (10098375, 5), (10098376, 3), (10098377, 2)), [], [new(490002959, 5)]),
            (490002952, Positions((10098378, 50), (10098379, 30), (10098380, 10), (10098381, 5), (10098382, 3), (10098383, 2)), [], [new(490002959, 5)]),
            (490002953, Positions((10006664, 50), (10006665, 30), (10006666, 10), (10006667, 5), (10006668, 3), (10006669, 2)), [], [new(490002959, 5)]),
            (490002954, Positions((10098384, 90), (10098385, 5), (10098386, 3), (10098387, 2)), [], [new(490002959, 5)]),
            (490002955, Positions((10098388, 90), (10098389, 5), (10098390, 3), (10098391, 2)), [], [new(490002959, 5)]),
            (490002956, Positions((10098392, 90), (10098393, 5), (10098394, 3), (10098395, 2)), [], [new(490002959, 5)]),
            (490002960, Positions((10098396, 90), (10098397, 5), (10098398, 3), (10098399, 2)), [], [new(490002959, 5)]),
            (490003011, Positions((490002961, 30), (490002962, 50), (490002963, 10), (490002964, 5), (490002965, 4), (490002966, 1)), [], [new(490002959, 5)]),
            (490003012, Positions((490002967, 30), (490002968, 50), (490002969, 10), (490002970, 5), (490002971, 4), (490002972, 1)), [], [new(490002959, 5)]),
            (490003013, Positions((490002973, 30), (490002974, 40), (490002975, 20), (490002976, 8), (490002977, 2)), [], [new(490002959, 5)]),
            (490003014, Positions((490002978, 30), (490002979, 40), (490002980, 20), (490002981, 8), (490002982, 2)), [], [new(490002959, 5)]),
            (490003015, Positions((490002983, 30), (490002984, 40), (490002985, 20), (490002986, 8), (490002987, 2)), [], [new(490002959, 5)]),
            (490003016, Positions((490002988, 30), (490002989, 40), (490002990, 20), (490002991, 8), (490002992, 2)), [], [new(490002959, 5)]),
            (490003017, Positions((490003001, 30), (490003002, 40), (490003003, 20), (490003004, 10)), [], [new(490002959, 5)]),
            (490003018, Positions((490002997, 30), (490002998, 40), (490002999, 20), (490003000, 10)), [], [new(490002959, 5)]),
            (490003019, Positions((490002993, 30), (490002994, 40), (490002995, 20), (490002996, 10)), [], [new(490002959, 5)]),
            (490012373, Positions((490012405, 64), (490012408, 21), (490012409, 10), (490012410, 4), (490012411, 1)), [400990347, 400990348, 400990349, 400990350, 400990351, 400990352, 400990353, 400990354, 400990355, 400990356, 400990357, 400990358, 400990359, 400990346], [new(490012441, 10)]),
            (490012435, Positions((490012406, 64), (490012412, 21), (490012413, 10), (490012414, 4), (490012415, 1)), [400990347, 400990348, 400990349, 400990350, 400990351, 400990352, 400990353, 400990354, 400990355, 400990356, 400990357, 400990358, 400990359, 400990346], [new(490012441, 10)]),
            (490012436, Positions((490012407, 64), (490012416, 21), (490012417, 10), (490012418, 4), (490012419, 1)), [400990347, 400990348, 400990349, 400990350, 400990351, 400990352, 400990353, 400990354, 400990355, 400990356, 400990357, 400990358, 400990359, 400990346], [new(490012441, 10)]),
            (490704380, Positions((490704091, 18), (490704092, 10), (490704093, 5), (490704094, 18), (490701145, 10), (490704095, 5), (490701147, 18), (490704096, 10), (490704097, 6)), [400330175, 400330176], []),
        ];
        foreach (var (beadId, expected, allowed, price) in cases)
        {
            var definition = enchantments.Get(beadId) ?? throw new InvalidDataException("真实多卡额外资格定义缺失: " + beadId);
            That(definition is { CardItemId: 0, RandomCards: not null, RandomTitle: null } &&
                definition.RandomCards.Positions.SequenceEqual(expected) && definition.LimitedItems.SetEquals(allowed),
                "真实69源逐项保留全部卡位、重复权重、白名单与普通附魔类型：" + beadId);
            var template = resources.Items.Get(beadId);
            That(template.ShopPrice.Materials.SequenceEqual(price), "真实need material仍为原NPC材料价格：" + beadId);
            // 有白名单用真实目标；无白名单以已验证部位的合成装备隔离源资格，不增加全装备扫描。
            var slot = definition!.Targets.OrderBy(value => value.Kind).ThenBy(value => value.Slot).First();
            var gear = allowed.Length > 0 ? resources.Items.Get(allowed[0]) :
                new ItemTemplate(990126, slot.Kind, slot.Slot, 1, 0, 1, 1, 2, 1, "[free]", ushort.MaxValue, 0, "Target");
            var source = new InventoryItem(71, new(InventoryListType.Main, 82), new(beadId, template.Kind, 2));
            var target = new InventoryItem(72, new(ItemSlotBoundService.List(gear.Kind), ItemSlotBoundService.Range(gear.Kind, 0).Start), new(gear.Id, gear.Kind, 1234567)
            { EnchantCardId = 3175, EnchantUpgrade = 2, Extension97 = 0x00012711, SortLockFlag = 1, Random0 = new(1, 2, 3) });
            if (gear.Kind == ItemKind.Creature)
                target = target with { Core = target.Core with { Value = checked((int)target.Uid) },
                    Creature = new() { Name = "Keep", Level = 7, Experience = 50, Satiety = 19 } };
            target.ValidateDetails(allocating: false);
            byte job = (byte)Enumerable.Range(0, 16).First(value => (template.Jobs & (1 << value)) != 0);
            Character role = new(1, 0, "RealPlace", job, new(1, 0, 10, 10), null)
            { Level = 95, Skills = template.RequiredSkill == 0 ? [] : [new(template.RequiredSkill, 1)], Inventory = new([source, target]) };
            var request = new EnchantmentRequest(source.Position, target.Position, uint.MaxValue);
            foreach (bool hasMaterials in price.Length == 0 ? new[] { false } : new[] { false, true })
            {
                var candidate = hasMaterials ? role with
                {
                    Inventory = role.Inventory with
                    {
                        Gold = 1234,
                        AccountWallet = AccountWallet.Empty.Set(CurrencyKind.Cera, 123),
                        Items = [.. role.Inventory.Items, .. price.Select((cost, index) =>
                            new InventoryItem(73 + index, new(InventoryListType.Main, (short)(121 + index)),
                                new(cost.ItemId, ItemKind.Material, cost.Count + 1)))]
                    }
                } : role;
                int draws = 0;
                var result = InventoryEnchantmentService.Plan(candidate, request, now, definition, template, gear,
                    maximum => { That(maximum == expected.Length, "真实69源完整位置域"); draws++; return maximum - 1; });
                That(result.Accepted && draws == 1 &&
                    result.Plan!.After.Find(source.Position) == source with { Core = source.Core with { Value = 1 } } &&
                    result.Plan.After.Find(target.Position) == target with
                    { Core = target.Core with { EnchantCardId = expected[^1], EnchantUpgrade = definition.Upgrade } } &&
                    result.Plan.After.Items.Where(item => item.Position != source.Position && item.Position != target.Position)
                        .SequenceEqual(candidate.Inventory.Items.Where(item => item.Position != source.Position && item.Position != target.Position)) &&
                    (result.Plan.After with { Items = candidate.Inventory.Items }) == candidate.Inventory,
                    "真实69源合法规划只抽一次、扣一珠；有无商店材料均成功且全部材料/钱包/核心保持：" + beadId);
                That(result.Plan!.CharacterPrecondition is { } qualification && qualification.Matches(candidate, now) &&
                    !qualification.Matches(candidate with { Position = candidate.Position with { X = 11 } }, now),
                    "真实69源使用地点随原角色位置绑定：" + beadId);
            }
        }
        foreach (int beadId in new[] { 10087971, 10096030, 10097853, 10148826, 10148960, 10148961, 10148962, 10148963 })
        {
            int draws = 0;
            var source = new InventoryItem(71, new(InventoryListType.Main, 82), new(beadId, ItemKind.Consumable, 2));
            var target = new InventoryItem(72, new(InventoryListType.Main, 9), new(100331462, ItemKind.Equipment, 1234567));
            Character role = new(1, 0, "PendingBead", 0, new(1, 0, 10, 10), null)
            { Level = 95, Inventory = new([source, target], Gold: 1234) };
            var rejected = new InventoryEnchantmentService(resources.Items, enchantments, random: _ => { draws++; return 0; })
                .Plan(role, new(source.Position, target.Position, uint.MaxValue), now);
            That(enchantments.Get(beadId) is null && !rejected.Accepted && rejected.Plan is null && draws == 0 &&
                role.Inventory.Find(source.Position) == source && role.Inventory.Find(target.Position) == target,
                "八项真实空体usable expired item未证前继续拒绝，不借地点放行误称已修：" + beadId);
        }
    }

    static void EnchantmentResourceParsingFailures()
    {
        const int beadId = 990141, cardId = 990142, gearId = 990143;
        const long now = 2_000_000_000;
        const string bead = "[stackable type]\n`[enchant waste]` 0\n[monster card id]\n990142 990142\n[/monster card id]\n";
        const string card = "[stackable type]\n`[material expert job]` 1\n[string data]\n`card.img` `[title]`\n[/string data]\n" +
            "[enchant table]\n[enchant index] 0\n[all elemental attack] 10\n[/enchant index]\n[/enchant table]\n";
        var index = new Dictionary<int, string> { [beadId] = "bead.stk", [cardId] = "card.stk" };
        var templates = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        templates.PrefillTemplates([
            new(beadId, new(beadId, ItemKind.Consumable, 0, 1, 0, 1000, 1, 2, 1, "[free]", ushort.MaxValue, 0, "Bead")),
            new(gearId, new(gearId, ItemKind.Equipment, 13, 1, 0, 1, 1, 2, 1, "[free]", ushort.MaxValue, 0, "Title"))]);
        var source = new InventoryItem(91, new(InventoryListType.Main, 82), new(beadId, ItemKind.Consumable, 2));
        var target = new InventoryItem(92, new(InventoryListType.Main, 9), new(gearId, ItemKind.Equipment, 1234567)
        { EnchantCardId = 3175, EnchantUpgrade = 2, Extension97 = 0x00012711 });
        Character role = new(1, 0, "ParseReject", 0, new(1, 0, 10, 10), null)
        { Level = 25, ExpertJobType = 1, Inventory = new([source, target], Gold: 1234) };
        var request = new EnchantmentRequest(source.Position, target.Position, uint.MaxValue);
        foreach (Exception resourceError in new Exception[] { new InvalidDataException("malformed bead resource"),
            new KeyNotFoundException("missing card resource"), new OverflowException("resource integer range") })
        {
            int draws = 0;
            var catalog = new EnchantmentCatalog(_ => throw resourceError, index);
            var service = new InventoryEnchantmentService(templates, catalog, random: _ => { draws++; return 0; });
            var result = service.Plan(role, request, now);
            That(!result.Accepted && result.Error == InventoryEnchantmentService.Rejection && result.Plan is null && draws == 0 &&
                role.Inventory.Find(source.Position) == source && role.Inventory.Find(target.Position) == target,
                "272共用规划入口将已知资源解析异常化为拒绝，不穿连接、不抽样、不扣源或改变目标");
            bool accepted = ExpertJobCraft.TryPlanEnchant(role, service, null!, (byte)InventoryListType.Main, 9,
                (byte)InventoryListType.Main, 82, now, out var inventory, out int experience, out ushort error);
            That(!accepted && inventory is null && experience == role.ExpertJobExperience &&
                error == InventoryEnchantmentService.Rejection && draws == 0,
                "601共用附魔规划也安全拒绝同一资源异常，不伪造库存计划或增加熟练度");
        }
        var unknown = new InventoryEnchantmentService(templates,
            new EnchantmentCatalog(_ => throw new InvalidOperationException("unexpected domain failure"), index));
        Reject<InvalidOperationException>(() => unknown.Plan(role, request, now),
            "资源拒绝只拦已证格式错误，不吞未知程序错误");
        var valid = new EnchantmentCatalog(path => path == "stackable/bead.stk" ? bead : card, index);
        var rngFailure = new InventoryEnchantmentService(templates, valid,
            random: _ => throw new InvalidDataException("random provider failure"));
        Reject<InvalidDataException>(() => rngFailure.Plan(role, request, now),
            "抽样属于业务计划而非资源读取，不能被资源catch吞成普通拒绝");
    }
}
