using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Characters;

// 官方「我的战斗力值」乘区模型的本服实现（cp-official/v1）。规格与假设见 Docs/模块/官方战斗力.md。
// 战斗力 = 基础属性分 × (1+白字) × (1+黄字) × (1+爆伤) × (1+装备)。
internal sealed record CombatPowerResult(ushort CharacterId, long CharacterRevision, long ObservedAt, bool Partial,
    int Total, int BaseScore, int WhiteBp, int YellowBp, int CritDamageBp, int EquipmentBp, int Grade,
    int MainStat, int Attack, int CritRate, int Element, ImmutableArray<string> Notes)
{
    public string GradeName => CombatPowerScore.GradeNames[Grade];
}

internal static class CombatPowerScore
{
    public const string Profile = "cp-official";
    public const string Version = "v1";
    // 段位：武炼及以下取公开资料中的单职业样本档（阿修罗，2017），极尊/绝世/斗皇取95版本统一门槛（2018测试服报道）。
    public static readonly ImmutableArray<string> GradeNames = ["探索", "开拓", "无畏", "征服", "战绝", "英杰", "武炼", "极尊", "绝世", "斗皇"];
    public static readonly ImmutableArray<int> GradeThresholds = [0, 3500, 9000, 23000, 40000, 65000, 85000, 500000, 750000, 1200000];
    // 装备自身分（百分点×100），按 PVF rarity：普通/高级/稀有/神器/史诗/勇者/传说。
    static readonly int[] RarityBp = [0, 50, 100, 150, 250, 150, 200];
    const int UpgradeBp = 10;        // 每级强化/增幅 +0.1%
    const int SetPieceBp = 100;      // 套装每激活件数 +1%
    const double BaseDivisor = 1.5;  // 将样本角色基础属性分校准到公开样例（8764）量级
    static readonly string[] Elements = ["fire", "water", "dark", "light"];

    public static int GradeOf(long total)
    {
        int grade = 0;
        for (int i = 0; i < GradeThresholds.Length; i++) if (total >= GradeThresholds[i]) grade = i;
        return grade;
    }

