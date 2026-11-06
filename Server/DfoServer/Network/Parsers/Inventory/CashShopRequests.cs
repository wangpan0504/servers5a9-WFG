using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Network.Parsers.Inventory;

internal static class CashShopRequests
{
    const int HeaderSize = 4;
    const int ItemStride = 15;
    const int MaxItems = 64;

    public static CashShopPurchaseRequest Buy(ReadOnlySpan<byte> body)
    {
        if (body.Length < HeaderSize + ItemStride)
            throw new InvalidDataException("商城购买请求长度不足");
        int count = body[2];
        if (count is < 1 or > MaxItems || body.Length < HeaderSize + count * ItemStride)
            throw new InvalidDataException("商城购买请求商品数量无效");
        var items = new CashShopPurchaseItem[count];
        int offset = HeaderSize;
        for (int index = 0; index < count; index++)
        {
            Require(body, offset, 8);
            uint commodity = BinaryPrimitives.ReadUInt32LittleEndian(body[(offset + 3)..]);
            if (commodity == 0 || commodity > int.MaxValue)
                throw new InvalidDataException("商城商品号无效");
            byte payment = body[offset], attribute = body[offset + 1], type = body[offset + 2];
            byte firstCount = body[offset + 7];
            offset += 8;
            Require(body, offset, firstCount * 5 + 1);
            var avatars = new CeraPackageChoice[firstCount];
            var avatarIds = new HashSet<int>();
            for (int choice = 0; choice < firstCount; choice++, offset += 5)
            {
                int id = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
                if (id <= 0 || !avatarIds.Add(id)) throw new InvalidDataException("商城装扮选择编号无效或重复");
                avatars[choice] = new(id, body[offset + 4]);
            }
            byte secondCount = body[offset++];
            Require(body, offset, secondCount * 8 + 6);
            var packages = new CashShopPackageChoice[secondCount];
            var groups = new HashSet<(int, ushort)>();
            for (int choice = 0; choice < secondCount; choice++, offset += 8)
            {
                int id = BinaryPrimitives.ReadInt32LittleEndian(body[offset..]);
                ushort group = BinaryPrimitives.ReadUInt16LittleEndian(body[(offset + 4)..]);
                ushort selection = BinaryPrimitives.ReadUInt16LittleEndian(body[(offset + 6)..]);
                if (id <= 0 || !groups.Add((id, group))) throw new InvalidDataException("商城礼包选择分组无效或重复");
                packages[choice] = new(id, group, selection);
            }
            items[index] = new((int)commodity, attribute, type, payment, firstCount, secondCount,
                BinaryPrimitives.ReadUInt32LittleEndian(body[offset..]), BinaryPrimitives.ReadUInt16LittleEndian(body[(offset + 4)..]))
                { AvatarChoices = avatars, PackageChoices = packages };
            offset += 6;
        }
        if (offset != body.Length) throw new InvalidDataException("商城购买请求有多余数据");
        return new(body[4], items) { CurrencyMode = body[0], GiftMode = body[1], PcRoom = body[3] };
    }

    static void Require(ReadOnlySpan<byte> body, int offset, int size)
    {
        if (body.Length - offset < size) throw new InvalidDataException("商城购买选择段不完整");
    }
}
