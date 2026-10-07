using System.Buffers.Binary;

namespace DfoServer.Network.Parsers;

internal readonly record struct SkillPurchaseItem(ushort SkillId, byte Decrease, byte Magnitude);

internal sealed record SkillPurchaseRequest(byte Context, IReadOnlyList<SkillPurchaseItem> Items, byte Mode);

internal static class SkillRequests
{
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
