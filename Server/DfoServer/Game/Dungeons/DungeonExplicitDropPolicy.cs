using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

internal sealed record DungeonExplicitDropEntry(int SourceOrdinal, ItemTemplate Template, int Weight);
internal sealed record DungeonExplicitDropChoice(int SourceOrdinal, ItemTemplate Template, int Count);

// 只有完整验证后的池可以进入选择；复制资源行，模板本身为不可变记录，不改写原资源。
internal sealed class DungeonExplicitDropPool
{
    public int MonsterId { get; }
    public string Path { get; }
    public ImmutableArray<DungeonExplicitDropEntry> Entries { get; }

    internal DungeonExplicitDropPool(string resourceSource, DungeonMonsterDropDeclaration declaration,
        Func<int, ItemTemplate> resolveItem)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(resolveItem);
        if (string.IsNullOrWhiteSpace(resourceSource))
            throw new InvalidDataException("掉落资源来源标识格式不完整");
        string expectedPath = declaration.MonsterId switch
        {
            1 => "monster/goblin/goblin.mob",
            2 => "monster/goblin/goblinthrower.mob",
            _ => throw new InvalidDataException("本服显式掉落规则不支持该怪物模板")
        };
        if (declaration.Path != expectedPath || declaration.Items is null)
            throw new InvalidDataException("本服显式掉落规则的怪物路径或声明不匹配");

        DungeonMonsterDropEntry[] source = declaration.Items.ToArray();
        DungeonMonsterDropEntry[] expected = [new(1000, 50), new(1047, 200), new(1004, 100)];
        if (!source.SequenceEqual(expected))
            throw new InvalidDataException("本服显式掉落规则要求完整三行及原始顺序，不过滤未知行");

        var entries = ImmutableArray.CreateBuilder<DungeonExplicitDropEntry>(source.Length);
        int totalWeight = 0;
        for (int ordinal = 0; ordinal < source.Length; ordinal++)
        {
            var row = source[ordinal];
            var template = resolveItem(row.ItemId);
            // waste后的整数只用于核对这三个已批准模板，未解释为使用方式或掉落数量。
            string[] type = template?.StackableType.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];
            string expectedParameter = row.ItemId == 1047 ? "1" : "5";
            if (template is null || template.Id != row.ItemId || template.Kind != ItemKind.Consumable ||
                template.EquipmentSlot != 0 || template.Durability != 0 || template.StackLimit != 1000 ||
                template.Expiration.AbsoluteTime != 0 || template.Expiration.Days != 0 ||
                template.AttachType.Trim().Trim('`').Trim() != "[free]" ||
                type.Length != 2 || type[0] != "[waste]" || type[1] != expectedParameter)
                throw new InvalidDataException("本服显式掉落规则要求匹配的普通永久消耗品模板: " + row.ItemId);
            entries.Add(new(ordinal, template, row.Parameter));
            totalWeight = checked(totalWeight + row.Parameter);
        }
        if (totalWeight != DungeonExplicitDropPolicy.SelectionDenominator)
            throw new InvalidDataException("本服显式掉落规则权重总量不匹配");
        MonsterId = declaration.MonsterId;
        Path = declaration.Path;
        Entries = entries.MoveToImmutable();
    }
}

// 用户批准的本服规则，不表示恢复S5A9原版随机公式。资格、熵、事件冻结与提交归调用方。
internal static class DungeonExplicitDropPolicy
{
    public const string RuleId = "local-3145-explicit-consumables-10pct-v1";
    public const int TriggerDenominator = 10000;
    public const int TriggerThreshold = 1000;
    public const int SelectionDenominator = 350;

    // 仅验证资源池；job0/普通单人Dungeon3/3145、难度及固定普通生成资格由Session准入核对。
    public static DungeonExplicitDropPool CreatePool(string resourceSource, DungeonMonsterDropDeclaration declaration,
        Func<int, ItemTemplate> resolveItem) => new(resourceSource, declaration, resolveItem);

    // 调用方分别提供有界均匀抽样，不使用取模，也不在此重抽或建立随机状态。
    public static DungeonExplicitDropChoice? Select(DungeonExplicitDropPool pool, int triggerRoll, int selectionRoll)
    {
        ArgumentNullException.ThrowIfNull(pool);
        if (triggerRoll is < 0 or >= TriggerDenominator)
            throw new ArgumentOutOfRangeException(nameof(triggerRoll));
        if (selectionRoll is < 0 or >= SelectionDenominator)
            throw new ArgumentOutOfRangeException(nameof(selectionRoll));
        if (triggerRoll >= TriggerThreshold)
            return null;
        int boundary = 0;
        foreach (var entry in pool.Entries)
        {
            boundary += entry.Weight;
            if (selectionRoll < boundary)
                return new(entry.SourceOrdinal, entry.Template, 1);
        }
        throw new InvalidOperationException("已验证的本服显式掉落池缺少选择区间");
    }
}
