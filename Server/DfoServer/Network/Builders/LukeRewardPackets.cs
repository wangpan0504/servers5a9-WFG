using System.Buffers.Binary;
using DfoServer.Game.Raids;

namespace DfoServer.Network.Builders;

internal static class LukeRewardPackets
{
    // 023CA1B0普通布局：2+4+16，单行单原箱；owner来自已提交receipt，不是party slot。
    internal static byte[] Result(LukeRewardResult result)
    {
        var item = result.Item;
        if (result.Kind is not (0 or 1) || item.RawCode is not { } code || item.ItemId is not > 0 ||
            item.DisplayNumber is not > 0 || item.RawFlag is not { } flag || item.RawTail1 is not { } tail ||
            item.EnchantUpgrade is not <= byte.MaxValue) throw new InvalidDataException("卢克结果字段不完整");
        byte[] body = new byte[22];
        body[0] = result.Kind; body[1] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), result.Owner);
        body[4] = result.Card; body[5] = 1; body[6] = code;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7), item.ItemId.Value);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(11), item.DisplayNumber.Value);
        body[13] = flag;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(14), tail);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(18), item.EnchantUpgrade.Value);
        return body;
    }
}
