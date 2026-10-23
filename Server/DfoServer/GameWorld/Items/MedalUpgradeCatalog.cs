using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

// 勋章强化行对应目标等级+1..+N；材料列固定为公会硬币。
internal sealed record MedalUpgradeRow(int FailureWeight, byte PenaltyType, int PenaltyValue, int MaterialId, int MaterialCount);

internal sealed record MedalUpgradeRules(ImmutableArray<MedalUpgradeRow> Rows, ImmutableArray<float> RarityWeights)
{
    public int CoinCost(int rarity, int materialCount)
    {
        if (rarity < 0 || rarity >= RarityWeights.Length) throw new InvalidDataException("勋章稀有度超出公会硬币权重");
        // 客户端按整十显示前先向下取整：18.4记18、36.8记36，不能四舍五入。
        return checked((int)Math.Floor(materialCount * (double)RarityWeights[rarity]));
    }
}

// etc/upgrade.etc 的 flag_upgrade_table：首行16项，其余行24项，
// 末8项为失败权重/惩罚/材料；尾部8项不足一行，按行尾校验失败后忽略。
internal sealed class MedalUpgradeCatalog
{
    readonly Func<string, PvfTokenDocument> read;
    readonly Func<int, string> resolve;
    readonly Lazy<MedalUpgradeRules> rules;
    public MedalUpgradeRules Rules => rules.Value;

    public MedalUpgradeCatalog(PvfArchive archive)
        : this(path => new(archive.ReadTokens(path), archive.ResolveString), archive.ResolveString) { }
    internal MedalUpgradeCatalog(Func<string, PvfTokenDocument> read, Func<int, string> resolve)
    {
        this.read = read; this.resolve = resolve;
        rules = new(() => Parse(read("etc/upgrade.etc"), resolve));
    }

    internal static MedalUpgradeRules Parse(PvfTokenDocument document, Func<int, string> resolve)
    {
        var tables = document.Sections.Where(row => row.Name == "table").SelectMany(row => row.Children)
            .Where(row => row.Name == "table type" && row.Values.Length > 1 && row.Values[0].Type == 6 &&
                resolve(row.Values[0].Value) == "flag_upgrade_table").ToArray();
        Require(tables.Length == 1);
        var rows = Rows(tables[0].Values);
        var weights = AmplificationFiles.Numbers(One(document.Sections, "cost weights by guild flag rarity"));
        Require(rows.Length >= 20 && rows.All(row => row.FailureWeight is >= 0 and <= 100000 && row.PenaltyType is >= 1 and <= 3 &&
            row.PenaltyValue is >= 0 and <= 255 && row.MaterialId >= 1000 && row.MaterialCount > 0) &&
            weights.Length == 7 && weights.All(value => float.IsFinite(value) && value >= 0));
        return new(rows, weights);
    }

    // 短行优先；短行尾部校验失败再按长行读取，表尾不足一行的残余自然终止解析。
    static ImmutableArray<MedalUpgradeRow> Rows(ImmutableArray<PvfToken> values)
    {
        var rows = ImmutableArray.CreateBuilder<MedalUpgradeRow>();
        for (int at = 1; at + 16 <= values.Length;)
        {
            MedalUpgradeRow row;
            int width;
            if (TryRow(values, at, 16, out var shortRow)) { row = shortRow; width = 16; }
            else if (TryRow(values, at, 24, out var longRow)) { row = longRow; width = 24; }
            else break;
            rows.Add(row);
            at += width;
        }
        return rows.ToImmutable();
    }

    // 行尾8项在两种宽度下都位于同一相对位置，短行与长行共用同一套校验。
    static bool TryRow(ImmutableArray<PvfToken> values, int at, int width, out MedalUpgradeRow row)
    {
        row = null!;
        if (at + width > values.Length) return false;
        int tail = at + width - 8;
        if (!Whole(values[tail], out int failureWeight) || !Whole(values[tail + 2], out int penaltyType) ||
            !Whole(values[tail + 3], out int penaltyValue) || !Whole(values[tail + 6], out int materialId) ||
            !Whole(values[tail + 7], out int materialCount) ||
            failureWeight is < 0 or > 100000 || penaltyType is < 1 or > 3 || penaltyValue is < 0 or > 255 ||
            materialId < 1000 || materialCount <= 0)
            return false;
        row = new(failureWeight, (byte)penaltyType, penaltyValue, materialId, materialCount);
        return true;
    }

    static bool Whole(PvfToken token, out int value)
    {
        value = 0;
        if (token.Type == 0) { value = token.Value; return true; }
        if (token.Type != 2) return false;
        float number = BitConverter.Int32BitsToSingle(token.Value);
        if (!float.IsFinite(number) || number != MathF.Truncate(number) || number < int.MinValue || number > int.MaxValue) return false;
        value = (int)number;
        return true;
    }
    static PvfTokenSection One(IEnumerable<PvfTokenSection> fields, string name)
    {
        var rows = fields.Where(row => row.Name == name).ToArray();
        return rows.Length == 1 ? rows[0] : throw new InvalidDataException("勋章强化字段缺失或重复: " + name);
    }
    static void Require(bool condition)
    {
        if (!condition) throw new InvalidDataException("勋章强化资源字段、范围或重复声明无效");
    }
}
