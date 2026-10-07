using DfoServer.Game.Characters;
using System.Buffers.Binary;
using System.Text;

namespace DfoServer.Network.Parsers;

internal sealed record CreateCharacterRequest(byte Job, string Name, byte[] Tail);
internal static class CharacterRequests
{
    public static bool IsRoster(ReadOnlySpan<byte> body) => body.SequenceEqual<byte>([255, 255, 2]);
    public static ushort Select(ReadOnlySpan<byte> body)
    {
        if (body.Length != 2)
            throw new InvalidDataException("Selection body width");
        return BinaryPrimitives.ReadUInt16LittleEndian(body);
    }

    public static string CheckName(ReadOnlySpan<byte> body)
    {
        string name = Name(body, 0);
        if (body.Length != CharacterName.Encoding.GetByteCount(name) + 4)
            throw new InvalidDataException("Name check tail");
        return name;
    }

    public static string Name(ReadOnlySpan<byte> body, int at)
    {
        if (body.Length < at + 4)
            throw new InvalidDataException("Name length absent");
        int n = BinaryPrimitives.ReadInt32LittleEndian(body[at..]);
        if (n is < 1 or > 12 || body.Length < at + 4 + n)
            throw new InvalidDataException("Name bounds");
        string name;
        try
        {
            name = CharacterName.Encoding.GetString(body.Slice(at + 4, n));
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("Invalid CP936 name", ex);
        }

        if (!CharacterName.IsValid(name))
            throw new InvalidDataException("Invalid name");
        return name;
    }

    public static CreateCharacterRequest ParseCreate(ReadOnlySpan<byte> body)
    {
        string name = Name(body, 1);
        int width = CharacterName.Encoding.GetByteCount(name);
        // 普通创建的公共尾部固定；目前仅 job0 实收过 tail[8]=1 的变体。
        if (body.Length != 17 + width || body[0] > 15 || !(body[(5 + width)..].SequenceEqual(new byte[] { 0, 0, 0, 0, 0, 0, 255, 0, 0, 0, 0, 0 }) || body[0] == 0 && body[(5 + width)..].SequenceEqual(new byte[] { 0, 0, 0, 0, 0, 0, 255, 0, 1, 0, 0, 0 })))
            throw new InvalidDataException("Unsupported ordinary Client job or creation tail");
        return new(body[0], name, body[(5 + width)..].ToArray());
    }
}
