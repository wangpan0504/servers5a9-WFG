using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal sealed record DungeonAltarClearProbability(int MinimumLevel, int MaximumLevel, int Probability, int RawExtra);

// 本版clearreward每行四整数；历史端只有前三列，附加列原样保存且不并入概率。
internal sealed record DungeonAltarClearItemCatalog(ImmutableArray<DungeonAltarClearProbability> Probabilities,
    ImmutableArray<int> KindWeights, ImmutableArray<int> RarityThresholds,
    ImmutableArray<DungeonDropItemReference> ItemReferences, ImmutableArray<DungeonDropGoldReference> GoldReferences,
    float SoloGoldRate, float RankGoldRate)
{
    public int RandomItemProbability { get; init; }
    public const string ResourcePath = "etc/itemdropinfo_clearreward.etc";

    public static DungeonAltarClearItemCatalog Load(PvfArchive archive) => Read(archive.ReadTokens(ResourcePath),
        archive.ReadTokens(DungeonDropRuleCatalog.CommonPath), archive.ReadTokens(DungeonAltarAdmissionCatalog.ResourcePath), archive.ResolveString);

    internal static DungeonAltarClearItemCatalog Read(IReadOnlyList<PvfToken> clear, IReadOnlyList<PvfToken> common,
        IReadOnlyList<PvfToken> server, Func<int, string> resolve)
    {
        var sections = new PvfTokenDocument(clear, resolve).Sections;
        ImmutableArray<PvfToken> Field(ImmutableArray<PvfTokenSection> source, string name)
        {
            var matches = source.Where(row => row.Name == name).ToArray();
            return matches is [var row] && row.Children.IsEmpty && !row.Values.IsEmpty ? row.Values :
                throw new InvalidDataException("祭坛翻牌字段缺失、重复或嵌套：" + name);
        }
        int[] Integers(ImmutableArray<PvfTokenSection> source, string name, int? count = null)
        {
            var field = Field(source, name);
            if (field.Any(token => token.Type != 0) || count is { } length && length != field.Length)
                throw new InvalidDataException("祭坛翻牌整数维度无效：" + name);
            return field.Select(token => token.Value).ToArray();
        }
        float[] Numbers(ImmutableArray<PvfTokenSection> source, string name, int count)
        {
            var field = Field(source, name);
            if (field.Length != count || field.Any(token => token.Type is not (0 or 2)))
                throw new InvalidDataException("祭坛翻牌倍率维度无效：" + name);
            var result = field.Select(token => token.Type == 0 ? token.Value : BitConverter.Int32BitsToSingle(token.Value)).ToArray();
            if (result.Any(value => !float.IsFinite(value) || value < 0)) throw new InvalidDataException("祭坛翻牌倍率越界");
            return result;
        }
        int count = Integers(sections, "drop prob count", 1)[0];
        if (count is < 1 or > 200) throw new InvalidDataException("祭坛翻牌概率行数越界");
        var raw = Field(sections, "drop prob");
        var result = ImmutableArray.CreateBuilder<DungeonAltarClearProbability>(count);
        bool found = false;
        for (int at = 0; at < raw.Length;)
        {
            if (raw[at].Type != 6) throw new InvalidDataException("祭坛翻牌概率缺少分组名");
            string group = resolve(raw[at++].Value);
            if (raw.Length - at < count * 4 || raw.Skip(at).Take(count * 4).Any(token => token.Type != 0))
                throw new InvalidDataException("祭坛翻牌概率组不是完整四整数行");
            if (group == "default")
            {
                if (found) throw new InvalidDataException("祭坛默认翻牌组重复");
                found = true;
                int previous = 0;
                for (int index = 0; index < count; index++)
                {
                    int row = at + index * 4;
                    int min = raw[row].Value, max = raw[row + 1].Value, probability = raw[row + 2].Value, extra = raw[row + 3].Value;
                    if (min != previous + 1 || max < min || max > 200 || probability is < 0 or > 10000 || extra < 0)
                        throw new InvalidDataException("祭坛翻牌等级段或概率无效");
                    result.Add(new(min, max, probability, extra));
                    previous = max;
                }
            }
            at += count * 4;
        }
        if (!found) throw new InvalidDataException("祭坛默认翻牌组缺失");
        int[] weights = Integers(sections, "drop item type prob", 4);
        int[] rarity = Integers(sections, "basis of rarity dicision", 7);
        if (weights.Any(value => value < 0) || weights.Sum(value => (long)value) != 10000 || rarity[0] < 0 ||
            rarity.Zip(rarity.Skip(1)).Any(pair => pair.First > pair.Second) || rarity[^1] < 1000000)
            throw new InvalidDataException("祭坛翻牌类别或稀有度范围无效");
        int[] item = Integers(sections, "item drop ref table");
        int[] gold = Integers(new PvfTokenDocument(common, resolve).Sections, "gold drop ref table");
        if (item.Length == 0 || item.Length % 3 != 0 || gold.Length == 0 || gold.Length % 3 != 0)
            throw new InvalidDataException("祭坛翻牌引用表不完整");
        var items = ImmutableArray.CreateBuilder<DungeonDropItemReference>();
        var money = ImmutableArray.CreateBuilder<DungeonDropGoldReference>();
        for (int at = 0; at < item.Length; at += 3)
        {
            if (item[at] != at / 3 + 1 || item[at + 1] < 0 || item[at + 2] < 0)
                throw new InvalidDataException("祭坛翻牌物品引用等级或范围无效");
            items.Add(new(item[at], item[at + 1], item[at + 2]));
        }
        var levels = new HashSet<int>();
        for (int at = 0; at < gold.Length; at += 3)
        {
            if (gold[at] is < 1 or > 200 || !levels.Add(gold[at]) || gold[at + 1] <= 0 || gold[at + 2] is < 0 or > 100)
                throw new InvalidDataException("祭坛翻牌金币引用无效");
            money.Add(new(gold[at], gold[at + 1], gold[at + 2]));
        }
        float solo = Numbers(sections, "dungeon difficulty gold drop bonusrate", 5)[0];
        float rank = Numbers(new PvfTokenDocument(server, resolve).Sections, "clear rank exp bonusrate", 5)[1];
        int[] dropKinds = Integers(sections, "drop kind prob", 2);
        if (dropKinds.Any(value => value is < 0 or > 10000) || dropKinds[1] != 0)
            throw new NotSupportedException("祭坛翻牌存在未绑定的指定物品分支");
        return new(result.MoveToImmutable(), weights.ToImmutableArray(), rarity.ToImmutableArray(), items.ToImmutable(), money.ToImmutable(), solo, rank)
            { RandomItemProbability = dropKinds[0] };
    }
}
