using DfoServer.Game.Characters;

namespace DfoServer.Network.Builders;

internal static class PacketEncoding
{
    public static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = CharacterName.Encoding.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
}
