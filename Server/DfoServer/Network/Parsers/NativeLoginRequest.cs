using System.Buffers.Binary;
using System.Text;
using DfoServer.Game.Auth;

namespace DfoServer.Network.Parsers;

internal static class NativeLoginRequest
{
    public static string Precheck(ReadOnlySpan<byte> body)
    {
        string account = Account(ref body);
        Take(ref body, 1);
        if (!body.IsEmpty)
            throw new InvalidDataException("Login precheck width");
        return account;
    }

    public static (string Account, string Digest) Login(ReadOnlySpan<byte> body)
    {
        string account = Account(ref body);
        Take(ref body, 1);
        string digest = Text(ref body, 32);
        if (!LoginCredentials.IsDigest(digest))
            throw new InvalidDataException("Invalid login credential format");
        Take(ref body, 1);
        // 02902CFF/02902D1D 写长度前缀数据；正常预检查链的参数为空，不含可选 16B。
        int length = Length(ref body, 128);
        Take(ref body, length);
        if (Length(ref body, 512) != 512)
            throw new InvalidDataException("Login verification block width");
        Take(ref body, 512 + 4 + 4 + 2);
        int count = Take(ref body, 1)[0];
        Take(ref body, count * 4 + 1);
        if (!body.IsEmpty)
            throw new InvalidDataException("Login trailing bytes");
        return (account, digest);
    }

    static string Account(ref ReadOnlySpan<byte> body)
    {
        string account = Text(ref body, 30);
        if (!LoginCredentials.IsAccount(account))
            throw new InvalidDataException("Invalid login account format");
        return account;
    }

    static string Text(ref ReadOnlySpan<byte> body, int maximum)
    {
        int length = Length(ref body, maximum);
        var bytes = Take(ref body, length);
        foreach (byte value in bytes)
            if (value is < 0x21 or > 0x7e)
                throw new InvalidDataException("Login text must be printable ASCII");
        return Encoding.ASCII.GetString(bytes);
    }

    static int Length(ref ReadOnlySpan<byte> body, int maximum)
    {
        int length = BinaryPrimitives.ReadInt32LittleEndian(Take(ref body, 4));
        if (length < 0 || length > maximum)
            throw new InvalidDataException("Login field bounds");
        return length;
    }

    static ReadOnlySpan<byte> Take(ref ReadOnlySpan<byte> body, int count)
    {
        if (body.Length < count)
            throw new InvalidDataException("Truncated login frame");
        var value = body[..count];
        body = body[count..];
        return value;
    }
}
