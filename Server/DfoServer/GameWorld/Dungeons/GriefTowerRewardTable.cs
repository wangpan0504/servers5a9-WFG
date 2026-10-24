using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal sealed record GriefTowerRewardSlot(int Number, ImmutableArray<DungeonSpecificCardItem> Items);

// 保留每个具名卡槽的完整原始行，不合并槽、不补空白项，也不把数值自动换算成独立掉落概率。
internal static class GriefTowerRewardTable
{
    public const string ResourcePath = "etc/itemdropinfo_togclearreward.etc";

    public static ImmutableDictionary<string, ImmutableArray<GriefTowerRewardSlot>> Parse(
        IReadOnlyList<PvfToken> tokens, Func<int,string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        var containers = document.Sections.Where(row => row.Name == "cardslot specific reward table").ToArray();
        if (!document.Diagnostics.IsEmpty || containers is not [{ IsBlock: true, Values.Length: 0 } container] ||
            container.Children.IsEmpty)
            throw new InvalidDataException("悲叹卡槽奖励表缺失、重复或结构非法");
        var groups = ImmutableDictionary.CreateBuilder<string, ImmutableArray<GriefTowerRewardSlot>>(StringComparer.Ordinal);
        foreach (var table in container.Children)
        {
            var values = table.Values;
            if (table.Name != "table" || !table.IsBlock || !table.Children.IsEmpty || values.Length < 5 ||
                values[0].Type != 6 || (values.Length - 1) % 4 != 0)
                throw new InvalidDataException("悲叹奖励组必须包含组名及完整四字段行");
            string group = resolve(values[0].Value);
            if (string.IsNullOrWhiteSpace(group) || groups.ContainsKey(group))
                throw new InvalidDataException("悲叹奖励组名为空或重复");
            var slots = new SortedDictionary<int, List<DungeonSpecificCardItem>>();
            for (int at = 1; at < values.Length; at += 4)
            {
                if (values[at].Type != 6 || values[at+1].Type != 0 || values[at+2].Type != 0 || values[at+3].Type != 0 ||
                    values[at+1].Value <= 0 || values[at+2].Value <= 0 || values[at+3].Value <= 0)
                    throw new InvalidDataException("悲叹奖励行需要卡槽名及正物品ID、数值、数量");
                string name = resolve(values[at].Value);
                if (!name.StartsWith("card ",StringComparison.Ordinal) ||
                    !int.TryParse(name.AsSpan(5),System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out int slot) ||
                    slot is < 1 or > 10 || name != "card " + slot.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    throw new InvalidDataException("悲叹卡槽名不符合当前客户端十槽结构");
                if (!slots.TryGetValue(slot,out var items)) slots.Add(slot,items = []);
                items.Add(new(values[at+1].Value, values[at+2].Value, values[at+3].Value));
            }
            groups.Add(group, slots.Select(row => new GriefTowerRewardSlot(row.Key,[..row.Value])).ToImmutableArray());
        }
        return groups.ToImmutable();
    }
}
