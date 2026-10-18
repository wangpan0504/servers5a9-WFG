using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Skills;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection.Inventory;

internal static class SkillSwitchInventoryProjection
{
    public static async Task PublishCommandAsync(
        GameConnectionContext context,
        SkillSwitchInventoryRequest request,
        SkillSwitchInventoryResult result)
    {
        var receipt = result.Receipt;
        if (receipt is null)
            return;
        bool CanSend() => context.Session.CanPublishSkillSwitchInventory(receipt);
        await context.Reply(
            CmdPacketType.SKILL_SWITCH_INVENTORY,
            SkillSwitchInventoryPackets.Ack(),
            result.Accepted ? "skill_switch_inventory_committed" : "skill_switch_inventory_rejected",
            CanSend);
        byte[] body;
        NotiPacketType opcode;
        if (request.Kind == SkillSwitchInventoryRequestKind.SetSkill)
        {
            opcode = NotiPacketType.SKILL_SWITCH_INVENTORY_ALLDATA;
            body = SkillSwitchInventoryPackets.AllData(
                receipt.Character.SkillSwitchInventory,
                receipt.Character.Inventory);
        }
        else
        {
            opcode = NotiPacketType.SKILL_SWITCH_INVENTORY;
            body = SkillSwitchInventoryPackets.Delta(
                receipt.Character.SkillSwitchInventory,
                receipt.Character.Inventory,
                [request.EquipmentPart]);
        }
        await context.Notify(opcode, body, "skill_switch_inventory_synchronized", CanSend);
    }

    public static async Task RefreshInventoryAsync(
        GameConnectionContext context,
        InventoryCommitReceipt receipt,
        InventorySnapshot previousInventory)
    {
        SkillSwitchInventoryState previous = receipt.PreviousSkillSwitchInventory;
        SkillSwitchInventoryState current = receipt.Character.SkillSwitchInventory;
        bool CanSend() => context.Session.CanPublishInventory(receipt);
        if (previous.SkillId != current.SkillId)
        {
            await context.Notify(
                NotiPacketType.SKILL_SWITCH_INVENTORY_ALLDATA,
                SkillSwitchInventoryPackets.AllData(current, receipt.Character.Inventory),
                "skill_switch_inventory_inventory_reset",
                CanSend);
            return;
        }
        byte[] parts = ChangedParts(previous, previousInventory, current, receipt.Character.Inventory);
        if (parts.Length == 0)
            return;
        await context.Notify(
            NotiPacketType.SKILL_SWITCH_INVENTORY,
            SkillSwitchInventoryPackets.Delta(current, receipt.Character.Inventory, parts),
            "skill_switch_inventory_inventory_delta",
            CanSend);
    }

    public static async Task RefreshLearningAsync(
        GameConnectionContext context,
        SkillLearningReceipt receipt)
    {
        SkillSwitchInventoryState previous = receipt.PreviousSkillSwitchInventory;
        SkillSwitchInventoryState current = receipt.Character.SkillSwitchInventory;
        if (previous.Same(current))
            return;
        bool CanSend() => context.Session.CanPublishLearning(receipt);
        await context.Notify(
            NotiPacketType.SKILL_SWITCH_INVENTORY_ALLDATA,
            SkillSwitchInventoryPackets.AllData(current, receipt.Character.Inventory),
            "skill_switch_inventory_skill_reset",
            CanSend);
    }

    public static async Task RefreshStateAsync(
        GameConnectionContext context,
        SkillSwitchInventoryState previous,
        InventorySnapshot previousInventory,
        Character current,
        Func<bool> canSend,
        string action)
    {
        SkillSwitchInventoryState state = current.SkillSwitchInventory;
        if (previous.SkillId != state.SkillId)
        {
            await context.Notify(
                NotiPacketType.SKILL_SWITCH_INVENTORY_ALLDATA,
                SkillSwitchInventoryPackets.AllData(state, current.Inventory),
                action + "_all_data",
                canSend);
            return;
        }
        byte[] parts = ChangedParts(previous, previousInventory, state, current.Inventory);
        if (parts.Length != 0)
            await context.Notify(
                NotiPacketType.SKILL_SWITCH_INVENTORY,
                SkillSwitchInventoryPackets.Delta(state, current.Inventory, parts),
                action + "_delta",
                canSend);
    }

    static byte[] ChangedParts(
        SkillSwitchInventoryState previous,
        InventorySnapshot previousInventory,
        SkillSwitchInventoryState current,
        InventorySnapshot currentInventory)
    {
        var previousLocated = Locations(previous, previousInventory);
        var currentLocated = Locations(current, currentInventory);
        return previous.Items.Select(item => item.EquipmentPart)
            .Concat(current.Items.Select(item => item.EquipmentPart))
            .Distinct()
            .Where(part =>
                !previousLocated.TryGetValue(part, out ProjectedBinding before) ||
                !currentLocated.TryGetValue(part, out ProjectedBinding after) ||
                before != after)
            .Order()
            .ToArray();
    }

    static Dictionary<byte, ProjectedBinding> Locations(
        SkillSwitchInventoryState state,
        InventorySnapshot inventory)
    {
        var located = state.Locate(inventory)
            .ToDictionary(
                item => item.Binding.EquipmentPart,
                item => (List: item.ListType, item.Slot));
        return state.Items.ToDictionary(
            item => item.EquipmentPart,
            item =>
            {
                var position = located.GetValueOrDefault(item.EquipmentPart);
                return new ProjectedBinding(
                    item.ItemUid,
                    item.ItemId,
                    position.List,
                    position.Slot,
                    located.ContainsKey(item.EquipmentPart));
            });
    }

    readonly record struct ProjectedBinding(
        long ItemUid,
        int ItemId,
        byte List,
        ushort Slot,
        bool Present);
}
