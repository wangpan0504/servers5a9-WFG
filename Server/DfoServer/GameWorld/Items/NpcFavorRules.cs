using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record NpcFavorGift(int NpcId, int ItemId, int Count, int Minimum, int Maximum);
internal sealed record NpcFavorRules(int MinimumLevel, int GiftCount, byte GiftLimit,
    ImmutableDictionary<int, NpcFavorGift> Gifts, ImmutableArray<NpcFavorGift> ExtraGifts,
    ImmutableHashSet<byte> Colors)
{
    public ImmutableSortedDictionary<int, int> NameColors { get; init; } = ImmutableSortedDictionary<int, int>.Empty;
    public int NameColor(int points, int maximum)
    {
        int percent = (int)((float)points / maximum * 100f);
        return NameColors.LastOrDefault(pair => pair.Key <= percent) is { Key: > 0 } selected ? selected.Value : unchecked((int)0xFF5AD6F7);
    }
    public NpcFavorGift? Extra(int ownerId, int itemId) =>
        ExtraGifts.FirstOrDefault(row => row.NpcId == ownerId && row.ItemId == itemId) ??
        ExtraGifts.FirstOrDefault(row => row.NpcId == -1 && row.ItemId == itemId);

    internal static NpcFavorRules Parse(PvfTokenDocument document)
    {
        if (document.Diagnostics.Length != 0) throw new InvalidDataException("NPC好感度配置结构不完整");
        PvfToken[] Values(string name, bool block = false)
        {
            var fields = document.Sections.Where(field => field.Name == name).ToArray();
            if (fields is not [{ Children.Length: 0 } field] || field.IsBlock != block)
                throw new InvalidDataException("NPC好感度字段缺失或重复: " + name);
            return field.Values.ToArray();
        }
        int Scalar(string name, int maximum)
        {
            var values = Values(name);
            return values is [{ Type: 0, Value: > 0 } value] && value.Value <= maximum ? value.Value :
                throw new InvalidDataException("NPC好感度数值无效: " + name);
        }
        int level = Scalar("favor condition level", byte.MaxValue);
        int count = Scalar("favor gift item count", int.MaxValue);
        byte limit = (byte)Scalar("favor gift limit", byte.MaxValue);
        var gifts = ImmutableDictionary.CreateBuilder<int, NpcFavorGift>();
        var values = Values("favor level point up", block: true);
        if (values.Length == 0 || values.Length % 3 != 0 || values.Any(token => token.Type != 0))
            throw new InvalidDataException("NPC普通赠礼表无效");
        for (int index = 0; index < values.Length; index += 3)
        {
            var gift = new NpcFavorGift(-1, values[index].Value, count, values[index + 1].Value, values[index + 2].Value);
            ValidateGift(gift);
            if (!gifts.TryAdd(gift.ItemId, gift)) throw new InvalidDataException("NPC普通赠礼重复物品");
        }
        var extras = ImmutableArray.CreateBuilder<NpcFavorGift>();
        values = Values("extra favor gift", block: true);
        if (values.Length % 6 != 0) throw new InvalidDataException("NPC特殊赠礼表宽度无效");
        var keys = new HashSet<(int, int)>();
        for (int index = 0; index < values.Length; index += 6)
        {
            if (values.Skip(index).Take(5).Any(token => token.Type != 0) || values[index + 5].Type != 6)
                throw new InvalidDataException("NPC特殊赠礼字段类型无效");
            var gift = new NpcFavorGift(values[index].Value, values[index + 1].Value, values[index + 2].Value,
                values[index + 3].Value, values[index + 4].Value);
            // -1/-1 属于删除旧NPC的迁移礼物，不当成普通加点或静默吞掉源物品。
            if (gift.Minimum == -1 && gift.Maximum == -1 && gift.NpcId == -1 && gift.ItemId > 0 && gift.Count > 0)
                continue;
            ValidateGift(gift);
            if (!keys.Add((gift.NpcId, gift.ItemId))) throw new InvalidDataException("NPC特殊赠礼重复条目");
            extras.Add(gift);
        }
        var colors = ImmutableHashSet.CreateBuilder<byte>();
        values = Values("npc color map", block: true);
        if (values.Length == 0 || values.Length % 5 != 0) throw new InvalidDataException("NPC染色表宽度无效");
        for (int index = 0; index < values.Length; index += 5)
        {
            if (values.Skip(index).Take(4).Any(token => token.Type != 0 || token.Value is < 0 or > 255) ||
                values[index + 4].Type != 6 || !colors.Add((byte)values[index].Value))
                throw new InvalidDataException("NPC染色表条目无效");
        }
        if (!colors.Contains(0)) throw new InvalidDataException("NPC染色表缺少默认颜色");
        var names = ImmutableSortedDictionary.CreateBuilder<int, int>();
        values = Values("favor npc name color", block: true);
        if (values.Length == 0 || values.Length % 4 != 0) throw new InvalidDataException("NPC名字颜色表宽度无效");
        for (int index = 0; index < values.Length; index += 4)
        {
            if (values[index] is not { Type: 0, Value: > 0 and <= 100 } threshold ||
                values.Skip(index + 1).Take(3).Any(token => token.Type != 0 || token.Value is < 0 or > 255))
                throw new InvalidDataException("NPC名字颜色表字段无效");
            // 0400E0D0按资源顺序写入低三个字节，不能按字段名字猜测RGB顺序。
            int color = unchecked((int)0xFF000000) | values[index + 1].Value | values[index + 2].Value << 8 | values[index + 3].Value << 16;
            if (!names.TryAdd(threshold.Value, color)) throw new InvalidDataException("NPC名字颜色阈值重复");
        }
        return new(level, count, limit, gifts.ToImmutable(), extras.ToImmutable(), colors.ToImmutable()) { NameColors = names.ToImmutable() };
    }

    static void ValidateGift(NpcFavorGift gift)
    {
        if (gift.NpcId != -1 && gift.NpcId <= 0 || gift.ItemId <= 0 || gift.Count <= 0 || gift.Minimum <= 0 ||
            gift.Maximum < gift.Minimum || gift.Maximum > ushort.MaxValue)
            throw new InvalidDataException("NPC赠礼数量或好感度区间无效");
    }
}
