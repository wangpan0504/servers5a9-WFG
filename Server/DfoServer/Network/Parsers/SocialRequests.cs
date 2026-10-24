using System.Buffers.Binary;
using System.Text;
using DfoServer.Game.Characters;

namespace DfoServer.Network.Parsers;

internal sealed record SocialRequest(string Name, byte Server = 0, string Message = "", string Memo = "", byte Mode = 0, byte Group = 0);

internal static class SocialRequests
{
    internal ref struct Reader(ReadOnlySpan<byte> body)
    {
        ReadOnlySpan<byte> remaining = body;
        public bool Empty => remaining.IsEmpty;
        public byte Byte()
        {
            if (remaining.IsEmpty) throw new InvalidDataException("社交请求缺少u8");
            byte value = remaining[0]; remaining = remaining[1..]; return value;
        }
        public ushort Word()
        {
            if (remaining.Length < 2) throw new InvalidDataException("社交请求缺少u16");
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(remaining); remaining = remaining[2..]; return value;
        }
        public string String(int maximum)
        {
            if (remaining.Length < 4) throw new InvalidDataException("社交请求缺少字符串长度");
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(remaining); remaining = remaining[4..];
            if (length > maximum || length > remaining.Length) throw new InvalidDataException("社交请求字符串越界");
            string value;
            try { value = CharacterName.Encoding.GetString(remaining[..(int)length]); }
            catch (DecoderFallbackException e) { throw new InvalidDataException("社交请求字符串不是CP936", e); }
            remaining = remaining[(int)length..];
            if (value.Any(char.IsControl)) throw new InvalidDataException("社交请求字符串包含控制字符");
            return value;
        }
        public void End() { if (!Empty) throw new InvalidDataException("社交请求包含额外字段"); }
    }

    public static SocialRequest Character(ReadOnlySpan<byte> body, bool add)
    {
        var reader = new Reader(body);
        if (add) _ = reader.Word(); // actor只是客户端提示，真实身份按名称查库。
        byte server = reader.Byte();
        string name = reader.String(12); reader.End();
        if (!CharacterName.IsValid(name)) throw new InvalidDataException("好友角色名非法");
        return new(name, server);
    }
    public static SocialRequest Blacklist(ReadOnlySpan<byte> body, bool add)
    {
        var reader = new Reader(body);
        string name = reader.String(12);
        byte server = reader.Byte();
        string memo = reader.String(510);
        reader.End();
        if (!CharacterName.IsValid(name)) throw new InvalidDataException("黑名单角色名非法");
        return new(name, server, Memo: memo);
    }
    public static SocialRequest Account(ReadOnlySpan<byte> body, ushort opcode)
    {
        var reader = new Reader(body);
        byte mode = opcode == 1442 ? reader.Byte() : (byte)0;
        string name = reader.String(32);
        string message = opcode == 1439 ? reader.String(80) : "";
        byte group = opcode is 1439 or 1440 ? reader.Byte() : (byte)0;
        string memo = opcode is 1439 or 1440 or 1443 ? reader.String(80) : "";
        if (opcode == 1443) mode = reader.Byte();
        reader.End();
        // 0239EF20 的目标为17/41个UTF16字符的定长数组，必须为结尾NUL留位。
        if (name.Length is 0 or > 16 || message.Length > 40 || memo.Length > 40 || group > 4 ||
            opcode == 1443 && mode > 4 || opcode == 1442 && mode is not (4 or 5))
            throw new InvalidDataException("冒险团好友操作非法");
        return new(name, Message: message, Memo: memo, Mode: mode, Group: group);
    }
    public static SocialRequest BlacklistMemo(ReadOnlySpan<byte> body)
    {
        var reader = new Reader(body);
        string name = reader.String(12);
        string memo = reader.String(510);
        reader.End();
        if (!CharacterName.IsValid(name)) throw new InvalidDataException("黑名单角色名非法");
        return new(name, Memo: memo);
    }
}
