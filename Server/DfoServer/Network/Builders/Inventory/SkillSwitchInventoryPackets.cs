using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Skills;

namespace DfoServer.Network.Builders.Inventory;

internal static class SkillSwitchInventoryPackets
{
    public static byte[] Ack() => [];

    public static byte[] Delta(
        SkillSwitchInventoryState state,
        InventorySnapshot inventory,
        IEnumerable<byte> equipmentParts)
    {
        state.Validate();
        byte[] parts = equipmentParts.Distinct().Order().ToArray();
        if (parts.Length > byte.MaxValue ||
            parts.Any(part => part > SkillSwitchInventoryState.MaximumEquipmentPart))
            throw new InvalidDataException("Buff强化登记差量部位无效");
        var located = state.Locate(inventory).ToDictionary(item => item.Binding.EquipmentPart);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(checked((byte)parts.Length));
        foreach (byte part in parts)
        {
            if (located.TryGetValue(part, out LocatedSkillSwitchItem item))
            {
                writer.Write((byte)1);
                writer.Write(part);
                writer.Write(item.ListType);
                writer.Write(item.Slot);
            }
            else
            {
                writer.Write((byte)0);
                writer.Write(part);
                writer.Write(SkillSwitchInventoryState.EmptyListType);
                writer.Write(SkillSwitchInventoryState.EmptySlot);
            }
        }
        return stream.ToArray();
    }

    public static byte[] AllData(SkillSwitchInventoryState state, InventorySnapshot inventory)
    {
        state.Validate();
        LocatedSkillSwitchItem[] located = state.Locate(inventory)
            .OrderBy(item => item.Binding.EquipmentPart).ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(state.SkillId);
        writer.Write(checked((byte)located.Length));
        foreach (var item in located)
        {
            writer.Write((byte)1);
            writer.Write(item.Binding.EquipmentPart);
            writer.Write(item.ListType);
            writer.Write(item.Slot);
        }
        return stream.ToArray();
    }

    public static void WriteUserInfo(
        BinaryWriter writer,
        SkillSwitchInventoryState state,
        InventorySnapshot inventory,
        long utcSeconds)
    {
        state.Validate();
        LocatedSkillSwitchItem[] located = state.Locate(inventory)
            .Where(item => UserInfoItemValid(item))
            .OrderBy(item => item.Binding.EquipmentPart).ToArray();
        writer.Write(state.SkillId);
        writer.Write(checked((byte)located.Length));
        foreach (var entry in located)
        {
            writer.Write((byte)0);
            writer.Write(entry.ListType);
            writer.Write(entry.Slot);
            ItemCore core = EffectiveCore(entry.Item, utcSeconds);
            ItemListProtocolWriter.Write(writer, entry.Item.Position.Index, core);
            if (entry.ListType == (byte)InventoryListType.Avatar ||
                entry.ListType == (byte)InventoryListType.Equipment &&
                entry.Binding.EquipmentPart <= 11)
            {
                ItemListProtocolWriter.WriteAvatarSockets(writer, entry.Item.Avatar);
                ItemListProtocolWriter.WriteAvatarColors(writer, entry.Item.Avatar);
            }
        }
    }

    static ItemCore EffectiveCore(InventoryItem item, long utcSeconds)
    {
        if (item.Avatar is not null)
            return item.Core with { Value = item.RemainingSeconds(utcSeconds) };
        if (item.Creature is not null)
            return item.Core with { Marker16 = item.RemainingSeconds(utcSeconds) };
        if (item.Core.IsRental)
            return item.Core with { ExpireTime = item.RemainingSeconds(utcSeconds) };
        return item.Core;
    }

    static bool UserInfoItemValid(LocatedSkillSwitchItem item)
    {
        byte part = item.Binding.EquipmentPart;
        return item.ListType switch
        {
            0 or 2 => true,
            1 => part <= 11 && item.Item.Avatar is not null,
            3 => item.Slot == part && (part > 11 || item.Item.Avatar is not null),
            7 => part is >= 26 and <= 29,
            38 => part == 32,
            _ => false
        };
    }
}
