namespace DfoServer.Game.Inventory.Models;

internal sealed record MagicBoxOpenRequest(byte RawListType, InventorySlot BoxSlot, int BoxItemId,
    InventorySlot MaterialSlot, int MaterialItemId, int Count);

internal sealed record MagicBoxGrantedItem(int ItemId, int Count, short Slot, ushort Durability, bool Equipment);

internal sealed record MagicBoxReceipt(int BoxItemId, short BoxSlot, short MaterialSlot, int OpenCount,
    IReadOnlyList<MagicBoxGrantedItem> Rewards, byte RawListType = 0,
    IReadOnlyList<MagicBoxGrantedItem>? DoubleRewards = null, int SeriaLuckBefore = 0, int SeriaLuckAfter = 0,
    IReadOnlyList<MagicBoxGrantedItem>? DisplayRewardRows = null)
{
    public IReadOnlyList<MagicBoxGrantedItem> PrimaryRewards => Rewards;
    public IReadOnlyList<MagicBoxGrantedItem> DoubledRewards => DoubleRewards ?? Array.Empty<MagicBoxGrantedItem>();
    // 赛丽亚十连左栏只显示基础抽取，不能把触发翻倍的那一轮再合并到左栏；
    // 客户端会按相同物品去重并清空重复行。普通单开/非赛丽亚连开没有独立展示列表，回退到 Rewards。
    public IReadOnlyList<MagicBoxGrantedItem> DisplayRewards => DisplayRewardRows ?? Rewards;
}
