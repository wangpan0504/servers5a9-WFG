using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal enum EnhancementPotionMode : byte { Reinforce, Amplify }

// 统一为十万分母的相对成功权重；炼金表万分比在资源装配时换算。
internal sealed record EnhancementPotionDefinition(EnhancementPotionMode Mode, long SuccessBonusWeight)
{
    public const int DefaultAlchemistBonusRate = 1000;

    public ImmutableHashSet<int>? ItemIds { get; init; }
    public bool IsFamilyItem(int itemId) => ItemIds?.Contains(itemId) == true;

    // 编号只补旧式STK未声明的强化身份；明确的新类型及其参数始终优先。
    internal static ImmutableHashSet<int> AlchemistReinforcementItems { get; } =
        ImmutableHashSet.Create(2600025, 2749959, 10006268, 10007343, 490001497, 490002394, 490004594,
            490006368, 490007876, 490007878, 490008465, 490009714, 490009901, 490011297, 490012267,
            490012268, 490702938);

    internal static ImmutableDictionary<int, EnhancementPotionDefinition> Load(PvfArchive archive,
        IReadOnlyDictionary<int, string> stackable, IReadOnlyDictionary<int, int> parameters)
    {
        var result = ImmutableDictionary.CreateBuilder<int, EnhancementPotionDefinition>();
        foreach (var pair in stackable)
        {
            string path = "stackable/" + pair.Value;
            if (!archive.Contains(path)) continue;
            var tokens = archive.ReadTokens(path);
            // 先看原始类别，避免为每一件无关物品构建完整消耗品模型；重复类别仍由完整解析拒绝。
            bool candidate = AlchemistReinforcementItems.Contains(pair.Key);
            for (int at = 0; !candidate && at + 1 < tokens.Length; at++)
                candidate = tokens[at].Type == 3 && archive.ResolveString(tokens[at].Value) == "[stackable type]" &&
                    tokens[at + 1].Type == 6 && archive.ResolveString(tokens[at + 1].Value) is
                        "[item upgrad buff usual upgrade]" or "[item upgrad buff amplify upgrade]";
            if (!candidate) continue;
            var file = RecoveryConsumableFile.Parse(pair.Key, path, tokens, archive.ResolveString);
            if (Read(file, parameters.TryGetValue(pair.Key, out int parameter) ? parameter : null) is { } definition)
                result.Add(pair.Key, definition);
        }
        foreach (var family in result.ToArray().GroupBy(pair => pair.Value.Mode))
        {
            var ids = family.Select(pair => pair.Key).ToImmutableHashSet();
            foreach (var pair in family) result[pair.Key] = pair.Value with { ItemIds = ids };
        }
        return result.ToImmutable();
    }

    internal static EnhancementPotionDefinition? Read(RecoveryConsumableFile file, int? alchemistParameter = null)
    {
        var raw = file.Stackable;
        if (file.TemplateId <= 0 || raw.TemplateId != file.TemplateId || file.Diagnostics.Length != 0 ||
            file.StructureDiagnostics.Length != 0 || file.StatChangeDuration is not { Target: "myself", Milliseconds: > 0 } ||
            file.EffectMaintenance is null or 0 || file.CoolTime < 0 ||
            raw.HasUseActionPacket && raw.DeclaredUseActionPacket != 0 || raw.HasActionType || raw.HasBoosterInfo) return null;
        if (raw.TypeDeclaration is { Parameters: [> 0] } type)
        {
            EnhancementPotionMode? mode = type.Label switch
            {
                "[item upgrad buff usual upgrade]" => EnhancementPotionMode.Reinforce,
                "[item upgrad buff amplify upgrade]" => EnhancementPotionMode.Amplify,
                _ => null
            };
            if (mode is { } value) return new(value, type.Parameters[0]);
        }
        if (!AlchemistReinforcementItems.Contains(file.TemplateId) ||
            raw.TypeDeclaration is not { Label: "[expert town potion]", Parameters: [0] }) return null;
        int rate = alchemistParameter ?? DefaultAlchemistBonusRate;
        // EXJ万分比转为既有十万分比；long保留完整非负int参数，避免乘10溢出。
        return rate >= 0 ? new(EnhancementPotionMode.Reinforce, (long)rate * 10) : null;
    }
}
