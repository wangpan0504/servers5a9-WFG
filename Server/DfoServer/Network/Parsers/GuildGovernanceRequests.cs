using System.Buffers.Binary;
using System.Text;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;

namespace DfoServer.Network.Parsers;

internal static class GuildGovernanceRequests
{
    public static bool TryPermissions(ReadOnlySpan<byte> body, out byte grade, out uint mask)
    {
        grade = 0;
        mask = 0;
        if (body.Length != 5) return false;
        grade = body[0];
        mask = BinaryPrimitives.ReadUInt32LittleEndian(body[1..]);
        return true;
    }

    public static bool TryRoleName(ReadOnlySpan<byte> body, out byte grade, out string name)
    {
        grade = body.IsEmpty ? (byte)0 : body[0];
        name = "";
        return body.Length >= 5 && TryText(body[1..], 23, out name);
    }

    public static bool TrySettings(ReadOnlySpan<byte> body, out GuildSettingsChange? change)
    {
        change = null;
        if (body.Length < 14) return false;
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
        if (length > 255 || length + 14 != body.Length) return false;
        int end = 8 + (int)length;
        if (!TryText(body[4..end], 255, out string promotion) || body[end] > 1) return false;
        change = new(BinaryPrimitives.ReadUInt32LittleEndian(body), promotion, body[end] == 1,
            BinaryPrimitives.ReadUInt32LittleEndian(body[(end + 1)..]), body[end + 5]);
        return true;
    }

    public static bool TryMemberGrade(ReadOnlySpan<byte> body, out byte region, out string name, out byte grade)
    {
        region = body.IsEmpty ? (byte)255 : body[0];
        name = "";
        grade = 0;
        if (body.Length < 7 || region >= 250 || !TryText(body[1..^1], 31, out name) || name.Length == 0) return false;
        grade = body[^1];
        return true;
    }

    static bool TryText(ReadOnlySpan<byte> body, int limit, out string text)
    {
        text = "";
        if (body.Length < 4 || body.Length - 4 > limit ||
            BinaryPrimitives.ReadUInt32LittleEndian(body) != body.Length - 4 || body[4..].Contains((byte)0)) return false;
        try
        {
            text = CharacterName.Encoding.GetString(body[4..]);
            return CharacterName.Encoding.GetBytes(text).AsSpan().SequenceEqual(body[4..]);
        }
        catch (DecoderFallbackException) { return false; }
        catch (EncoderFallbackException) { return false; }
    }
}
