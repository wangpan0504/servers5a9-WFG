using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Game.Characters;
using DfoServer.Game.Town;
using DfoServer.Game.Dungeons;

namespace DfoServer.Network.Projection;

internal sealed class TownProjection(TownCatalog towns, CharacterStatComputer stats, TownPresence? presence, EpicPieceCatalog pieces,
    MailboxProjection? mailbox = null, SettingsProjection? settings = null, QuestProjection? quests = null)
{
    public async Task EnterAsync(GameConnectionContext context)
    {
        var settingReceipt = settings?.Capture(context);
        var c = context.Session.Selected ?? throw new InvalidOperationException("No selected character");
        await context.Reply(CmdPacketType.SELECT_CHARACTER, TownPackets.Select(c), "select_character");
        if (c.AcceptedStoryLevel > 0)
            await context.Notify(NotiPacketType.STORY_DIGEST_INFO, TownPackets.StoryLevel(c), "accepted_story_level");
        await context.Notify(NotiPacketType.CREATURE_ITEM_LIST, InventoryPackets.Creatures(c.Inventory), "creature_details_initialize");
        await context.Notify(NotiPacketType.USERINFO, TownPackets.Actor(c), "actor");
        await context.Notify(NotiPacketType.USERINFO, TownPackets.Player(c, stats.Compute(c)), "player");
        await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.Full(c.Inventory), "main_inventory_initialize");
        await context.Notify(NotiPacketType.EQUIPMENT_RENTAL_LIST, RentalPackets.State(c.Inventory, DateTimeOffset.UtcNow), "rental_state_initialize");
        if (c.CompletedQuests.Length > 0 || c.AccountCompletedQuests.Length > 0)
            await context.Notify(NotiPacketType.CLEAR_QUEST_LIST, TownPackets.CompletedQuests(c), "completed_quests");
        if (c.Learning.Initialized)
            await context.Notify(NotiPacketType.SKILLINFO, SkillPackets.Initialized(c.Learning), "skill_info");
        if (c.Learning.ExpertContractExpiresAt > 0)
            await context.Notify(NotiPacketType.CERA_SPECIALITEM, SkillPackets.ExpertContract(c.Learning.ExpertContractExpiresAt,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()), "expert_contract_initialize");
        await context.Notify(NotiPacketType.ITEM_LIST, CargoPackets.Full(c.Inventory, DfoServer.Game.Inventory.Models.InventoryListType.PersonalCargo), "personal_cargo_initialize");
        await context.Notify(NotiPacketType.ITEM_LIST, CargoPackets.Full(c.Inventory, DfoServer.Game.Inventory.Models.InventoryListType.AccountCargo), "account_cargo_initialize");
        await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.SpecialFull(c.Inventory, DfoServer.Game.Inventory.Models.InventoryListType.Avatar), "avatar_inventory_initialize");
        await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.SpecialFull(c.Inventory, DfoServer.Game.Inventory.Models.InventoryListType.Pet), "pet_inventory_initialize");
        await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.SpecialFull(c.Inventory, DfoServer.Game.Inventory.Models.InventoryListType.GuildMedal), "medal_inventory_initialize");
        await InventoryProjection.RestoreLocksAsync(context, c.Inventory, c.Inventory.Items.Select(item => item.Position));
        await context.Notify(NotiPacketType.UPGRADE_CARRY_GOLD, CurrencyPackets.GoldLimit(c.Inventory), "gold_limit_initialize");
        await context.Notify(NotiPacketType.CERA, CurrencyPackets.Cera(c.Inventory.AccountWallet), "account_cera_initialize");
        await context.Notify(NotiPacketType.EPIC_BOOK_INFO, EpicBookPackets.Info(c.Inventory.EpicPieces, pieces), "epic_book_initialize");
        if (mailbox is not null)
            await mailbox.InitializeAsync(context);
        await context.Notify(NotiPacketType.USER_STATE, TownPackets.ActorState(c.Id), "actor_town_state");
        await LocationAsync(context, c, initial: true);
        // 场景绑定可能重建技能管理器；带自定义指令的角色在23/24后补回原字节。
        if (c.Learning.Initialized && c.Learning.Trees.Any(tree => tree.Skills.Any(skill => skill.Command.Length > 0)))
            await context.Notify(NotiPacketType.SKILLINFO, SkillPackets.Initialized(c.Learning), "skill_commands_after_scene");
        if (c.Job == 9 && c.Learning.Initialized)
            await context.Notify(NotiPacketType.COMBO_SKILL_INFO, SkillPackets.Combos(c.Learning), "skill_combo_initialize");
        if (settings is not null && settingReceipt is not null)
            await settings.InitializeAsync(context, settingReceipt);
        if (quests is not null)
            await quests.InitializeAsync(context, c);
        await context.Notify(NotiPacketType.ENTER_GAMEWORLD_COMPLETE, [], "enter_gameworld_complete");
    }

    public Task ReconcileQuestsAsync(GameConnectionContext context) => quests?.CloseReadyChildrenAsync(context,refreshList: true) ?? Task.CompletedTask;

    async Task LocationAsync(GameConnectionContext context, Character c, bool initial = false, Func<bool>? canSend = null)
    {
        // 切图前不在旧场景移动实体；124 只属于选角入城。
        if (initial)
            await context.Notify(NotiPacketType.USER_POSITION, TownPackets.Position(c.Id, c.Position, false), "position", canSend);
        await context.Notify(NotiPacketType.USER_AREA, TownPackets.Position(c.Id, c.Position, true), "area", canSend);
        await context.Notify(NotiPacketType.AREA_USERS, TownPackets.Transition(c.Id, c.Position), "transition", canSend);
        var completed = DfoServer.Game.Quests.QuestEligibility.Completed(c);
        if (towns.IsRoom(c.Position, completed) && (c.Return ?? towns.DefaultReturn(c.Position, completed)) is { } back)
            await context.Notify(NotiPacketType.PREV_VILLAGE, TownPackets.RoomReturnTown(back), "room_return_town", canSend);
    }

    public async Task TravelAsync(GameConnectionContext context, TownTravelReceipt receipt)
    {
        bool CanSend() => context.Session.CanPublishTravel(receipt);
        // 清场也绑定原提交；不能用迟到的旅行结果移除同连接后来加入的角色。
        Task? detached = context.Owner.Execute(() =>
        {
            if (!CanSend())
                return null;
            Remove(context);
            return context.Sender.ClearScene();
        });
        if (detached is null)
            return;
        await detached;
        await LocationAsync(context, receipt.Character, canSend: CanSend);
        context.Owner.Execute(() =>
        {
            if (CanSend())
                presence?.Join(context.Owner, context.Sender, receipt.Character);
            return true;
        });
    }

    public void Join(GameConnectionContext context)
    {
        if (context.Session.Ready && context.Session.Selected is { } c)
            presence?.Join(context.Owner, context.Sender, c);
    }

    public async Task<bool> DetachForDungeonSelectionAsync(GameConnectionContext context, DungeonSelectionReceipt receipt)
    {
        Task? detached = context.Owner.Execute(() =>
        {
            if (!context.Session.CanPublishDungeonSelection(receipt))
                return null;
            Remove(context);
            return context.Sender.ClearScene();
        });
        if (detached is null)
            return false;
        await detached;
        return context.Session.CanPublishDungeonSelection(receipt);
    }

    public async Task ReturnFromDungeonSelectionAsync(GameConnectionContext context, DungeonSelectionReceipt receipt)
    {
        bool CanSend() => context.Session.CanPublishDungeonSelection(receipt, returning: true);
        await context.Reply(CmdPacketType.BACK_2_VILLAGE, DungeonPackets.SelectionReturned(), "dungeon_selection_return_ack", CanSend);
        await context.Notify(NotiPacketType.USER_STATE, TownPackets.ActorState(receipt.Character.Id), "selection_return_town_state", CanSend);
        await LocationAsync(context, receipt.Character, canSend: CanSend);
        context.Owner.Execute(() =>
        {
            if (CanSend())
                presence?.Join(context.Owner, context.Sender, receipt.Character);
            return true;
        });
    }

    public async Task ReturnFromFormalDungeonAsync(GameConnectionContext context, TownTravelReceipt receipt, string action)
    {
        bool CanSend() => context.Session.CanPublishTravel(receipt);
        await context.Notify(NotiPacketType.AREA_USERS, TownPackets.Transition(receipt.Character.Id, receipt.Character.Position), action, CanSend);
        // 城镇场景绑定后重新投影当前任务，不依赖重新选角。
        if (quests is not null)
            await quests.RefreshAsync(context, receipt.Character, CanSend);
        context.Owner.Execute(() =>
        {
            if (CanSend()) Join(context);
            return true;
        });
    }

    public void Move(GameConnectionContext context)
    {
        if (context.Session.Selected is { } c)
            presence?.Move(context.Owner, context.Sender, c);
    }

    public void Remove(GameConnectionContext context) => presence?.Remove(context.Sender);
    public void Refresh(GameConnectionContext context)
    {
        if (context.Session.Selected is { } c)
            presence?.Refresh(context.Owner, context.Sender, c);
    }
    public async Task DetachAsync(GameConnectionContext context)
    {
        Remove(context);
        await context.Sender.ClearScene();
    }
}
