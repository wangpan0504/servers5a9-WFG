using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed partial class InventoryRandomOptionService
{
    public InventoryResult Change(Character character, EquipmentRandomOptionChangeRequest request, DateTimeOffset? utcNow = null)
    {
        if (request.Reserved != 0 || request.OptionIndex > 2) return InventoryResult.Reject(19);
        if (request.Slot > short.MaxValue) return InventoryResult.Reject(19);
        var before = character.Inventory;
        var target = before.Find(new(InventoryListType.Main, (short)request.Slot));
        if (target is null || target.Core.Kind != ItemKind.Equipment) return InventoryResult.Reject(19);
        if (target.Core.EquipmentLockId != 0) return InventoryResult.Reject(0xD5);
        var core = target.Core;
        var original = new[] { core.Random0, core.Random1, core.Random2 };
        if (target.IsExpired((utcNow ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds()) || core.IsRental ||
            core.Random0.Type == 0 || original[request.OptionIndex].Type == 0) return InventoryResult.Reject(19);
        if (core.RandomChange.Type != 0 && core.RandomChangedIndex != request.OptionIndex) return InventoryResult.Reject(4);
        var definition = options.ResolveChange(items.Get(core.ItemId));
        if (definition is null) return InventoryResult.Reject(19);
        if (before.Gold < definition.GoldCost) return InventoryResult.Reject(10);
        var changed = GenerateChange(core, request.OptionIndex, definition, roll);
        if (changed is null) return InventoryResult.Reject(4);
        return new(true, 0, new(before, before with
        {
            Gold = before.Gold - definition.GoldCost,
            Items = before.Items.Replace(target, target with { Core = changed })
        }, "equipment_random_option_change", new(character)));
    }

    internal static ItemCore? GenerateChange(ItemCore core, byte index, EquipmentRandomOptionChangeDefinition definition,
        Func<long, long> random)
    {
        var original = new[] { core.Random0, core.Random1, core.Random2 };
        if (index > 2 || original[index].Type == 0 || core.RandomChange.Type != 0 && core.RandomChangedIndex != index) return null;
        var type = definition.Types[Choose(definition.Types.Select((row, at) => (at, row.Weight)).ToArray(), random)];
        var group = type.Groups[Choose(type.Groups.Select((row, at) => (at, row.Weight)).ToArray(), random)];
        var excluded = original.Where((_, at) => at != index).Select(row => row.Type).ToArray();
        var candidates = group.Options.Where(row => !excluded.Contains(row.Type)).ToArray();
        if (candidates.Sum(row => row.Weight) <= 0) return null;
        byte optionType = candidates[Choose(candidates.Select((row, at) => (at, row.Weight)).ToArray(), random)].Type;
        var candidate = new RandomOption(optionType, Value(), Value());
        // 原词条保留；客户端用独立索引停用原词条，再展示候选及其状态。
        return core with { RandomChangedIndex = index, RandomChange = candidate,
            RandomChangeState = checked((byte)((Draw(65) & 63) << 2 | index)) };

        byte Value()
        {
            var ratio = definition.ValueRatios[Choose(definition.ValueRatios.Select((row, at) => (at, (long)row.Weight)).ToArray(), random)];
            int minimum = (int)(type.MinimumValue * (double)ratio.Lower);
            int span = (int)((type.MaximumValue - type.MinimumValue) * (double)ratio.Upper);
            return checked((byte)(minimum + Draw(span + 1)));
        }

        int Draw(int maximum)
        {
            long value = random(maximum);
            if (value < 0 || value >= maximum) throw new InvalidDataException("装备随机属性变换随机值越界");
            return (int)value;
        }
    }
}
