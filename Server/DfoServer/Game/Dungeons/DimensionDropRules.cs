using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DfoServer.Game.Dungeons;

// 绿名材料、普通三色气息、神圣气息概率及领主整组材料均为本人拍板，非原版还原。
internal sealed record DimensionNamedDropRules(int Equip = 100, int Set = 0, int Shards = 7, int Memories = 5,
    int HolyAuraPercent = 1);
internal sealed record DimensionBossDropRules(int Count = 2, int Set = 0,
    int ShardStacks = 3, int ShardStackSize = 7, int MemoryStacks = 5, int MemoryStackSize = 4);
internal sealed record DimensionTierDropRules(DimensionNamedDropRules Named, DimensionBossDropRules Boss);
internal sealed record DimensionDropRules
{
    public string Mode { get; init; } = "server-v2";
    public bool ExtraEntryEquipment { get; init; }
    public string SetRateSource { get; init; } = "table";
    public ImmutableDictionary<int, DimensionTierDropRules> Tiers { get; init; } =
        new Dictionary<int, DimensionTierDropRules>
        {
            [0] = new(new(60, 0, 7, 5, 1), new(2, 0)),
            [1] = new(new(80, 10, 7, 5, 5), new(2, 15)),
            [2] = new(new(100, 15, 7, 5, 10), new(2, 20))
        }.ToImmutableDictionary();
    public static DimensionDropRules Default { get; } = new();
    [JsonIgnore]
    public string Rule => Mode == "compat-v1" ? DimensionRewardPolicy.Rule : "otherverse-rewards-server-v2";

    public static DimensionDropRules Parse(string json)
    {
        var options = new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        var merged = JsonSerializer.SerializeToNode(Default, options)!.AsObject();
        if (JsonNode.Parse(json) is not JsonObject input) throw new InvalidDataException("异界掉落规则必须为JSON对象");
        Merge(merged, input);
        var rules = merged.Deserialize<DimensionDropRules>(options) ?? throw new InvalidDataException("异界掉落规则不能为空");
        rules.Validate();
        return rules;

        static void Merge(JsonObject target, JsonObject input)
        {
            foreach (var row in input)
            {
                if (!target.ContainsKey(row.Key)) throw new InvalidDataException("未知异界掉落字段：" + row.Key);
                if (target[row.Key] is JsonObject nested && row.Value is JsonObject patch) Merge(nested, patch);
                else target[row.Key] = row.Value?.DeepClone();
            }
        }
    }

    public void Validate()
    {
        if (Mode is not ("server-v2" or "compat-v1") || SetRateSource is not ("table" or "card") ||
            Tiers is null || !Tiers.Keys.Order().SequenceEqual([0, 1, 2]) || Tiers.Values.Any(t =>
                t is null || t.Named is null || t.Boss is null ||
                t.Named.Equip is < 0 or > 100 || t.Named.Set is < 0 or > 100 || t.Named.Shards is < 0 or > 100 ||
                t.Named.Memories is < 0 or > 100 || t.Named.HolyAuraPercent is < 0 or > 100 ||
                t.Boss.Count is < 0 or > 4 || t.Boss.Set is < 0 or > 100 || t.Boss.ShardStacks is < 0 or > 100 ||
                t.Boss.ShardStackSize is < 0 or > 1000 || t.Boss.MemoryStacks is < 0 or > 100 ||
                t.Boss.MemoryStackSize is < 0 or > 1000))
            throw new InvalidDataException("异界掉落模式、来源、三档概率或数量无效");
    }
}
