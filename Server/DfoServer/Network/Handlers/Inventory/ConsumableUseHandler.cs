using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Inventory.Models;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Projection.Inventory;

namespace DfoServer.Network.Handlers.Inventory;

internal sealed class ConsumableUseHandler(InventoryConsumableUseService use, ServerLog log,
    TutorialCatalog? tutorials = null, ItemCatalog? items = null, InventoryProjection? inventory = null,
    InventoryPremiumContractUseService? contracts = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.USE_STACKABLE, Handle, nameof(ConsumableUseHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        var tutorialRequest = tutorials is null && contracts is null ? null : StackableUseRequests.Parse(packet.Body);
        if (tutorialRequest is not null && contracts?.Recognizes(tutorialRequest.ItemId) == true)
        {
            var before = context.Owner.Execute(() => (Character: context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
            var premium = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(123) :
                context.Session.PlanAndCommitInventory(character => contracts.Plan(character,
                    new(tutorialRequest.Position, tutorialRequest.Token, tutorialRequest.ItemId, 0),
                    contracts.Clock.GetUtcNow().ToUnixTimeSeconds()), 123, requireTown: false);
            bool Guard() => premium.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) :
                context.Owner.Execute(() => ReferenceEquals(before.Character, context.Session.Selected) &&
                    before.SelectionEpoch == context.Session.SelectionEpoch && before.Phase == context.Session.Phase);
            log.Write("consumable_use", new { Connection = context.Id, Item = tutorialRequest.ItemId,
                Slot = tutorialRequest.Position.Index, Accepted = premium.Accepted, Reason = "premium-contract", Character = before.Character?.Id });
            await context.Reply(CmdPacketType.USE_STACKABLE, StackableUsePackets.Result(checked((ushort)tutorialRequest.Position.Index),
                0, tutorialRequest.Token, tutorialRequest.ItemId, premium.Error), "premium_contract_used", Guard);
            if (premium.Accepted && premium.Receipt is { } committed)
            {
                // ACK44已按原生使用路径扣源；这里只发布同事务提交的账号期限，不再次发送源行删除。
                if (context.Premiums is { } premiums) await premiums.ContractsAsync(committed.Character, guard: Guard);
                else await context.Notify(NotiPacketType.PREMIUM_INFO_LIST, PremiumPackets.SelectEntries(committed.Character.Inventory.AccountWallet),
                    "premium_info_committed", Guard);
                if (context.Premiums is null && premium.Plan!.Before.AccountWallet.ContractExpiry(27) != committed.Character.Inventory.AccountWallet.ContractExpiry(27))
                    await context.Notify(NotiPacketType.CERA_SPECIALITEM, SkillPackets.ExpertContract(
                        committed.Character.Learning.ExpertContractExpiresAt, contracts.Clock.GetUtcNow().ToUnixTimeSeconds()),
                        "expert_contract_committed", Guard);
                if (committed.PreviousQuests is { } previous && !previous.SequenceEqual(committed.Character.ActiveMainQuests))
                    await context.Notify(NotiPacketType.ACCEPTED_QUEST, QuestPackets.ActiveList(committed.Character.ActiveMainQuests),
                        "premium_contract_quest_conditions", Guard);
            }
            return CommandResult.Handled;
        }
        if (tutorialRequest is not null && tutorials is not null && items is not null && inventory is not null)
        {
            var before = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch));
            var consumed = context.Session.UseTutorialConsumable(tutorialRequest.Position, tutorialRequest.ItemId, tutorials!, items);
            if (consumed is not null)
            {
                bool Guard() => consumed.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) :
                    context.Owner.Execute(() => ReferenceEquals(before.Selected, context.Session.Selected) && before.SelectionEpoch == context.Session.SelectionEpoch);
                await context.Reply(CmdPacketType.USE_STACKABLE, StackableUsePackets.Result(checked((ushort)tutorialRequest.Position.Index),
                    0, tutorialRequest.Token, tutorialRequest.ItemId, consumed.Error), "tutorial_potion_used", Guard);
                await inventory.RefreshAsync(context, consumed);
                return CommandResult.Handled;
            }
        }
        var request = ConsumableUseRequests.Read(packet.Body);
        var result = context.Session.UseConsumable(request, use, context.Activity?.BlocksTownActions == true);
        bool accepted = result.Outcome == ConsumableUseOutcome.Committed;
        log.Write("consumable_use", new { Connection = context.Id, Item = request.ItemId, Slot = request.Position.Index,
            List = request.Position.List, Accepted = accepted, result.Reason, Character = result.Guard?.Character?.Id,
            Revision = result.Guard?.Character?.Revision, result.SourceUid });
        if (result.Outcome == ConsumableUseOutcome.CloseConnection) return CommandResult.CloseConnection;
        var guard = result.Guard ?? throw new InvalidOperationException("道具使用缺少原状态发布身份");
        bool CanSend() => context.Session.CanPublishConsumable(guard);
        await context.Reply(CmdPacketType.USE_STACKABLE, ConsumableUsePackets.Ack(request, accepted),
            accepted ? "consumable_use_committed" : "consumable_use_rejected", CanSend);
        // ACK44仍执行原生药效与pending收尾；1681沿同一条目解析静默还原数量，不触发获得提示。
        if (accepted && result.PreserveSource)
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST_NOT_NOTICE, InventoryPackets.Delta(guard.Character!.Inventory,
                request.Position.List,[request.Position]), "training_consumable_source_preserved", CanSend);
        // 保留实际库存事务更新的寻物任务条件；修炼场无库存变更，不虚构任务进度。
        if (result.Inventory?.Receipt is { } receipt && receipt.PreviousQuests is { } old &&
            !old.SequenceEqual(receipt.Character.ActiveMainQuests))
            await context.Notify(NotiPacketType.ACCEPTED_QUEST, QuestPackets.ActiveList(receipt.Character.ActiveMainQuests),
                "consumable_quest_conditions", CanSend);
        return CommandResult.Handled;
    }
}
