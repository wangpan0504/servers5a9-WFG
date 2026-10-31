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
using DfoServer.Game.Events;
using DfoServer.Game.CashShopPromotions;

namespace DfoServer.Network.Projection;

internal sealed record TownCharacterProjection(Character Character, DateTimeOffset AppearanceTime);

internal sealed class TownProjection(TownCatalog towns, CharacterStatComputer stats, TownPresence? presence, EpicPieceCatalog pieces,
    MailboxProjection? mailbox = null, SettingsProjection? settings = null, QuestProjection? quests = null,
    SkillLearningCatalog? learning = null, TitleProjection? titles = null, AdventureProjection? adventure = null, TimeProvider? clock = null,
    IGuildPermitDeliverer? guildPermits = null, DfoServer.Game.Inventory.Services.DevilContractService? devil = null,
    CollectBoxProjection? collectBoxes = null, SkinStorageProjection? weaponSkins = null, DfoServer.Game.Party.PartyService? parties = null,
    EpicProductionProjection? epicProduction = null, DfoServer.GameWorld.Dungeons.DungeonCatalog? dungeons = null,
    MarriageProjection? marriage = null, ServerEventService? events = null, MileageShopCatalog? mileageShop = null,
    Func<CashShopPromotionCatalog>? promotionCatalog = null, CashShopPromotionPageService? promotionPages = null, ItemCatalog? items = null,
    DfoServer.Game.Inventory.Services.InventoryKnightShieldService? knightShields = null)
{
    const ushort JonathanVisibleEvent = 2395;
    internal bool MileageAvailable => mileageShop?.Available == true;
    internal DfoServer.Game.Inventory.Models.KnightShieldDeck KnightShieldDeck(Character character) =>
        knightShields?.EffectiveDeck(character) ?? character.Inventory.ShieldDeck;

    internal async Task ServerEventsAsync(GameConnectionContext context, Func<bool>? canSend = null,
        long? minimumPromotionRevision = null)
    {
        // 108替换整张活动目录；积分入口更新不能清掉其他活动和各自数值段。
        var openEvents = DfoServer.Game.Raids.RaidEntryEvents.OpenEvents(RaidWindows, timer.GetUtcNow());
        openEvents = [GuildHongbaoPackets.OpenEvent, JonathanVisibleEvent, .. openEvents];
        if (events?.Read(ServerEventService.NolanVisibilityId).Active == true)
            openEvents = [.. openEvents, ServerEventService.NolanVisibilityId];
        bool mileage = MileageAvailable && events?.Read(ServerEventService.MileageShopId).Active == true;
        if (mileage) openEvents = [.. openEvents, ServerEventService.MileageShopId];
        IReadOnlyList<EventInfoDesignPage> designPages = [];
        Func<bool>? publish = canSend;
        if (promotionCatalog is not null && promotionPages is not null)
        {
            if (canSend?.Invoke() == false) return;
            var catalog = promotionCatalog();
            var capture = context.Session.CaptureNativePromotionPage(catalog);
            if (capture is null) return;
            // 页面On独立于购买资格；Disabled仍由同一只读服务校验真实账本。
            var snapshot = promotionPages.Read(context.Owner, catalog, 10000);
            bool Current() => canSend?.Invoke() != false && context.Session.CanPublishNativePromotionPage(
                capture, promotionCatalog(), snapshot, minimumPromotionRevision);
            if (!Current()) return;
            designPages = PromotionNativeDesignPage.Create(snapshot) is { } page ? [page] : [];
            publish = Current;
        }
        byte[] eventInfo = SeriaLuckPackets.EventInfoCompressed(openEvents, activeEvents: [JonathanVisibleEvent], designPages: designPages);
        string action = canSend is null ? "seria_luck_event_initialize" : "server_events_changed";
        if (designPages.Count != 0)
            await context.Sender.SendProtectedRevalidated(0, (ushort)NotiPacketType.EVENT_INFO, eventInfo, action, publish!);
        else
            await context.NotifyProtected(NotiPacketType.EVENT_INFO, eventInfo, action, publish);
        if (mileage && context.Session.Selected is { } selected)
            await context.Notify(NotiPacketType.CASH_SHOP_PURCHASE_LIMIT_INFO,
                MileagePackets.Limits(selected.Inventory.AccountWallet, mileageShop!), "mileage_shop_limits", publish);
        if (publish?.Invoke() != false) context.ServerEvents?.Published(mileage);
    }
    internal DfoServer.Game.Guild.GuildPanelService? GuildPanels { get; set; }
    internal GuildEconomyProjection? GuildEconomy { get; set; }
    internal GuildNpcProjection? GuildNpcs { get; set; }
    internal GuildDungeonProjection? GuildDungeons { get; set; }
    readonly TimeProvider timer = clock ?? TimeProvider.System;
    internal async Task PvpGradeRecoveredAsync(GameConnectionContext context, Character expected, long epoch, DfoServer.Game.Pvp.PvpDisplayGrade before)
    {
        bool Current() => context.Owner.Execute(() => context.Session.Ready &&
            context.Session.SelectionEpoch == epoch && ReferenceEquals(context.Session.Selected, expected));
        var after = context.Owner.Execute<DfoServer.Game.Pvp.PvpDisplayGrade?>(() => Current() ? context.Session.ReadPvpGrade(expected) : null);
        if (after is not { } grade || grade == before) return;
        // 复用登录后的既有恢复时序；仅等级变化时更新本人Actor，不推进评分或替换角色引用。
        await context.Notify(NotiPacketType.USERINFO,
            TownPackets.Actor(context.Session.ReadSkillCharacter(expected), grade, context.Channel,
                timer.GetUtcNow().ToUnixTimeSeconds(), context.Session.ReadAdventureName(),
                premiumPcRoom: context.Session.PremiumPcRoom), "pvp_grade_recovered", Current);
    }

    public async Task OtherPlayerAsync(GameConnectionContext context, DfoServer.Game.Session.PublicCharacterSnapshot snapshot,
        Func<bool> canSend)
    {
        var character = snapshot.SkillCharacter;
        var now = timer.GetUtcNow();
        var actor = TownPackets.Actor(character, snapshot.PvpGrade, snapshot.Channel, now.ToUnixTimeSeconds(), snapshot.AdventureName,
            snapshot.Phase == DfoServer.Game.Session.SessionPhase.Town ? ActorSceneState.Town : ActorSceneState.Dungeon,
            snapshot.PremiumPcRoom);
        var player = TownPackets.OtherPlayer(character, stats.Compute(character), snapshot.AdventureLevel,
            snapshot.Channel, now);
        // 公开属性依赖同一目标的Actor身份，不能复用本人Player或接收者的资产。
        await context.Notify(NotiPacketType.USERINFO, actor, "other_player_actor", canSend);
        await context.Notify(NotiPacketType.USERINFO, player, "other_player_info", canSend);
        foreach (var set in EchonEquipmentPackets.Snapshot(character))
            await context.Notify(NotiPacketType.CUSTOM_ABILITY_PART_SET_OPTION, set, "other_player_echon_set", canSend);
    }
    public async Task<TownCharacterProjection> EnterAsync(GameConnectionContext context, bool resumeTown = false)
    {
        marriage?.PrepareInitialization(context);
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
            TownPackets.Actor(context.Session.ReadSkillCharacter(c), context.Session.ReadPvpGrade(c), context.Channel, appearanceTime.ToUnixTimeSeconds(), context.Session.ReadAdventureName(),
                premiumPcRoom: premiumPcRoom), "actor");
        context.Premiums?.RecordPremiumAppearance(c, appearanceTime.ToUnixTimeSeconds(), premiumPcRoom);
        // Actor先建立本人对象；穿戴表装载前恢复装备准入技能，避免光剑、拳套、扫把被客户端按空技能树卸下。
        if (c.Learning.Initialized)
            await context.Notify(NotiPacketType.SKILLINFO, SkillPackets.Initialized(context.Session.ReadSkillLearning(c)), "skill_info");
        await context.Notify(NotiPacketType.USERINFO,
            TownPackets.Player(context.Session.ReadSkillCharacter(c), stats.Compute(context.Session.ReadSkillCharacter(c)), context.Session.ReadAdventureLevel(), context.Channel, appearanceTime), "player");
        await PcRoomBuffProjection.PostAsync(context);
        await GuildTrainingProjection.PostAsync(context, clear: true);
        await DungeonEnterCountProjection.RefreshAsync(context, dungeons);
        // 本人Actor初始职位为6；成员名单和70均不会设置它，必须由127独立安装权限索引。
        await context.Notify(NotiPacketType.GUILD_MEMBER_INFO, [c.Guild?.Grade ?? (byte)100], "guild_member_grade_initialize");
        await DazzlementTowerProjection.StateAsync(context);
        if (c.Guild is { } membership)
            await context.Notify(NotiPacketType.GUILD_INFO, GuildPackets.Information(membership, context.Channel.ServerIndex,
                GuildPanels?.Read(context.Owner, c.Id, context.Channel.ServerIndex)), "guild_information_initialize");
        await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.Full(c.Inventory), "main_inventory_initialize");
        foreach (var set in EchonEquipmentPackets.Snapshot(c))
            await context.Notify(NotiPacketType.CUSTOM_ABILITY_PART_SET_OPTION, set, "echon_set_initialize");
        await context.Notify(NotiPacketType.TOTAL_ITEM_MAKING_SKILL_POINT, CompoundPackets.MakingSkills(c.Inventory.ItemMakingSkills), "item_making_skills_initialize");
        long consumableNow = timer.GetUtcNow().ToUnixTimeMilliseconds();
        if (ConsumableStatePackets.Restore(c.Inventory, DfoServer.Game.Inventory.Models.ConsumableStateKind.Cooldown, consumableNow) is { } cooldowns)
            await context.Notify(NotiPacketType.LOAD_COOLTIME_ITEM_INFO, cooldowns, "consumable_cooldowns_initialize");
        if (ConsumableStatePackets.Restore(c.Inventory, DfoServer.Game.Inventory.Models.ConsumableStateKind.Effect, consumableNow) is { } effects)
            await context.Notify(NotiPacketType.LOAD_EFFECT_ITEM_INFO, effects, "consumable_effects_initialize");
        if (context.BoosterUses is { } boosterUses) await boosterUses.InitializeAsync(c);
        await context.Notify(NotiPacketType.EQUIPMENT_RENTAL_LIST, RentalPackets.State(c.Inventory, DateTimeOffset.UtcNow), "rental_state_initialize");
        // 空历史也须初始化月轮山显示标记；后续所有356均由同一Builder保留它。
        await context.Notify(NotiPacketType.CLEAR_QUEST_LIST, TownPackets.CompletedQuests(c), "completed_quests");
        // 本人Player已建立后再发送，023D0060要求当前角色及盾牌UI对象均存在。
        if (c.Job == DfoServer.Game.Inventory.Services.InventoryKnightShieldService.Job)
            await context.Notify(NotiPacketType.SEND_DECK_INFO, KnightShieldPackets.Deck(KnightShieldDeck(c)),
                "knight_shield_deck_initialize");
        if (c.Learning.ExpertContractExpiresAt > 0)
            await context.Notify(NotiPacketType.CERA_SPECIALITEM, SkillPackets.ExpertContract(c.Learning.ExpertContractExpiresAt,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()), "expert_contract_initialize");
        await context.Notify(NotiPacketType.ITEM_LIST, CargoPackets.Full(c.Inventory, DfoServer.Game.Inventory.Models.InventoryListType.PersonalCargo), "personal_cargo_initialize");
        await context.Notify(NotiPacketType.ITEM_LIST, CargoPackets.Full(c.Inventory, DfoServer.Game.Inventory.Models.InventoryListType.AccountCargo), "account_cargo_initialize");
        await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.SpecialFull(c.Inventory, DfoServer.Game.Inventory.Models.InventoryListType.Avatar), "avatar_inventory_initialize");
        await context.Notify(NotiPacketType.SRC_AVATAR_INFO_OF_CLONE, ClearAvatarPackets.Sources(c.Inventory), "clear_avatar_sources_initialize");
        await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.SpecialFull(c.Inventory, DfoServer.Game.Inventory.Models.InventoryListType.Pet), "pet_inventory_initialize");
        await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.SpecialFull(c.Inventory, DfoServer.Game.Inventory.Models.InventoryListType.GuildMedal), "medal_inventory_initialize");
        await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.SpecialFull(c.Inventory, DfoServer.Game.Inventory.Models.InventoryListType.Talisman), "talisman_inventory_initialize");
        if (Math.Max(c.TeleportCooldowns.PartyAuraUntil, Math.Max(c.TeleportCooldowns.AuraUntil, c.TeleportCooldowns.UpgradeAuraUntil)) >
            timer.GetUtcNow().ToUnixTimeMilliseconds())
            await context.Notify(NotiPacketType.AURA_TYPE_COOLTIME, TeleportPackets.Cooldown(c, timer.GetUtcNow().ToUnixTimeMilliseconds()),
                "teleport_aura_cooldown_initialize");
        await InventoryProjection.RestoreLocksAsync(context, c.Inventory, c.Inventory.Items.Select(item => item.Position));
        await context.Notify(NotiPacketType.UPGRADE_CARRY_GOLD, CurrencyPackets.GoldLimit(c.Inventory), "gold_limit_initialize");
        await context.Notify(NotiPacketType.CERA, CurrencyPackets.Cera(c.Inventory.AccountWallet), "account_cera_initialize");
        await DungeonCardMembershipProjection.RefreshAsync(context, frozenAt: appearanceTime);
        await context.Notify(NotiPacketType.MILEAGE_DATA, MileagePackets.State(c.Inventory.AccountWallet), "account_mileage_initialize");
        await context.Notify(NotiPacketType.EPIC_BOOK_INFO, EpicBookPackets.Info(c.Inventory.EpicPieces, pieces), "epic_book_initialize");
        if (epicProduction is not null)
            await epicProduction.InitializeAsync(context);
        if (collectBoxes is not null)
            await collectBoxes.InitializeAsync(context);
        // #233 实验：下发今日强袭模式索引，避免客户端以默认0xFF判定“今天无法入场”。
        await context.Notify(NotiPacketType.ASSAULT_MODE_DAILY_INFO, AssaultModePackets.Today(timer, context.Session.AssaultDailyCounts(timer.GetLocalNow())), "assault_mode_daily_info");
        // DungeonIncountInfo 表内全部限次副本：入城时按已持久的日/周使用量逐个下发 NOTI 550（日剩余→+0x6C68，周剩余→+0x6C7C）。
        foreach (var count in context.Session.EnterCounts())
            await context.Notify(NotiPacketType.DUNGEON_ENTER_COUNT_INFO, DungeonEnterCountPackets.Info(count.Dungeon, count.Daily, count.Weekly),
                "dungeon_enter_count_initialize");
        await DimensionEntranceProjection.RefreshAsync(context);
        await DimensionEntranceProjection.AncientAsync(context);
        if (mailbox is not null)
            await mailbox.InitializeAsync(context);
        if (marriage is not null)
            await marriage.InitializeAsync(context);
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
        await TrainingSkillsAsync(context, initializing: true);
        await LocationAsync(context, c, initial: true, transition: !resumeTown);
        await ServerEventsAsync(context);
        await context.Notify(NotiPacketType.FATIGUE, FatiguePackets.Current(context.Session.FatigueForClient(c)), "fatigue_after_town_hud");
        await context.Notify(NotiPacketType.BOOSTER_GAGE, SeriaLuckPackets.Gauge(c.Inventory.AccountWallet.SeriaLuck,
            c.Inventory.AccountWallet.SeriaLuck), "seria_luck_gauge_initialize");
        RaidClearProjection.Refresh(context, initialize: true);
        // 03BD1990读取单个u8后调用01DC6480，释放寻找攻坚队的初始忙状态；客户端保留原生3秒冷却。
        await context.Notify(NotiPacketType.POST_CHARAC_INFO, [0], "post_character_info");
        if (resumeTown)
        {
            // 124会恢复换频道前的地图；最终24必须在它之后绑定本人，不能在恢复前宣布场景完成。
            await context.Notify(NotiPacketType.ENTER_GAMEWORLD_COMPLETE, [], "channel_reentry_world_complete");
            await context.Notify(NotiPacketType.AREA_USERS, TownPackets.Transition(c), "channel_reentry_transition");
        }
        // 显式恢复资源默认指令，不能只重发缺省field4并假定按住方向已经注册。
        if (c.Learning.Initialized)
            await context.Notify(NotiPacketType.SKILLINFO, SkillPackets.Initialized(context.Session.ReadSkillLearning(c),
                learning is null ? null : id => learning.Get(c.Job, id).DefaultCommand), "skill_commands_after_scene");
        if (c.Job == 9 && c.Learning.Initialized)
            await context.Notify(NotiPacketType.COMBO_SKILL_INFO, SkillPackets.Combos(context.Session.ReadSkillLearning(c)), "skill_combo_initialize");
        if (learning is not null)
            await SkillGuideProjection.PublishAsync(context, c, learning);
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
        // 返回实际用于普通Actor/Player/技能投影的快照，不能拿随后变化的Selected冒充客户端已安装资料。
        return new(c, appearanceTime);
    }

    // GM 团本入场时段（与 GmWebData 共用同一实例）；为空时三张团队频道卡都按关闭下发。
    internal DfoServer.Game.Raids.RaidEntryStore? RaidWindows { get; set; }
    internal DfoServer.Game.Raids.RaidWeeklyCycle? RaidClearLukeCycle { get; set; }

    public async Task QuestCycleAsync(GameConnectionContext context)
    {
        if (context.Session.AdvanceQuestCycle(timer.GetUtcNow()) is not { } cycle) return;
        bool Guard() => context.Session.CanPublishQuestCycle(cycle);
        if (!cycle.Before.ActiveMainQuests.SequenceEqual(cycle.Character.ActiveMainQuests))
            await context.Notify(NotiPacketType.ACCEPTED_QUEST, QuestPackets.ActiveList(cycle.Character.ActiveMainQuests), "quest_auto_daily_cycle", Guard);
        await context.Notify(NotiPacketType.CLEAR_QUEST_LIST, TownPackets.CompletedQuests(cycle.Character), "quest_daily_cycle", Guard);
        if (cycle.Phase == DfoServer.Game.Session.SessionPhase.Town && quests is not null)
            await quests.RefreshAsync(context, cycle.Character, Guard);
    }

    public async Task LegacyServiceDayAsync(GameConnectionContext context)
    {
        var capture = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch));
        if (capture.Selected is not { } character) return;
        int day = DfoServer.Game.Inventory.Models.ShopPurchaseCount.Today(timer.GetUtcNow());
        bool Guard() => context.Owner.Execute(() => context.Session.Ready && context.Session.SelectionEpoch == capture.SelectionEpoch &&
            context.Session.Selected?.Id == character.Id && DfoServer.Game.Inventory.Models.ShopPurchaseCount.Today(timer.GetUtcNow()) == day);
        foreach (var count in context.Session.EnterCounts())
            await context.Notify(NotiPacketType.DUNGEON_ENTER_COUNT_INFO, DungeonEnterCountPackets.Remaining(count), "dungeon_enter_count_daily", Guard);
        await DungeonEnterCountProjection.RefreshAsync(context, dungeons, Guard);
        if (adventure is not null) await adventure.InformationAsync(context);
        if (context.Premiums is { } premiums) await premiums.RefreshAsync();
        if (context.BoosterUses is { } boosters) await boosters.RefreshAsync();
        if (marriage is not null) await marriage.DailyAsync(context);
    }

    public async Task AssaultServiceDayAsync(GameConnectionContext context)
    {
        var capture = context.Owner.Execute(() => (context.Session.Selected, context.Session.SelectionEpoch));
        if (capture.Selected is not { } character) return;
        int day = DfoServer.Game.Characters.FatigueDailyReset.Day(timer.GetUtcNow(), timer.LocalTimeZone);
        bool Guard() => context.Owner.Execute(() => context.Session.Ready && context.Session.SelectionEpoch == capture.SelectionEpoch &&
            context.Session.Selected?.Id == character.Id && DfoServer.Game.Characters.FatigueDailyReset.Day(timer.GetUtcNow(), timer.LocalTimeZone) == day);
        await context.Notify(NotiPacketType.ASSAULT_MODE_DAILY_INFO,
            AssaultModePackets.Today(timer, context.Session.AssaultDailyCounts(timer.GetLocalNow())), "assault_mode_daily_reset", Guard);
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
        // 赛丽亚房间的出门城镇：特殊区域与团队频道按频道落点，普通频道与无频道按存档返回地区（与 TownTravel.LeaveRoom 同一规则，#740）。
        if (towns.IsRoom(c.Position, completed) &&
            (ChannelAreaLanding.RoomExit(towns, context.Session.ChannelEntry, c.Position, c.Level, completed) ?? c.Return ?? towns.DefaultReturn(c.Position, completed)) is { } back)
            await context.Notify(NotiPacketType.PREV_VILLAGE, TownPackets.RoomReturnTown(back), "room_return_town", canSend);
    }

    public async Task TravelAsync(GameConnectionContext context, TownTravelReceipt receipt, bool auraTeleport = false, bool partyTeleport = false)
    {
        bool CanSend() => context.Session.CanPublishTravel(receipt);
        if (marriage is not null)
            await marriage.WeddingSceneAsync(context, receipt.Character, arrival: false, CanSend);
        auraTeleport |= partyTeleport;
        byte[]? prepare = partyTeleport ? TeleportPackets.PrepareAuraEffect(receipt.Character.Id) : null;
        byte[]? departure = auraTeleport ? TeleportPackets.AuraEffect(receipt.Character.Id, arrival: false) : null;
        byte[]? arrival = auraTeleport ? TeleportPackets.AuraEffect(receipt.Character.Id, arrival: true) : null;
        if (prepare is not null)
        {
            await context.Notify(NotiPacketType.SHOW_EFFECT, prepare, "teleport_party_prepare", CanSend);
            await context.Notify(NotiPacketType.PARTY_TELEPORT_STATUS, TeleportPackets.PartyReady(receipt.Character),
                "teleport_party_ready", CanSend);
        }
        if (departure is not null)
            await context.Notify(NotiPacketType.SHOW_EFFECT, departure, "teleport_aura_departure", CanSend);
        // 清场也绑定原提交；不能用迟到的旅行结果移除同连接后来加入的角色。
        Task? detached = context.Owner.Execute(() =>
        {
            if (!CanSend())
                return null;
            // 旧区域仅移除地图实体，保留Actor及队伍引用；真正离线/换角仍使用完整清理。
            presence?.Remove(context.Sender, departure, prepare, TownPackets.Position(receipt.Character, true));
            return context.Sender.ClearScene(SceneActorClear.Preserve);
        });
        if (detached is null)
            return;
        await detached;
        await TrainingSkillsAsync(context, canSend: CanSend);
        await LocationAsync(context, receipt.Character, canSend: CanSend);
        bool joined = context.Owner.Execute(() =>
        {
            if (!CanSend()) return false;
            presence?.Join(context.Owner, context.Sender, receipt.Character, context.Channel, context.Session.ReadAdventureName(), pvpGrade: context.Session.ReadPvpGrade(receipt.Character));
            if (arrival is not null)
                presence?.Publish(context.Owner, context.Sender, (ushort)NotiPacketType.SHOW_EFFECT, arrival, "teleport_aura_arrival_peer");
            return true;
        });
        // Join已排入远端Actor/区域入场；本人和同屏接收者都在目标角色建立后才收到到达特效。
        if (arrival is not null)
            await context.Notify(NotiPacketType.SHOW_EFFECT, arrival, "teleport_aura_arrival", CanSend);
        if (joined && CanSend() && parties is not null)
            parties.Publish(parties.SceneChanged(context.Session));
        if (marriage is not null)
        {
            await marriage.WeddingSceneAsync(context, receipt.Character, arrival: true, CanSend);
            await marriage.HomeSceneAsync(context, receipt.Character, CanSend);
        }
    }

    // 在地区切换触发客户端训练对象绑定前，先给出独立技能快照；不覆盖普通 NOTI19 双树。
    internal static async Task TrainingSkillsAsync(GameConnectionContext context, bool initializing = false, Func<bool>? canSend = null)
    {
        if (context.Session.ReadPvpTrainingSkills(initializing) is not {State: { } state, Receipt: { } receipt}) return;
        bool Guard() => (canSend?.Invoke() ?? true) && context.Session.CanPublishPvpTrainingSkills(receipt, initializing);
        await context.Notify(NotiPacketType.ARCADE_MODE_SKILL_USE_STATUS, [0], "pvp_training_skill_status", Guard);
        await context.Notify(NotiPacketType.ARCADE_MODE_SKILL_DATA, PvpTrainingPackets.Skills(state), "pvp_training_skill_snapshot", Guard);
    }

    public void Join(GameConnectionContext context, Func<bool>? canJoin = null)
    {
        var captured = context.Owner.Execute(() =>
            context.Session.Ready && (canJoin?.Invoke() ?? true) ? context.Session.Selected : null);
        if (captured is null || context.Session.CaptureGuildPanelQuery() is not { } query) return;
        // 面板会逐个捕获在线会友，必须在本人账号锁外读取；安装场景前复验原角色和旅行回执。
        var panel = GuildDungeons is null ? null : GuildPanels?.Read(context.Owner, captured.Id, context.Channel.ServerIndex);
        context.Owner.Execute(() =>
        {
            if (!query.IsCurrent() || !ReferenceEquals(context.Session.Selected, captured) || !(canJoin?.Invoke() ?? true)) return false;
            if (panel is not null)
                GuildDungeons?.Post(context, panel, query.IsCurrent);
            presence?.Join(context.Owner, context.Sender, captured, context.Channel, context.Session.ReadAdventureName(), pvpGrade: context.Session.ReadPvpGrade(captured));
            GuildEconomy?.Post(context, force: true);
            return true;
        });
    }

    internal void QueueRaidDeparture(GameConnectionContext context, Character character, Func<bool> current)
    {
        using var publication = context.Sender.QueuePublication(current);
        presence?.Remove(context.Sender, destinationArea: TownPackets.Position(character, true));
        // QueuePublication使该有界队列标记不创建需要后台等待的Task。
        context.Sender.ClearScene(SceneActorClear.Preserve).GetAwaiter().GetResult();
    }

    internal static void QueuePvpReturn(GameConnectionContext context, Character character, Func<bool> current)
    {
        // 清席仅撤销房间引用；必须显式恢复城镇场景，避免仍在练习场时重置读取空房间。
        using var publication = context.Sender.QueuePublication(current);
        context.Sender.Post(0, (ushort)NotiPacketType.USER_STATE, TownPackets.ActorState(character.Id), "pvp_return_town_state", current);
        context.Sender.Post(0, (ushort)NotiPacketType.USER_AREA, TownPackets.Position(character, true), "pvp_return_town_area", current);
        context.Sender.Post(0, (ushort)NotiPacketType.AREA_USERS, TownPackets.Transition(character), "pvp_return_town_scene", current);
    }

    internal void QueueRaidArrival(GameConnectionContext context, Character character, Func<bool> current)
    {
        context.Sender.Post(0, (ushort)NotiPacketType.USER_AREA, TownPackets.Position(character, true), "raid_waiting_area", current);
        context.Sender.Post(0, (ushort)NotiPacketType.AREA_USERS, TownPackets.Transition(character), "raid_waiting_transition", current);
        presence?.Join(context.Owner, context.Sender, character, context.Channel, context.Session.ReadAdventureName(), pvpGrade: context.Session.ReadPvpGrade(character));
    }

    public async Task ReturnToSelectionAsync(GameConnectionContext context, Func<bool>? canReturn = null)
    {
        DfoServer.Game.Party.PartySnapshot? departedParty = null;
        long returnedEpoch = 0;
        var notices = new DfoServer.Game.Session.GameSession.DepartureNotices();
        Exception? failure = null;
        bool returning;
        try
        {
            returning = context.Owner.Execute(() =>
            {
                if (canReturn?.Invoke() == false) return false;
                departedParty = parties?.Current(context.Session);
                context.Session.ReturnToRoster(notices);
                returnedEpoch = context.Session.SelectionEpoch;
                context.Activity?.Reset();
                return true;
            });
        }
        catch (Exception error) { failure = error; throw; }
        finally { notices.Publish(failure); }
        if (!returning) return;
        if (context.LostLand is not null) await context.LostLand.StopAsync();
        parties?.Detach(context.Session);
        await DetachAsync(context);
        if (departedParty is not null)
            await context.Notify(NotiPacketType.PARTY_INFO, PartyPackets.Information(departedParty.Id,
                context.Channel.ServerIndex, checked((byte)context.Channel.Number), [], departedParty.Leader, true),
                "party_return_selection", () => context.Session.Selected is null && context.Session.SelectionEpoch == returnedEpoch);
        context.RosterRequested = true;
        context.SelectionRosterPushed = true;
        await context.Reply(CmdPacketType.RETURN_SELECT_CHARACTER, SessionPackets.ReturnToSelection(), "return_selection_ack");
        var roster = context.Session.PrepareRoster();
        await context.Notify(NotiPacketType.USERINFO, CharacterPackets.Roster(roster.Entries.ToArray(), roster.Capacity, items), "return_selection_roster",
            () => context.Session.CanPublishRoster(roster));
        context.Session.MarkRosterPublished(roster);
    }

    public async Task<bool> DetachForDungeonSelectionAsync(GameConnectionContext context, DungeonSelectionReceipt receipt,
        IReadOnlySet<ushort>? preservePartyActors = null)
    {
        Task? detached = context.Owner.Execute(() =>
        {
            if (!context.Session.CanPublishDungeonSelection(receipt))
                return null;
            // 组队及团本选图只退城镇实体；删除Actor会清掉队伍槽并使客户端重新创建P2P peer。
            presence?.Remove(context.Sender, preserveActors: preservePartyActors);
            return context.Sender.ClearScene(preservePartyActors is not null ? SceneActorClear.Hide : SceneActorClear.Delete);
        });
        if (detached is null)
            return false;
        await detached;
        if (context.Session.CanPublishDungeonSelection(receipt)) GuildDungeonPartyBuffProjection.Post(context, clear: true);
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
                presence?.Join(context.Owner, context.Sender, receipt.Character, context.Channel, context.Session.ReadAdventureName(), pvpGrade: context.Session.ReadPvpGrade(receipt.Character));
            return true;
        });
    }

    public async Task<bool> RestoreDungeonSelectionAsync(GameConnectionContext context, CmdPacketType cancellation)
    {
        var state = context.Owner.Execute<(DungeonSelectionReceipt Receipt, int Legend)?>(() =>
        {
            var session = context.Session;
            if (context.BlocksTownActions || !session.Ready || session.Phase != DfoServer.Game.Session.SessionPhase.MapSelect ||
                session.Selected is not { } character || session.CurrentDungeonSelection is not { IsTutorial: false, IsClassExperience: false } selection ||
                session.CurrentParty?.Invoke() is not null)
                return null;
            return (new(character, context.Owner.Generation, session.SelectionEpoch, selection.Id), selection.HellLegendDungeonId ?? 0);
        });
        if (state is not { } current) return false;
        bool Guard() => context.Session.CanPublishDungeonSelection(current.Receipt) && context.Session.CurrentParty?.Invoke() is null;
        // 取消ACK没有界面恢复逻辑；重发正常27请求刷新选图，保留本次选择身份及冻结返回点。
        await context.Reply(cancellation, [1], "quick_party_cancelled", Guard);
        await GriefTowerProjection.SelectionAsync(context, dungeons, current.Legend, "quick_party_selection_restored", Guard);
        return true;
    }

    public async Task ReturnFromFormalDungeonAsync(GameConnectionContext context, TownTravelReceipt receipt, string action)
    {
        if (receipt.RaidBattleChange is not null) context.Session.PublishRaidCombat();
        bool CanSend() => context.Session.CanPublishTravel(receipt);
        if (CanSend()) GuildDungeonPartyBuffProjection.Post(context, clear: true);
        await context.Notify(NotiPacketType.USER_STATE, TownPackets.ActorState(receipt.Character.Id), "formal_return_town_state", CanSend);
        // 返城重建Actor后补发网吧特权buff；副本内的buff栏不会保留系统buff。
        await PcRoomBuffProjection.PostAsync(context, CanSend);
        await GuildTrainingProjection.PostAsync(context, clear: true);
        await context.Notify(NotiPacketType.AREA_USERS, TownPackets.Transition(receipt.Character), action, CanSend);
        if (context.Dazzlement is { HasBindings: true })
            await DazzlementTowerProjection.StateAsync(context, CanSend);
        // 城镇场景绑定后重新投影当前任务，不依赖重新选角。
        if (quests is not null)
            await quests.RefreshAsync(context, receipt.Character, CanSend);
        if (context.Premiums is { } premiums) await premiums.StaminaAsync(receipt.Character, CanSend);
        if (mailbox is not null) await mailbox.RestoreAsync(context, CanSend);
        Join(context, CanSend);
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

    public async Task SharedItemEffectAsync(GameConnectionContext context, int itemId, byte outcome, Func<bool> guard)
    {
        if (!guard() || context.Session.Selected is not { } character) return;
        byte[] body = SharedEffectItemPackets.Effect(character.Id, itemId, outcome);
        await context.Notify(NotiPacketType.USE_SHARED_EFFECT_ITEM, body, "shared_item_effect", guard);
        if (guard()) presence?.Publish(context.Owner, context.Sender, (ushort)NotiPacketType.USE_SHARED_EFFECT_ITEM,
            body, "shared_item_effect");
    }

    public void Move(GameConnectionContext context, ushort scaledSpeed = 100)
    {
        if (context.Session.Selected is { } c)
            presence?.Move(context.Owner, context.Sender, c, scaledSpeed);
    }

    public void Remove(GameConnectionContext context) => presence?.Remove(context.Sender);
    public void Refresh(GameConnectionContext context, bool marriageOnly = false)
    {
        if (context.Session.Selected is { } c)
            presence?.Refresh(context.Owner, context.Sender, c, context.Session.ReadAdventureName(), marriageOnly: marriageOnly, pvpGrade: context.Session.ReadPvpGrade(c));
    }
    public async Task DetachAsync(GameConnectionContext context)
    {
        Remove(context);
        await context.Sender.ClearScene();
    }
}
