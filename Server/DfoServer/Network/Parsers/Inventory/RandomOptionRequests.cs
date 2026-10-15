using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class RandomOptionRequests
{
    public static EquipmentRandomOptionChangeRequest Change(ReadOnlySpan<byte> body)
    {
        if (body.Length != 5)
            throw new InvalidDataException("装备随机属性变换请求必须为5字节");
        return new(BinaryPrimitives.ReadUInt16LittleEndian(body), BinaryPrimitives.ReadUInt16LittleEndian(body[2..]), body[4]);
    }

    public static UnsealRandomOptionRequest Read(ReadOnlySpan<byte> body)
    {
        if (body.Length != 4)
            throw new InvalidDataException("魔法封印解封请求必须为4字节");
        short slot = BinaryPrimitives.ReadInt16LittleEndian(body);
        if (slot < 0)
            throw new InvalidDataException("魔法封印目标槽位无效");
        // 第二个u16是客户端背包管理状态；不能当成金币费用、实例UID或固定零。
        return new(slot, BinaryPrimitives.ReadUInt16LittleEndian(body[2..]));
    }
}
