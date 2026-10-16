using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Inventory.Services;

internal sealed partial class InventoryRandomOptionService
{
    public InventoryResult ResetQuality(Character character, EquipmentQualityRequest request, DateTimeOffset? utcNow = null)
    {
        var before = character.Inventory;
        var targetSlot = new InventorySlot(InventoryListType.Main, request.TargetSlot);
        var materialSlot = new InventorySlot(InventoryListType.Main, request.MaterialSlot);
        var target = before.Find(targetSlot);
        var material = before.Find(materialSlot);
        long now = (utcNow ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        if (target is null || targetSlot == materialSlot || request.TargetSlot < 0 || request.MaterialSlot < 0 ||
            target.Core.Kind != DfoServer.GameWorld.Items.ItemKind.Equipment ||
            !request.RandomOptions && target.Core.ItemId != request.ItemId || target.IsExpired(now) || target.Core.IsRental)
            return InventoryResult.Reject(19);
        if (target.Core.EquipmentLockId != 0 || material is { Core.EquipmentLockId: not 0 })
            return InventoryResult.Reject(0xD5);
        if (material is null || !material.Core.IsStackable || material.IsExpired(now)) return InventoryResult.Reject(17);
        var box = items.Get(material.Core.ItemId);
        var template = items.Get(target.Core.ItemId);
        if (box.QualityBox is not { } policy || !box.AllowsUse(character.Level, character.Job) || !policy.Allows(template))
            return InventoryResult.Reject(19);
        if (request.RandomOptions && (policy.Fixed || target.Core.Random0.Type == 0 || template.EquipmentSlot == 13))
            return InventoryResult.Reject(19);
        // 尚未确认的变换候选仍归原变换流程，不能调整已停用原词条而留下旧候选。
        if (target.Core.RandomChange.Type != 0) return InventoryResult.Reject(4);
        var core = target.Core;
        var bands = options.GradeBands(template);
        bool adjustOptions = !policy.Fixed && template.EquipmentSlot != 13 && core.Random0.Type != 0;
        if (adjustOptions && bands.IsEmpty) return InventoryResult.Reject(19);
        var paid = InventoryDeleteService.Plan(before, materialSlot, material.Core.Value, 1);
        if (!paid.Accepted) return InventoryResult.Reject(17);
        core = core with { Value = policy.Roll(roll) };
        if (adjustOptions)
            core = core with { Random0 = Adjust(core.Random0), Random1 = Adjust(core.Random1), Random2 = Adjust(core.Random2) };
        var after = paid.Plan!.After;
        after = after with { Items = after.Items.Replace(target, target with { Core = core }) };
        return new(true, 0, new(before, after, "equipment_quality_reset", new(character)));

        RandomOption Adjust(RandomOption option)
        {
            if (!options.CanAdjustGrade(option.Type)) return option;
            var band = bands[Choose(bands.Select((row, at) => (at, row.Weight)).ToArray(), roll)];
            return option with { Value1 = Value(), Value2 = Value() };
            byte Value()
            {
                long value = roll(band.Maximum - band.Minimum);
                if (value < 0 || value >= band.Maximum - band.Minimum) throw new InvalidDataException("魔封品级随机值越界");
                return checked((byte)(band.Minimum + value));
            }
        }
    }
}
