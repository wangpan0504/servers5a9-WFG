using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record TreasureHunterPotionDefinition(int BonusRate, bool FromExj = true)
{
    public const string AlchemistPath = "character/expertjob/alchemist.exj";
    public const int DefaultBonusRate = 5000;

    // 编号只标识效果身份；EXJ逐项覆盖参数，缺项统一使用DefaultBonusRate，不按名称猜倍率。
    internal static ImmutableHashSet<int> FamilyItems { get; } = ImmutableHashSet.Create(
        2600018, 2683806, 2749958, 10002062, 10004241, 10008143, 10099194, 10100278, 10147839,
        490000010, 490001535, 490002439);

    internal static ImmutableDictionary<int, TreasureHunterPotionDefinition> Load(PvfArchive archive,
        IReadOnlyDictionary<int, string> stackable, IReadOnlyDictionary<int, int> parameters)
    {
        var result = ImmutableDictionary.CreateBuilder<int, TreasureHunterPotionDefinition>();
        foreach (int id in FamilyItems)
        {
            if (!stackable.TryGetValue(id, out var path) || !archive.Contains("stackable/" + path)) continue;
            var file = RecoveryConsumableFile.Parse(id, "stackable/" + path,
                archive.ReadTokens("stackable/" + path), archive.ResolveString);
            if (Read(file, parameters.TryGetValue(id, out int value) ? value : null) is { } definition) result.Add(id, definition);
        }
        return result.ToImmutable();
    }

    internal static TreasureHunterPotionDefinition? Read(RecoveryConsumableFile file, int? parameter)
    {
        int value = parameter ?? DefaultBonusRate;
        if (!FamilyItems.Contains(file.TemplateId) || file.Stackable.TemplateId != file.TemplateId || value < 0 ||
            !file.Diagnostics.IsEmpty || !file.StructureDiagnostics.IsEmpty ||
            file.Stackable.TypeDeclaration is not { Label: "[waste]", Parameters: [0] } ||
            file.Stackable.HasActionType || file.Stackable.HasBoosterInfo ||
            file.StatChangeDuration is not { Target: "myself", Milliseconds: > 0 } || file.EffectMaintenance is null or 0)
            return null;
        return new(value, parameter.HasValue);
    }
}
