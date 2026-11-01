using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DfoServer.Game.Inventory.Services;

// 本服材料升级规则；翻牌产物的初始等级不使用本成功率。
internal sealed record MonsterCardUpgradeRules
{
    public const string Source = "monster-card-material-local-v1";
    public ImmutableArray<int> MaterialSuccessPercent { get; init; } = [30, 70, 100];

    public static MonsterCardUpgradeRules Parse(string json)
    {
        var rules = JsonSerializer.Deserialize<MonsterCardUpgradeRules>(json, new JsonSerializerOptions
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? throw new InvalidDataException("卡片材料升级规则不能为空");
        rules.Validate();
        return rules;
    }

    public void Validate()
    {
        if (MaterialSuccessPercent.IsDefault || MaterialSuccessPercent.Length != 3 ||
            MaterialSuccessPercent.Any(value => value is < 0 or > 100) ||
            MaterialSuccessPercent[0] > MaterialSuccessPercent[1] || MaterialSuccessPercent[2] != 100)
            throw new InvalidDataException("卡片升级须提供三个递增百分比，三张材料必须成功");
    }

    public int Chance(int materials)
    {
        if (materials is < 1 or > 3)
            throw new InvalidDataException("卡片升级材料须为一至三张");
        return MaterialSuccessPercent[materials - 1];
    }
}
