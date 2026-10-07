using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal readonly record struct SkillPurchaseItem(ushort SkillId, byte Decrease, byte Magnitude);

internal sealed record SkillPurchaseRequest(byte Context, IReadOnlyList<SkillPurchaseItem> Items, byte Mode);

// 数字为正文偏移；这里只保留已核对的写宽，不把技能使用记录解释为命中或伤害事实。
internal sealed record SkillUseRequest(byte Unknown0, ushort Unknown1, ushort Unknown3, ushort Unknown5,
    byte Unknown7, ushort Unknown8, IReadOnlyList<byte> RemainingFields);

internal static class SkillRequests
{
    public static bool TryParseUse(ReadOnlySpan<byte> body, out SkillUseRequest? request)
    {
        request = null;
        // 02BF2AB9 的直接写入为10B；匹配ARAD正常战斗实收为14B，末4B的生产及意义仍待确认。
        // 两种已知形状分别保留，其他宽度不能截断后借用本布局。
        if (body.Length is not (10 or 14))
            return false;
        request = new(body[0], BinaryPrimitives.ReadUInt16LittleEndian(body[1..]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[3..]), BinaryPrimitives.ReadUInt16LittleEndian(body[5..]),
            body[7], BinaryPrimitives.ReadUInt16LittleEndian(body[8..]), Array.AsReadOnly(body[10..].ToArray()));
        return true;
    }

    public static SkillPurchaseRequest ParsePurchase(ReadOnlySpan<byte> body)
    {
        // 028A1130 发送的正文不含 echo；未知业务取值保持原样，由业务层另行校验。
        if (body.Length < 3)
            throw new InvalidDataException("技能学习正文缺少上下文、数量或尾模式");
        int count = body[1];
        if (body.Length != 3 + count * 4)
            throw new InvalidDataException("技能学习数量与正文长度不匹配");
        var items = new SkillPurchaseItem[count];
        for (int i = 0; i < count; i++)
        {
            int at = 2 + i * 4;
            items[i] = new SkillPurchaseItem(BinaryPrimitives.ReadUInt16LittleEndian(body[at..]),
                body[at + 2], body[at + 3]);
        }

        return new SkillPurchaseRequest(body[0], Array.AsReadOnly(items), body[^1]);
    }
}
