using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace DfoServer.Network.Parsers;

internal sealed record HelperRequest(string Nonce, ushort CharacterId, bool HellRotation);

internal static class HelperRequests
{
    static ReadOnlySpan<byte> Root => "#95-helper"u8;
    internal static bool Reserved(ReadOnlySpan<byte> body)
    {
        if (body.Length < ChatRequests.HeaderLength + Root.Length) return false;
        var text = body[ChatRequests.HeaderLength..];
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(body[7..]);
        return text.StartsWith(Root) && (length == Root.Length || text.Length == Root.Length ||
            text[Root.Length] == '/' || text[Root.Length] <= 0x20 || text[Root.Length] == 0x7f);
    }

    internal static HelperRequest? Parse(ReadOnlySpan<byte> body)
    {
        if (!Reserved(body) || body.Length > ChatRequests.HeaderLength + 96 || body[0] != 3 ||
            BinaryPrimitives.ReadUInt32LittleEndian(body[3..]) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(body[7..]) != body.Length - ChatRequests.HeaderLength) return null;
        var raw = body[ChatRequests.HeaderLength..];
        foreach (byte value in raw) if (value is < 0x20 or > 0x7e) return null;
        var fields = Encoding.ASCII.GetString(raw).Split(' ');
        if (fields is not ["#95-helper/1", var nonce, var character, "hell", var enabled] || nonce.Length != 32 ||
            nonce.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) || nonce.All(c => c == '0') ||
            !ushort.TryParse(character, NumberStyles.None, CultureInfo.InvariantCulture, out ushort id) || id == 0 ||
            character != id.ToString(CultureInfo.InvariantCulture) || enabled is not ("0" or "1")) return null;
        return new(nonce, id, enabled == "1");
    }
}
