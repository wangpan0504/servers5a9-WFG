using System.Text.Json;
using System.Text.Json.Serialization;

namespace DfoServer.Game.Dungeons;

// 资料只证明奖励种类；数量沿本服普通深渊最低档，精炼石概率沿本服稀有补全口径，不冒充原服参数。
internal sealed record PandemoniumRiftMaterialRules
{
    public int FragmentRolls { get; init; } = 5;
    public int FragmentPermille { get; init; } = 1000;
    public int RefinedTerraniumPermille { get; init; } = 10;

    public static PandemoniumRiftMaterialRules Parse(string json)
    {
        var rules = JsonSerializer.Deserialize<PandemoniumRiftMaterialRules>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? throw new InvalidDataException("魔界裂缝材料规则不能为空");
        rules.Validate();
        return rules;
    }

    public void Validate()
    {
        if (FragmentRolls is < 0 or > 100 || FragmentPermille is < 0 or > 1000 ||
            RefinedTerraniumPermille is < 0 or > 1000)
            throw new InvalidDataException("魔界裂缝碎片次数须为0至100，材料千分比须为0至1000");
    }
}
