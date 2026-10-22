using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Characters;
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
    MailboxProjection? mailbox = null, SettingsProjection? settings = null, QuestProjection? quests = null,
    SkillLearningCatalog? learning = null, TitleProjection? titles = null, AdventureProjection? adventure = null, TimeProvider? clock = null,
    IGuildPermitDeliverer? guildPermits = null, DfoServer.Game.Inventory.Services.DevilContractService? devil = null,
    CollectBoxProjection? collectBoxes = null, SkinStorageProjection? weaponSkins = null)
{
    readonly TimeProvider timer = clock ?? TimeProvider.System;
    public async Task EnterAsync(GameConnectionContext context, bool resumeTown = false)
    {
        if (devil is not null) context.Session.InitializeDevilContract(devil, timer.GetUtcNow());
        titles?.PrepareInitialization(context);
        adventure?.Prepare(context);
        var settingReceipt = settings?.Capture(context);
        var c = context.Session.Selected ?? throw new InvalidOperationException("No selected character");
        await context.Reply(CmdPacketType.SELECT_CHARACTER, TownPackets.Select(c, context.Session.FatigueForClient(c)), "select_character");
        byte[] hoverPermission = DungeonPackets.Permission(WorldMapHoverDungeons.DungeonIds(towns));
        if (hoverPermission.Length > 2)
            await context.Notify(NotiPacketType.DUNGEON_PERMISSION, hoverPermission, "worldmap_hover_dungeon_permission");
        if (c.AcceptedStoryLevel > 0)
            await context.Notify(NotiPacketType.STORY_DIGEST_INFO, TownPackets.StoryLevel(c), "accepted_story_level");
        await context.Notify(NotiPacketType.CREATURE_ITEM_LIST, InventoryPackets.Creatures(c.Inventory), "creature_details_initialize");
        // 首次创建本人UI前先初始化主设置；地区绑定后仍回放完整设置，补齐依赖角色实体的应用。
        if (settings is not null && settingReceipt is not null)
            await settings.PreloadGameOptionsAsync(context, settingReceipt);
        var appearanceTime = timer.GetUtcNow();
        bool premiumPcRoom = context.Session.PremiumPcRoom;
        await context.Notify(NotiPacketType.USERINFO,
            TownPackets.Actor(c, context.Channel, appearanceTime.ToUnixTimeSeconds(), context.Session.ReadAdventureName(),
                premiumPcRoom: premiumPcRoom), "actor");
        context.Premiums?.RecordPremiumAppearance(c, appearanceTime.ToUnixTimeSeconds(), premiumPcRoom);
        await context.Notify(NotiPacketType.USERINFO,
            TownPackets.Player(c, stats.Compute(c), context.Session.ReadAdventureLevel(), context.Channel, appearanceTime), "player");
        if (c.Guild is { } membership)
            await context.Notify(NotiPacketType.GUILD_INFO, GuildPackets.Information(membership, context.Channel.ServerIndex), "guild_information_initialize");
        await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.Full(c.Inventory), "main_inventory_initialize");
        long consumableNow = timer.GetUtcNow().ToUnixTimeMilliseconds();
        if (ConsumableStatePackets.Restore(c.Inventory, DfoServer.Game.Inventory.Models.ConsumableStateKind.Cooldown, consumableNow) is { } cooldowns)
            await context.Notify(NotiPacketType.LOAD_COOLTIME_ITEM_INFO, cooldowns, "consumable_cooldowns_initialize");
        if (ConsumableStatePackets.Restore(c.Inventory, DfoServer.Game.Inventory.Models.ConsumableStateKind.Effect, consumableNow) is { } effects)
            await context.Notify(NotiPacketType.LOAD_EFFECT_ITEM_INFO, effects, "consumable_effects_initialize");
        if (context.BoosterUses is { } boosterUses) await boosterUses.InitializeAsync(c);
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
        await context.Notify(NotiPacketType.SRC_AVATAR_INFO_OF_CLONE, ClearAvatarPackets.Sources(c.Inventory), "clear_avatar_sources_initialize");
        await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.SpecialFull(c.Inventory, DfoServer.Game.Inventory.Models.InventoryListType.Pet), "pet_inventory_initialize");
        await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.SpecialFull(c.Inventory, DfoServer.Game.Inventory.Models.InventoryListType.GuildMedal), "medal_inventory_initialize");
        if (c.TeleportCooldowns.AuraUntil > timer.GetUtcNow().ToUnixTimeMilliseconds())
            await context.Notify(NotiPacketType.AURA_TYPE_COOLTIME, TeleportPackets.Cooldown(c, timer.GetUtcNow().ToUnixTimeMilliseconds()),
                "teleport_aura_cooldown_initialize");
        await InventoryProjection.RestoreLocksAsync(context, c.Inventory, c.Inventory.Items.Select(item => item.Position));
        await context.Notify(NotiPacketType.UPGRADE_CARRY_GOLD, CurrencyPackets.GoldLimit(c.Inventory), "gold_limit_initialize");
        await context.Notify(NotiPacketType.CERA, CurrencyPackets.Cera(c.Inventory.AccountWallet), "account_cera_initialize");
        await DungeonCardMembershipProjection.RefreshAsync(context, frozenAt: appearanceTime);
        await context.Notify(NotiPacketType.EPIC_BOOK_INFO, EpicBookPackets.Info(c.Inventory.EpicPieces, pieces), "epic_book_initialize");
        if (collectBoxes is not null)
            await collectBoxes.InitializeAsync(context);
        // #233 实验：下发今日强袭模式索引，避免客户端以默认0xFF判定“今天无法入场”。
        await context.Notify(NotiPacketType.ASSAULT_MODE_DAILY_INFO, AssaultModePackets.Today(timer, context.Session.AssaultDailyCounts(timer.GetLocalNow())), "assault_mode_daily_info");
        if (mailbox is not null)
            await mailbox.InitializeAsync(context);
        if (titles is not null)
            await titles.InitializeAsync(context);
        if (adventure is not null)
            await adventure.InformationAsync(context, initialization: true);
        // 拍卖行服务开启通知：本服尚未实现拍卖行玩法，先按已确认的 183 包体发送，用于确认客户端是否以它作为拍卖行 UI 的前置。
        await context.Notify(NotiPacketType.AUCTION_NOTIFY_AUCTION_SERVICE, AuctionPackets.ServiceNotice(AuctionPackets.ServiceTypeZero), "auction_service_notice_0");
        await context.Notify(NotiPacketType.AUCTION_NOTIFY_AUCTION_SERVICE, AuctionPackets.ServiceNotice(AuctionPackets.ServiceTypeOne), "auction_service_notice_1");
        await context.Notify(NotiPacketType.USER_STATE, TownPackets.ActorState(c.Id), "actor_town_state");
        // NOTI1153：技能窗职业变更菜单的已变更次数(报价索引)与等级(012A6DAA 等级>=50拒绝)。
        await context.Notify(NotiPacketType.CHARACTER_MANAGE_INFO,
            GrowUpChangePackets.ManageInfo(context.Session.GrowUpChangeCount(), c.Level), "growup_change_info");
        await LocationAsync(context, c, initial: true, transition: !resumeTown);
        await context.NotifyProtected(NotiPacketType.EVENT_INFO, SeriaLuckPackets.EventInfoCompressed(), "seria_luck_event_initialize");
        await context.Notify(NotiPacketType.FATIGUE, FatiguePackets.Current(context.Session.FatigueForClient(c)), "fatigue_after_town_hud");
        await context.Notify(NotiPacketType.BOOSTER_GAGE, SeriaLuckPackets.Gauge(c.Inventory.AccountWallet.SeriaLuck,
            c.Inventory.AccountWallet.SeriaLuck), "seria_luck_gauge_initialize");
        if (resumeTown)
        {
            // 124会恢复换频道前的地图；最终24必须在它之后绑定本人，不能在恢复前宣布场景完成。
            await context.Notify(NotiPacketType.ENTER_GAMEWORLD_COMPLETE, [], "channel_reentry_world_complete");
            await context.Notify(NotiPacketType.AREA_USERS, TownPackets.Transition(c), "channel_reentry_transition");
        }
        // 显式恢复资源默认指令，不能只重发缺省field4并假定按住方向已经注册。
        if (c.Learning.Initialized)
            await context.Notify(NotiPacketType.SKILLINFO, SkillPackets.Initialized(c.Learning,
                learning is null ? null : id => learning.Get(c.Job, id).DefaultCommand), "skill_commands_after_scene");
        if (c.Job == 9 && c.Learning.Initialized)
            await context.Notify(NotiPacketType.COMBO_SKILL_INFO, SkillPackets.Combos(c.Learning), "skill_combo_initialize");
        if (settings is not null && settingReceipt is not null)
            await settings.InitializeAsync(context, settingReceipt);
        if (quests is not null)
            await quests.InitializeAsync(context, c);
        if (weaponSkins is not null) await weaponSkins.InitializeAsync(context, c);
        if (!resumeTown)
            await context.Notify(NotiPacketType.ENTER_GAMEWORLD_COMPLETE, [], "enter_gameworld_complete");
        // 建会认证的补投（顺序模式：创建者与被邀请方不同时在线）：入城序列发完后，若登记表里有指向
        // 本人的未过期申请就推 NOTI 0x2AD。公会规则归 GuildPermitDelivery，这里只调用可选投递器。
        if (guildPermits is not null)
            await guildPermits.DeliverPendingAsync(context);
    }

    public Task ReconcileQuestsAsync(GameConnectionContext context) => quests?.CloseReadyChildrenAsync(context,refreshList: true) ?? Task.CompletedTask;

    async Task LocationAsync(GameConnectionContext context, Character c, bool initial = false, Func<bool>? canSend = null, bool transition = true)
    {
        // 切图前不在旧场景移动实体；124 只属于选角入城。
        if (initial)
            await context.Notify(NotiPacketType.USER_POSITION, TownPackets.Position(c, false), "position", canSend);
        await context.Notify(NotiPacketType.USER_AREA, TownPackets.Position(c, true), "area", canSend);
        if (transition)
            await context.Notify(NotiPacketType.AREA_USERS, TownPackets.Transition(c), "transition", canSend);
        if (context.Premiums is { } premiums) await premiums.StaminaAsync(c, canSend);
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
                presence?.Join(context.Owner, context.Sender, receipt.Character, context.Channel, context.Session.ReadAdventureName());
            return true;
        });
    }

    public void Join(GameConnectionContext context)
    {
        if (context.Session.Ready && context.Session.Selected is { } c)
            presence?.Join(context.Owner, context.Sender, c, context.Channel, context.Session.ReadAdventureName());
    }

    public async Task ReturnToSelectionAsync(GameConnectionContext context, Func<bool>? canReturn = null)
    {
        bool returning = context.Owner.Execute(() =>
        {
            if (canReturn?.Invoke() == false) return false;
            context.Session.ReturnToRoster();
            context.Activity?.Reset();
            return true;
        });
        if (!returning) return;
        await DetachAsync(context);
        context.RosterRequested = true;
        context.SelectionRosterPushed = true;
        await context.Reply(CmdPacketType.RETURN_SELECT_CHARACTER, SessionPackets.ReturnToSelection(), "return_selection_ack");
        var roster = context.Session.PrepareRoster();
        await context.Notify(NotiPacketType.USERINFO, CharacterPackets.Roster(roster.Entries.ToArray(), roster.Capacity), "return_selection_roster",
            () => context.Session.CanPublishRoster(roster));
        context.Session.MarkRosterPublished(roster);
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
                presence?.Join(context.Owner, context.Sender, receipt.Character, context.Channel, context.Session.ReadAdventureName());
            return true;
        });
    }

    public async Task ReturnFromFormalDungeonAsync(GameConnectionContext context, TownTravelReceipt receipt, string action)
    {
        bool CanSend() => context.Session.CanPublishTravel(receipt);
        await context.Notify(NotiPacketType.USER_STATE, TownPackets.ActorState(receipt.Character.Id), "formal_return_town_state", CanSend);
        await context.Notify(NotiPacketType.AREA_USERS, TownPackets.Transition(receipt.Character), action, CanSend);
        // 城镇场景绑定后重新投影当前任务，不依赖重新选角。
        if (quests is not null)
            await quests.RefreshAsync(context, receipt.Character, CanSend);
        if (context.Premiums is { } premiums) await premiums.StaminaAsync(receipt.Character, CanSend);
        context.Owner.Execute(() =>
        {
            if (CanSend()) Join(context);
            return true;
        });
    }

    public async Task GiftEffectAsync(GameConnectionContext context, int itemId, Func<bool> guard)
    {
        if (!guard()) return;
        byte[] body = ConsumableStatePackets.Grant(itemId, ConsumableSpecialItems.TownGiftSeconds);
        presence?.Publish(context.Owner, context.Sender, (ushort)NotiPacketType.ADD_EFFECT_ITEM_INFO, body,
            "town_gift_effect", DfoServer.Game.Inventory.Services.ConsumableStatePolicy.AllowsTownGift);
        byte[] visual = ConsumableStatePackets.ItemEffect(context.Session.Selected!.Id, itemId);
        await context.Notify(NotiPacketType.ITEM_EFFECT, visual, "town_gift_visual", guard);
        if (guard()) presence?.Publish(context.Owner, context.Sender, (ushort)NotiPacketType.ITEM_EFFECT, visual, "town_gift_visual");
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
            presence?.Refresh(context.Owner, context.Sender, c, context.Session.ReadAdventureName());
    }
    public async Task DetachAsync(GameConnectionContext context)
    {
        Remove(context);
        await context.Sender.ClearScene();
    }
}
