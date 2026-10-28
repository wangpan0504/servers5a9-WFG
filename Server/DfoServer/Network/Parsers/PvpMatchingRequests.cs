using System.Buffers.Binary;
using System.Collections.Immutable;

namespace DfoServer.Network.Parsers;

internal sealed record PvpMatchingPair(byte First, byte Second);
internal sealed record PvpMatchingRequest(byte Kind, ushort GameType, ImmutableArray<byte> Parameters,
    ImmutableArray<PvpMatchingPair> Pairs, uint? TrailingValue);
internal sealed record PvpTotalTeamRequest(string Name, ImmutableArray<byte> CharacterIndices);

internal static class PvpMatchingRequests
{
    // 两个已证writer均回传NOTI504安装的原u32标识，不能以空正文代替加载确认。
    public static uint DirectComplete(ReadOnlySpan<byte> body)
    {
        if (body.Length != 4) throw new InvalidDataException("匹配加载确认正文长度错误");
        return BinaryPrimitives.ReadUInt32LittleEndian(body);
    }

    // 00447578为7字节；01FFEDA2通用writer为18字节，末尾u32不能省略成外版14字节。
    // 尚未闭合的选项保留原值；模式准入与角色资格应由业务用例校验。
    public static PvpMatchingRequest Match(ReadOnlySpan<byte> body)
    {
        if (body.Length is not (7 or 18)) throw new InvalidDataException("匹配请求正文长度错误");
        byte kind = body[0];
        ushort gameType = BinaryPrimitives.ReadUInt16LittleEndian(body[1..]);
        if (body.Length == 7)
            return new(kind, gameType, [body[3], body[4], body[5], body[6]], [], null);
        var pairs = ImmutableArray.CreateBuilder<PvpMatchingPair>(4);
        for (int at = 5; at < 13; at += 2) pairs.Add(new(body[at], body[at + 1]));
        return new(kind, gameType, [body[3], body[4], body[13]], pairs.MoveToImmutable(),
            BinaryPrimitives.ReadUInt32LittleEndian(body[14..]));
    }

    // 仅普通/总力入口已核的共同前缀；其他玩法的扩展取消请求不经过本解析器。
    public static (byte Kind, ushort GameType) Cancel(ReadOnlySpan<byte> body)
    {
        if (body.Length != 3) throw new InvalidDataException("PK取消匹配请求正文长度错误");
        return (body[0], BinaryPrimitives.ReadUInt16LittleEndian(body[1..]));
    }

    // 0157DC60先将角色对象映射到客户端列表位置，873发送三个索引，不能当作持久角色槽位。
    public static PvpTotalTeamRequest Team(ReadOnlySpan<byte> body)
    {
        var reader = new SocialRequests.Reader(body);
        string name = Name(ref reader);
        var request = new PvpTotalTeamRequest(name, [reader.Byte(), reader.Byte(), reader.Byte()]);
        reader.End();
        return request;
    }

    public static string CheckName(ReadOnlySpan<byte> body)
    {
        var reader = new SocialRequests.Reader(body);
        string name = Name(ref reader);
        reader.End();
        return name;
    }

    // 0157B3EC按490002850查Main容器，029CBD60返回索引，无命中为FFFF。
    public static (string Name, ushort Slot) Rename(ReadOnlySpan<byte> body)
    {
        var reader = new SocialRequests.Reader(body);
        var request = (Name(ref reader), reader.Word());
        reader.End();
        return request;
    }

    public static byte SelectCharacter(ReadOnlySpan<byte> body)
    {
        if (body.Length != 1) throw new InvalidDataException("总力选人请求正文长度错误");
        return body[0];
    }

    static string Name(ref SocialRequests.Reader reader)
    {
        // 客户端以32个UTF16字符缓冲区接收名称，为末尾NUL留位；CP936字符最多占两字节。
        string name = reader.String(62);
        if (name.Length > 31) throw new InvalidDataException("总力队名超出客户端容量");
        return name;
    }
}
