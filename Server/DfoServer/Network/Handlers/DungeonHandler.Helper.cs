using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Currency;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class DungeonHandler
{
    async Task PublishHelperHellRotationAsync(GameConnectionContext context, FormalDungeonReceipt? receipt)
    {
        log.Write("helper_hell_rotation", new { Connection = context.Id, Accepted = receipt is not null,
            receipt?.Entry.DungeonId, receipt?.Entry.BasisLevel, receipt?.Entry.Difficulty,
            RejectReason = receipt is null ? context.Session.LastFormalDungeonRejection : null });
        if (receipt is null)
        {
            await context.Reply(CmdPacketType.MOVE_MAP, DungeonPackets.MoveRejected(), "helper_hell_move_rejected");
            return;
        }
        bool Guard() => context.Session.CanPublishFormalDungeon(receipt);
        await context.Reply(CmdPacketType.MOVE_MAP, DungeonPackets.MoveAccepted(), "helper_hell_move_ack", Guard);
        // 完整新副本加载，不用旧房缓存或伪装旧房复活。37加载完成后才沿原链安装角色与扣房间疲劳。
        await context.Notify(NotiPacketType.ENTER_SELECT_DUNGEON, DungeonPackets.SelectionNotice(0), "helper_hell_scene_transition", Guard);
        await context.Notify(NotiPacketType.DUNGEON_INFO, DungeonPackets.Information(receipt.Entry, receipt.Snapshot.BossPosition),
            "helper_hell_dungeon_information", Guard);
        await MonsterAiProjection.SendAsync(context, receipt, "helper_hell_monster_ai", Guard);
        await context.Notify(NotiPacketType.START_MAP, DungeonPackets.StartMap(receipt.Entry, receipt.Monsters, receipt.MapSeed,
            passiveItems: receipt.PassiveItems), "helper_hell_start_map", Guard);
        if (DungeonPackets.HellPartyMonsterInfo(receipt.Monsters) is { } hellInfo)
            await context.Notify(NotiPacketType.HELL_PARTY_MONSTER_INFO, hellInfo, "helper_hell_monster_info", Guard);
        await PublishEntryAssetsAsync(context, receipt, Guard);
    }

    async Task PublishEntryAssetsAsync(GameConnectionContext context, FormalDungeonReceipt receipt, Func<bool> Guard)
    {
        if (!receipt.FatigueEnergySlots.IsEmpty)
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(receipt.Character.Inventory,
                InventoryListType.Main, [.. receipt.FatigueEnergySlots]), "entry_quick_slot_energy", Guard);
        if (!receipt.HellConsumedSlots.IsDefaultOrEmpty && receipt.HellTicket is { } hellTicket)
        {
            // 实验性：深渊派对入场票已在入场提交时扣除；把背包变化投给客户端并留日志。
            log.Write("hell_party_entry_ticket_consumed", new { Connection = context.Id, receipt.Entry.DungeonId, hellTicket.ItemId, hellTicket.Count, receipt.LegendTicket,
                Slots = receipt.HellConsumedSlots.Select(slot => slot.Index).ToArray(), CharacterId = receipt.Character.Id, CharacterName = receipt.Character.Name });
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(receipt.Character.Inventory,
                InventoryListType.Main, [.. receipt.HellConsumedSlots]), "hell_party_entry_items", Guard);
        }
        if (receipt.SpecialEntry is { Gold: > 0 } special)
        {
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(receipt.Character.Inventory,
                InventoryListType.Main, [new(InventoryListType.Main, 0)]), "formal_altar_entry_gold", Guard);
            log.Write("formal_altar_entry_committed", new { Connection = context.Id, receipt.Entry.DungeonId,
                special.Day, special.Kind, special.Gold, EntryNumber = special.Expected.CharacterEntries + 1 });
        }
        if (!receipt.EntryItemConsumedSlots.IsDefaultOrEmpty)
        {
            // #510/#511 普通入场票已在入场提交时与疲劳、尝试号同事务扣除；把背包变化投给客户端并留日志。
            log.Write("dungeon_entry_item_consumed", new { Connection = context.Id, receipt.Entry.DungeonId,
                ItemId = receipt.Entry.EntryItem?.ItemId ?? GriefTowerEntryDecision.InvitationItem,
                Count = receipt.Entry.EntryItem?.Count ?? 1,
                Slots = receipt.EntryItemConsumedSlots.Select(slot => slot.Index).ToArray(), receipt.EntryFatigueCharged,
                CharacterId = receipt.Character.Id, CharacterName = receipt.Character.Name });
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(receipt.Character.Inventory,
                InventoryListType.Main, [.. receipt.EntryItemConsumedSlots]), "dungeon_entry_items", Guard);
        }
        if (!receipt.ChannelConsumedSlots.IsDefaultOrEmpty)
        {
            log.Write("channel_town_entry_items_consumed", new { Connection = context.Id, receipt.Entry.DungeonId,
                Slots = receipt.ChannelConsumedSlots.Select(slot => slot.Index).ToArray(), CharacterId = receipt.Character.Id });
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(receipt.Character.Inventory,
                InventoryListType.Main, [.. receipt.ChannelConsumedSlots]), "channel_town_entry_items", Guard);
        }
        // 祭坛金币、入场票、深渊票、强袭与频道代价已各自投影过同槽位；入场货币只补未投影的槽位，同一格不重复下发。
        var projectedSlots = new HashSet<short>();
        if (receipt.SpecialEntry is { Gold: > 0 }) projectedSlots.Add(0);
        foreach (var slot in receipt.EntryItemConsumedSlots) projectedSlots.Add(slot.Index);
        foreach (var slot in receipt.ChannelConsumedSlots) projectedSlots.Add(slot.Index);
        foreach (var slot in receipt.HellConsumedSlots) projectedSlots.Add(slot.Index);
        foreach (var slot in receipt.AssaultConsumedSlots) projectedSlots.Add(slot.Index);
        var currencySlots = EntryCurrencySlots(receipt.EntryChangedCurrencies, projectedSlots);
        if (currencySlots.Length != 0)
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(receipt.Character.Inventory,
                InventoryListType.Main, currencySlots), "dungeon_entry_currencies", Guard);
        if (receipt.EntryChangedCurrencies.Any(kind => kind is >= CurrencyKind.Cera and <= CurrencyKind.HappyTokenCera))
            await context.Notify(NotiPacketType.CERA, CurrencyPackets.Cera(receipt.Character.Inventory.AccountWallet), "dungeon_entry_cera", Guard);
        foreach (var enterCount in receipt.EnterCounts.IsDefault ? [] : receipt.EnterCounts)
            await context.Notify(NotiPacketType.DUNGEON_ENTER_COUNT_INFO, DungeonEnterCountPackets.Info(enterCount.Dungeon, enterCount.Daily, enterCount.Weekly),
                "dungeon_enter_count_after_entry", Guard);
        if (!receipt.AssaultConsumedSlots.IsDefaultOrEmpty)
        {
            log.Write("assault_mode_entry_committed", new { Connection = context.Id, receipt.Entry.DungeonId,
                Slots = receipt.AssaultConsumedSlots.Select(slot => slot.Index).ToArray() });
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(receipt.Character.Inventory,
                InventoryListType.Main, [.. receipt.AssaultConsumedSlots]), "assault_mode_entry_items", Guard);
        }
    }

}
