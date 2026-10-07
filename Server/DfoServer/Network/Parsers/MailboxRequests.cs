using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using DfoServer.Game.Characters;
using DfoServer.Game.Mailbox;

namespace DfoServer.Network.Parsers;

internal static class MailboxRequests
{
    public static MailboxSendRequest Send(ReadOnlySpan<byte> body, bool multiple)
    {
        var reader = new Reader(body);
        string name = reader.String(29);
        uint gold = reader.U32();
        int count = multiple ? reader.U8() : 1;
        if (count > 10)
            throw new InvalidDataException("邮件附件数量越界");
        var items = ImmutableArray.CreateBuilder<MailboxSendItem>();
        for (int index = 0; index < count; index++)
        {
            byte type = reader.U8();
            short slot = unchecked((short)reader.U16());
            int id = unchecked((int)reader.U32());
            int quantity = unchecked((int)reader.U32());
            if (type != 0 || slot != 0 || id != 0 || quantity != 0)
                items.Add(new(type, slot, id, quantity));
        }
        string text = reader.String(511);
        uint special = reader.U32();
        byte global = reader.U8();
        reader.End();
        return new(name, gold, items.ToImmutable(), text, special, global);
    }

    public static ImmutableArray<uint> Extract(ReadOnlySpan<byte> body)
    {
        var reader = new Reader(body);
        var ids = reader.Ids();
        reader.End();
        return ids;
    }
    public static (ImmutableArray<uint> Ids, ushort State) State(ReadOnlySpan<byte> body)
    {
        var reader = new Reader(body);
        var ids = reader.Ids();
        ushort state = reader.U16();
        reader.End();
        return (ids, state);
    }
    public static (string Name, byte Mode) Query(ReadOnlySpan<byte> body)
    {
        var reader = new Reader(body);
        string name = reader.String(29);
        byte mode = reader.U8();
        reader.End();
        return (name, mode);
    }
    public static void Open(ReadOnlySpan<byte> body)
    {
        if (!body.IsEmpty)
            throw new InvalidDataException("打开邮箱请求应为空");
    }

    ref struct Reader(ReadOnlySpan<byte> body)
    {
        readonly ReadOnlySpan<byte> data = body;
        int position;
        ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > data.Length - position)
                throw new InvalidDataException("邮件正文长度不足");
            var bytes = data.Slice(position, count);
            position += count;
            return bytes;
        }
        public byte U8() => Take(1)[0];
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public string String(int maximum)
        {
            uint count = U32();
            if (count > maximum)
                throw new InvalidDataException("邮件字符串超过客户端缓冲区");
            var bytes = Take((int)count);
            if (bytes.Contains((byte)0))
                throw new InvalidDataException("邮件字符串包含内嵌零");
            try { return CharacterName.Encoding.GetString(bytes); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("邮件字符串编码错误", error); }
        }
        public ImmutableArray<uint> Ids()
        {
            uint count = U32();
            if (count is < 1 or > 100 || count > (data.Length - position) / 4)
                throw new InvalidDataException("邮件对象数量越界");
            var ids = ImmutableArray.CreateBuilder<uint>((int)count);
            var seen = new HashSet<uint>();
            for (int index = 0; index < count; index++)
            {
                uint id = U32();
                if (id == 0 || id >= 0x80000000 || id == MailboxAttachment.ClaimFlag || !seen.Add(id))
                    throw new InvalidDataException("邮件对象无效或重复");
                ids.Add(id);
            }
            return ids.ToImmutable();
        }
        public void End()
        {
            if (position != data.Length)
                throw new InvalidDataException("邮件请求有未消费字节");
        }
    }
}
