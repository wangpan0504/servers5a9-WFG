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
    InventoryPremiumContractUseService? contracts = null, Game.Creatures.CreatureService? creatures = null,
    Projection.CreatureProjection? creatureProjection = null, Projection.TownProjection? town = null,
    Game.ExpertJob.ExpertJobLearning? expertLearning = null, ExpertJobScriptCatalog? expertJobs = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.USE_STACKABLE, Handle, nameof(ConsumableUseHandler));

    async Task<CommandResult> Handle(GameConnectionContext context, Packet packet)
    {
        var recipeRequest = expertLearning is not null && packet.Body.Length == 15 ? StackableUseRequests.Parse(packet.Body) : null;
        if (recipeRequest is not null && expertLearning!.Recognizes(recipeRequest.ItemId) && expertJobs is not null)
        {
            var captured = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
            var learned = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(123) :
                context.Session.LearnExpertJobRecipe(recipeRequest.Position, recipeRequest.ItemId, expertLearning);
            bool Guard() => learned.Receipt is { } receipt ? context.Session.CanPublishInventory(receipt) :
                context.Owner.Execute(() => ReferenceEquals(context.Session.Selected, captured.Selected) &&
                    context.Session.SelectionEpoch == captured.SelectionEpoch && context.Session.Phase == captured.Phase);
            log.Write("consumable_use", new { Connection = context.Id, Item = recipeRequest.ItemId, Slot = recipeRequest.Position.Index,
                Accepted = learned.Accepted, Reason = "expert-job-recipe", Character = captured.Selected?.Id });
            await context.Reply(CmdPacketType.USE_STACKABLE, StackableUsePackets.Result((ushort)recipeRequest.Position.Index,
                0, recipeRequest.Token, recipeRequest.ItemId, learned.Error), "expert_job_recipe_learned", Guard);
            if (learned.Accepted && learned.Receipt is { } committed)
            {
                if (inventory is not null) await inventory.RefreshAsync(context, learned,
                    acknowledgedSlots: new HashSet<InventorySlot> { recipeRequest.Position });
                expertJobs.TryLearningRecipe(recipeRequest.ItemId, out var definition);
                // 205 的配方列表重建，稀有度列表只追加；成功时只发送本次新资格，避免重复通知持续追加。
                await context.Notify(NotiPacketType.EXPERT_JOB_INFO,
                    ExpertJobPackets.Info(committed.Character, expertJobs, learnedRarity: definition.Rarity),
                    "expert_job_recipe_info", Guard);
            }
            return CommandResult.Handled;
        }
        if (creatures is not null && packet.Body.Length == 15)
        {
            var feedRequest = ConsumableUseRequests.Read(packet.Body);
            var selectedCreatureOwner = context.Owner.Execute(() => context.Session.Selected?.Inventory.Find(feedRequest.Position) is { } source &&
                source.Core.ItemId == feedRequest.ItemId && source.Core.Kind is ItemKind.CreatureConsumable or ItemKind.Consumable ? context.Session.Selected : null);
            if (context.Session.RecentCreatureFeed(feedRequest, creatures) || selectedCreatureOwner is not null &&
                creatures.RecognizesFeed(selectedCreatureOwner, feedRequest.ItemId, creatures.Clock.GetUtcNow().ToUnixTimeSeconds()))
            {
                var fed = context.Session.FeedCreature(feedRequest, creatures, context.Activity?.BlocksTownActions == true);
                if (fed.Outcome == ConsumableUseOutcome.CloseConnection) return CommandResult.CloseConnection;
                bool Guard() => fed.Guard is { } guard && context.Session.CanPublishConsumable(guard);
                await context.Reply(CmdPacketType.USE_STACKABLE, ConsumableUsePackets.Ack(feedRequest,
                    fed.Outcome == ConsumableUseOutcome.Committed), "creature_feed_ack", Guard);
                if (fed.Inventory is { } committed)
                {
                    if (inventory is not null) await inventory.RefreshAsync(context, committed,
                        acknowledgedSlots: new HashSet<InventorySlot> { feedRequest.Position }, refreshEquipment: false);
                    else if (creatureProjection is not null) await creatureProjection.ChangedAsync(context, committed);
                }
                log.Write("creature_feed", new { Connection = context.Id, feedRequest.ItemId, fed.Outcome, fed.Reason,
                    Revision = fed.Inventory?.Receipt?.Character.Revision });
                return CommandResult.Handled;
            }
        }
        var tutorialRequest = tutorials is null && contracts is null ? null : StackableUseRequests.Parse(packet.Body);
        if (tutorialRequest is not null && contracts?.Recognizes(tutorialRequest.ItemId) == true)
        {
            var before = context.Owner.Execute(() => (Character: context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
            var premium = context.Activity?.BlocksTownActions == true ? InventoryResult.Reject(123) :
                context.Session.UsePremiumContract(new(tutorialRequest.Position, tutorialRequest.Token, tutorialRequest.ItemId, 0), contracts);
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
            Revision = result.Guard?.Character?.Revision, result.SourceUid, result.NativeNoConsumption, result.TownGiftItem,
            StaminaRecovered = result.Inventory?.Plan is { } used && used.Before.StaminaUntil != used.After.StaminaUntil });
        if (result.Outcome == ConsumableUseOutcome.CloseConnection) return CommandResult.CloseConnection;
        var guard = result.Guard ?? throw new InvalidOperationException("道具使用缺少原状态发布身份");
        bool CanSend() => context.Session.CanPublishConsumable(guard);
        await context.Reply(CmdPacketType.USE_STACKABLE, ConsumableUsePackets.Ack(request, accepted, result.NativeNoConsumption),
            accepted ? "consumable_use_committed" : "consumable_use_rejected", CanSend);
        if (accepted && result.TownGiftItem != 0 && town is not null)
            await town.GiftEffectAsync(context, result.TownGiftItem, CanSend);
        if (accepted && result.Inventory is { Plan: { } plan, Receipt: { } recoveryReceipt } &&
            plan.Before.StaminaUntil != plan.After.StaminaUntil)
        {
            if (context.Premiums is { } premiums) await premiums.StaminaAsync(recoveryReceipt.Character, CanSend, force: true);
            else await context.Notify(NotiPacketType.STAMINA, [100], "consumable_stamina_recovered", CanSend);
        }
        // 子类型2在成功ACK后才执行效果；13完整载入关闭新图标标记，不能用1681删后补回。
        if (accepted && result.PreserveSource && !result.NativeNoConsumption)
        {
            var current = guard.Character!.Inventory;
            await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.Full(current), "dungeon_consumable_source_preserved", CanSend);
            await InventoryProjection.RestoreLocksAsync(context, current,
                current.Items.Where(item => item.Position.List == InventoryListType.Main).Select(item => item.Position),
                canSend: CanSend, currentIndex: result.Inventory?.Receipt?.Index);
        }
        // 保留实际库存事务更新的寻物任务条件；修炼场无库存变更，不虚构任务进度。
        if (result.Inventory?.Receipt is { } receipt && receipt.PreviousQuests is { } old &&
            !old.SequenceEqual(receipt.Character.ActiveMainQuests))
            await context.Notify(NotiPacketType.ACCEPTED_QUEST, QuestPackets.ActiveList(receipt.Character.ActiveMainQuests),
                "consumable_quest_conditions", CanSend);
        return CommandResult.Handled;
    }
}
