using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection.Inventory;

internal sealed partial class InventoryProjection
{
    // 已提交的原投影失效时，只发送当前绝对值，不重放购买获得型增量。
    internal async Task ResynchronizeCommittedAsync(GameConnectionContext context, InventoryCommitReceipt snapshot,
        InventoryCommitReceipt committed, InventoryPlan plan)
    {
        bool CanSend() => context.Session.CanPublishInventory(snapshot);
        if (!CanSend()) return;
        var character = snapshot.Character;
        var current = character.Inventory;
        var lists = plan.ChangedSlots.Select(slot => slot.List).Where(list => list is InventoryListType.Main or
            InventoryListType.Avatar or InventoryListType.Pet or InventoryListType.Equipment).ToHashSet();
        if (plan.Before.Expansion != plan.After.Expansion ||
            plan.ChangedCurrencies.Any(kind => kind is >= CurrencyKind.Gold and <= CurrencyKind.EpicSoul))
            lists.Add(InventoryListType.Main);
        if (plan.Before.AvatarExpansion != plan.After.AvatarExpansion) lists.Add(InventoryListType.Avatar);
        if (lists.Count != 0) await SynchronizeItemSpaceContentsAsync(context, snapshot, lists);
        foreach (var list in new[] { InventoryListType.GuildMedal, InventoryListType.Talisman, InventoryListType.AccountCommon })
            if (plan.ChangedSlots.Any(slot => slot.List == list))
            {
                await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.SpecialFull(current, list),
                    "certificate_inventory_resync", CanSend);
                await RestoreLocksAsync(context, current, current.Items.Where(item => item.Position.List == list)
                    .Select(item => item.Position), canSend: CanSend);
            }
        foreach (var list in new[] { InventoryListType.PersonalCargo, InventoryListType.AccountCargo })
            if (plan.ChangedSlots.Any(slot => slot.List == list) ||
                list == InventoryListType.PersonalCargo && plan.Before.CargoCapacity != plan.After.CargoCapacity ||
                list == InventoryListType.AccountCargo && plan.AccountCargoChanged)
            {
                if (list == InventoryListType.PersonalCargo && current.CargoCapacity > plan.Before.CargoCapacity)
                    await context.Notify(NotiPacketType.CERA_SPECIALITEM, CargoPackets.PersonalCapacity(current.CargoCapacity),
                        "certificate_cargo_capacity_resync", CanSend);
                await context.Notify(NotiPacketType.ITEM_LIST, CargoPackets.Full(current, list),
                    "certificate_cargo_resync", CanSend);
                await RestoreLocksAsync(context, current, current.Items.Where(item => item.Position.List == list)
                    .Select(item => item.Position), canSend: CanSend);
            }
        if (plan.ChangedCurrencies.Any(kind => kind is >= CurrencyKind.Cera and <= CurrencyKind.HappyTokenCera))
            await context.Notify(NotiPacketType.CERA, CurrencyPackets.Cera(current.AccountWallet), "certificate_cera_resync", CanSend);
        if (plan.ChangedCurrencies.Contains(CurrencyKind.Mileage) ||
            !plan.Before.AccountWallet.MileageShop.Same(plan.After.AccountWallet.MileageShop))
            await context.Notify(NotiPacketType.MILEAGE_DATA, MileagePackets.State(current.AccountWallet), "certificate_mileage_resync", CanSend);
        if (plan.AccountMaterialsChanged)
            await town.AccountMaterialsAsync(context, character, before: null, canSend: CanSend);
        if (!plan.ChangedEpicPieces.IsEmpty)
            await context.Notify(NotiPacketType.EPIC_BOOK_INFO, EpicBookPackets.Info(current.EpicPieces, pieces, plan.ChangedEpicPieces),
                "certificate_epic_resync", CanSend);
        if (plan.Before.AccountWallet.SeriaLuck != plan.After.AccountWallet.SeriaLuck)
            await context.Notify(NotiPacketType.BOOSTER_GAGE,
                SeriaLuckPackets.Gauge(current.AccountWallet.SeriaLuck, current.AccountWallet.SeriaLuck), "certificate_gauge_resync", CanSend);
        if (!plan.Before.AccountWallet.PremiumContracts.SequenceEqual(plan.After.AccountWallet.PremiumContracts))
        {
            if (context.Premiums is { } premiums) await premiums.ContractsAsync(character, guard: CanSend);
            else
            {
                await context.Notify(NotiPacketType.PREMIUM_INFO_LIST, PremiumPackets.SelectEntries(current.AccountWallet),
                    "certificate_contracts_resync", CanSend);
                if (plan.Before.AccountWallet.ContractExpiry(27) != plan.After.AccountWallet.ContractExpiry(27))
                    await context.Notify(NotiPacketType.CERA_SPECIALITEM,
                        SkillPackets.ExpertContract(character.Learning.ExpertContractExpiresAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                        "certificate_expert_contract_resync", CanSend);
            }
        }
        if (!plan.Before.DevilUses.SequenceEqual(plan.After.DevilUses) ||
            Enumerable.Range(0, 8).Any(slot => DevilContractPolicy.Expiry(plan.Before.AccountWallet, slot) !=
                DevilContractPolicy.Expiry(plan.After.AccountWallet, slot)))
        {
            var membership = context.Owner.Execute(() => CanSend() ? context.Session.CardMembershipForClient() : null);
            if (membership is not null)
                await context.Reply(CmdPacketType.PREMIUM_SERVICE,
                    DungeonCardMembershipPackets.DevilService0(membership.Membership, DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                    "certificate_card_membership_resync", () => CanSend() && context.Session.CanPublishCardMembership(membership));
        }
        if (committed.PreviousQuests is { } previous && !previous.SequenceEqual(character.ActiveMainQuests))
            await QuestProjection.ProgressAsync(context,previous,character,CanSend,"certificate_quest_resync");
    }
}
