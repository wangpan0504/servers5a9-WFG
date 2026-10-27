using System.Collections.Immutable;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record EquipmentTransportRule(int Type, ImmutableArray<int> Grades,
    ImmutableDictionary<int, int> RarityWeights, ImmutableHashSet<byte> EquipmentSlots,
    ImmutableHashSet<string> StackableTypes, ImmutableHashSet<int> Included,
    ImmutableHashSet<int> Excluded, ImmutableHashSet<string> ExcludedProperties,
    ImmutableArray<int> Souls, ImmutableArray<int> SoulWeights, ImmutableArray<int> CrystalWeights,
    ImmutableArray<int> PieceWeights, ImmutableArray<int> ParticularCosts, int ChronicleCost)
{
    public int StoneCost(ItemTemplate target, int levelOffset = 0)
    {
        if ((Included.IsEmpty ? Excluded.Contains(target.Id) : !Included.Contains(target.Id)) ||
            !(target.Kind is ItemKind.Equipment or ItemKind.Avatar or ItemKind.Creature or ItemKind.GuildMedal
                ? EquipmentSlots.Contains(target.EquipmentSlot) : StackableTypes.Contains(Regex.Match(target.StackableType, @"\[[^\]]+\]").Value)) ||
            ExcludedProperties.Contains("growthType") && target.GrowthEquipment ||
            ExcludedProperties.Contains("randomoption") && target.RandomOptionEquipment ||
            ExcludedProperties.Contains("periodEquipment") && target.PeriodEquipment) return 0;
        // 宠物等专用类型没有等级和稀有度成本表，资格仍受部位和白黑名单约束。
        if (Grades.IsEmpty && RarityWeights.IsEmpty) return 1;
        int grade = RangeValue(Grades, target.MinimumLevel + levelOffset);
        return checked(grade * RarityWeights.GetValueOrDefault(target.Rarity));
    }

    internal static int RangeValue(ImmutableArray<int> rows, int level)
    {
        for (int index = 0; index < rows.Length; index += 3)
            if (level >= rows[index] && level <= rows[index + 1]) return rows[index + 2];
        return 0;
    }

    public ImmutableArray<ItemMaterialCost>? NpcCosts(ItemTemplate target, EpicPieceCatalog pieces, int levelOffset = 0)
    {
        int column = target.EquipmentSlot switch { 12 => 0, >= 14 and <= 18 => 1, >= 19 and <= 21 => 2, 22 or 23 or 25 => 3, _ => -1 };
        int baseCost = StoneCost(target, levelOffset);
        if (column < 0 || baseCost <= 0 || Souls.Length % 2 != 0 || SoulWeights.Length % 5 != 0 ||
            CrystalWeights.Length < 1 || (CrystalWeights.Length - 1) % 5 != 0 || PieceWeights.Length != 4 || ParticularCosts.Length % 4 != 0) return null;
        int soul = 0, soulWeight = -1, crystalWeight = -1;
        for (int index = 0; index < Souls.Length; index += 2)
            if (Souls[index] == target.Rarity) soul = Souls[index + 1];
        for (int index = 0; index < SoulWeights.Length; index += 5)
            if (SoulWeights[index] == target.Rarity) soulWeight = SoulWeights[index + 1 + column];
        for (int index = 1; index < CrystalWeights.Length; index += 5)
            if (CrystalWeights[index] == target.Rarity) crystalWeight = CrystalWeights[index + 1 + column];
        int soulCost = target.Rarity == 5 ? ChronicleCost : checked(baseCost * soulWeight);
        int crystalCost = checked(baseCost * crystalWeight);
        if (soul <= 0 || soulCost <= 0 || crystalCost <= 0) return null;
        var result = ImmutableArray.CreateBuilder<ItemMaterialCost>();
        result.Add(new(soul, soulCost));
        result.Add(new(CrystalWeights[0], crystalCost));
        if (target.Rarity == 4 && target.MinimumLevel > 65)
        {
            var piece = pieces.Entries.FirstOrDefault(entry => entry.EquipmentId == target.Id);
            int count = checked(baseCost * PieceWeights[column]);
            if (piece is not null && count > 0) result.Add(new(piece.PieceId, count));
        }
        for (int index = 0; index < ParticularCosts.Length; index += 4)
            if (ParticularCosts[index] == target.Rarity && ParticularCosts[index + 1] == target.MinimumLevel)
                result.Add(new(ParticularCosts[index + 2], ParticularCosts[index + 3]));
        return result.ToImmutable();
    }
}

internal sealed class EquipmentTransportCatalog
{
    public ImmutableDictionary<int, EquipmentTransportRule> Rules { get; }
    public EquipmentTransportCatalog(PvfArchive pvf) : this(pvf.ReadText("etc/cargotransportstone.etc")) { }

    internal EquipmentTransportCatalog(string text)
    {
        var rules = ImmutableDictionary.CreateBuilder<int, EquipmentTransportRule>();
        foreach (Match match in Regex.Matches(text, @"\[stone type\](.*?)\[/stone type\]", RegexOptions.Singleline))
        {
            string block = match.Groups[1].Value;
            int type = int.Parse(Regex.Match(block, @"^\s*(\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            int[] Numbers(string tag) => PvfScript.Integers(PvfScript.Section(block, tag));
            ImmutableHashSet<string> Tags(string tag) => Regex.Matches(PvfScript.Section(block, tag), @"`([^`]+)`")
                .Select(row => row.Groups[1].Value).ToImmutableHashSet(StringComparer.Ordinal);
            var grades = Numbers("cargo transport stone grade");
            var rarities = Numbers("cargo transport stone rarity weight");
            if (grades.Length % 3 != 0 || rarities.Length % 2 != 0 || grades.Any(value => value < 0) || rarities.Any(value => value < 0))
                throw new InvalidDataException("跨界石费用表格式无效");
            for (int index = 0; index < grades.Length; index += 3)
                if (grades[index] > grades[index + 1] || index > 0 && grades[index] <= grades[index - 2])
                    throw new InvalidDataException("跨界石等级范围无效");
            var weights = Enumerable.Range(0, rarities.Length / 2).ToImmutableDictionary(index => rarities[index * 2], index => rarities[index * 2 + 1]);
            var rule = new EquipmentTransportRule(type, [.. grades], weights,
                Tags("cargo transport stone enable equip type").Select(tag => ItemCatalog.Slot(tag.Trim('[', ']'))).ToImmutableHashSet(),
                Tags("cargo transport stone enable stackable type"),
                Numbers("cargo transport stone include index").ToImmutableHashSet(),
                Numbers("cargo transport stone except index").ToImmutableHashSet(), Tags("cargo transport stone except etc"),
                [.. Numbers("cargo transport stone soul index")], [.. Numbers("cargo transport stone soul weight")],
                [.. Numbers("cargo transport stone element crystals cost weight")], [.. Numbers("cargo transport stone epic piece cost weight")],
                [.. Numbers("cargo transport stone particular cost")], Numbers("cargo transport stone chronicle cost").SingleOrDefault());
            if (!rules.TryAdd(type, rule)) throw new InvalidDataException("跨界石类型重复");
        }
        if (rules.Count == 0) throw new InvalidDataException("跨界石目录为空");
        Rules = rules.ToImmutable();
    }
}
