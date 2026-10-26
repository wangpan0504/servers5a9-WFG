using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal static class InventoryGuardianGemService
{
    public const int SocketCount = 4;
    public const int BaseItemId = 89999;

    public static int ItemId(ulong keys, int socket)
    {
        if (socket is < 0 or >= SocketCount)
            throw new ArgumentOutOfRangeException(nameof(socket));
        // 02971B19 将 key 按有符号16位扩展，再加89999；零值是空孔。
        short key = unchecked((short)(keys >> (socket * 16)));
        return key == 0 ? 0 : BaseItemId + key;
    }

    public static InventoryResult Plan(Character character, GuardianGemUseRequest request, ItemCatalog catalog, long utcSeconds, TimeProvider? clock = null)
    {
        var before = character.Inventory;
        if (request.Socket >= SocketCount || request.MedalItemId <= 0 || request.GemItemId <= BaseItemId ||
            request.GemItemId - BaseItemId > short.MaxValue ||
            !ItemSlotBoundService.Allows(before, new(InventoryListType.GuildMedal, request.MaterialSlot), ItemKind.GuardianGem))
            return InventoryResult.Reject(4);
        var target = before.Find(new(InventoryListType.Equipment, ItemSlotBoundService.GuildMedalEquipmentSlot));
        if (target is null || target.Core.Kind != ItemKind.GuildMedal || target.Core.ItemId != request.MedalItemId ||
            target.Core.EquipmentLockId != 0 || target.IsExpired(utcSeconds))
            return InventoryResult.Reject(4);
        var material = before.Find(new(InventoryListType.GuildMedal, request.MaterialSlot));
        if (material is null || material.Core.Kind != ItemKind.GuardianGem || material.Core.ItemId != request.GemItemId ||
            material.Core.Count <= 0 || material.Core.EquipmentLockId != 0 || material.IsExpired(utcSeconds))
            return InventoryResult.Reject(0x12);
        var template = catalog.Get(material.Core.ItemId);
        if (template.Kind != ItemKind.GuardianGem || template.GuardianGemEffects.Count == 0)
            return InventoryResult.Reject(0x12);
        for (int socket = 0; socket < SocketCount; socket++)
        {
            // 覆盖当前孔会销毁旧珠；同属性互斥只检查其余孔，不额外限制品质或角色资格。
            if (socket == request.Socket)
                continue;
            int id = ItemId(target.Core.GuardianGemKeys, socket);
            if (id == 0)
                continue;
            if (id <= BaseItemId)
                return InventoryResult.Reject(4);
            var existing = catalog.Get(id);
            if (existing.Kind != ItemKind.GuardianGem || existing.GuardianGemEffects.Count == 0)
                return InventoryResult.Reject(4);
            if (template.GuardianGemEffects.Overlaps(existing.GuardianGemEffects))
                return InventoryResult.Reject(0x12);
        }
        var consumed = InventoryDeleteService.Plan(before, material.Position, material.Core.Value, 1);
        if (!consumed.Accepted)
            return InventoryResult.Reject(0x12);
        int shift = request.Socket * 16;
        ulong keys = (target.Core.GuardianGemKeys & ~(0xffffUL << shift)) | ((ulong)(request.GemItemId - BaseItemId) << shift);
        var after = consumed.Plan!.After with
        {
            Items = consumed.Plan.After.Items.Replace(target, target with { Core = target.Core with { GuardianGemKeys = keys } })
        };
        after.Validate();
        long? deadline = InventoryAvatarPackageService.EarliestDeadline(target, [material]);
        return new(true, 0, new(before, after, "guardian_gem_use", new(character, deadline, clock)));
    }
}
