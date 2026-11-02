namespace DfoServer.Game.Inventory.Models;

// 原礼包奖池完成抽取并冻结库存后记录；持有箱、初始配给或普通库存转移不是取得事实。
internal sealed record BoosterSongAcquisition(ushort Character, long Revision, InventoryItem Source,
    int OpenCount, int DeclaredAmount, DateTimeOffset At)
{
    public const int ItemId = 10308807;
}
