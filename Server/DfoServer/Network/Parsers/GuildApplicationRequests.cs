using System.Buffers.Binary;
using DfoServer.Game.Guild;

namespace DfoServer.Network.Parsers;

internal static class GuildApplicationRequests
{
    public static GuildSearchFilter Search(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty) throw new InvalidDataException("Missing guild search mode");
        GuildSearchFilter filter;
        if (body[0] == 2)
        {
            if (body.Length != 6) throw new InvalidDataException("Invalid guild tag search width");
            filter = new(2, Tags: BinaryPrimitives.ReadUInt32LittleEndian(body[1..]), Page: body[5]);
        }
        else
            filter = new(body[0], GuildRequests.Text(body[1..]).Trim());
        if (!filter.IsValid) throw new InvalidDataException("Invalid guild search selection");
        return filter;
    }

    public static (string Name, string Message) Apply(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4) throw new InvalidDataException("Missing guild application name");
        int length = BinaryPrimitives.ReadInt32LittleEndian(body);
        if (length < 1 || length > body.Length - 4) throw new InvalidDataException("Invalid guild application name width");
        string name = GuildRequests.Text(body[..(length + 4)]).Trim();
        string message = body.Length == length + 4 ? "" : GuildRequests.Text(body[(length + 4)..]).Trim();
        if (name.Length == 0 || !GuildApplications.ValidMessage(message))
            throw new InvalidDataException("Invalid guild application text");
        return (name, message);
    }

    public static uint Cancel(ReadOnlySpan<byte> body) => body.Length == 4
        ? BinaryPrimitives.ReadUInt32LittleEndian(body) : throw new InvalidDataException("Invalid guild cancellation width");

    public static (ushort CharacterId, byte Region) Decision(ReadOnlySpan<byte> body, bool approve)
    {
        if (body.Length != (approve ? 5 : 8)) throw new InvalidDataException("Invalid guild decision width");
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(body);
        uint region = approve ? body[4] : BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
        if (id is 0 or > ushort.MaxValue || region >= 250) throw new InvalidDataException("Invalid guild decision identity");
        return ((ushort)id, (byte)region);
    }
}
