using System.Buffers.Binary;
using DfoServer.Game.Raids;

namespace DfoServer.Network.Builders;

internal static class PreyRewardPackets
{
    // 原模型按owner/itemId替换；先合并实际个人产物数量，避免同物品的固定奖和分队奖相互覆盖。
    internal static byte[] Personal(ushort character, IReadOnlyList<RaidRewardOffer> offers)
    {
        if (offers.Any(offer => !PreyGuideRewards.ValidDisplay(offer)))
            throw new InvalidDataException("普雷个人结果字段与冻结产物不一致");
        var items = offers.SelectMany(offer => offer.PreyDisplay).GroupBy(row => row.ItemId)
            .Select(group => group.Last() with { DisplayNumber = checked((ushort)group.Sum(row => row.DisplayNumber!.Value)) }).ToArray();
        return Result(character, 1, 0, items);
    }

    // 普雷0x10C全团展示读取kind3；每行继续使用同一通用16B实际产物合同。
    internal static byte[] Team(IReadOnlyList<(ushort Owner, RaidRewardOffer Offer)> rows)
    {
        var blocks = rows.Select(row => Reward(row.Owner, row.Offer)).ToArray();
        byte[] body = new byte[checked(2 + blocks.Sum(block => block.Length - 2))];
        body[0] = 3; body[1] = checked((byte)blocks.Length);
        int at = 2;
        foreach (var block in blocks)
        {
            block.AsSpan(2).CopyTo(body.AsSpan(at));
            at += block.Length - 2;
        }
        return body;
    }

    // 漩涡 wire3 是完整团队页，数据来自已密封权益；不能用尚未领取的本人 kind0/1 结果代替。
    internal static byte[] FiendwarTeamResults(IReadOnlyList<(ushort Owner, RaidRewardOffer Offer)> rows)
    {
        if (rows.Count is < 1 or > 8 || rows.Select(row => row.Owner).Distinct().Count() != rows.Count ||
            rows.Any(row => row.Offer.Kind != 0)) throw new InvalidDataException("漩涡团队权益表无效");
        var bodies = rows.Select(row => Reward(row.Owner, row.Offer, RaidKind.Fiendwar)).ToArray();
        byte[] body = new byte[checked(2 + bodies.Sum(row => row.Length - 2))];
        body[0] = 3; body[1] = checked((byte)rows.Count);
        int at = 2;
        foreach (var row in bodies) { row.AsSpan(2).CopyTo(body.AsSpan(at)); at += row.Length - 2; }
        return body;
    }

    // 023CA1B0/023CA3D4..023CA56F：普雷走通用600，每个物品16B，不能套安徒恩8B。
    internal static byte[] Reward(ushort character, RaidRewardOffer offer, RaidKind raidType = RaidKind.Prey)
    {
        // 漩涡0..3只改变展示卡位；产物仍逐字段验证原冻结Card0，普雷保留原卡位合同。
        bool valid = raidType == RaidKind.Fiendwar
            ? offer.Card <= 3 && PreyGuideRewards.ValidDisplay(offer with { Card = 0 })
            : raidType == RaidKind.Prey && PreyGuideRewards.ValidDisplay(offer);
        if (!valid) throw new InvalidDataException("团本结果字段与冻结产物不一致");
        return Result(character, offer.Kind, offer.Card, offer.PreyDisplay);
    }

    static byte[] Result(ushort character, byte kind, byte card, IReadOnlyList<LukeRewardResultSpec> items)
    {
        byte[] body = new byte[checked(6 + 16 * items.Count)];
        body[0] = kind; body[1] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), character);
        body[4] = card; body[5] = checked((byte)items.Count);
        int at = 6;
        foreach (var row in items)
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
