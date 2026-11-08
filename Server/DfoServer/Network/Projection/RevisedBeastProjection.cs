using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Game.Inventory.Models;
namespace DfoServer.Network.Projection;

internal static class RevisedBeastProjection
{
    public static async Task<bool> PublishAnneAsync(GameConnectionContext context,ItemCatalog items,GoldLimitCatalog gold,
        EpicPieceCatalog pieces,ServerLog log,DfoServer.GameWorld.Dungeons.DungeonRoomCatalog rooms)
    {
        var receipt=context.Session.SettleRevisedBeastAnne(items,gold,pieces);
        if (receipt is null) return false;
        bool Guard()=>context.Session.CanPublishRevisedBeastStage(receipt);
        if (!receipt.Commit.ChangedSlots.IsEmpty)
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST,InventoryPackets.Delta(receipt.Commit.Character.Inventory,
                InventoryListType.Main,[..receipt.Commit.ChangedSlots]),"revised_beast_anne_inventory",Guard);
        if (receipt.Commit.Mailed && Guard()) context.Session.NotifyMailbox(receipt.Commit.Character.Id);
        if (!receipt.Commit.Rewards.IsEmpty)
            await context.Notify(NotiPacketType.BEAST_DUNGEON_REWARD_STATE,RevisedBeastPackets.StageReward(receipt),
                "revised_beast_anne_drawer",Guard);
        await RevisedBeastCountProjection.RefreshAsync(context,rooms,Guard);
        log.Write("revised_beast_anne_reward",new {Connection=context.Id,receipt.Scene.Entry.DungeonId,receipt.Scene.Entry.Difficulty,
            receipt.Stage.Identity,receipt.Commit.Awarded,receipt.Commit.Duplicate,receipt.Commit.Mailed,receipt.Commit.Rewards});
        return true;
    }

    public static async Task PublishAsync(GameConnectionContext context,ItemCatalog items,GoldLimitCatalog gold,
        EpicPieceCatalog pieces,ServerLog log,DfoServer.GameWorld.Dungeons.DungeonRoomCatalog rooms)
    {
        var receipt=context.Session.SettleRevisedBeast(items,gold,pieces);
        if(receipt is null)
        {
            log.Write("revised_beast_settlement_pending",new {Connection=context.Id,Reason="terminal-or-reward-plan-unavailable"});
            return;
        }
        bool Guard()=>context.Session.CanPublishRevisedBeast(receipt);
        if(!receipt.Commit.ChangedSlots.IsEmpty)
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST,InventoryPackets.Delta(receipt.Commit.Character.Inventory,
                InventoryListType.Main,[..receipt.Commit.ChangedSlots]),"revised_beast_reward_inventory",Guard);
        if(receipt.Commit.Mailed && Guard()) context.Session.NotifyMailbox(receipt.Commit.Character.Id);
        await context.Notify(NotiPacketType.BEAST_DUNGEON_RESULT,RevisedBeastPackets.Result(receipt),"revised_beast_terminal_result",Guard);
        await RevisedBeastCountProjection.RefreshAsync(context,rooms,Guard);
        log.Write("revised_beast_terminal",new {Connection=context.Id,receipt.Scene.Entry.DungeonId,receipt.Scene.Entry.Difficulty,
            receipt.Terminal.Identity,receipt.Terminal.Success,receipt.ElapsedSeconds,receipt.Points,receipt.BestRecords,receipt.Commit.Awarded,receipt.Commit.Duplicate,receipt.Commit.Mailed,
            receipt.Commit.Rewards,receipt.DisplayRewards,receipt.Terminal.Bosses});
    }
}
