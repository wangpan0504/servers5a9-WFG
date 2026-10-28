using DfoServer.Game.Characters;
using DfoServer.Game.Guild;

namespace DfoServer.Network.Builders;

// 01132E90/01133140的固定行；名字缓冲必须留NUL，不把商城商品号当作到账模板。
internal static class GuildHongbaoPackets
{
    public const ushort OpenEvent = 2378;

    public static byte[] EmptyList(ushort members, uint spentCera = 0)
    {
        byte[] body = new byte[15];
        body[0] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), members);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(3), spentCera);
        // 领取次数和记录数量均为u32；没有已提交的发行、领取记录，两项为0。
        return body;
    }

    public static byte[] EmptyHistory() => [1, 0, 0, 0, 0];

    public static byte[] List(GuildHongbaoList list)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(list.Members);
        writer.Write(list.SpentCera);
        writer.Write(list.Taken);
        writer.Write((uint)list.Rows.Length);
        foreach (var row in list.Rows)
        {
            writer.Write(row.Id);
            writer.Write(row.ItemId);
            Name(writer, row.Sender);
            writer.Write(row.Remaining);
            writer.Write(row.Remaining == 0 ? (byte)1 : (byte)0);
            writer.Write(checked((uint)row.Expires));
        }
        return stream.ToArray();
    }

    public static byte[] History(IReadOnlyList<GuildHongbaoClaim> claims)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write((uint)claims.Count);
        foreach (var claim in claims)
        {
            writer.Write(claim.Level);
            Name(writer, claim.Name);
            writer.Write(claim.Amount);
            writer.Write((byte)0); // 原生36B行最后一字节尚未证实用途。
        }
        return stream.ToArray();
    }

    public static byte[] Mutation(uint second, uint third)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(0u);
        writer.Write(second);
        writer.Write(third);
        return stream.ToArray();
    }

    static void Name(BinaryWriter writer, string name)
    {
        byte[] encoded = CharacterName.Encoding.GetBytes(name);
        if (encoded.Length > 29) throw new InvalidDataException("公会红包姓名超过30B缓冲");
        writer.Write(encoded);
        writer.Write(new byte[30 - encoded.Length]);
    }

    // 查询失败仍消费一个额外u32；不能套用只含标志与错误的3B拒绝。
    public static byte[] Rejected(bool query) => query ? new byte[7] : new byte[3];
}
