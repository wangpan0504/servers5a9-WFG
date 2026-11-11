using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Trade;
using DfoServer.GameWorld.Items;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public (Character First, Character Second) CommitPlayerTrade(AccountOwners.Lease firstOwner, AccountOwners.Lease secondOwner,
        Character first, Character second, TradeTicket ticket, TradeSettlementPlan plan, ItemCatalog? items = null) =>
        CommitPlayerTrade(firstOwner, secondOwner, first, second, plan.First, plan.Second, ticket: ticket, items: items);

    public (Character First, Character Second) CommitPlayerTrade(AccountOwners.Lease firstOwner, AccountOwners.Lease secondOwner,
        Character first, Character second, InventoryPlan firstPlan, InventoryPlan secondPlan,
        IReadOnlySet<long>? firstTransfers = null, IReadOnlySet<long>? secondTransfers = null, TradeTicket? ticket = null, ItemCatalog? items = null) =>
        AccountOwners.Lease.ExecuteTogether(firstOwner, secondOwner, () =>
        {
            if (first.Id == second.Id || firstPlan.Reason != "player_trade" || secondPlan.Reason != "player_trade" ||
                (long)firstPlan.Before.Gold + secondPlan.Before.Gold != (long)firstPlan.After.Gold + secondPlan.After.Gold ||
                HasSideEffects(firstPlan) || HasSideEffects(secondPlan) ||
                !firstPlan.After.AvatarPresets.Same(firstPlan.Before.AvatarPresets) ||
                !secondPlan.After.AvatarPresets.Same(secondPlan.Before.AvatarPresets) ||
                !SameInventory(firstPlan.After with { Gold = firstPlan.Before.Gold, Items = firstPlan.Before.Items }, firstPlan) ||
                !SameInventory(secondPlan.After with { Gold = secondPlan.Before.Gold, Items = secondPlan.Before.Items }, secondPlan))
                throw new InvalidDataException("交易资产计划不守恒或包含未支持的物品变更");
            Dictionary<long, InventoryItem> firstIncoming, secondIncoming;
            if (ticket is not null)
            {
                var verified = TradeSettlement.Plan(first, second, ticket, items);
                if (verified is null || !Matches(verified.First, firstPlan) || !Matches(verified.Second, secondPlan))
                    throw new InvalidDataException("交易计划与冻结报价不一致");
                firstTransfers = ticket.FirstOffer.Items.Where(line => line.Count == line.Item.Core.Count).Select(line => line.Item.Uid).ToHashSet();
                secondTransfers = ticket.SecondOffer.Items.Where(line => line.Count == line.Item.Core.Count).Select(line => line.Item.Uid).ToHashSet();
                firstIncoming = ticket.SecondOffer.Items.Where(line => secondTransfers.Contains(line.Item.Uid)).ToDictionary(line => line.Item.Uid, line => line.Item);
                secondIncoming = ticket.FirstOffer.Items.Where(line => firstTransfers.Contains(line.Item.Uid)).ToDictionary(line => line.Item.Uid, line => line.Item);
            }
            else
            {
                firstTransfers ??= new HashSet<long>();
                secondTransfers ??= new HashSet<long>();
                firstIncoming = ValidateTransfers(firstPlan, secondPlan, firstTransfers, secondTransfers);
                secondIncoming = ValidateTransfers(secondPlan, firstPlan, secondTransfers, firstTransfers);
            }
            ValidateInventoryPlan(first, firstPlan);
            ValidateInventoryPlan(second, secondPlan);
            using var connection = database.Open();
            using var write = connection.BeginWrite("CharacterRepository.CommitPlayerTrade");
            using var transaction = write.Transaction;
            var currentFirst = Require(connection, transaction, firstOwner.Account, first.Name);
            var currentSecond = Require(connection, transaction, secondOwner.Account, second.Name);
            ValidateCurrentInventoryPlan(currentFirst, first, firstPlan);
            ValidateCurrentInventoryPlan(currentSecond, second, secondPlan);
            long now = ObservedUnixSeconds();
            if (OutgoingExpired(ticket?.FirstOffer, first, firstTransfers!, now) ||
                OutgoingExpired(ticket?.SecondOffer, second, secondTransfers!, now))
                throw new InventoryQualificationExpiredException();
            TransferItems(connection, transaction, firstOwner.Account, first.Id, secondOwner.Account, second.Id, secondIncoming.Values);
            TransferItems(connection, transaction, secondOwner.Account, second.Id, firstOwner.Account, first.Id, firstIncoming.Values);
            var savedFirst = CommitInventoryInTransaction(connection, transaction, firstOwner, currentFirst, first, firstPlan, firstTransfers, firstIncoming);
            var savedSecond = CommitInventoryInTransaction(connection, transaction, secondOwner, currentSecond, second, secondPlan, secondTransfers, secondIncoming);
            try { transaction.Commit(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                throw new PlayerTradeCommitUnknownException(error);
            }
            return (savedFirst, savedSecond);
        });

    static bool OutgoingExpired(TradeOffer? offer, Character owner, IReadOnlySet<long> transfers, long now)
    {
        if (offer is not null)
            return offer.Items.Any(line => line.Item.IsExpired(now));
        foreach (var item in owner.Inventory.Items)
            if (transfers.Contains(item.Uid) && item.IsExpired(now))
                return true;
        return false;
    }

    static bool Matches(InventoryPlan expected, InventoryPlan actual) =>
        SameInventory(actual.After, new InventoryPlan(expected.After, expected.After, "player_trade")) &&
        actual.After.AvatarPresets.Same(expected.After.AvatarPresets);

    static Dictionary<long, InventoryItem> ValidateTransfers(InventoryPlan own, InventoryPlan other,
        IReadOnlySet<long> outgoing, IReadOnlySet<long> incoming)
    {
        var originals = own.Before.Items.ToDictionary(item => item.Uid);
        var otherOriginals = other.Before.Items.ToDictionary(item => item.Uid);
        var received = new Dictionary<long, InventoryItem>();
        foreach (var uid in outgoing)
            if (!originals.TryGetValue(uid, out var item) || item.Position.List != ItemSlotBoundService.List(item.Core.Kind))
                throw new InvalidDataException("待转移实例不在原背包中");
        foreach (var uid in incoming)
            if (!otherOriginals.TryGetValue(uid, out var item) || item.Position.List != ItemSlotBoundService.List(item.Core.Kind) || !received.TryAdd(uid, item))
                throw new InvalidDataException("待接收实例不在对方背包中");
        if (own.After.Items.Length != originals.Count - outgoing.Count + received.Count)
            throw new InvalidDataException("交易物品数量不守恒");
        foreach (var item in own.After.Items)
        {
            if (originals.TryGetValue(item.Uid, out var original) && !outgoing.Contains(item.Uid))
            {
                if (item != original) throw new InvalidDataException("交易修改了未转移实例");
            }
            else if (!received.TryGetValue(item.Uid, out original) || item.Position.List != ItemSlotBoundService.List(item.Core.Kind) ||
                item != original with { Position = item.Position })
                throw new InvalidDataException("交易接收实例与原实例不一致");
        }
        return received;
    }

    static void TransferItems(SqliteConnection connection, SqliteTransaction transaction, string fromAccount,
        ushort fromCharacter, string toAccount, ushort toCharacter, IEnumerable<InventoryItem> items)
    {
        foreach (var item in items)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$fromAccount", fromAccount);
            command.Parameters.AddWithValue("$fromCharacter", fromCharacter);
            command.Parameters.AddWithValue("$toAccount", toAccount);
            command.Parameters.AddWithValue("$toCharacter", toCharacter);
            command.Parameters.AddWithValue("$uid", item.Uid);
            command.Parameters.AddWithValue("$list", (byte)item.Position.List);
            command.Parameters.AddWithValue("$slot", item.Position.Index);
            command.CommandText = "DELETE FROM character_inventory_slots WHERE account=$fromAccount AND character_id=$fromCharacter AND list_type=$list AND slot_index=$slot AND item_uid=$uid";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("交易物品槽位已变化");
            command.CommandText = "UPDATE character_inventory_items SET account=$toAccount,character_id=$toCharacter,updated_at=CURRENT_TIMESTAMP WHERE account=$fromAccount AND character_id=$fromCharacter AND item_uid=$uid";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("交易物品所有权已变化");
        }
    }

    static bool HasSideEffects(InventoryPlan plan) => plan.PreserveMaterials || !plan.UsedItems.IsEmpty ||
        !plan.AccountRefillClaims.IsEmpty || !plan.DailyItemMails.IsEmpty || !plan.ResetItemIds.IsEmpty ||
        !plan.OverflowItems.IsEmpty || plan.ExpBonus is not null || plan.EquipmentSlotFlagsAfter is not null ||
        plan.UnlockSkillTree || plan.GuildHongbaoPurchase is not null;
}
