using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal readonly record struct SkillPurchaseItem(ushort SkillId, byte Decrease, byte Magnitude);

internal sealed record SkillPurchaseRequest(byte Context, IReadOnlyList<SkillPurchaseItem> Items, byte Mode)
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
