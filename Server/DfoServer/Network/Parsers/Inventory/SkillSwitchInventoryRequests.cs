using System.Buffers.Binary;
using DfoServer.Game.Skills;

namespace DfoServer.Network.Parsers.Inventory;

internal static class SkillSwitchInventoryRequests
{
    static readonly HashSet<byte> ItemLists = [0, 1, 2, 3, 7, 17, 38, 46];

    public static SkillSwitchInventoryRequest Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length != 6)
            throw new InvalidDataException("Buff强化登记请求正文必须为6字节");
        ushort skillId = BinaryPrimitives.ReadUInt16LittleEndian(body);
        byte part = body[2];
        byte list = body[3];
        ushort slot = BinaryPrimitives.ReadUInt16LittleEndian(body[4..]);
        if (part == SkillSwitchInventoryState.SkillSentinelPart)
        {
            if (list != SkillSwitchInventoryState.EmptyListType || slot != SkillSwitchInventoryState.EmptySlot)
                throw new InvalidDataException("Buff强化指定技能哨兵不完整");
            return new(SkillSwitchInventoryRequestKind.SetSkill,
                skillId == ushort.MaxValue ? (ushort)0 : skillId, part, list, slot);
        }
        if (part > SkillSwitchInventoryState.MaximumEquipmentPart)
            throw new InvalidDataException("Buff强化登记部位越界");
        if (list == SkillSwitchInventoryState.EmptyListType || slot == SkillSwitchInventoryState.EmptySlot)
        {
            if (list != SkillSwitchInventoryState.EmptyListType || slot != SkillSwitchInventoryState.EmptySlot)
                throw new InvalidDataException("Buff强化移除哨兵不完整");
            return new(SkillSwitchInventoryRequestKind.RemoveItem, skillId, part, list, slot);
        }
        if (!ItemLists.Contains(list))
            throw new InvalidDataException("Buff强化登记容器无效");
        return new(SkillSwitchInventoryRequestKind.SetItem, skillId, part, list, slot);
    }
}
