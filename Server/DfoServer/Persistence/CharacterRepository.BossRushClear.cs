using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Mailbox;
using Microsoft.Data.Sqlite;
using System.Collections.Immutable;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    uint? CommitBossRushClear(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        Character expected, PreparedDungeonSettlement prepared)
    {
        if (prepared.BossRush is not { } clear) return null;
        if (prepared.Card is not null || prepared.Options is not null || prepared.Grief is not null)
            throw new InvalidDataException("BossRush 通关不能同时提交普通翻牌或悲叹奖励");
        var delivery = clear.Delivery;
        if (clear.Rank > 4 || clear.Score < 0 || clear.Rank != 0 && clear.Item is null ||
            clear.Rank == 0 && clear.Item is not null)
            throw new InvalidDataException("BossRush 冻结段位与奖励不一致");
        if (clear.Item is null)
        {
            if (delivery.Rewards.Length != 0 || delivery.Inventory.Changed || !delivery.MailItems.IsEmpty)
                throw new InvalidDataException("无段位时不能发放 BossRush 物品");
            return null;
        }
        if (delivery.Rewards.Length != 1 || delivery.Rewards[0].Item is not { } core ||
            core.ItemId != clear.Item.Value.ItemId || core.Count != clear.Item.Value.Count)
            throw new InvalidDataException("BossRush 完整核心与冻结奖励不一致");
        ValidateCardDelivery(delivery, delivery.Rewards, 0);
        var current = Require(db, tx, owner.Account, expected.Name);
        if (!SameInventory(current.Inventory, delivery.Inventory))
            throw new InvalidOperationException("BossRush 奖励背包前提已变化");
        if (delivery.Inventory.Changed)
            WriteInventory(db, tx, owner, expected.Id, delivery.Inventory, delivery.Inventory.After);
        uint? mail = null;
        if (!delivery.MailItems.IsEmpty)
        {
            var recipient = new MailboxRecipient(expected.Id, owner.Account, expected.Name, expected.Job, expected.GrowType, current.Level);
            var attachments = delivery.MailItems.Select(item => new Game.Inventory.Models.InventoryItem(0,
                new(Game.Inventory.Models.InventoryListType.Main, 9), item)).ToImmutableArray();
            var message = new MailboxDelivery("boss-rush-clear:" + prepared.EventId.ToString("N"), recipient, "灵魂之冢奖励",
                "背包空间不足，通关物品已通过永久邮件保存。", 0, attachments, delivery.Rewards[0].CreatedAt);
            var (error, id) = DeliverMailInTransaction(db, tx, message);
            if (error != MailboxError.None) throw new InvalidOperationException("BossRush 奖励邮件失败: " + error);
            mail = id;
        }
        return mail;
    }
}
