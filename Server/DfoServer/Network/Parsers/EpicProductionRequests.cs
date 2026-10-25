using System.Buffers.Binary;
using System.Collections.Immutable;

namespace DfoServer.Network.Parsers;

internal static class EpicProductionRequests
{
    // 匹配客户端01365C6A：CMD1417只有一个u32选择武器ID；不把它扩展为任意领取请求。
    public static int Start(ReadOnlySpan<byte> body)
    {
        if (body.Length!=4 || BinaryPrimitives.ReadUInt32LittleEndian(body) is 0 or >int.MaxValue)
            throw new InvalidDataException("史诗制作选择请求必须是合法的单个武器ID");
        return BinaryPrimitives.ReadInt32LittleEndian(body);
    }
    // 01368B75：u16行数，逐行u32物品ID/u32实际背包槽；29CBD60返回的是槽，不是数量。
    public static ImmutableArray<(int ItemId,short Slot)> Process(ReadOnlySpan<byte> body)
    {
        if (body.Length<2) throw new InvalidDataException("史诗加工请求缺少材料行数");
        int count=BinaryPrimitives.ReadUInt16LittleEndian(body);
        if (count is <1 or >3 || body.Length!=2+8*count) throw new InvalidDataException("史诗加工材料行数或长度无效");
        var rows=ImmutableArray.CreateBuilder<(int,short)>(count);
        for (int index=0;index<count;index++)
        {
            uint id=BinaryPrimitives.ReadUInt32LittleEndian(body[(2+8*index)..]);
            uint slot=BinaryPrimitives.ReadUInt32LittleEndian(body[(6+8*index)..]);
            if (id is 0 or >int.MaxValue || slot>short.MaxValue) throw new InvalidDataException("史诗加工物品或槽越界");
            rows.Add((checked((int)id),checked((short)slot)));
        }
        if (rows.Select(row=>row.Item2).Distinct().Count()!=count) throw new InvalidDataException("史诗加工材料槽重复");
        return rows.MoveToImmutable();
    }
    // 01360D35/01360D94：完成的背包装备槽u16、组别u8、选项u8；没有随机或收费字段。
    public static (short Slot,byte Group,byte Option) Ability(ReadOnlySpan<byte> body)
    {
        if(body.Length!=4 || BinaryPrimitives.ReadUInt16LittleEndian(body)>short.MaxValue || body[2]>1 || body[3]==0)
            throw new InvalidDataException("史诗属性附加请求的长度、槽位或组别无效");
        return ((short)BinaryPrimitives.ReadUInt16LittleEndian(body),body[2],body[3]);
    }
    // 01369042：u32新武器、u16行数、逐行u32物品ID/u32背包槽。没有客户端数量字段。
    public static (int WeaponId,ImmutableArray<(int ItemId,short Slot)> Materials) Change(ReadOnlySpan<byte> body)
    {
        if (body.Length<6) throw new InvalidDataException("史诗换型请求头不完整");
        int weapon=Start(body[..4]);
        int count=BinaryPrimitives.ReadUInt16LittleEndian(body[4..]);
        if (count is <1 or >32768 || body.Length!=6+8*count)
            throw new InvalidDataException("史诗换型材料行数或长度无效");
        var rows=ImmutableArray.CreateBuilder<(int,short)>(count);
        var ids=new HashSet<int>();var slots=new HashSet<short>();
        for (int index=0;index<count;index++)
        {
            uint id=BinaryPrimitives.ReadUInt32LittleEndian(body[(6+8*index)..]);
            uint slot=BinaryPrimitives.ReadUInt32LittleEndian(body[(10+8*index)..]);
            if (id is 0 or >int.MaxValue || slot>short.MaxValue || !ids.Add((int)id) || !slots.Add((short)slot))
                throw new InvalidDataException("史诗换型材料或槽无效、重复");
            rows.Add(((int)id,(short)slot));
        }
        return (weapon,rows.MoveToImmutable());
    }
    // 01373DAE/01371170/0136DDF0：目标催化剂u32、u16行数、逐行物品ID/槽/数量三个u32。
    public static (int LiquidId,ImmutableArray<(int ItemId,short Slot,int Count)> Materials) Craft(ReadOnlySpan<byte> body)
    {
        if (body.Length<6) throw new InvalidDataException("催化剂制作请求头不完整");
        uint liquid=BinaryPrimitives.ReadUInt32LittleEndian(body);
        int count=BinaryPrimitives.ReadUInt16LittleEndian(body[4..]);
        if (liquid is 0 or >int.MaxValue || count is <1 or >32768 || body.Length!=6+12*count)
            throw new InvalidDataException("催化剂制作目标、行数或长度无效");
        var rows=ImmutableArray.CreateBuilder<(int,short,int)>(count);
        var slots=new HashSet<short>();
        for (int index=0;index<count;index++)
        {
            int offset=6+12*index;
            uint id=BinaryPrimitives.ReadUInt32LittleEndian(body[offset..]);
            uint slot=BinaryPrimitives.ReadUInt32LittleEndian(body[(offset+4)..]);
            uint quantity=BinaryPrimitives.ReadUInt32LittleEndian(body[(offset+8)..]);
            if (id is 0 or >int.MaxValue || slot>short.MaxValue || quantity is 0 or >int.MaxValue || !slots.Add((short)slot))
                throw new InvalidDataException("催化剂制作材料越界或重复");
            rows.Add(((int)id,(short)slot,(int)quantity));
        }
        return ((int)liquid,rows.MoveToImmutable());
    }

}