    public static CombatPowerResult Calculate(CharacterInvestmentSnapshot snapshot, CharacterStats? levelStats,
        Func<int, ItemTemplate> template, Func<int, ImmutableArray<CombatEffectRow>> effects,
        Func<int, byte, ImmutableArray<CombatEffectRow>> enchant, Func<int, int?> set,
        Func<int, ImmutableArray<CombatSetTier>> setTiers)
    {
        var notes = ImmutableArray.CreateBuilder<string>();
        bool partial = levelStats is null;
        if (levelStats is null) notes.Add("level_stats_unavailable");
        long observed = snapshot.Equipment.ObservedAt;
        var permanent = snapshot.Permanent;
        double strength = ((levelStats?.PhysicalAttack ?? 0) + permanent.Strength) / 10d;
        double intelligence = ((levelStats?.MagicalAttack ?? 0) + permanent.Intelligence) / 10d;
        double physical = 0, magical = 0, separate = 0, physicalCrit = 0, magicalCrit = 0, allElement = 0;
        var element = Elements.ToDictionary(e => e, _ => 0d, StringComparer.Ordinal);
        double equipmentBp = 0, yellowMax = 0, yellowAdd = 0, critMax = 0, critAdd = 0, allAttack = 0;
        double skillAttack = 1;
        var whiteRows = new List<(string Element, double Value)>();

        void Apply(ImmutableArray<CombatEffectRow> rows, string source)
        {
            // 普通声明逐条叠加；仅同一来源的条件阶梯按同名词条取最高档。
            var effective = rows.Where(r => !r.Conditional).Concat(rows.Where(r => r.Conditional)
                .GroupBy(r => (r.Stat, r.Mode, r.Element)).Select(g => g.MaxBy(r => r.Value)!));
            foreach (var row in effective)
            {
                double value = row.Value;
                var (stat, mode, elem) = (row.Stat, row.Mode, row.Element);
                switch (stat)
                {
                    case "physical attack" when mode == "+": strength += value; break;
                    case "magical attack" when mode == "+": intelligence += value; break;
                    case "equipment physical attack" when mode == "+": physical += value; break;
                    case "equipment magical attack" when mode == "+": magical += value; break;
                    case "separate attack" when mode == "+": separate += value; break;
                    case "physical critical hit" when mode == "+": physicalCrit += value; break;
                    case "magical critical hit" when mode == "+": magicalCrit += value; break;
                    case "all elemental attack" when mode == "+": allElement += value; break;
                    case "fire attack" or "water attack" or "dark attack" or "light attack" when mode == "+":
                        element[stat[..stat.IndexOf(' ')]] += value; break;
                    case "unique increase damage": yellowMax = Math.Max(yellowMax, value); break;
                    case "add increase damage": yellowAdd += value; break;
                    case "unique increase critical damage": critMax = Math.Max(critMax, value); break;
                    case "add increase critical damage": critAdd += value; break;
                    case "all attack bonus rate" when mode == "%": allAttack += value; break;
                    case "skill attack bonus rate" when mode == "%": skillAttack *= 1 + value / 100d; break;
                    case "add absolute damage buff": whiteRows.Add((elem, value)); break;
                }
            }
        }

        var setPieces = new Dictionary<int, int>();
        foreach (var item in snapshot.Worn.Where(i => i.Position.List == InventoryListType.Equipment).OrderBy(i => i.Position.Index))
        {
            var core = item.Core;
            if (item.IsExpired(observed)) continue;
            if (core.Kind is not (ItemKind.Equipment or ItemKind.Creature or ItemKind.CreatureEquipment)) continue;
            string source = $"equipment:{item.Position.Index}/item:{core.ItemId}";
            try
            {
                var t = template(core.ItemId);
                if (core.Kind == ItemKind.Equipment)
                {
                    equipmentBp += (t.Rarity is >= 0 and < 7 ? RarityBp[t.Rarity] : 0) + UpgradeBp * core.Upgrade;
                    switch (core.AmplifyType & 0x0f)
                    {
                        case 3 when core.AmplifyValue > 0: strength += core.AmplifyValue; break;
                        case 4 when core.AmplifyValue > 0: intelligence += core.AmplifyValue; break;
                    }
                    if (set(core.ItemId) is { } s)
                        setPieces[s] = setPieces.GetValueOrDefault(s) + 1;
                }
                Apply(effects(core.ItemId), source);
            }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or FileNotFoundException or FormatException or OverflowException)
            {
                partial = true;
                notes.Add(source + "/unavailable");
            }
            if (core.EnchantCardId > 0)
            {
                try { Apply(enchant(core.EnchantCardId, core.EnchantUpgrade), source + "/enchant"); }
                catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or FileNotFoundException or FormatException or OverflowException)
                { partial = true; notes.Add(source + "/enchant_unavailable"); }
            }
        }
        foreach (var (id, count) in setPieces.OrderBy(p => p.Key))
        {
            try
            {
                foreach (var tier in setTiers(id).Where(t => t.Threshold <= count))
                {
                    equipmentBp += (double)SetPieceBp * tier.Threshold;
                    Apply(tier.Rows, $"set:{id}/threshold:{tier.Threshold}");
                }
            }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or FileNotFoundException or FormatException or OverflowException)
            { partial = true; notes.Add($"set:{id}/unavailable"); }
        }

        double main = Math.Max(strength, intelligence);
        double attack = Math.Max(Math.Max(physical, magical), separate);
        double crit = Math.Clamp(3 + Math.Max(physicalCrit, magicalCrit), 0, 100);
        double bestElement = allElement + Elements.Max(e => element[e]);
        double ElementFactor(double strengthValue) => 1.05 + 0.0045 * strengthValue;
        double white = 0;
        foreach (var (elem, value) in whiteRows)
            white += elem == "all" ? value : value * ElementFactor(allElement + element.GetValueOrDefault(elem));
        double yellow = yellowMax + yellowAdd;
        double critDamage = critMax + critAdd;
        double equipment = ((1 + (equipmentBp / 100d + allAttack) / 100d) * skillAttack - 1) * 100;

        double baseScore = (attack + 1) * (1 + main / 250d) * ElementFactor(bestElement) * (1 + 0.5 * crit / 100d) / BaseDivisor;
        double total = baseScore * (1 + white / 100) * (1 + yellow / 100) * (1 + critDamage / 100) * (1 + equipment / 100);
        int Clamp(double value) => (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, int.MaxValue);
        int totalValue = Clamp(total);
        return new(snapshot.Equipment.CharacterId, snapshot.Equipment.CharacterRevision, observed, partial, totalValue,
            Clamp(baseScore), Clamp(white * 100), Clamp(yellow * 100), Clamp(critDamage * 100), Clamp(equipment * 100),
            GradeOf(totalValue), Clamp(main), Clamp(attack), Clamp(crit), Clamp(bestElement), notes.ToImmutable());
    }
}
