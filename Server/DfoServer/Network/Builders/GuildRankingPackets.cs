using System.Buffers.Binary;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;

namespace DfoServer.Network.Builders;

internal static class GuildRankingPackets
{
    // 公共分派器028F82A0先消费正文成功字节，排行回调02351B80再读取116B模型。
    public static byte[] Summary(GuildRankingSummary summary)
    {
        byte[] body = new byte[117];
        body[0] = 1;
        var data = body.AsSpan(1);
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
        // FUN_005b9b20：福利取0x30，组队取0x2c，攻坚取0x58；0x28/0x54不在本版九项中显示。
        float[] early = [f.Members, f.WeeklyUv, f.Dungeon, f.War, f.Party, f.Contents];
        float[] late = [f.Adventure, f.Pvp, f.Event, f.Raid, f.Contribution];
        for (int i = 0; i < early.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(data[(first + i * 4)..], early[i]);
        for (int i = 0; i < late.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(data[(second + i * 4)..], late[i]);
    }

    public static byte[] Page(GuildRankingPage page)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        // 公共层先读成功字节；02345830接到成功参数后才读取mode及行数组。
        writer.Write((byte)1); writer.Write(page.Mode); writer.Write(page.Total); writer.Write(checked((ushort)page.Entries.Length));
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
