using System.Collections.Immutable;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Raids;

// 普通团600：首字节写展示+5C，末u8写+41；名称只表示线上位置，不推业务含义。
// 未指定展示策略的列保持null；编码器不得自行补值。
internal sealed record AntonRewardDisplayItem(byte? RawCode, uint ItemId, ushort DisplayNumber,
    byte? RawFlag, uint? RawTail1, byte EnchantUpgrade)
{
    internal static AntonRewardDisplayItem FromFrozen(InventoryReward grant)
    {
        if (grant.ItemId <= 0 || grant.Count is < 1 or > ushort.MaxValue)
            throw new InvalidDataException("安徒恩600冻结物品超出展示范围");
        return new(null, checked((uint)grant.ItemId), checked((ushort)grant.Count), null, null, grant.EnchantUpgrade);
    }

    internal bool Complete => RawCode.HasValue && RawFlag.HasValue && RawTail1.HasValue;

    // 本服已提交权益的普通展示：code0不触发01C950B0/E0的1/3标记，flag0避开0229F110限制遮罩。
    // tail1只作为原读取器的额外元数据保留；本策略不启用它，不把它解释为数量或稀有度。
    internal static AntonRewardDisplayItem PlainGranted(InventoryReward grant) =>
        FromFrozen(grant) with { RawCode = 0, RawFlag = 0, RawTail1 = 0 };
}

internal sealed record AntonRewardDisplayRow(ushort Owner, byte Card, ImmutableArray<AntonRewardDisplayItem> Items);
