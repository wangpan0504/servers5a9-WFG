using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Characters;

internal enum LevelUpConsumableKind { Ticket, StatusTable, JumpingTable }

internal sealed record LevelUpTicketSource(InventoryItem Item, InventoryPlan Inventory,
    CharacterGrowthPlan Growth, long UtcSeconds)
{
    public LevelUpTicketDefinition? Definition { get; init; }
    public int GainedLevels { get; init; } = 1;
    public LevelUpByTableDefinition? ByTable { get; init; }
    public byte? ProfessionGrowType { get; init; }
    public CharacterLearningState? ProfessionLearning { get; init; }
    public CharacterLearningState Learning => ProfessionLearning ?? Growth.Learning;
    public LevelUpConsumableKind Kind => ByTable is { } table
        ? table.Jumping ? LevelUpConsumableKind.JumpingTable : LevelUpConsumableKind.StatusTable
        : LevelUpConsumableKind.Ticket;
    public int TemplateId => ByTable?.Item.Id ?? Definition!.TemplateId;
    public int CoolTime => ByTable?.CoolTime ?? Definition!.CoolTime;
    public bool TownOnly => ByTable?.TownOnly ?? Definition!.VillageOnly;
    public bool SeriaRoomOnly => ByTable?.SeriaRoomOnly == true || Definition?.SeriaRoomOnly == true;
    public byte TargetLevel(byte level) => ByTable?.TargetLevel(level) ?? Definition!.TargetLevel(level, GainedLevels);
    public bool Allows(Character character) => (Definition, ByTable) switch
    {
        ({ } ticket, null) => ProfessionGrowType is null && ProfessionLearning is null && ticket.Allows(character.Level, character.Job) && ticket.AllowsRoll(GainedLevels),
        (null, { } table) => table.Allows(character.Level, character.Job, character.GrowType) &&
            (table.Jumping && character.GrowType == 0 && character.Job is not (9 or 10)
                ? ProfessionGrowType is >= 1 and <= 5 && ProfessionLearning is not null
                : ProfessionGrowType is null && ProfessionLearning is null),
        _ => false
    };
    public TimeProvider Clock { get; init; } = TimeProvider.System;
}

internal sealed class LevelUpTicketService(ItemCatalog items, CharacterGrowthCatalog growth,
    TimeProvider? time = null, CharacterCatalog? characters = null, SkillLearningCatalog? skills = null, Func<long,long>? random = null)
{
    public TimeProvider Clock { get; } = time ?? TimeProvider.System;

    public LevelUpTicketSource Prepare(Character expected, InventorySlot position)
    {
        var now = Clock.GetUtcNow();
        var item = RequireSource(expected, position, now);
        var definition = items.LevelUpTicket(item.Core.ItemId);
        if (definition is null || !definition.Allows(expected.Level, expected.Job))
            throw new InvalidDataException("升级券等级、职业或资源资格不符");
        int levels = definition.Roll(random ?? Random.Shared.NextInt64);
        return PrepareGrowth(expected, item, definition.TargetLevel(expected.Level, levels), now) with { Definition = definition, GainedLevels = levels };
    }

    public bool IsTableSource(Character? character, InventorySlot position) => character?.Inventory.Find(position) is { } item &&
        items.LevelUpByTable(item.Core.ItemId) is not null;

    public LevelUpTicketSource PrepareTable(Character expected, InventorySlot position, LevelUpConsumableKind kind, uint selectedGrowType)
    {
        var now = Clock.GetUtcNow();
        var item = RequireSource(expected, position, now);
        var definition = items.LevelUpByTable(item.Core.ItemId);
        if (definition is null || !definition.Allows(expected.Level, expected.Job, expected.GrowType) ||
            kind != (definition.Jumping ? LevelUpConsumableKind.JumpingTable : LevelUpConsumableKind.StatusTable))
            throw new InvalidDataException("等级表道具的职业、区间或使用入口不符");
        var source = PrepareGrowth(expected, item, definition.TargetLevel(expected.Level), now) with { ByTable = definition };
        bool chooseProfession = definition.Jumping && expected.GrowType == 0 && expected.Job is not (9 or 10);
        if (!chooseProfession)
        {
            if (selectedGrowType != 0) throw new InvalidDataException("等级表道具不能修改已转职或特殊职业");
            return source;
        }
        if (selectedGrowType is < 1 or > 5 || characters is null || skills is null ||
            !characters.Get(expected.Job).GrowthOptions.GrowTypes.Any(option => option.Value == selectedGrowType))
            throw new InvalidDataException("成长胶囊需要选择该职业实际存在的转职分支");
        byte target = (byte)selectedGrowType;
        return source with { ProfessionGrowType = target,
            ProfessionLearning = QuestProfessionReward.Prepare(expected, target, source.Growth.TargetLevel, source.Growth.Learning, skills, now) };
    }

    static InventoryItem RequireSource(Character expected, InventorySlot position, DateTimeOffset now)
    {
        var item = expected.Inventory.Find(position);
        if (position.List != InventoryListType.Main || item is null || item.Uid <= 0 || item.Core.Count <= 0 ||
            item.Core.Kind != ItemKind.Consumable || item.Core.EquipmentLockId != 0 || item.IsExpired(now.ToUnixTimeSeconds()) ||
            !ItemSlotBoundService.Allows(expected.Inventory, position, ItemKind.Consumable) || expected.DungeonState is null ||
            expected.Level is < 1 or >= CharacterGrowthCatalog.MaximumSupportedLevel)
            throw new InvalidDataException("升级券实例或槽位无效");
        return item;
    }

    LevelUpTicketSource PrepareGrowth(Character expected, InventoryItem item, byte target, DateTimeOffset now)
    {
        var result = CharacterGrowthPlan.Create(expected, target, growth.GetLevelStartExperience(target), growth);
        if (!result.Success || result.Plan!.GainedExperience > uint.MaxValue)
            throw new InvalidDataException("升级券成长规划失败: " + result.Failure);
        var deletion = InventoryDeleteService.Plan(expected.Inventory, item.Position, item.Core.Value, 1);
        if (!deletion.Accepted) throw new InvalidDataException("升级券不能消费一张");
        return new(item, new(expected.Inventory, deletion.Plan!.After, "level_up_ticket"), result.Plan, now.ToUnixTimeSeconds()) { Clock=Clock };
    }
}
