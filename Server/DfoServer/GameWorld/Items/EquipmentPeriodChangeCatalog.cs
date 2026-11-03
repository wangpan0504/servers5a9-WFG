using System.Collections.Frozen;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

// [equipment item period change]的声明正文是成对武器ID：前项是誓约之缚武器，后项是对应的哈林史诗。
// 本类型只做资源装配，不改动任何角色状态。
internal sealed record EquipmentPeriodChangeDefinition(ItemTemplate Item, FrozenDictionary<int, int> Upgrades)
{
    public const string Label = "[equipment item period change]";
    public static readonly ImmutableArray<string> Places = ["[village]", "[seria room]"];

    public ImmutableArray<string> UsePlaces { get; init; } = Places;
    public bool AllowsPlace(string place) => StackableUsePlaces.Allows(UsePlaces, place);

    public bool AllowsSource(int itemId) => Upgrades.ContainsKey(itemId);

    // [need material]是NPC商店卖价，不是使用成本，这里刻意不读取也不扣除。
    public static EquipmentPeriodChangeDefinition? Parse(RecoveryConsumableFile file, ItemTemplate item)
    {
        var raw = file.Stackable;
        var action = StackableActionCatalog.Arguments(raw);
        // 只接完整成对列表：奇数长度或非正ID都视为资源损坏，不做猜测性对齐。
        if (raw.TemplateId != item.Id || item.Kind != ItemKind.Consumable || item.UseForbidden ||
            file.StructureDiagnostics.Length != 0 || raw.TypeDeclaration is null ||
            action is not { Label: Label } declaration || declaration.Parameters.Count == 0 ||
            declaration.Parameters.Count % 2 != 0 ||
            declaration.Parameters.Any(id => id <= 0) ||
            raw.HasMinimumLevel && raw.DeclaredMinimumLevel is null || raw.HasMaximumLevel && raw.DeclaredMaximumLevel is null ||
            raw.HasUsableJobs && raw.DeclaredUsableJobs is not { Count: > 0 } ||
            file.DeclaredFields.Contains("cool time") && file.CoolTime is null or < 0 ||
            file.DeclaredFields.Contains("cooltime group") && file.CoolGroup is null or < 0)
            return null;
        var upgrades = new Dictionary<int, int>(declaration.Parameters.Count / 2);
        for (int index = 0; index < declaration.Parameters.Count; index += 2)
        {
            if (!upgrades.TryAdd(declaration.Parameters[index], declaration.Parameters[index + 1]))
                return null;
        }
        return new(item, upgrades.ToFrozenDictionary()) { UsePlaces = raw.DeclaredActionUsablePlaces?.ToImmutableArray() ?? [] };
    }
}
