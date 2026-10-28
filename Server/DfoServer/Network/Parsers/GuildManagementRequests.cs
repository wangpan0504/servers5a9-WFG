using System.Buffers.Binary;
using DfoServer.Game.Characters;

namespace DfoServer.Network.Parsers;

internal static class GuildManagementRequests
{
    public static bool TryTarget(ReadOnlySpan<byte> body, bool allowSelf, out byte region, out string name)
    {
        region = 0;
        name = "";
        if (body.Length < 5 || body[0] >= 250 || !TryText(body[1..], 31, false, out name) ||
            !allowSelf && name.Length == 0 || name.Trim() != name) return false;
        region = body[0];
        return true;
    }

    public static bool TryText(ReadOnlySpan<byte> body, int limit, bool multiline, out string text)
    {
        text = "";
        if (body.Length < 4 || body.Length - 4 > limit || BinaryPrimitives.ReadUInt32LittleEndian(body) != body.Length - 4) return false;
        text = CharacterName.Encoding.GetString(body[4..]);
        if (!CharacterName.Encoding.GetBytes(text).AsSpan().SequenceEqual(body[4..]) ||
            text.Any(value => char.IsControl(value) && !(multiline && value is '\r' or '\n' or '\t'))) return false;
        return true;
    }
}
