using System.Buffers.Binary;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;

namespace DfoServer.Network.Builders;

internal static class GuildRankingPackets
{
    // 正文从第 0 字节起就是原生模型：客户端 FUN_02351B80 原样读取 0x74 字节，
    // 帧头 kind 已由 GameFrame 承载，正文不得再前置标志位。
    public static byte[] Summary(GuildRankingSummary summary)
    {
        byte[] body = new byte[116];
        var data = body.AsSpan();
        Rank(data[4..], summary.Current); Rank(data[12..], summary.Official); Rank(data[20..], summary.Previous);
        Factors(data, summary.Current.Factors, false);
        if (summary.Official is { } official) Factors(data, official.Factors, true);
        return body;
    }

    static void Rank(Span<byte> data, GuildRankingEntry? entry)
    {
        if (entry is null) return;
        BinaryPrimitives.WriteInt32LittleEndian(data, entry.Rank);
        BinaryPrimitives.WriteSingleLittleEndian(data[4..], entry.Factors.Total);
    }

    static void Factors(Span<byte> data, GuildRankingFactors f, bool official)
    {
        int first = official ? 0x34 : 0x1c, second = official ? 0x60 : 0x4c;
        float[] early = [f.Members, f.WeeklyUv, f.Dungeon, f.Contents, f.War, f.Party];
        // 0x50 是公会成员决斗等级(PvP)，0x54 本版不渲染；顺序按显示侧 FUN_005b9b20 分类表排。
        float[] late = [f.Adventure, f.Pvp, f.Raid, f.Event, f.Contribution];
        for (int i = 0; i < early.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(data[(first + i * 4)..], early[i]);
        for (int i = 0; i < late.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(data[(second + i * 4)..], late[i]);
    }

    public static byte[] Page(GuildRankingPage page)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        // 客户端 FUN_02345830 第一口读就是 mode，正文没有前置标志位；
        // 多写一个字节会被当成 mode，count 随后错读成 0x0200，整页行数组全部报废。
        writer.Write(page.Mode); writer.Write(page.Total); writer.Write(checked((ushort)page.Entries.Length));
        foreach (var entry in page.Entries)
        {
            writer.Write(entry.Id); writer.Write(entry.Rank); writer.Write(entry.Factors.Total);
            writer.Write(entry.JoinMode); writer.Write(new byte[3]); writer.Write(entry.Mark);
            // 客户端排行表第一列“公会名”读 20 起的 23B 缓冲，第二列“公会会长名”读 43 起的 31B；
            // 2026-10-28 实机截图证明原顺序把两个名字填反（公会名显示成会长名）。
            Name(writer, entry.Name, 23); Name(writer, entry.Master, 31); writer.Write(entry.Members);
        }
        return stream.ToArray();
    }

    static void Name(BinaryWriter writer, string name, int width)
    {
        byte[] bytes = CharacterName.Encoding.GetBytes(name);
        if (bytes.Length >= width) throw new InvalidDataException("公会排行名称超过原生缓冲");
        writer.Write(bytes); writer.Write(new byte[width - bytes.Length]);
    }
}
