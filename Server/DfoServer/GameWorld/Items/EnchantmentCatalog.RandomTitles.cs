using System.Collections.Immutable;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record RandomTitleEnchantmentEntry(ushort Index, int Weight);
internal sealed record RandomTitleEnchantmentPool(ushort Index, ImmutableArray<RandomTitleEnchantmentEntry> Entries, int TotalWeight)
{
    public uint Draw(Func<int, int> next)
    {
        int roll = next(TotalWeight);
        if (roll < 0 || roll >= TotalWeight) throw new InvalidDataException("随机称号附魔抽样越界");
        foreach (var entry in Entries)
        {
            if (roll < entry.Weight) return Index | ((uint)entry.Index << 16);
            roll -= entry.Weight;
        }
        throw new InvalidDataException("随机称号附魔权重无效");
    }

    internal static RandomTitleEnchantmentPool Parse(PvfTokenDocument document, int index)
    {
        if (index is <= 0 or > ushort.MaxValue || !document.Diagnostics.IsEmpty)
            throw new InvalidDataException("随机称号附魔奖池声明无效");
        var groups = document.Sections.Where(row => row.Name == "index" && !row.Values.IsEmpty &&
            row.Values[0].Type == 0 && row.Values[0].Value == index).ToArray();
        if (groups.Length != 1 || !groups[0].IsBlock || !groups[0].Children.IsEmpty)
            throw new InvalidDataException("随机称号附魔奖池缺失或重复");
        var values = groups[0].Values;
        var seen = new HashSet<int>();
        var entries = ImmutableArray.CreateBuilder<RandomTitleEnchantmentEntry>();
        int at = 1, total = 0;
        int Integer()
        {
            if (at >= values.Length || values[at].Type != 0)
                throw new InvalidDataException("随机称号附魔整数token缺失");
            return values[at++].Value;
        }
        while (at < values.Length)
        {
            int id = Integer(), count = Integer(), weight = Integer();
            if (id is <= 0 or > ushort.MaxValue || count is < 1 or > 2 || weight < 0)
                throw new InvalidDataException("随机称号附魔条目无效");
            for (int skill = 0; skill < count; skill++)
            {
                if (at >= values.Length || values[at++].Type != 6 || Integer() is < 0 or >= 512 || Integer() <= 0)
                    throw new InvalidDataException("随机称号附魔技能向量无效");
            }
            // 客户端按序号建表，重复序号保留首次声明；不能抽中无法按同一序号显示的后续效果。
            if (seen.Add(id) && weight > 0)
            {
                total = checked(total + weight);
                entries.Add(new((ushort)id, weight));
            }
        }
        if (total == 0) throw new InvalidDataException("随机称号附魔没有正权重条目");
        return new((ushort)index, entries.ToImmutable(), total);
    }
}

internal sealed partial class EnchantmentCatalog
{
    EnchantmentDefinition? LoadRandomTitle(int itemId, string bead, ImmutableHashSet<int> restricted)
    {
        if (restricted.IsEmpty || Fields(bead, "monster card id").Count != 0 || Fields(bead, "enchant index").Count != 0)
            return null;
        var strings = Regex.Matches(MaskStrings(bead), @"\[string data\](.*?)\[/string data\]", RegexOptions.Singleline);
        if (strings.Count != 1 || !Regex.IsMatch(RawValues(bead, strings[0]), @"^\s*`\[title name\]`\s*$")) return null;
        int index = Scalar(bead, "random title enchant");
        return new(itemId, 0, 0, [(ItemKind.Equipment, 13)], restricted)
        { RandomTitle = RandomTitleEnchantmentPool.Parse(randomTitles.Value, index) };
    }
}
