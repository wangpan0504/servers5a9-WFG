using System.Collections.Immutable;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Raids;

// 普通团600：首字节写展示+5C，末u8写+41；名称只表示线上位置，不推业务含义。
// 三个未证字段必须显式传入；null是来源缺失，不能由编码器补零。
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
}

internal sealed record AntonRewardDisplayRow(ushort Owner, byte Card, ImmutableArray<AntonRewardDisplayItem> Items);
