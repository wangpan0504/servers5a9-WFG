using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal enum EnhancementPotionMode : byte { Reinforce, Amplify }

// 保留资源整数；本服按十万分母的相对成功加成解释，不宣称是原服公式。
internal sealed record EnhancementPotionDefinition(EnhancementPotionMode Mode, int SuccessBonusWeight)
{
    public ImmutableHashSet<int>? ItemIds { get; init; }
    public bool IsFamilyItem(int itemId) => ItemIds?.Contains(itemId) == true;

    // 四个已收录旧声明与新版490004594有相同的三十分钟自身药效合同；仅在新版1000声明有效时补值。
    internal static ImmutableHashSet<int> LegacyReinforcementItems { get; } =
        ImmutableHashSet.Create(2600025, 2749959, 490012267, 490012268);

    internal static ImmutableDictionary<int, EnhancementPotionDefinition> Load(PvfArchive archive,
        IReadOnlyDictionary<int, string> stackable)
    {
        var result = ImmutableDictionary.CreateBuilder<int, EnhancementPotionDefinition>();
        var legacy = new List<RecoveryConsumableFile>();
        bool equivalent = false;
        foreach (var pair in stackable)
        {
            string path = "stackable/" + pair.Value;
            if (!archive.Contains(path)) continue;
            var tokens = archive.ReadTokens(path);
            // 先看原始类别，避免为每一件无关物品构建完整消耗品模型；重复类别仍由完整解析拒绝。
            bool candidate = LegacyReinforcementItems.Contains(pair.Key);
            for (int at = 0; !candidate && at + 1 < tokens.Length; at++)
                candidate = tokens[at].Type == 3 && archive.ResolveString(tokens[at].Value) == "[stackable type]" &&
                    tokens[at + 1].Type == 6 && archive.ResolveString(tokens[at + 1].Value) is
                        "[item upgrad buff usual upgrade]" or "[item upgrad buff amplify upgrade]";
            if (!candidate) continue;
            var file = RecoveryConsumableFile.Parse(pair.Key, path, tokens, archive.ResolveString);
            if (Read(file) is { } definition)
            {
                result.Add(pair.Key, definition);
                if (pair.Key == 490004594 && definition is { Mode: EnhancementPotionMode.Reinforce, SuccessBonusWeight: 1000 } &&
                    file.StatChangeDuration is { Milliseconds: 1800000 } && file.EffectMaintenance == 7)
                    equivalent = true;
            }
            else if (LegacyReinforcementItems.Contains(pair.Key)) legacy.Add(file);
        }
        foreach (var file in legacy)
            if (Read(file, equivalent) is { } definition) result.Add(file.TemplateId, definition);
        foreach (var family in result.ToArray().GroupBy(pair => pair.Value.Mode))
        {
            var ids = family.Select(pair => pair.Key).ToImmutableHashSet();
            foreach (var pair in family) result[pair.Key] = pair.Value with { ItemIds = ids };
        }
        return result.ToImmutable();
    }

    internal static EnhancementPotionDefinition? Read(RecoveryConsumableFile file, bool legacyEquivalent = false)
    {
        var raw = file.Stackable;
        if (file.TemplateId <= 0 || raw.TemplateId != file.TemplateId || file.Diagnostics.Length != 0 ||
            file.StructureDiagnostics.Length != 0 || file.StatChangeDuration is not { Target: "myself", Milliseconds: > 0 } ||
            file.EffectMaintenance is null or 0 || file.CoolTime < 0 ||
            raw.HasUseActionPacket && raw.DeclaredUseActionPacket != 0 || raw.HasActionType || raw.HasBoosterInfo ||
            raw.HasActionUsablePlace && (raw.DeclaredActionUsablePlaces is not { Count: > 0 } places ||
                places.Any(place => place is not ("[village]" or "[seria room]")))) return null;
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
        // 兼容是明确的本服策略；不能用名称、图标或缺省参数放行其他expert0物品。
        return legacyEquivalent && LegacyReinforcementItems.Contains(file.TemplateId) &&
            raw.TypeDeclaration is { Label: "[expert town potion]", Parameters: [0] } &&
            file.StatChangeDuration.Milliseconds == 1800000 && file.EffectMaintenance == 7
                ? new(EnhancementPotionMode.Reinforce, 1000) : null;
    }
}
