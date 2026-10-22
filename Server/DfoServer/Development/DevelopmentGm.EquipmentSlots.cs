using System.Globalization;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;

namespace DfoServer.Development;

// 扩展装备栏（characters.equipment_slot_flags）：只读查询，或按位或开启辅助装备1/魔法石2/耳环16。
// 写入复用背包提交（revision、owner 与 equipment_slot_flags 原值复核），在游戏中的角色拒绝，须先回到选角；
// 光环外观8与宠物幻化32由各自道具开栏，GM 不开放。
internal static partial class DevelopmentGm
{
    const string EquipmentSlotReason = "gm_equipment_slot_flags";
    const string EquipmentSlotUsage = "用法：equip-slots <角色> [位掩码]（位掩码只能由 1 辅助装备、2 魔法石、16 耳环按位或组成，例如 19 全开；8 光环与 32 宠物幻化不开放）";

    static string ExecuteEquipmentSlots(ICharacterRepository repository, AccountOwners owners, GameSession? live, DevelopmentRequest request)
    {
        if (request.Args.Length == 0)
        {
            var current = live is not null ? live.InspectCharacter(request.Name) :
                WithLease(owners, request.Account, lease => LoadByName(repository, lease, request.Name));
            return DescribeEquipmentSlots(current, 0, false);
        }
        if (!int.TryParse(request.Args[0], NumberStyles.None, CultureInfo.InvariantCulture, out int mask) || mask is <= 0 or > 255)
            throw new InvalidDataException(EquipmentSlotUsage);
        if ((mask & ~InventoryMoveService.ExpansionSlotFlags) != 0)
            throw new InvalidDataException(EquipmentSlotUsage);
        byte before = 0;
        var updated = Commit(live, owners, repository, request, true, character =>
        {
            before = character.EquipmentSlotFlags;
            byte after = (byte)(before | mask);
            var inventory = character.Inventory;
            // 已全部开启时不提交、不递增 revision。
            return after == before
                ? new InventoryResult(true, 0, new InventoryPlan(inventory, inventory, EquipmentSlotReason))
                : new InventoryResult(true, 0, new InventoryPlan(inventory, inventory, EquipmentSlotReason, new(character)) { EquipmentSlotFlagsAfter = after });
        }, "扩展装备栏开启被拒绝");
        return DescribeEquipmentSlots(updated, (byte)(updated.EquipmentSlotFlags & ~before), true);
    }

    static string DescribeEquipmentSlots(Character character, byte added, bool write) => Ok(new
    {
        name = character.Name, rev = character.Revision, flags = (int)character.EquipmentSlotFlags, added = (int)added,
        changed = added != 0,
        slots = new
        {
            support = (character.EquipmentSlotFlags & InventoryMoveService.SupportSlotFlag) != 0,
            magicStone = (character.EquipmentSlotFlags & InventoryMoveService.MagicStoneSlotFlag) != 0,
            earring = (character.EquipmentSlotFlags & InventoryMoveService.EarringSlotFlag) != 0,
            aura = (character.EquipmentSlotFlags & InventoryCreatureSkinService.AuraUnlockFlag) != 0,
            creatureSkin = (character.EquipmentSlotFlags & InventoryCreatureSkinService.UnlockFlag) != 0
        },
        note = write ? (added != 0 ? "已提交；客户端重新选择角色后由 Player 包刷新装备栏" : "所选装备栏已全部开启，未修改") : null
    });
}
