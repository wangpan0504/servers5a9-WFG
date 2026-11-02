using System.Buffers.Binary;
using DfoServer.Game.Raids;

namespace DfoServer.Network.Builders;

internal static class LukeRewardPackets
{
    // 023CA1B0普通布局：2+4+16*N；owner来自已提交receipt，不是party slot。
    internal static byte[] Result(LukeRewardResult result)
    {
        var items = result.AdditionalItems.Insert(0, result.Item);
        if (result.Kind is not (0 or 1) || items.Length is < 1 or > 32 || items.Any(item =>
            item.RawCode is null || item.ItemId is not > 0 || item.DisplayNumber is not > 0 ||
            item.RawFlag is null || item.RawTail1 is null || item.EnchantUpgrade is not <= byte.MaxValue))
            throw new InvalidDataException("卢克结果字段不完整");
        byte[] body = new byte[6 + 16 * items.Length];
        body[0] = result.Kind; body[1] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), result.Owner);
        body[4] = result.Card; body[5] = checked((byte)items.Length);
        for (int index = 0; index < items.Length; index++)
        {
            var item = items[index]; int at = 6 + 16 * index;
            body[at] = item.RawCode!.Value;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at + 1), item.ItemId!.Value);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(at + 5), item.DisplayNumber!.Value);
            body[at + 7] = item.RawFlag!.Value;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at + 8), item.RawTail1!.Value);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at + 12), item.EnchantUpgrade!.Value);
        }
        return body;
    }

    // wire3是原生自动揭示的团队页，只复用阶段事务已密封的kind0权益，不另发资产。
    internal static byte[] TeamResults(IReadOnlyList<LukeRewardResult> results)
    {
        if (results.Count > 8 || results.Any(row => row.Kind != 0) ||
            results.Select(row => row.Owner).Distinct().Count() != results.Count)
            throw new InvalidDataException("卢克团队汇总结果无效");
        var rows = results.Select(Result).ToArray();
        byte[] body = new byte[2 + rows.Sum(row => row.Length - 2)];
        body[0] = 3; body[1] = checked((byte)rows.Length);
        int at = 2;
        foreach (var row in rows) { row.AsSpan(2).CopyTo(body.AsSpan(at)); at += row.Length - 2; }
        return body;
    }
}
