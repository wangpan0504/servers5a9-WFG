using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Raids;

namespace DfoServer.Network.Builders;

internal static class AntonRewardPackets
{
    // 来源门控不能由旧8B Display中的合成code/flag绕过；正式冻结资产仅能提供三列。
    internal static bool TryFrozenResult(byte kind, ushort owner, byte card, IReadOnlyList<InventoryReward> grants,
        out byte[] body, out string missing)
    {
        var items = grants.Select(AntonRewardDisplayItem.FromFrozen).ToImmutableArray();
        missing = "RawCode,RawFlag,RawTail1";
        if (items.Length == 0 || items.Any(item => !item.Complete))
        {
            body = Choices(kind);
            return false;
        }
        body = Result(kind, [new(owner, card, items)]);
        missing = "";
        return true;
    }

    internal static byte[] Choices(byte kind) => kind is 0 or 1 ? [kind, 0]
        : throw new InvalidDataException("安徒恩600类别无效");

    // 023CA1B0普通20人团：2+Σ(4+16M)，仅供完整显式字段编码；有值不自动证明来源合法。
    internal static byte[] Result(byte kind, IReadOnlyList<AntonRewardDisplayRow> rows)
    {
        if (kind is not (0 or 1) || rows.Count > byte.MaxValue || rows.Any(row =>
            row.Card > 3 || row.Items.IsDefaultOrEmpty || row.Items.Length > byte.MaxValue ||
            row.Items.Any(item => !item.Complete || item.ItemId == 0 || item.DisplayNumber == 0)))
            throw new InvalidDataException("安徒恩600字段缺证或超出范围");
        byte[] body = new byte[checked(2 + rows.Sum(row => checked(4 + 16 * row.Items.Length)))];
        body[0] = kind; body[1] = checked((byte)rows.Count);
        int at = 2;
        foreach (var row in rows)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(at), row.Owner);
            body[at + 2] = row.Card; body[at + 3] = checked((byte)row.Items.Length); at += 4;
            foreach (var item in row.Items)
            {
                body[at] = item.RawCode!.Value;
                BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at + 1), item.ItemId);
                BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(at + 5), item.DisplayNumber);
                body[at + 7] = item.RawFlag!.Value;
                BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at + 8), item.RawTail1!.Value);
                BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at + 12), item.EnchantUpgrade);
                at += 16;
            }
        }
        return body;
    }
}
