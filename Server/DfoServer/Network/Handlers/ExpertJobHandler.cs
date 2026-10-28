using DfoServer.Game.Dungeons;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class ExpertJobHandler(QuestCatalog quests, ExpertJobScriptCatalog scripts, ItemCatalog items,
    GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, InventoryProjection inventory, ServerLog log,
    TownPresence? presence = null, ExpertJobStoreBoard? stores = null,
    InventoryEnchantmentService? enchantments = null, ExpertJobDisassembly? disassembly = null, ExpertJobEnchanting? cardEnchanting = null, OnlineSessionDirectory? online = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.GIVEUP_EXPERT_JOB, GiveUpAsync, nameof(ExpertJobHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.COMPOUND_ITEM_BY_EXPERT_JOB, CompoundAsync, nameof(ExpertJobHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.CREATE_EXPERT_JOB_STORE, CreateStoreAsync, nameof(ExpertJobHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.CLOSE_EXPERT_JOB_STORE, CloseStoreAsync, nameof(ExpertJobHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.ENTER_EXPERT_JOB_STORE, EnterStoreAsync, nameof(ExpertJobHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.USE_ENCHANT_STORE, EnchantStoreAsync, nameof(ExpertJobHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.REQUEST_DISJOINT_ITEM, DisjointAsync, nameof(ExpertJobHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.EXPERT_EXTRACTION, ExtractAsync, nameof(ExpertJobHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.UPGRADE_DISJOINT_MACHINE, UpgradeAsync, nameof(ExpertJobHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.REPAIR_DISJOINT_MACHINE, RepairDisjointerAsync, nameof(ExpertJobHandler), UserStateRequirement.WorldReady);
        commands.Register(CmdPacketType.REPAIR_EXPERT_JOB_STORE, RepairEnchanterAsync, nameof(ExpertJobHandler), UserStateRequirement.WorldReady);
    }

    async Task<CommandResult> GiveUpAsync(GameConnectionContext context, Packet packet)
    {
        try { ExpertJobRequests.ReadEmpty(packet.Body); }
        catch (InvalidDataException)
        {
            await context.Reply(CmdPacketType.GIVEUP_EXPERT_JOB, ExpertJobPackets.Fail(19), "expert_job_giveup_malformed");
            return CommandResult.Handled;
        }
        var result = context.Activity?.BlocksTownActions == true
            ? context.Owner.Execute(() => new ExpertJobGiveUpResult(19, "active-town-operation", context.Session.Selected,
                context.Owner.Generation, context.Session.SelectionEpoch, context.Session.Phase))
            : context.Session.GiveUpExpertJob(scripts);
        bool Guard() => result.Error != 0
            ? context.Owner.Execute(() => context.Session.SelectionEpoch == result.Epoch && context.Session.Phase == result.Phase)
            : context.Session.CanPublishExpertJobGiveUp(result);
        log.Write("expert_job_giveup", new { Connection = context.Id, Accepted = result.Error == 0, result.Error, result.Reason,
            Character = result.Character?.Id, result.Character?.Revision, result.FinalGold, result.GiveUpState });
        if (result.Error != 0)
        {
            await context.Reply(CmdPacketType.GIVEUP_EXPERT_JOB, ExpertJobPackets.Fail(result.Error), "expert_job_giveup_rejected", Guard);
            return CommandResult.Handled;
        }
        var character = result.Character!;
        if (stores is not null && stores.TryClose(character.Id, out _))
            presence?.Publish(context.Owner, context.Sender, (ushort)NotiPacketType.CLOSE_EXPERT_JOB_STORE,
                ExpertJobPackets.CloseStoreNotice(character.Id), "expert_job_store_closed");
        await SkillProjection.PublishAsync(context, character, "expert_job_giveup_skills", "expert_job_giveup_combo", Guard);
        await context.Notify(NotiPacketType.USERINFO, TownPackets.Actor(character, context.Channel,
            adventureName: context.Session.ReadAdventureName(), premiumPcRoom: context.Session.PremiumPcRoom),
            "expert_job_giveup_actor", Guard);
        await context.Reply(CmdPacketType.GIVEUP_EXPERT_JOB, ExpertJobPackets.GiveUpSuccess(result.FinalGold, result.GiveUpState),
            "expert_job_giveup_committed", Guard);
        return CommandResult.Handled;
    }

    async Task<CommandResult> CompoundAsync(GameConnectionContext context, Packet packet)
    {
        try { _ = ExpertJobRequests.ReadCompound(packet.Body); }
        catch (InvalidDataException)
        {
            await context.Reply(CmdPacketType.COMPOUND_ITEM_BY_EXPERT_JOB, ExpertJobPackets.Fail(19), "expert_job_compound_malformed");
            return CommandResult.Handled;
        }
        if (context.Activity?.BlocksTownActions == true)
        {
            await context.Reply(CmdPacketType.COMPOUND_ITEM_BY_EXPERT_JOB, ExpertJobPackets.Fail(19), "expert_job_compound_blocked");
            return CommandResult.Handled;
        }
        var parsed = ExpertJobRequests.ReadCompound(packet.Body);
        var type = context.Owner.Execute(() => context.Session.Selected?.ExpertJobType ?? 0);
        if (type is not (1 or 2 or 4))
        {
            await context.Reply(CmdPacketType.COMPOUND_ITEM_BY_EXPERT_JOB, ExpertJobPackets.Fail(19), "expert_job_compound_type");
            return CommandResult.Handled;
        }
        var current = context.Owner.Execute(() => context.Session.Selected);
        ushort craftError = 19;
        if (current is not null && ExpertJobCraft.TryPlan(current, scripts, items, goldLimits, pieces, parsed.First, parsed.Second,
            parsed.Third, Random.Shared.Next, DateTimeOffset.UtcNow, out var craft, out craftError))
        {
            var crafted = context.Session.CommitExpertJobCraft(craft.Inventory, craft.Experience, craft.SuccessCount > 0 ? quests : null);
            if (!crafted.Accepted || crafted.Receipt is not { } craftedReceipt)
            {
                await context.Reply(CmdPacketType.COMPOUND_ITEM_BY_EXPERT_JOB, ExpertJobPackets.Fail(19), "expert_job_compound_commit");
                return CommandResult.Handled;
            }
            bool Crafted() => context.Session.CanPublishInventory(craftedReceipt);
            await inventory.RefreshAsync(context, crafted);
            await context.Notify(NotiPacketType.USERINFO, TownPackets.Actor(craftedReceipt.Character, context.Channel,
                adventureName: context.Session.ReadAdventureName(), premiumPcRoom: context.Session.PremiumPcRoom),
                "expert_job_compound_actor", Crafted);
            await NotifyInfo(context, craftedReceipt.Character, Crafted, "expert_job_compound_info");
            await context.Reply(CmdPacketType.COMPOUND_ITEM_BY_EXPERT_JOB,
                ExpertJobPackets.CompoundResult(craft.Products.Select(row => (row.ItemId, row.Count)).ToArray(),
                    checked((uint)craft.SuccessCount), checked((uint)craft.FailureCount)),
                "expert_job_compound_crafted", Crafted);
            return CommandResult.Handled;
        }
        // 未知配方、材料不足与无效卡片槽都必须拒绝；教程只随真实产物的原子事务推进。
        await context.Reply(CmdPacketType.COMPOUND_ITEM_BY_EXPERT_JOB, ExpertJobPackets.Fail(craftError), "expert_job_compound_rejected");
        return CommandResult.Handled;
    }

    async Task<CommandResult> EnchantStoreAsync(GameConnectionContext context, Packet packet)
    {
        try { _ = ExpertJobRequests.ReadEnchantStore(packet.Body); }
        catch (InvalidDataException)
        {
            await context.Reply(CmdPacketType.USE_ENCHANT_STORE, ExpertJobPackets.Fail(19), "expert_job_enchant_malformed");
            return CommandResult.Handled;
        }
        if (context.Activity?.BlocksTownActions == true)
        {
            await context.Reply(CmdPacketType.USE_ENCHANT_STORE, ExpertJobPackets.Fail(19), "expert_job_enchant_blocked");
            return CommandResult.Handled;
        }
        var parsed = ExpertJobRequests.ReadEnchantStore(packet.Body);
        var current = context.Session.CaptureExpertJobTrade();
        if (current is not null && parsed.First != current.Id && parsed.Third == 2 && parsed.Fourth == 0 && parsed.Sixth == 0 &&
            parsed.Second <= int.MaxValue && parsed.Fifth <= short.MaxValue && parsed.Seventh <= short.MaxValue)
            return await TradeStoreAsync(context, parsed.First, 1, candidate => cardEnchanting?.Plan(candidate,
                (int)parsed.Second, new(InventoryListType.Main, (short)parsed.Fifth), new(InventoryListType.Main, (short)parsed.Seventh)));
        ushort error = 19;
        ExpertJobActionPlan? action = null;
        InventoryResult committed;
        // 本人路径与跨玩家店主路径必须区分；不能把客人的卡/金币当成店主操作结算。
        if (current is null || parsed.First != current.Id || parsed.Third != 2 || parsed.Fourth != 0 || parsed.Sixth != 0 ||
            parsed.Second > int.MaxValue || parsed.Fifth > short.MaxValue || parsed.Seventh > short.MaxValue)
            committed = InventoryResult.Reject(19);
        else if (cardEnchanting?.Plan(current, (int)parsed.Second, new(InventoryListType.Main, (short)parsed.Fifth),
            new(InventoryListType.Main, (short)parsed.Seventh)) is { } cardPlan)
        {
            action = cardPlan;
            committed = context.Session.CommitExpertJobAction(cardPlan);
        }
        else if (enchantments is not null && ExpertJobCraft.TryPlanEnchant(current, enchantments, scripts,
            parsed.Fourth, parsed.Fifth, parsed.Sixth, parsed.Seventh, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            out var beadPlan, out int experience, out error))
            committed = context.Session.CommitExpertJobCraft(beadPlan, experience);
        else committed = InventoryResult.Reject(error == 0 ? (ushort)19 : error);
        if (!committed.Accepted || committed.Receipt is not { } craftedReceipt)
        {
            await context.Reply(CmdPacketType.USE_ENCHANT_STORE, ExpertJobPackets.Fail(committed.Error), "expert_job_enchant_rejected");
            return CommandResult.Handled;
        }
        bool Crafted() => context.Session.CanPublishInventory(craftedReceipt);
        if (stores is not null && stores.TryGet(craftedReceipt.Character.Id, out var ownStore))
            stores.Open(ownStore with { Endurance = (uint)craftedReceipt.Character.ExpertMachineEndurance });
        await inventory.RefreshAsync(context, committed);
        await context.Notify(NotiPacketType.USERINFO, TownPackets.Actor(craftedReceipt.Character, context.Channel,
            adventureName: context.Session.ReadAdventureName(), premiumPcRoom: context.Session.PremiumPcRoom),
            "expert_job_enchant_actor", Crafted);
        await NotifyInfo(context, craftedReceipt.Character, Crafted, "expert_job_enchant_info");
        await context.Reply(CmdPacketType.USE_ENCHANT_STORE, ExpertJobPackets.EnchantStoreSuccess((uint)craftedReceipt.Character.ExpertMachineEndurance,
            (uint)scripts.Level(1, action is null && scripts.LearnedRarities(current!.ExpertJobRecipes).Length == 0
                ? current.ExpertJobExperience : craftedReceipt.Character.ExpertJobExperience),
            action?.EnchantRarity ?? scripts.LearnedRarities(current!.ExpertJobRecipes).FirstOrDefault(), action?.Succeeded != false), "expert_job_enchant_crafted",
            Crafted);
        var observed = action?.Succeeded == false ? null : context.Session.ObserveExpertJobTutorial(QuestExpertJobProgress.Enchanter, quests);
        if (observed is { Outcome: QuestLifecycleOutcome.Committed or QuestLifecycleOutcome.Unchanged } &&
            observed.Commit is { ProjectionCurrent: false })
            return CommandResult.CloseConnection;
        if (observed is { Commit: not null })
            await context.Notify(NotiPacketType.ACCEPTED_QUEST, QuestPackets.ActiveList(observed.Character.ActiveMainQuests),
                "expert_job_enchant_quest", () => context.Session.CanPublishQuestLifecycle(observed));
        return CommandResult.Handled;
    }

    async Task<CommandResult> ExtractAsync(GameConnectionContext context, Packet packet)
    {
        try { _ = ExpertJobRequests.ReadExtraction(packet.Body); }
        catch (InvalidDataException)
        {
            await context.Reply(CmdPacketType.EXPERT_EXTRACTION, ExpertJobPackets.Fail(19), "expert_job_extract_malformed");
            return CommandResult.Handled;
        }
        if (context.Activity?.BlocksTownActions == true)
        {
            await context.Reply(CmdPacketType.EXPERT_EXTRACTION, ExpertJobPackets.Fail(19), "expert_job_extract_blocked");
            return CommandResult.Handled;
        }
        var parsed = ExpertJobRequests.ReadExtraction(packet.Body);
        var current = context.Owner.Execute(() => context.Session.Selected);
        ushort error = 19;
        if (current is null || !ExpertJobCraft.TryPlanExtract(current, scripts, items, goldLimits, pieces, parsed.ExtractorSlot,
            parsed.TargetList, parsed.TargetSlot, Random.Shared.Next, DateTimeOffset.UtcNow, out var craft, out error))
        {
            await context.Reply(CmdPacketType.EXPERT_EXTRACTION, ExpertJobPackets.Fail(error == 0 ? (ushort)19 : error),
                "expert_job_extract_rejected");
            return CommandResult.Handled;
        }
        var committed = context.Session.CommitExpertJobCraft(craft.Inventory, craft.Experience);
        if (!committed.Accepted || committed.Receipt is not { } craftedReceipt)
        {
            await context.Reply(CmdPacketType.EXPERT_EXTRACTION, ExpertJobPackets.Fail(19), "expert_job_extract_commit");
            return CommandResult.Handled;
        }
        bool Crafted() => context.Session.CanPublishInventory(craftedReceipt);
        await inventory.RefreshAsync(context, committed);
        await context.Notify(NotiPacketType.USERINFO, TownPackets.Actor(craftedReceipt.Character, context.Channel,
            adventureName: context.Session.ReadAdventureName(), premiumPcRoom: context.Session.PremiumPcRoom),
            "expert_job_extract_actor", Crafted);
        await NotifyInfo(context, craftedReceipt.Character, Crafted, "expert_job_extract_info");
        var products = Products(craftedReceipt.Character.Inventory, craft.Inventory.Before, craft.Products);
        await context.Reply(CmdPacketType.EXPERT_EXTRACTION,
            ExpertJobPackets.ExtractionResult(parsed.TargetList, (ushort)parsed.TargetSlot, products),
            "expert_job_extract_crafted", Crafted);
        return CommandResult.Handled;
    }

    async Task<CommandResult> DisjointAsync(GameConnectionContext context, Packet packet)
    {
        if (context.Session.Phase is SessionPhase.Dungeon or SessionPhase.Settlement)
        {
            var scene = context.Session.CurrentFormalDungeonScene();
            if (scene?.Entry.HellParty is null) return CommandResult.Unhandled;
            log.Write("formal_hell_party_start_observed", new { Connection = context.Id, scene.Entry.DungeonId,
                Length = packet.Body.Length, Body = Convert.ToHexString(packet.Body), Experimental = true });
            return CommandResult.Handled;
        }
        ExpertJobDisjointRequest request;
        try { request = ExpertJobRequests.ReadDisjoint(packet.Body); }
        catch (InvalidDataException)
        {
            await context.Reply(CmdPacketType.REQUEST_DISJOINT_ITEM, ExpertJobPackets.Fail(19), "expert_job_disjoint_malformed");
            return CommandResult.Handled;
        }
        var captured = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch, context.Session.Phase));
        bool Guard() => context.Owner.Execute(() => ReferenceEquals(context.Session.Selected, captured.Selected) &&
            context.Session.SelectionEpoch == captured.SelectionEpoch && context.Session.Phase == captured.Phase);
        var current = captured.Selected;
        if (current is not null && request.OwnerActor != current.Id && request.SourceList == 0 && request.SourceSlot <= short.MaxValue)
            return await TradeStoreAsync(context, request.OwnerActor, 3, candidate => disassembly?.Plan(candidate,
                new(InventoryListType.Main, (short)request.SourceSlot), ownMachine: false));
        // 本人机器与跨玩家店铺分别结算，后者必须双账号原子提交。
        var plan = current is not null && request.OwnerActor == current.Id && request.SourceList == 0 &&
            request.SourceSlot <= short.MaxValue && context.Activity?.BlocksTownActions != true
            ? disassembly?.Plan(current, new(InventoryListType.Main, (short)request.SourceSlot)) : null;
        var committed = plan is null ? InventoryResult.Reject(19) : context.Session.CommitExpertJobAction(plan);
        if (!committed.Accepted || committed.Receipt is not { } receipt || plan?.Disjoint is not { } products)
        {
            await context.Reply(CmdPacketType.REQUEST_DISJOINT_ITEM, ExpertJobPackets.Fail(19), "expert_job_disjoint_rejected", Guard);
            return CommandResult.Handled;
        }
        bool Current() => context.Session.CanPublishInventory(receipt);
        // ACK235自身消费源和安装主背包产物，不能再用增量包叠加同一份奖励。
        await context.Reply(CmdPacketType.REQUEST_DISJOINT_ITEM,
            ExpertJobPackets.DisjointResult(current!.Id, products, receipt.Character.Inventory.Gold, (uint)receipt.Character.ExpertMachineEndurance),
            "expert_job_machine_disjoint", Current);
        var acknowledged = plan.Inventory.ChangedSlots.Where(slot => slot.List == InventoryListType.Main).ToHashSet();
        foreach (var kind in plan.Inventory.ChangedCurrencies)
            if (ItemSlotBoundService.CurrencySlot(kind) is { } slot) acknowledged.Add(new(InventoryListType.Main, slot));
        await inventory.RefreshAsync(context, committed, acknowledgedSlots: acknowledged);
        if (stores is not null && stores.TryGet(current.Id, out var ownStore))
            stores.Open(ownStore with { Endurance = (uint)receipt.Character.ExpertMachineEndurance });
        await context.Notify(NotiPacketType.USERINFO, TownPackets.Actor(receipt.Character, context.Channel,
            adventureName: context.Session.ReadAdventureName(), premiumPcRoom: context.Session.PremiumPcRoom), "expert_job_disjoint_actor", Current);
        await NotifyInfo(context, receipt.Character, Current, "expert_job_disjoint_info");
        var observed = context.Session.ObserveExpertJobTutorial(QuestExpertJobProgress.Disjointer, quests);
        if (observed is { Commit: not null })
            await context.Notify(NotiPacketType.ACCEPTED_QUEST, QuestPackets.ActiveList(observed.Character.ActiveMainQuests),
                "expert_job_disjoint_quest", () => context.Session.CanPublishQuestLifecycle(observed));
        log.Write("expert_job_disjoint", new { Connection = context.Id, Character = current.Id, Accepted = true,
            Rule = "pvf-disjointer-v1", request.SourceSlot, SourceItem = current.Inventory.Find(new(InventoryListType.Main, (short)request.SourceSlot))?.Core.ItemId,
            current.ExpertMachineGrade, Rewards = products.Rewards, receipt.Character.ExpertMachineEndurance });
        return CommandResult.Handled;
    }

    async Task<CommandResult> CreateStoreAsync(GameConnectionContext context, Packet packet)
    {
        ExpertJobCreateStoreRequest parsed;
        try { parsed = ExpertJobRequests.ReadCreateStore(packet.Body); }
        catch (InvalidDataException)
        {
            await context.Reply(CmdPacketType.CREATE_EXPERT_JOB_STORE, ExpertJobPackets.Fail(19), "expert_job_store_malformed");
            return CommandResult.Handled;
        }
        var character = context.Owner.Execute(() => context.Session.Selected);
        if (character is null || character.ExpertJobType == 3 && parsed.Mode != 0 ||
            character.ExpertJobType == 1 && parsed.Mode != 3 || context.Activity?.BlocksTownActions == true ||
            !context.Session.OpenExpertJobStore())
        {
            await context.Reply(CmdPacketType.CREATE_EXPERT_JOB_STORE, ExpertJobPackets.Fail(19), "expert_job_store_rejected");
            return CommandResult.Handled;
        }
        byte grade = checked((byte)(character.ExpertJobType == 3 ? character.ExpertMachineGrade
            : scripts.Level(character.ExpertJobType, character.ExpertJobExperience)));
        var store = new ExpertJobStoreRecord(character.Id, character.ExpertJobType, parsed.Mode, ((char)grade).ToString(),
            parsed.First, parsed.Second, parsed.Third, parsed.Fourth, grade, (uint)Math.Max(0, character.ExpertMachineEndurance))
        { Town = character.Position.Town, Area = character.Position.Area };
        stores?.Open(store);
        byte[] notice = ExpertJobPackets.CreateStoreNotice(store);
        presence?.Publish(context.Owner, context.Sender, (ushort)NotiPacketType.CREATE_EXPERT_JOB_STORE, notice,
            "expert_job_store_created");
        if (parsed.Mode == 3)
            presence?.Publish(context.Owner, context.Sender, (ushort)NotiPacketType.UPDATE_EXPERT_JOB_STORE,
                ExpertJobPackets.UpdateStoreNotice(parsed.Mode, character.Id, store.Name), "expert_job_store_goods");
        await context.Reply(CmdPacketType.CREATE_EXPERT_JOB_STORE, ExpertJobPackets.CreateStoreSuccess(), "expert_job_store_opened");
        return CommandResult.Handled;
    }

    Task<CommandResult> CloseStoreAsync(GameConnectionContext context, Packet packet)
    {
        try { ExpertJobRequests.ReadEmpty(packet.Body); }
        catch (InvalidDataException) { return Task.FromResult(CommandResult.Unhandled); }
        var character = context.Owner.Execute(() => context.Session.Selected);
        context.Session.CloseExpertJobStore();
        if (character is not null && stores is not null && stores.TryClose(character.Id, out _))
            presence?.Publish(context.Owner, context.Sender, (ushort)NotiPacketType.CLOSE_EXPERT_JOB_STORE,
                ExpertJobPackets.CloseStoreNotice(character.Id), "expert_job_store_closed");
        return Task.FromResult(CommandResult.Handled);
    }

    async Task<CommandResult> EnterStoreAsync(GameConnectionContext context, Packet packet)
    {
        try { _ = ExpertJobRequests.ReadEnterStore(packet.Body); }
        catch (InvalidDataException)
        {
            await context.Reply(CmdPacketType.ENTER_EXPERT_JOB_STORE, ExpertJobPackets.Fail(19), "expert_job_enter_malformed");
            return CommandResult.Handled;
        }
        ushort actor = ExpertJobRequests.ReadEnterStore(packet.Body);
        if (actor == 0 || stores is null || !stores.TryGet(actor, out var store))
        {
            await context.Reply(CmdPacketType.ENTER_EXPERT_JOB_STORE, ExpertJobPackets.Fail(19), "expert_job_enter_missing");
            return CommandResult.Handled;
        }
        var customer = context.Session.CaptureExpertJobTrade();
        if (customer is null || actor != customer.Id &&
            (online?.FindConnectionByCharacterId(actor) is not { } provider || !provider.Session.ExpertJobStoreOpen ||
                provider.Session.CaptureExpertJobTrade() is not { } owner || presence?.CanInteract(context.Owner, customer.Id,
                    provider.Session.ExpertJobOwner, owner.Id) != true))
        {
            await context.Reply(CmdPacketType.ENTER_EXPERT_JOB_STORE, ExpertJobPackets.Fail(19), "expert_job_enter_unavailable");
            return CommandResult.Handled;
        }
        context.Session.QuoteExpertJobStore(store);
        byte[] body = store.Mode == 3
            ? ExpertJobPackets.EnterStoreKind3(store.OwnerId, store.Endurance)
            : ExpertJobPackets.EnterStoreKind0(store.Grade, store.First, store.Endurance);
        await context.Reply(CmdPacketType.ENTER_EXPERT_JOB_STORE, body, "expert_job_enter");
        return CommandResult.Handled;
    }

    Task<CommandResult> UpgradeAsync(GameConnectionContext context, Packet packet) =>
        MachineAsync(context, packet, CmdPacketType.UPGRADE_DISJOINT_MACHINE, disjointer: true, upgrade: true);

    Task<CommandResult> RepairDisjointerAsync(GameConnectionContext context, Packet packet) =>
        MachineAsync(context, packet, CmdPacketType.REPAIR_DISJOINT_MACHINE, disjointer: true, upgrade: false);

    Task<CommandResult> RepairEnchanterAsync(GameConnectionContext context, Packet packet) =>
        MachineAsync(context, packet, CmdPacketType.REPAIR_EXPERT_JOB_STORE, disjointer: false, upgrade: false);

    async Task<CommandResult> MachineAsync(GameConnectionContext context, Packet packet, CmdPacketType opcode, bool disjointer,
        bool upgrade)
    {
        try { ExpertJobRequests.ReadEmpty(packet.Body); }
        catch (InvalidDataException)
        {
            await context.Reply(opcode, ExpertJobPackets.Fail(19), "expert_job_machine_malformed");
            return CommandResult.Handled;
        }
        if (context.Activity?.BlocksTownActions == true)
        {
            await context.Reply(opcode, ExpertJobPackets.Fail(19), "expert_job_machine_blocked");
            return CommandResult.Handled;
        }
        var current = context.Owner.Execute(() => context.Session.Selected);
        byte expected = disjointer ? (byte)3 : (byte)1;
        ushort error = 19;
        InventoryPlan? plan = null;
        int grade = 0, endurance = 0;
        if (current is { } character && character.ExpertJobType == expected)
        {
            if (upgrade)
            {
                if (ExpertJobCraft.TryPlanUpgrade(character, scripts, out plan, out grade, out endurance, out error))
                { }
            }
            else if (ExpertJobCraft.TryPlanRepair(character, scripts, out plan, out endurance, out error))
                grade = character.ExpertMachineGrade;
        }
        if (plan is null)
        {
            await context.Reply(opcode, ExpertJobPackets.Fail(error == 0 ? (ushort)19 : error), "expert_job_machine_rejected");
            return CommandResult.Handled;
        }
        var committed = context.Session.CommitExpertJobMachine(plan, grade, endurance);
        if (!committed.Accepted || committed.Receipt is not { } receipt)
        {
            await context.Reply(opcode, ExpertJobPackets.Fail(19), "expert_job_machine_commit");
            return CommandResult.Handled;
        }
        bool Guard() => context.Session.CanPublishInventory(receipt);
        await inventory.RefreshAsync(context, committed);
        await context.Notify(NotiPacketType.USERINFO, TownPackets.Actor(receipt.Character, context.Channel,
            adventureName: context.Session.ReadAdventureName(), premiumPcRoom: context.Session.PremiumPcRoom),
            "expert_job_machine_actor", Guard);
        await NotifyInfo(context, receipt.Character, Guard, "expert_job_machine_info");
        byte[] body = opcode switch
        {
            CmdPacketType.UPGRADE_DISJOINT_MACHINE => ExpertJobPackets.UpgradeMachineSuccess(receipt.Character.Inventory.Gold,
                (uint)receipt.Character.ExpertMachineGrade, (uint)receipt.Character.ExpertMachineEndurance),
            CmdPacketType.REPAIR_DISJOINT_MACHINE => ExpertJobPackets.RepairMachineSuccess(receipt.Character.Inventory.Gold,
                (uint)receipt.Character.ExpertMachineEndurance),
            _ => ExpertJobPackets.RepairStoreSuccess()
        };
        await context.Reply(opcode, body, "expert_job_machine_committed", Guard);
        return CommandResult.Handled;
    }

    Task NotifyInfo(GameConnectionContext context, DfoServer.Game.Characters.Character character, Func<bool> guard, string action) =>
        context.Notify(NotiPacketType.EXPERT_JOB_INFO, ExpertJobPackets.Info(character, scripts), action, guard);

    static (ushort Slot, uint ItemId, uint Count)[] Products(InventorySnapshot after, InventorySnapshot before,
        ExpertJobProduct[] planned)
    {
        var rows = new List<(ushort Slot, uint ItemId, uint Count)>();
        foreach (var product in planned)
            foreach (var item in after.Items.Where(item => item.Core.ItemId == product.ItemId && item.Position.List == InventoryListType.Main))
            {
                int increment = item.Core.Count - (before.Find(item.Position)?.Core.Count ?? 0);
                if (increment > 0)
                    rows.Add(((ushort)item.Position.Index, (uint)product.ItemId, (uint)increment));
            }
        return rows.ToArray();
    }
}
