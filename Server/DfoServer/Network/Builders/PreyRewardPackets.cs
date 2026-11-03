using System.Buffers.Binary;
using DfoServer.Game.Raids;

namespace DfoServer.Network.Builders;

internal static class PreyRewardPackets
{
    // 023CA1B0/023CA3D4..023CA56F：普雷走通用600，每个物品16B，不能套安徒恩8B。
    internal static byte[] Reward(ushort character, RaidRewardOffer offer, RaidKind raidType = RaidKind.Prey)
    {
        // 漩涡0..3只改变展示卡位；产物仍逐字段验证原冻结Card0，普雷保留原卡位合同。
        bool valid = raidType == RaidKind.Fiendwar
            ? offer.Card <= 3 && PreyGuideRewards.ValidDisplay(offer with { Card = 0 })
            : raidType == RaidKind.Prey && PreyGuideRewards.ValidDisplay(offer);
        if (!valid) throw new InvalidDataException("团本结果字段与冻结产物不一致");
        byte[] body = new byte[checked(6 + 16 * offer.PreyDisplay.Length)];
        body[0] = offer.Kind; body[1] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), character);
        body[4] = offer.Card; body[5] = checked((byte)offer.PreyDisplay.Length);
        int at = 6;
        foreach (var row in offer.PreyDisplay)
        {
            body[at] = row.RawCode!.Value;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at + 1), row.ItemId!.Value);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(at + 5), row.DisplayNumber!.Value);
            body[at + 7] = row.RawFlag!.Value;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at + 8), row.RawTail1!.Value);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at + 12), row.EnchantUpgrade!.Value);
            at += 16;
        }
        return body;
    }
}
