using DfoServer.GameWorld.Items;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class CharacterInvestmentMetadataTests
{
    public static void RunCore()
    {
        SpringSkillCardInvestment();
        var strings = new List<string>();
        var documents = new Dictionary<string, PvfTokenDocument>(StringComparer.Ordinal);
        string Resolve(int index) => strings[index];
        PvfToken Text(string text, byte type = 6)
        {
            strings.Add(text);
            return new(type, strings.Count - 1);
        }
        PvfToken Tag(string name) => Text("[" + name + "]", 3);
        PvfToken Number(int value) => new(0, value);
        PvfTokenDocument Doc(params PvfToken[] tokens) => new(tokens, Resolve);
        CharacterInvestmentMetadataCatalog Catalog() => new(
            path => documents.TryGetValue(path, out var document) ? document : throw new FileNotFoundException(path),
            Resolve, new Dictionary<int, string> { [1] = "child.equ" }, new Dictionary<int, string> { [2] = "card.stk" });

        var tableTokens = new[] { Tag("equipment part set"), Number(7), Text("sets/test.equ"), Text("合成测试套装"),
            Text("项链成员"), Text("[amulet]"), Number(0), Number(0),
            Text("上衣成员"), Text("[coat]"), Number(0), Number(0), Tag("/equipment part set") };
        var table = Doc(tableTokens);
        documents["etc/equipmentpartset.etc"] = table;
        documents["equipment/child.equ"] = Doc(Tag("import script"), Text("parent.equ"));
        documents["equipment/parent.equ"] = Doc(Tag("part set index"), Number(7));
        var tiers = Doc(Tag("piece set ability"), Number(5), Tag("physical attack"), Number(5), Tag("/piece set ability"),
            Tag("piece set ability"), Number(2), Tag("magical attack"), Number(2), Tag("/piece set ability"));
        documents["equipment/sets/test.equ"] = tiers;
        var result = Catalog().ReadSet(1);
        That(result is { SetId: 7 } && result.Thresholds.SequenceEqual(new[] { 2, 5 }), "建设套装继承与件数排序不解释效果");
        documents["equipment/child.equ"] = Doc();
        That(Catalog().ReadSet(1) is null, "合法无套装声明返回无套装");
        foreach (var bad in new[]
        {
            Doc(Tag("part set index")),
            Doc(Tag("part set index"), Number(0)),
            Doc(Tag("part set index"), Text("7")),
            Doc(Tag("part set index"), new PvfToken(2, 7)),
            Doc(Tag("part set index"), Number(7), Number(8)),
            Doc(Tag("part set index"), Number(7), Tag("part set index"), Number(7)),
            Doc(Tag("condition"), Tag("part set index"), Number(7), Tag("/condition")),
            Doc(Tag("import script"), Text("parent.equ"), Tag("part set index")),
            Doc(Tag("import script"), Text("child.equ")),
            Doc(Tag("import script"), Text("../outside.equ")),
            Doc(Tag("import script"), Text("missing.equ")),
            Doc(Tag("import script"), Text("parent.equ"), Tag("import script"), Text("parent.equ"))
        })
        {
            documents["equipment/child.equ"] = bad;
            Reject<InvalidDataException>(() => Catalog().ReadSet(1));
        }
        documents["equipment/child.equ"] = Doc(Tag("part set index"), Number(7), Tag("import script"), Text("parent.equ"));
        documents["equipment/parent.equ"] = Doc(Tag("import script"), Text("child.equ"));
        Reject<InvalidDataException>(() => Catalog().ReadSet(1));
        documents["equipment/parent.equ"] = Doc();
        foreach (var badTable in new[]
        {
            // 旧合成叶声明不是当前真实目录格式，不能继续当作兼容合同。
            Doc(Tag("equipment part set"), Number(7), Text("sets/test.equ"), Text("a")),
            Doc(Tag("equipment part set"), Number(7), Text("sets/test.equ"), Text("a"),
                Text("项链成员"), Text("[amulet]"), Number(0), Number(0)),
            Doc(Tag("equipment part set"), Number(7), Text("sets/test.equ"), Text("a"), Tag("/equipment part set")),
            Doc(Tag("equipment part set"), Number(7), Text("sets/test.equ"), Text("a"),
                Text("项链成员"), Text("[amulet]"), Number(0), Tag("/equipment part set")),
            Doc(Tag("equipment part set"), Number(7), Text("sets/test.equ"), Text("a"),
                Text("项链成员"), Text("[amulet]"), Text("0"), Number(0), Tag("/equipment part set")),
            Doc(Tag("equipment part set"), Number(7), Text("sets/test.equ"), Text("a"),
                Text("项链成员"), Text("[unknown]"), Number(0), Number(0), Tag("/equipment part set")),
            Doc(Tag("equipment part set"), Number(7), Text("sets/test.equ"), Text("a"),
                Text("项链成员"), Text("[amulet]"), Number(0), Number(0), Tag("extra"), Number(1), Tag("/equipment part set")),
            Doc(Tag("equipment part set"), Number(7), Text("sets/test.equ"), Text("a"),
                Text("项链成员"), Text("[amulet]"), Number(0), Number(0), Tag("/equipment part set"),
                Tag("equipment part set"), Number(7), Text("sets/test.equ"), Text("b"),
                Text("上衣成员"), Text("[coat]"), Number(0), Number(0), Tag("/equipment part set")),
            Doc(Tag("container"), Tag("equipment part set"), Number(7), Text("sets/test.equ"), Text("a"),
                Text("项链成员"), Text("[amulet]"), Number(0), Number(0), Tag("/equipment part set"), Tag("/container"))
        })
        {
            documents["etc/equipmentpartset.etc"] = badTable;
            Reject<InvalidDataException>(() => Catalog().ReadSet(1));
        }
        foreach (PvfToken[] unrelated in new PvfToken[][]
        {
            [Tag("equipment part set"), Number(8), Text("sets/other.equ"), Text("a"),
                Text("成员"), Text("[coat]"), Number(0), Number(0), Tag("/equipment part set"),
                Tag("equipment part set"), Number(8), Text("sets/other.equ"), Text("a"),
                Text("成员"), Text("[coat]"), Number(0), Number(0), Tag("/equipment part set")],
            [Tag("equipment part set"), Number(8), Text("../outside.equ"), Text("a"),
                Text("成员"), Text("[coat]"), Number(0), Number(0), Tag("/equipment part set")],
            [Tag("equipment part set"), Number(8), Text("sets/other.equ"), Text("a"),
                Text("成员"), Text("[coat]"), Text("0"), Number(0), Tag("/equipment part set")]
        })
        {
            documents["etc/equipmentpartset.etc"] = Doc(tableTokens.Concat(unrelated).ToArray());
            documents["equipment/child.equ"] = Doc(Tag("part set index"), Number(7));
            var catalog = Catalog();
            That(catalog.ReadSet(1) is { SetId: 7 }, "无关套装重复或坏路径成员不污染唯一合法目标");
            documents["equipment/child.equ"] = Doc(Tag("part set index"), Number(8));
            Reject<InvalidDataException>(() => catalog.ReadSet(1));
        }
        documents["equipment/child.equ"] = Doc(Tag("part set index"), Number(7));
        documents["etc/equipmentpartset.etc"] = Doc(tableTokens.Concat(new[]
        {
            Tag("equipment part set"), Text("8"), Text("sets/other.equ"), Text("a"), Tag("/equipment part set")
        }).ToArray());
        Reject<InvalidDataException>(() => Catalog().ReadSet(1));
        documents["etc/equipmentpartset.etc"] = table;
        documents["equipment/sets/test.equ"] = Doc(Tag("piece set ability"), Number(2), Tag("stat"), Number(1), Tag("/piece set ability"),
            Tag("piece set ability"), Number(2), Tag("stat"), Number(2), Tag("/piece set ability"));
        Reject<InvalidDataException>(() => Catalog().ReadSet(1));
        documents["equipment/sets/test.equ"] = tiers;

        PvfTokenDocument Card(params PvfToken[] body) => Doc(new[]
        {
            Tag("stackable type"), Text("[material expert job]"), Number(1),
            Tag("string data"), Text("目标说明"), Text("[weapon]"), Text("[coat]"), Tag("/string data"), Tag("enchant table")
        }.Concat(body).Append(Tag("/enchant table")).ToArray());
        var goodCard = Card(Tag("enchant index"), Number(3), Tag("magical attack"), Number(15), Tag("/enchant index"));
        documents["stackable/card.stk"] = goodCard;
        That(Catalog().ReadEnchantment(2, 3) is { Known: true, ValidIndex: true }, "建设附魔按真实索引验证而非要求连续等级");
        var enchantment = Catalog().ReadEnchantment(2, 3);
        That(enchantment.Targets.SetEquals(new[] { (ItemKind.Equipment, (byte)12), (ItemKind.Equipment, (byte)14) }) &&
            !enchantment.Targets.Contains((ItemKind.Creature, 26)) && !enchantment.Targets.Contains((ItemKind.Equipment, 13)),
            "合法卡片索引仍不得用于宠物或称号等错误宿主");
        That(Catalog().ReadEnchantment(2, 0) is { Known: true, ValidIndex: false }, "缺少附魔索引不能默认零级");
        That(Catalog().ReadEnchantment(9, 0) is { Known: false, ValidIndex: false }, "未知卡片不能按编号推定效果");
        foreach (var bad in new[]
        {
            Card(Tag("enchant index"), Number(3), Tag("stat"), Number(1), Tag("/enchant index"),
                Tag("enchant index"), Number(3), Tag("stat"), Number(1), Tag("/enchant index")),
            Card(Tag("enchant index"), Text("3"), Tag("stat"), Number(1), Tag("/enchant index")),
            Card(Tag("enchant index"), Number(3), Tag("/enchant index")),
            Card(Tag("enchant index"), Number(3), Tag("stat"), Text("15"), Tag("/enchant index")),
            Card(Tag("enchant index"), Number(3), Tag("condition"), Tag("stat"), Number(1), Tag("/condition"), Tag("/enchant index")),
            Card(Tag("enchant index"), Number(3), Tag("stat"), Number(1), Tag("/enchant index"),
                Tag("string data"), Text("目标说明"), Text("[weapon]"), Tag("/string data")),
            Doc(Tag("stackable type"), Text("[material expert job]"), Number(1), Tag("enchant table"),
                Tag("enchant index"), Number(3), Tag("stat"), Number(1), Tag("/enchant index"), Tag("/enchant table")),
            Doc(Tag("stackable type"), Text("[material expert job]"), Number(1), Tag("string data"),
                Text("目标说明"), Text("[unknown target]"), Tag("/string data")),
            Doc(Tag("stackable type"), Text("[material expert job]"), Number(1), Tag("string data"),
                Text("目标说明"), Text("[weapon]"), Text("[weapon]"), Tag("/string data")),
            Doc(Tag("condition"), Tag("enchant table"), Tag("enchant index"), Number(3), Tag("stat"), Number(1),
                Tag("/enchant index"), Tag("/enchant table"), Tag("/condition"), Tag("stackable type"), Text("[material expert job]"), Number(1))
        })
        {
            documents["stackable/card.stk"] = bad;
            Reject<InvalidDataException>(() => Catalog().ReadEnchantment(2, 3));
        }
        documents["stackable/card.stk"] = goodCard;
        documents["etc/randomtitleenchant.etc"] = Doc(Tag("index"), Number(4), Number(9), Number(1), Number(100),
            Text("[mage]"), Number(10), Number(1), Tag("/index"));
        That(Catalog().ValidateRandomTitle(4U | (9U << 16)), "建设随机称号附魔校验真实奖池与条目");
        That(!Catalog().ValidateRandomTitle(4U | (8U << 16)) && !Catalog().ValidateRandomTitle(0), "缺少随机称号条目不算安装");
    }
}
