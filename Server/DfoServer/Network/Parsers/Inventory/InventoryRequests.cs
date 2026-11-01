using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Network.Parsers.Inventory;

internal static class InventoryRequests
{
    public static byte SynchronizeItemSpace(ReadOnlySpan<byte> body)
    {
        if (body.Length != 1)
            throw new InvalidDataException("库存同步请求必须是一个字节");
        return body[0];
    }

    public static byte DurabilityLoss(ReadOnlySpan<byte> body)
    {
        if (body.Length != 1)
            throw new InvalidDataException("耐久损耗报告必须是一个穿戴槽字节");
        return body[0];
    }

    public static InventoryDiscardRequest Discard(ReadOnlySpan<byte> body)
    {
        if (body.Length != 12) throw new InvalidDataException("CMD47丢弃请求宽度");
        return new(BinaryPrimitives.ReadUInt16LittleEndian(body), BinaryPrimitives.ReadUInt16LittleEndian(body[2..]), body[4],
            BinaryPrimitives.ReadUInt16LittleEndian(body[5..]), BinaryPrimitives.ReadUInt32LittleEndian(body[7..]), body[11]);
    }

    public static InventorySlot Lock(ReadOnlySpan<byte> body)
    {
        // 036F3A42..036F3B17：五种锁请求均写入 u8 列表与 u16 槽位。
        if (body.Length != 3)
            throw new InvalidDataException("Client物品锁请求宽度");
        return new((InventoryListType)body[0], BinaryPrimitives.ReadInt16LittleEndian(body[1..]));
    }

    public static InventoryMoveRequest Move(ReadOnlySpan<byte> body)
    {
        // 021586E9、029D3826、029DA611 的普通移动/穿脱路径均写28B。
        if (body.Length != 28)
            throw new InvalidDataException("Client移动物品请求宽度");
        return new(new((InventoryListType)body[0], BinaryPrimitives.ReadInt16LittleEndian(body[1..])),
            BinaryPrimitives.ReadInt32LittleEndian(body[3..]), BinaryPrimitives.ReadInt32LittleEndian(body[7..]),
            new((InventoryListType)body[11], BinaryPrimitives.ReadInt16LittleEndian(body[12..])),
            BinaryPrimitives.ReadInt32LittleEndian(body[14..]), BinaryPrimitives.ReadInt32LittleEndian(body[18..]),
            BinaryPrimitives.ReadInt32LittleEndian(body[22..]), body[26], body[27]);
    }

    public static InventorySortRequest Sort(ReadOnlySpan<byte> body)
    {
        var fields = Fields(body);
        if (Scalar(fields, 1, 20) != 20 || fields.Any(field => field.Number is < 1 or > 4))
            throw new InvalidDataException("Client整理消息字段");
        return new((InventoryListType)Byte(Scalar(fields, 2)), (ItemKind)Byte(Scalar(fields, 3)), Byte(Scalar(fields, 4)));
    }

    public static InventoryDeleteRequest Delete(ReadOnlySpan<byte> body)
    {
        // 材料扣除路径在protobuf后另写客户端状态u8，不计入length；普通丢弃没有此尾部。
        byte? clientState = null;
        if (body.Length >= 5 && BinaryPrimitives.ReadUInt32LittleEndian(body) == body.Length - 5)
        {
            clientState = body[^1];
            body = body[..^1];
        }
        var fields = Fields(body);
        if (Scalar(fields, 1, 18) != 18 || fields.Any(field => field.Number is < 1 or > 4))
            throw new InvalidDataException("Client丢弃消息字段");
        var entries = new List<InventoryDeleteEntry>();
        foreach (var field in fields.Where(field => field.Number == 3))
        {
            if (field.Bytes is null)
                throw new InvalidDataException("Client丢弃子消息类型");
            var item = Parse(field.Bytes);
            if (item.Any(value => value.Number is < 1 or > 4) || entries.Count >= 352)
                throw new InvalidDataException("Client丢弃子消息字段");
            int slot = Scalar(item, 2, -1);
            if (slot is < 0 or > short.MaxValue)
                throw new InvalidDataException("Client丢弃槽位");
            entries.Add(new(Byte(Scalar(item, 1)), (short)slot, Scalar(item, 3), Scalar(item, 4)));
        }
        return new((InventoryListType)Byte(Scalar(fields, 2)), entries.ToArray(), Scalar(fields, 4)) { ClientState = clientState };
    }

    sealed record Field(int Number, int Value, byte[]? Bytes = null);
    static List<Field> Fields(ReadOnlySpan<byte> body)
    {
        // 03BDFA20 -> 03BDF7E0: u32字节长度后接protobuf，ACK18/20也采用此长度前缀。
        if (body.Length is < 4 or > 16384 || BinaryPrimitives.ReadUInt32LittleEndian(body) != body.Length - 4)
            throw new InvalidDataException("Client物品protobuf长度");
        return Parse(body[4..]);
    }
    static List<Field> Parse(ReadOnlySpan<byte> message)
    {
        var fields = new List<Field>();
        while (!message.IsEmpty)
        {
            uint tag = Varint(ref message);
            int number = checked((int)(tag >> 3));
            if (number is < 1 or > 4 || fields.Count >= 356)
                throw new InvalidDataException("Client物品protobuf tag");
            if ((tag & 7) == 0)
                fields.Add(new(number, checked((int)Varint(ref message))));
            else if ((tag & 7) == 2)
            {
                int length = checked((int)Varint(ref message));
                if (length > message.Length)
                    throw new InvalidDataException("Client物品protobuf子消息长度");
                fields.Add(new(number, 0, message[..length].ToArray()));
                message = message[length..];
            }
            else
                throw new InvalidDataException("Client物品protobuf wire type");
        }
        return fields;
    }
    static int Scalar(List<Field> fields, int number, int fallback = 0)
    {
        var selected = fields.Where(field => field.Number == number).ToArray();
        if (selected.Length > 1 || selected.Any(field => field.Bytes is not null))
            throw new InvalidDataException("Client物品protobuf标量重复或类型错误");
        return selected.Length == 0 ? fallback : selected[0].Value;
    }
    static byte Byte(int value) => value is >= 0 and <= 255 ? (byte)value : throw new InvalidDataException("Client物品字段越界");
    static uint Varint(ref ReadOnlySpan<byte> input)
    {
        uint value = 0;
        for (int shift = 0; shift <= 28; shift += 7)
        {
            if (input.IsEmpty)
                break;
            byte next = input[0];
            input = input[1..];
            if (shift == 28 && next > 15)
                break;
            value |= (uint)(next & 127) << shift;
            if ((next & 128) == 0)
                return value;
        }
        throw new InvalidDataException("Client物品varint截断或溢出");
    }
}
