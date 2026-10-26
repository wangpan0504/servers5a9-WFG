using System.Buffers.Binary;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;

namespace DfoServer.Network.Parsers;

internal readonly record struct SkillPurchaseItem(ushort SkillId, byte Decrease, byte Magnitude);

internal sealed record SkillPurchaseRequest(byte Context, IReadOnlyList<SkillPurchaseItem> Items, byte Mode)
{
    public uint? BodyChecksum { get; init; }
    public bool? ChecksumMatches { get; init; }
}

internal sealed record SkillSlotChangeRequest(byte Context, byte From, byte To, int ContextIndex, byte Mode)
{
    public uint? BodyChecksum { get; init; }
    public bool? ChecksumMatches { get; init; }
}

// 数字为正文偏移；这里只保留已核对的写宽，不把技能使用记录解释为命中或伤害事实。
internal sealed record SkillUseRequest(byte Unknown0, ushort Unknown1, ushort Unknown3, ushort Unknown5,
    byte Unknown7, ushort Unknown8, IReadOnlyList<byte> RemainingFields)
{
    public uint? BodyChecksum { get; init; }
    public bool? ChecksumMatches { get; init; }
}

internal static class SkillRequests
{
    public static byte ParseTreeChange(ReadOnlySpan<byte> body)
    {
        // 02B69610写入当前方案u8；通用发送前处理可追加4B检查值。
        if (body.Length is not (1 or 5)) throw new InvalidDataException("切换技能方案正文必须为1字节或带4字节检查尾");
        return body[0];
    }

    public static IReadOnlyList<SkillCommandChange> ParseCommands(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty) throw new InvalidDataException("技能指令缺少记录数");
        int count = body[0], offset = 1;
        var records = new SkillCommandChange[count];
        for (int i = 0; i < count; i++)
        {
            if (offset + 3 > body.Length) throw new InvalidDataException("技能指令记录截断");
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(body[offset..]);
            int length = body[offset + 2];
            offset += 3;
            if (offset + length > body.Length) throw new InvalidDataException("技能指令字节截断");
            records[i] = new(id, Convert.ToHexString(body.Slice(offset, length)));
            offset += length;
        }
        if (offset != body.Length) throw new InvalidDataException("技能指令有多余正文");
        return Array.AsReadOnly(records);
    }

    public static (byte Context, IReadOnlyList<SkillCombo> Combos, byte Mode) ParseCombo(ReadOnlySpan<byte> body)
    {
        if (body.Length < 2 || body[1] > 6) throw new InvalidDataException("连招上下文或数量截断");
        byte context = body[0];
        int count = body[1], offset = 2;
        var roots = new SkillCombo[count];
        for (int i = 0; i < count; i++)
        {
            if (offset + 3 > body.Length) throw new InvalidDataException("连招根截断");
            ushort root = BinaryPrimitives.ReadUInt16LittleEndian(body[offset..]);
            int children = body[offset + 2];
            offset += 3;
            if (children > 6 || offset + children * 2 > body.Length) throw new InvalidDataException("连招子技能截断或超限");
            var ids = new ushort[children];
            for (int j = 0; j < children; j++) ids[j] = BinaryPrimitives.ReadUInt16LittleEndian(body[(offset + j * 2)..]);
            offset += children * 2;
            roots[i] = new(root, Array.AsReadOnly(ids));
        }
        if (offset + 1 != body.Length) throw new InvalidDataException("连招正文缺少尾模式或有多余字段");
        return (context, Array.AsReadOnly(roots), body[offset]);
    }
    public static (byte Context, byte Mode) ParseReset(ReadOnlySpan<byte> body)
    {
        if (body.Length != 2)
            throw new InvalidDataException("技能重置正文必须是上下文与模式两个字节");
        return (body[0], body[1]);
    }
    public static SkillSlotChangeRequest ParseSlotChange(ReadOnlySpan<byte> body)
    {
        if (body.Length is not (8 or 12))
            throw new InvalidDataException("技能换槽正文必须为8字节或带4字节检查尾");
        uint? checksum = body.Length == 12 ? BinaryPrimitives.ReadUInt32LittleEndian(body[8..]) : null;
        return new(body[0], body[1], body[2], BinaryPrimitives.ReadInt32LittleEndian(body[3..]), body[7])
        {
            BodyChecksum = checksum,
            ChecksumMatches = checksum.HasValue ? checksum.Value == ClientBodyChecksum.Compute(body[..8]) : null
        };
    }

    public static bool TryParseUse(ReadOnlySpan<byte> body, out SkillUseRequest? request)
    {
        request = null;
        // 02BF2AB9直接写10B；历史完整转储的026DC5E0在发送前追加004280E0的摘要折叠值。
        // 公式已与正常战斗实收及新正常启动进程的生产代码指纹核对；原消费者的拒绝语义仍未知。
        // 这里只报告匹配状态，不新增拒绝规则，也不把摘要当成战斗资格。
        // 两种已知形状分别保留，其他宽度不能截断后借用本布局。
        if (body.Length is not (10 or 14))
            return false;
        uint? checksum = body.Length == 14 ? BinaryPrimitives.ReadUInt32LittleEndian(body[10..]) : null;
        bool? matches = checksum.HasValue ? checksum.Value == ClientBodyChecksum.Compute(body[..10]) : null;
        request = new(body[0], BinaryPrimitives.ReadUInt16LittleEndian(body[1..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[3..]), BinaryPrimitives.ReadUInt16LittleEndian(body[5..]),
            body[7], BinaryPrimitives.ReadUInt16LittleEndian(body[8..]), Array.AsReadOnly(body[10..].ToArray()))
        {
            BodyChecksum = checksum,
            ChecksumMatches = matches
        };
        return true;
    }

    public static SkillPurchaseRequest ParsePurchase(ReadOnlySpan<byte> body)
    {
        // 028A1130 直接写入不含 echo；正式实收另有通用发送前处理追加的4B检查值。
        // 严格区分直接正文和带检查值正文，尾部不能充当mode或技能条目。
        if (body.Length < 3)
            throw new InvalidDataException("技能学习正文缺少上下文、数量或尾模式");
        int count = body[1];
        int requestLength = 3 + count * 4;
        if (body.Length != requestLength && body.Length != requestLength + 4)
            throw new InvalidDataException("技能学习数量与正文长度不匹配");
        uint? checksum = body.Length == requestLength + 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(body[requestLength..]) : null;
        bool? matches = checksum.HasValue ? checksum.Value == ClientBodyChecksum.Compute(body[..requestLength]) : null;
        var items = new SkillPurchaseItem[count];
        for (int i = 0; i < count; i++)
        {
            int at = 2 + i * 4;
            items[i] = new SkillPurchaseItem(BinaryPrimitives.ReadUInt16LittleEndian(body[at..]),
                body[at + 2], body[at + 3]);
        }

        return new SkillPurchaseRequest(body[0], Array.AsReadOnly(items), body[requestLength - 1])
        {
            BodyChecksum = checksum,
            ChecksumMatches = matches
        };
    }
}
