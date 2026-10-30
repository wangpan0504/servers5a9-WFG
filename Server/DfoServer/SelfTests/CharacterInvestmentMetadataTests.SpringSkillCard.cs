using System.Globalization;
using System.Text.RegularExpressions;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class CharacterInvestmentMetadataTests
{
    static void SpringSkillCardInvestment()
    {
        const int cardId = 490705381, titleId = 990103;
        string card = EnchantmentTests.Spring2020Card381Text();
        // 复用已取证完整381文本；仅将该测试文本的整数/标签/字符串转成同序token。
        CharacterInvestmentMetadataCatalog Metadata(string text)
        {
            var strings = new List<string>();
            var tokens = new List<PvfToken>();
            int at = 0;
            foreach (Match match in Regex.Matches(text, @"`[^`]*`|\[[^\]\r\n]+\]|-?\d+"))
            {
                if (!string.IsNullOrWhiteSpace(text[at..match.Index]))
                    throw new InvalidDataException("建设381测试文本含未识别片段");
                string value = match.Value;
                if (value[0] is '`' or '[')
                {
                    strings.Add(value[0] == '`' ? value[1..^1] : value);
                    tokens.Add(new(value[0] == '`' ? (byte)6 : (byte)3, strings.Count - 1));
                }
                else tokens.Add(new(0, int.Parse(value, CultureInfo.InvariantCulture)));
                at = match.Index + match.Length;
            }
            if (!string.IsNullOrWhiteSpace(text[at..]))
                throw new InvalidDataException("建设381测试文本尾部含未识别片段");
            string Resolve(int value) => strings[value];
            var document = new PvfTokenDocument(tokens, Resolve);
            return new(_ => document, Resolve, new Dictionary<int, string>(),
                new Dictionary<int, string> { [cardId] = "cash/chn_490705381.stk" });
        }
        var title = new ItemTemplate(titleId, ItemKind.Equipment, 13, 95, 30, 1, 0,
            4, 1, "[free]", ushort.MaxValue, 0, "Title");
        var cardTemplate = title with { Id = cardId, Kind = ItemKind.Material, IsMonsterCard = true };
        CharacterInvestmentResult Score(string text, byte index = 0, byte slot = 13)
        {
            var metadata = Metadata(text);
            var host = title with { EquipmentSlot = slot };
            Character character = new(1, 0, "SpringSkillScore", 0, default!, null)
            {
                Level = 95,
                Inventory = new([new(82, new(InventoryListType.Equipment, slot),
                    new(titleId, ItemKind.Equipment, 1) { Durability = 30, EnchantCardId = cardId, EnchantUpgrade = index })])
            };
            return CharacterInvestmentScore.Calculate(CharacterInvestmentScore.Capture(character, 1000),
                id => id == titleId ? host : id == cardId ? cardTemplate : throw new KeyNotFoundException(),
                (id, level, kind, targetSlot) =>
                {
                    var known = metadata.ReadEnchantment(id, level);
                    return known.ValidIndex && known.Targets.Contains((kind, targetSlot));
                }, _ => false, _ => null, _ => null, _ => null, _ => throw new KeyNotFoundException(), (_, _) => false);
        }
        var original = Metadata(card).ReadEnchantment(cardId, 0);
        That(original.Known && original.ValidIndex && original.Targets.SetEquals([(ItemKind.Equipment, (byte)13)]),
            "完整真实381技能块应能确认索引0和称号宿主，不将合法安装标为未知");
        var scored = Score(card);
        That(scored is { Status: "Complete", Partial: false, Score: 800 } &&
            scored.Parts.Single(part => part.Module == "enchant") is { Score: 100, Partial: false },
            "真实381嵌套技能附魔固定计建设100分，不计算技能效果强度");
        string variant = card.Replace("[lower bound level]\n1 ", "[lower bound level]\n20 ", StringComparison.Ordinal)
            .Replace("[upper bound level]\n50 ", "[upper bound level]\n85 ", StringComparison.Ordinal)
            .Replace("[value]\n1 ", "[value]\n2 ", StringComparison.Ordinal);
        That(Score(variant) is { Status: "Complete", Score: 800 },
            "已知技能语法不绑定真实381的1至50级和加1数值");
        string signedVariant = card.Replace("[lower bound level]\n1 ", "[lower bound level]\n0 ", StringComparison.Ordinal)
            .Replace("[upper bound level]\n50 ", "[upper bound level]\n2147483647 ", StringComparison.Ordinal)
            .Replace("[value]\n1 ", "[value]\n-1 ", StringComparison.Ordinal);
        That(Score(signedVariant) is { Status: "Complete", Score: 800 },
            "建设元数据保留整数范围与有符号技能值，不用数值大小作伤害或建设倍率");
        foreach (string bad in new[]
        {
            card.Replace("[lower bound level]\n1 ", "[lower bound level]\n-1 ", StringComparison.Ordinal),
            card.Replace("[lower bound level]\n1 ", "[lower bound level]\n51 ", StringComparison.Ordinal),
            card.Replace("[value]\n1 ", "[value]\n`1` ", StringComparison.Ordinal),
            card.Replace("[lower bound level]\n1 ", "[lower bound level]\n1 \n[lower bound level]\n1 ", StringComparison.Ordinal),
            card.Replace("[value]\n1 ", "", StringComparison.Ordinal),
            card.Replace("`[active]`", "`[unknown group]`", StringComparison.Ordinal),
            card.Replace("`[except for ex skill]`", "`[unknown exclusion]`", StringComparison.Ordinal),
            card.Replace("`[except for ex skill]`", "", StringComparison.Ordinal),
            card.Replace("[value]\n1 ", "[value]\n1 \n[unknown bonus]\n1 ", StringComparison.Ordinal),
            card.Replace("[skill phase]", "[unknown phase]", StringComparison.Ordinal),
            card.Replace("[/all skill item container]", "[/all skill item container]\n[if]\n[attack success]\n1\n[/if]\n[then]\n[stat]\n`unknown` `%` 3\n[/then]", StringComparison.Ordinal)
        })
        {
            Reject<InvalidDataException>(() => Metadata(bad).ReadEnchantment(cardId, 0));
            var rejected = Score(bad);
            That(rejected.Partial && rejected.Parts.Single(part => part.Module == "enchant") is
                { Score: 0, Partial: true, Note: "resource_or_state_unavailable" },
                "未知块、非法边界、缺漏或错误token不因评分放宽而成为合法附魔");
        }
        That(Metadata(card).ReadEnchantment(cardId, 1) is { Known: true, ValidIndex: false },
            "完整技能块不使未知附魔索引合法");
        That(Score(card, index: 1).Parts.Single(part => part.Module == "enchant") is { Score: 0, Partial: true } &&
            Score(card, slot: 24).Parts.Single(part => part.Module == "enchant") is { Score: 0, Partial: true },
            "真实卡未知索引与错误装备宿主仍独立拒绝");
    }
}
