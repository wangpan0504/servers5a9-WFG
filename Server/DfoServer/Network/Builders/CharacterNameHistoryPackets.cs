using DfoServer.Game.Characters;

namespace DfoServer.Network.Builders;

internal static class CharacterNameHistoryPackets
{
    public static byte[] List(IReadOnlyList<CharacterFormerName> names, byte serverIndex)
    {
        if (names.Count > byte.MaxValue) throw new InvalidDataException("曾用名列表超过原生行数上限");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        // 01C062D0：没有目标角色头，窗口已由CMD8/mode5指定；名字沿03BE25F0的u32长度编码。
        writer.Write((byte)names.Count);
        foreach (var row in names)
        {
            if (!CharacterName.IsValid(row.Name)) throw new InvalidDataException("曾用名无效");
            PacketEncoding.WriteString(writer, row.Name);
            writer.Write(serverIndex);
            writer.Write(row.ChangedAt);
        }
        return stream.ToArray();
    }
}
