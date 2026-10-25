using System.Collections.Frozen;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

// 此枚举仅表示资源阵营，不定义客户端wire颜色编号。
internal enum RuneCamp { Terracotta, CircleMage, Guardians, AntiquityLibrary, SecondPact }

internal sealed record TalismanDefinition(ItemTemplate Item, ImmutableArray<byte> FirstGrowTypes, int? SkillIndex)
{
    public bool IsPlaceholder => SkillIndex is null;

    public bool AllowsUse(byte level, byte job, byte growType) => !IsPlaceholder && Item.AllowsUse(level, job) &&
        FirstGrowTypes.Contains((byte)(growType & 0x0F));

    public static TalismanDefinition Parse(TalismanFile file, ItemTemplate item)
    {
        var raw = file.Equipment;
        if (raw.TemplateId != item.Id || raw.DeclaredEquipmentType?.Label != "[talisman]" ||
            raw.DeclaredUsableJobs is not { Count: > 0 } || item.Jobs == 0 ||
            raw.UnsupportedFields.Contains("equipment type") || raw.UnsupportedFields.Contains("usable job"))
            throw new InvalidDataException("护石类型或职业声明无效: " + item.Id);
        // 碎裂护石是资源中的占位物，不能因缺字段被补成任意职业可穿的有效护石。
        if (file.DeclaredSkillIndex is null && file.DeclaredGrowTypes is null && item.Rarity == 0 && item.Jobs == ushort.MaxValue)
            return new(item, [], null);
        if (file.DeclaredSkillIndex is not > 0)
            throw new InvalidDataException("护石缺少有效技能编号: " + item.Id);
        return new(item, ReadGrowTypes(file.DeclaredGrowTypes), file.DeclaredSkillIndex);
    }

    internal static ImmutableArray<byte> ReadGrowTypes(IEnumerable<int>? declared)
    {
        if (declared is null)
            throw new InvalidDataException("护石或符文缺少首转声明");
        var values = declared.ToArray();
        if (values.Length == 0 || values.Any(value => value is < 0 or > 5) || values.Distinct().Count() != values.Length)
            throw new InvalidDataException("护石或符文首转声明为空、重复或越界");
        return values.Select(value => (byte)value).ToImmutableArray();
    }
}

internal sealed record RuneDefinition(ItemTemplate Item, ImmutableArray<byte> FirstGrowTypes,
    int SkillIndex, RuneCamp Camp, byte Grade)
{
    public const int StackLimit = 999;

    public byte SocketColor => Camp switch
    {
        RuneCamp.Terracotta => 1,
        RuneCamp.CircleMage => 2,
        RuneCamp.Guardians => 3,
        RuneCamp.AntiquityLibrary => 4,
        RuneCamp.SecondPact => 5,
        _ => throw new InvalidDataException("未知符文阵营")
    };

    public bool AllowsUse(byte level, byte job, byte growType) => Item.AllowsUse(level, job) &&
        FirstGrowTypes.Contains((byte)(growType & 0x0F));

    public static RuneDefinition Parse(RuneFile file, ItemTemplate item)
    {
        var raw = file.Stackable;
        if (raw.TemplateId != item.Id || raw.TypeDeclaration?.Label != "[rune]" ||
            raw.DeclaredUsableJobs is not { Count: > 0 } || item.Jobs == 0 ||
            file.DeclaredSkillIndex is not > 0 || file.Option is not { Grade: >= 0 and <= byte.MaxValue } option ||
            raw.UnsupportedFields.Contains("stackable type") || raw.UnsupportedFields.Contains("usable job"))
            throw new InvalidDataException("符文类型、职业、技能或品级声明无效: " + item.Id);
        var camp = option.Camp switch
        {
            "terracotta" => RuneCamp.Terracotta,
            "circlemage" => RuneCamp.CircleMage,
            "guardians" => RuneCamp.Guardians,
            "antiquitylibrary" => RuneCamp.AntiquityLibrary,
            "secondpact" => RuneCamp.SecondPact,
            _ => throw new InvalidDataException("未知符文资源阵营: " + option.Camp)
        };
        return new(item, TalismanDefinition.ReadGrowTypes(raw.DeclaredUsableFirstGrowTypes),
            file.DeclaredSkillIndex.Value, camp, (byte)option.Grade);
    }
}

// [compound info] 每块以族与模式开头，随后是若干组「品级 件数 金币 升档权重 同档权重」。
// 两列权重就是升档判定：模式0（普通合成）的普通/高级符文与稀有护石升档权重为正，
// 末档（稀有符文、神器护石）与模式1（当前转职合成）固定为 0，与客户端说明「只有普通合成有几率获得高稀有度」一致。
internal sealed record TalismanCompoundCost(
    FrozenDictionary<(int Family, int Mode, int Grade), (int Count, int Gold, int UpgradeWeight, int SameWeight)> Rows)
{
    public bool TryGet(int family, int mode, int grade, out int count, out int gold, out int upgradeWeight, out int sameWeight)
    {
        if (Rows.TryGetValue((family, mode, grade), out var row))
        {
            (count, gold, upgradeWeight, sameWeight) = row;
            return true;
        }
        count = 0;
        gold = 0;
        upgradeWeight = 0;
        sameWeight = 0;
        return false;
    }

    public static TalismanCompoundCost Parse(RuneSystemFile file)
    {
        if (file.CompoundInfo.IsDefaultOrEmpty)
            throw new InvalidDataException("符文系统缺少合成费用表");
        var rows = new Dictionary<(int, int, int), (int, int, int, int)>();
        foreach (var row in file.CompoundInfo)
        {
            if (row.Length < 7 || (row.Length - 2) % 5 != 0 || row[0] is < 0 or > 1 || row[1] is < 0 or > 1)
                throw new InvalidDataException("符文合成费用表的族、模式或行长无效");
            for (int i = 2; i < row.Length; i += 5)
            {
                // 品级上界按资源实际取值放宽到 6；件数必须为正，金币与两列权重非负，权重和必须为正。
                if (row[i] is < 0 or > 6 || row[i + 1] <= 0 || row[i + 2] < 0 ||
                    row[i + 3] < 0 || row[i + 4] < 0 || row[i + 3] + row[i + 4] <= 0 ||
                    !rows.TryAdd((row[0], row[1], row[i]), (row[i + 1], row[i + 2], row[i + 3], row[i + 4])))
                    throw new InvalidDataException("符文合成费用表的品级重复或件数、金币、权重无效");
            }
        }
        return new(rows.ToFrozenDictionary());
    }
}

internal sealed record TalismanRefineCost(int MaterialId, FrozenDictionary<int, int> CountsByRarity)
{
    public static TalismanRefineCost Parse(RuneSystemFile file)
    {
        if (file.RefineMaterialId is not > 0 || file.RefineMaterialCounts is not { Length: > 0 } rows || rows.Length % 2 != 0)
            throw new InvalidDataException("护石重锻费用缺少材料或品级数量表");
        var counts = new Dictionary<int, int>();
        for (int i = 0; i < rows.Length; i += 2)
        {
            if (rows[i] < 0 || rows[i + 1] <= 0 || !counts.TryAdd(rows[i], rows[i + 1]))
                throw new InvalidDataException("护石重锻费用品级重复、越界或数量非正");
        }
        return new(file.RefineMaterialId.Value, counts.ToFrozenDictionary());
    }
}
