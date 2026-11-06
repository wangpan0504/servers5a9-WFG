using System.Collections.Immutable;
using System.Globalization;

namespace PvfLib;

public sealed record EquipmentQuestDropTier(int RequiredPieces, int Percent);

// 装备与套装的原始百分比声明；不决定哪些角色实例当前生效。
public static class EquipmentQuestDropBonusFile
{
    public const string Field = "quest item drop rate bonus";

    public static int Read(PvfDocument document)
    {
        string? value = document.Sections(Field).LastOrDefault();
        if (value is null) return 0;
        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int percent))
            throw new InvalidDataException("任务物品掉落加成必须是一个完整整数");
        return percent;
    }

    public static ImmutableArray<EquipmentQuestDropTier> ReadSet(PvfTokenDocument document)
    {
        var result = ImmutableArray.CreateBuilder<EquipmentQuestDropTier>();
        foreach (var tier in document.Sections.Where(row => row.Name == "piece set ability"))
        {
            var fields = tier.Children.Where(row => row.Name == Field).ToArray();
            if (fields.Length == 0) continue;
            if (!tier.IsBlock || tier.Values is not [{ Type: 0, Value: > 0 } pieces] ||
                fields.Any(field => field.IsBlock || field.Values is not [{ Type: 0 }]))
                throw new InvalidDataException("任务物品掉落套装阶梯的件数或百分比无效");
            result.Add(new(pieces.Value, fields[^1].Values[0].Value));
        }
        if (result.Select(row => row.RequiredPieces).Distinct().Count() != result.Count)
            throw new InvalidDataException("任务物品掉落套装阶梯重复");
        return result.ToImmutable();
    }
}
