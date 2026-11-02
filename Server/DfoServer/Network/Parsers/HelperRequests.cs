using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace DfoServer.Network.Parsers;

internal sealed record HelperRequest(string Nonce, ushort CharacterId, bool HellRotation, byte Cargo = 0,
    bool Safety = false, bool? DirectPickup = null, byte CapabilityVersion = 1);

internal static class HelperRequests
{
    static ReadOnlySpan<byte> Root => "#95-helper"u8;
    internal static HelperRequest? ParseCapability(ReadOnlySpan<byte> body)
    {
        if (body.Length > 90) return null;
        foreach (byte value in body) if (value is < 0x20 or > 0x7e) return null;
        var fields = Encoding.ASCII.GetString(body).Split(' ');
        if (fields is not [var version, var nonce, var character] || version is not ("95_HELPER_CAPS/1" or "95_HELPER_CAPS/2") ||
            !Identity(nonce, character, out ushort id)) return null;
        return new(nonce, id, false, CapabilityVersion: version == "95_HELPER_CAPS/2" ? (byte)2 : (byte)1);
    }

    static bool Identity(string nonce, string character, out ushort id)
    {
        id = 0;
        return nonce.Length == 32 && nonce.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
            nonce.Any(c => c != '0') && ushort.TryParse(character, NumberStyles.None, CultureInfo.InvariantCulture, out id) &&
            id != 0 && character == id.ToString(CultureInfo.InvariantCulture);
    }
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
        if (fields is not ["#95-helper/1", var nonce, var character, var operation, var argument] ||
            !Identity(nonce, character, out ushort id)) return null;
        return operation switch
        {
            "hell" when argument is "0" or "1" => new(nonce, id, argument == "1"),
            "pickup" when argument is "0" or "1" => new(nonce, id, false, DirectPickup: argument == "1"),
            "safe" when argument is "1" or "2" => new(nonce, id, false, Safety: true),
            "cargo" when argument is "1" or "2" => new(nonce, id, false, argument == "1" ? (byte)1 : (byte)2),
            _ => null
        };
    }
}
