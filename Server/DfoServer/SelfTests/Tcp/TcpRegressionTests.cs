using System.Text.Json;
using System.Diagnostics;

namespace DfoServer.Testing;

internal static class TcpRegressionTests
{
    static readonly (string Name, Func<ServerOptions, string, TcpTestResources, Task> Run)[] Groups =
    [
        ("knight-shield", KnightShieldTcpTests.RunAsync),
        ("fiendwar-raid-flow", FiendwarRaidTcpTests.RunAsync),
        ("fiendwar-guide", FiendwarGuideTcpTests.RunAsync),
        ("fiendwar-recovery", FiendwarRecoveryTests.RunAsync),
        ("fiendwar-reward-rules", (_, _, resources) =>
        {
            FiendwarRewardClosureTests.Run(resources);
            return Task.CompletedTask;
        }),
        ("prey-guide", PreyGuideTests.RunAsync),
        ("prey-guide-flow", PreyGuideTcpTests.RunAsync),
        ("anton-raid-phase-timer-221-224", AntonRaidGapTcpTests.RunPhaseTimersAsync),
        ("anton-raid-reward-formal", (_, _, resources) =>
        {
            AntonResourceRewardTests.Run(resources);
            return Task.CompletedTask;
        }),
        ("quest-stage-growth", async (options, root, resources) =>
        {
            QuestStagePolicyTests.RunResources(resources);
            QuestSceneTests.RunStageBudgets(resources);
            QuestStageRepositoryTests.Run(resources);
            await QuestCompletionPublicationTests.RunStageAsync(resources);
            await QuestLifecycleTcpTests.RunStageAsync(options,root,resources);
        }),
        ("settings-snapshot-rules", (_, _, _) => { ClientSettingsSnapshotsTests.Run(); return Task.CompletedTask; }),
        ("equipment-power-rules", (_, _, _) => { EquipmentPowerScoreTests.RunCore(); return Task.CompletedTask; }),
        ("frame-crc-rules", (_, _, _) => { FrameCrcTests.Run(); return Task.CompletedTask; }),
        ("chat-rate-rules", (_, _, _) => { ChatServiceTests.RunRateRules(); return Task.CompletedTask; }),
        ("commands", (options, root, _) => CommandLineTcpTests.RunAsync(options, root)),
        ("startup", (options, root, _) => StartupTcpTests.RunAsync(options, root)),
        ("packet-capture", async (options, root, _) =>
        {
            await PacketCaptureTests.RunAsync();
            await PacketCaptureTcpTests.RunAsync(options, root);
        }),
        ("packet-http", PacketCaptureTcpTests.RunHttpAsync),
        ("town", TownProtocolTcpTests.RunAsync),
        ("party-dungeon", PartyDungeonTcpTests.RunAsync),
        ("helper-hell-rotation", PartyDungeonTcpTests.RunHelperHellAsync),
        ("guild-dungeon-party", PartyDungeonTcpTests.RunGuildPartyBuffAsync),
        ("monster-battle-level", FormalDungeonTcpTests.RunBattleLevelsAsync),
        ("monster-ai", FormalDungeonTcpTests.RunMonsterAiAsync),
        ("party-dungeon-modes", PartyDungeonTcpTests.RunQuestAndHellAsync),
        ("social", SocialTcpTests.RunAsync),
        ("trade", TradeTcpTests.RunAsync),
        ("marriage", MarriageTcpTests.RunAsync),
        ("marriage-wedding", MarriageWeddingTcpTests.RunAsync),
        ("marriage-home", MarriageHomeTcpTests.RunAsync),
        ("marriage-dungeon", PartyDungeonTcpTests.RunMarriageAsync),
        ("tutorial", TutorialTcpTests.RunAsync),
        ("channel-entry", ChannelEntryTcpTests.RunAsync),
        ("tag-evildom", TagEvildomTcpTests.RunAsync),
        ("imperial-arena", ImperialArenaTcpTests.RunAsync),
        ("evildom-war", EvildomWarTcpTests.RunAsync),
        ("quest-lifecycle", QuestLifecycleTcpTests.RunAsync),
        ("quest-extras", QuestLifecycleTcpTests.RunExtrasAsync),
        ("quest-chains", QuestLifecycleTcpTests.RunChainsAsync),
        ("quest-chain-settlement", QuestLifecycleTcpTests.RunChainSettlementAsync),
        ("quest-metro", QuestLifecycleTcpTests.RunMetroAsync),
        ("quest-auto-daily", MetroQuestEntryTcpTests.RunAutoDailyAsync),
        ("class-change-rules", (_, _, resources) =>
        {
            ClassChangeTests.RunResources(resources);
            return Task.CompletedTask;
        }),
        ("class-change", ClassChangeTcpTests.RunAsync),
        ("growup-change-rules", (_, _, resources) =>
        {
            GrowUpChangeTests.RunResources(resources);
            return Task.CompletedTask;
        }),
        ("growup-change", GrowUpChangeTcpTests.RunAsync),
        ("stadium-quest", MetroQuestEntryTcpTests.RunStadiumAsync),
        ("titles", TitleTcpTests.RunAsync),
        ("collectbox", CollectBoxTcpTests.RunAsync),
        ("epic-production", EpicProductionTcpTests.RunAsync),
        ("epic-production-rules", (_, _, resources) =>
        {
            EpicProductionTests.RunResources(resources.Archive,resources.ItemResources);
            return Task.CompletedTask;
        }),
        ("clear-avatar", ClearAvatarTcpTests.RunAsync),
        ("adventure", AdventureCoreTcpTests.RunAsync),
        ("adventure-names", AdventureNameTcpTests.RunAsync),
        ("adventure-representatives", AdventureRepresentativeTcpTests.RunAsync),
        ("adventure-expeditions", AdventureExpeditionTcpTests.RunAsync),
        ("adventure-shop", AdventureShopTcpTests.RunAsync),
        ("adventure-capsules", AdventureCapsuleTcpTests.RunAsync),
        ("adventure-weekly", AdventureWeeklyTcpTests.RunAsync),
        ("selection-contents", SelectionContentsTcpTests.RunAsync),
        ("quest-gold-mail", QuestGoldMailTcpTests.RunAsync),
        ("quest-cash-reward", QuestCashRewardTcpTests.RunAsync),
        ("quest-anton-unlock", QuestAntonUnlockTcpTests.RunAsync),
        ("quest-pet-reward", QuestPetRewardTcpTests.RunAsync),
        ("quest-eligibility-mail", QuestEligibilityMailTcpTests.RunAsync),
        ("quest-account-sync", QuestAccountSynchronizationTcpTests.RunAsync),
        ("quest-hell-challenge", QuestHellChallengeTcpTests.RunAsync),
        ("quest-continuation", QuestLifecycleTcpTests.RunContinuationAsync),
        ("story-digest", StoryDigestTcpTests.RunAsync),
        ("quest-manual-skip", async (_, _, resources) =>
        {
            QuestQuickClearPlanTests.Run(resources.Archive,resources.Quests);
            QuestQuickClearRepositoryTests.Run(resources.Towns,resources.Archive,resources.Characters,resources.ItemResources);
            await QuestManualSkipTests.RunAsync(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources, resources.Learning);
        }),
        ("quest-question-state", (_, _, resources) =>
        {
            QuestProtocolTests.Run();
            QuestOrdinaryCompletionTests.RunQuestionState(resources.Towns,resources.Archive,resources.Characters,resources.ItemResources,resources.Learning);
            QuestFactionSelectionTests.Run(resources.Towns,resources.Archive,resources.Characters,resources.ItemResources,resources.Learning);
            return Task.CompletedTask;
        }),
        ("quest-ticket", (_, _, resources) => QuestTicketTests.RunAsync(resources.Towns,resources.Archive,resources.Characters,resources.ItemResources,resources.Learning)),
        ("levelup-ticket", LevelUpTicketTcpTests.RunAsync),
        ("levelup-ticket-rules", (_, _, resources) =>
        {
            LevelUpTicketTests.Run(resources.Towns,resources.Archive,resources.Characters,resources.ItemResources,resources.Learning);
            return Task.CompletedTask;
        }),
        ("levelup-table-rules", (_, _, resources) =>
        {
            LevelUpByTableTests.Run(resources.Towns,resources.Archive,resources.Characters,resources.ItemResources,resources.Learning,resources.Quests);
            return Task.CompletedTask;
        }),
        ("experience-consumable", ExperienceConsumableTcpTests.RunAsync),
        ("legacy-status", LegacyStatusConsumableTcpTests.RunAsync),
        ("legacy-status-rules", (_, _, resources) =>
        {
            LegacyStatusConsumableTests.Run(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources, resources.Learning);
            return Task.CompletedTask;
        }),
        ("action-consumables", ActionConsumableTcpTests.RunAsync),
        ("dungeon-incount-ticket", DungeonIncountTicketTcpTests.RunAsync),
        ("stackable-actions", (_, _, resources) =>
        {
            StackableActionTests.RunResources(resources.Archive, resources.ItemResources.Items);
            return Task.CompletedTask;
        }),
        ("permanent-growth", PermanentGrowthTcpTests.RunAsync),
        ("experience-consumable-rules", (_, _, resources) =>
        {
            ExperienceConsumableTests.Run(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources, resources.Learning);
            return Task.CompletedTask;
        }),
        ("quest-specified-ticket", (_, _, resources) => QuestSpecifiedTicketTests.RunAsync(resources.Towns,resources.Archive,
            resources.Characters,resources.ItemResources,resources.Quests,resources.Learning)),
        ("quest-awakening-ticket", (_, _, resources) => QuestAwakeningTicketTests.RunAsync(resources.Towns,resources.Archive,
            resources.Characters,resources.ItemResources,resources.Quests,resources.Learning)),
        ("quest-play-only", (_, _, resources) => QuestPlayOnlyTests.RunAsync(resources.Towns,resources.Archive,
            resources.Characters,resources.ItemResources,resources.Quests,resources.Learning)),
        ("quest-auto-meet", (_, _, resources) => QuestAutoMeetTests.RunAsync(resources.Towns,resources.Archive,
            resources.Characters,resources.ItemResources,resources.Quests,resources.Learning)),
        ("quest-npc-mapping", (_, _, resources) =>
        {
            QuestNpcMappingTests.RunCore();
            QuestNpcMappingTests.RunResources(resources.Towns,resources.Quests);
            return Task.CompletedTask;
        }),
        ("quest-hidden", (_, _, resources) => QuestHiddenEntryTests.RunAsync(resources.Towns,resources.Archive,
            resources.Characters,resources.ItemResources,resources.Quests,resources.Learning)),
        ("quest-replay", (_, _, resources) => QuestReplayPublicationTests.RunAsync(resources.Archive,
            resources.Characters,resources.ItemResources,resources.Learning)),
        ("quest-initialization", (_, _, resources) =>
        {
            QuestDungeonClearProgressTests.Run(resources.Towns,resources.Archive,resources.Characters,resources.ItemResources);
            QuestDungeonClearProgressTests.RunInitialization(resources.Towns,resources.Archive,resources.Characters,resources.ItemResources);
            return Task.CompletedTask;
        }),
        // 原 dungeon-story-events 拆为互不共享状态的四段，可并行；旧名保留为别名。
        ("story-cinematic", async (options, root, resources) =>
        {
            QuestCinematicSettlementTests.Run(resources.Towns,resources.Archive,resources.Characters,resources.ItemResources,resources.Quests,resources.Learning);
            QuestAdventureSettlementTests.Run(resources.Towns,resources.Archive,resources.Characters,resources.ItemResources,resources.Quests,resources.Learning);
            await FormalDungeonTcpTests.RunTrainQuestAsync(options,root,resources);
        }),
        ("story-scenes", (_, _, resources) =>
        {
            QuestSceneTests.Run(resources.Towns,resources.Archive,resources.Characters,resources.ItemResources,resources.Quests,resources.Learning);
            return Task.CompletedTask;
        }),
        ("story-quest-entry", (_, _, resources) =>
        {
            MiddleQuestEntryTests.Run(resources.Towns,resources.Archive,resources.Characters,resources.ItemResources,resources.Quests,resources.Learning);
            MetroQuestEntryCoverageTests.Run(resources.Towns,resources.Archive,resources.Characters,resources.ItemResources,resources.Quests,resources.Learning);
            HarlemQuestEntryTests.Run(resources.Towns,resources.Archive,resources.Characters,resources.ItemResources,resources.Quests,resources.Learning);
            return Task.CompletedTask;
        }),
        ("story-publication", (_, _, resources) => DungeonStoryEventPublicationTests.RunAsync(resources.Towns,resources.Archive,
            resources.Characters,resources.ItemResources,resources.Learning)),
        ("quest-core-rewards", async (_, _, resources) =>
        {
            await QuestCompletionPublicationTests.RunRewardSlotsAsync(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources);
            ImageCommunicationTests.RunResources(resources.Towns, resources.Archive, resources.Characters,
                resources.ItemResources, resources.Quests, resources.Learning);
            QuestLifecycleTests.Run(resources.Towns, resources.ItemResources);
            QuestOrdinaryCompletionTests.Run(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources, resources.Quests);
            QuestCascadeCompletionTests.Run(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources);
            QuestCharacterRewardTests.Run(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources);
            QuestTpRewardTests.Run(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources, resources.Quests, resources.Learning);
            QuestThresholdRelationTests.Run(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources);
            await QuestCorePublicationTests.RunAsync(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources, resources.Quests, resources.Learning);
        }),
        ("retired-dungeon-entry", (_, _, resources) => DungeonRetiredEntryTests.RunAsync(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources)),
        ("dungeon-probe", DungeonProbeTcpTests.RunAsync),
        ("experience-bonuses", FormalDungeonTcpTests.RunExperienceBonusesAsync),
        ("hosted-dungeon-eligibility", HostedDungeonEligibilityTcpTests.RunAsync),
        ("formal-dungeon", FormalDungeonTcpTests.RunAsync),
        ("dungeon-growth", async (_, _, resources) =>
        {
            DungeonGrowthRepositoryTests.Run(resources.Towns, resources.Archive, resources.Characters);
            await FormalDungeonSessionTests.RunAsync(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources);
        }),
        ("grief-tower", FormalDungeonTcpTests.RunGriefAsync),
        ("durability", FormalDungeonTcpTests.RunDurabilityAsync),
        ("dungeon-training", FormalDungeonTcpTests.RunTrainingAsync),
        ("class-experience", FormalDungeonTcpTests.RunClassExperienceAsync),
        ("dungeon-practice", FormalDungeonTcpTests.RunPracticeAsync),
        ("dungeon-revive", FormalDungeonTcpTests.RunReviveAsync),
        ("dungeon-altars", FormalDungeonTcpTests.RunAltarsAsync),
        ("altar-seeking-entry", FormalDungeonTcpTests.RunAltarSeekingEntryAsync),
        ("lost-land", FormalDungeonTcpTests.RunLostLandAsync),
        ("dungeon-enter-count", FormalDungeonTcpTests.RunEnterCountsAsync),
        ("daybreak-entry", FormalDungeonTcpTests.RunDaybreakAsync),
        ("dungeon-despair", FormalDungeonTcpTests.RunDespairAsync),
        ("dungeon-dazzlement", FormalDungeonTcpTests.RunDazzlementAsync),
        ("hero-tomb-entry", FormalDungeonTcpTests.RunHeroTombAsync),
        ("assault-result-menu", FormalDungeonTcpTests.RunAssaultResultMenuAsync),
        ("assault-hunt-quest", (_, _, resources) =>
        {
            AssaultHuntQuestTests.RunResources(resources.Archive, resources.Quests);
            AssaultHuntQuestTests.Run(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources,
                resources.Quests, resources.Learning);
            return Task.CompletedTask;
        }),
        ("operation-hope", FormalDungeonTcpTests.RunOperationHopeAsync),
        ("otherverse-drop-rules", (_, _, resources) => { OtherverseAncientTests.RunDropRegression(resources.Archive, resources.ItemResources); return Task.CompletedTask; }),
        ("otherverse-ancient", FormalDungeonTcpTests.RunOtherverseAncientAsync),
        ("party-otherverse-ancient", PartyDungeonTcpTests.RunOtherverseAncientAsync),
        ("special-normal-entry", FormalDungeonTcpTests.RunSpecialNormalAsync),
        ("dungeon-entry-plan", (_, _, resources) =>
        {
            QuestReplayTests.RunCore();
            DungeonEntryPlanTests.Run(resources.Archive);
            DungeonEntryCostTests.Run(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources,
                resources.Quests, resources.Learning);
            return Task.CompletedTask;
        }),
        ("dungeon-entry-cost", (_, _, resources) =>
        {
            DungeonEntryCostTests.Run(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources,
                resources.Quests, resources.Learning);
            return Task.CompletedTask;
        }),
        ("sleepy-hollow-socket", FormalDungeonTcpTests.RunSleepyHollowAsync),
        ("pandemonium-regional-drops", FormalDungeonTcpTests.RunRegionalDropsAsync),
        ("sleepy-hollow-materials", (_, _, resources) =>
        {
            SleepyHollowMaterialDropTests.RunCore();
            SleepyHollowMaterialDropTests.RunResources(resources.Towns, resources.Archive, resources.Characters,
                resources.ItemResources, resources.Quests, resources.Learning);
            return Task.CompletedTask;
        }),
        ("ekern-cycle", EkernCycleTcpTests.RunAsync),
        ("tayberrs-cards", (_, _, resources) =>
        {
            TayberrsCardRewardTests.RunCore();
            TayberrsCardRewardTests.RunResources(resources.Archive, resources.ItemResources);
            return Task.CompletedTask;
        }),
        ("dungeon-settlement", async (_, _, resources) =>
        {
            DungeonItemDropCatalogTests.RunCore();
            DungeonDropRuleCatalogTests.RunCore();
            DungeonDropGenerationPlanTests.RunCore();
            DungeonRoomCatalogTests.Run(resources.Archive);
            DungeonMonsterPlanTests.Run(resources.Archive);
            SinglePlayerDungeonRunTests.Run(resources.Archive);
            DungeonSettlementTests.RunResources(resources.Archive);
            TournamentSettlementTests.RunResources(resources.Towns,resources.Archive,resources.Characters,resources.ItemResources);
            GriefTowerClearTests.RunResources(resources.Archive,resources.ItemResources.Items,resources.ItemResources.EpicPieces);
            DungeonSettlementTests.RunAdventureResources(resources.Archive);
            DungeonSettlementTests.RunProficiencyResources(resources.Archive);
            DungeonSettlementSessionTests.Run(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources);
            HarlemMaterialDropTests.RunResources(resources.Towns, resources.Archive, resources.Characters,
                resources.ItemResources, resources.Quests, resources.Learning);
            await FormalDungeonPublicationTests.RunAsync(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources);
        }),
        ("dungeon-drop-storage", (_, _, resources) =>
        {
            EquipmentAmplificationDropTests.RunResources(resources.ItemResources);
            DungeonDropCollectionRepositoryTests.Run(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources);
            return Task.CompletedTask;
        }),
        ("dungeon-passive-items", FormalDungeonTcpTests.RunPassiveAsync),
        ("dungeon-pickup-slots", FormalDungeonTcpTests.RunPickupSlotsAsync),
        ("jobs", TownProtocolTcpTests.JobsAsync),
        ("inventory", InventoryTcpTests.RunAsync),
        ("talisman", TalismanTcpTests.RunAsync),
        ("consumable-use", ConsumableUseTcpTests.RunAsync),
        ("consumable-effects", ConsumableUseTcpTests.RunEffectsAsync),
        ("consumable-disguise", ConsumableUseTcpTests.RunDisguiseAsync),
        ("teleport", TeleportTcpTests.RunAsync),
        ("teleport-privilege", PrivilegeTeleportTcpTests.RunAsync),
        ("solo-party", SoloPartyTcpTests.RunAsync),
        ("pvp-training", PvpTrainingTcpTests.RunAsync),
        ("steel-learning", SteelLearningTcpTests.RunAsync),
        ("pvp-arena", PvpArenaTcpTests.RunAsync),
        ("pvp-private-character", PvpPrivateCharacterTcpTests.RunAsync),
        ("consumable-use-rules", (_, _, resources) =>
        {
            ConsumableUseTests.RunResources(resources.ItemResources);
            ConsumableUseSessionTests.Run(resources.Towns, resources.Characters, resources.Archive);
            ConsumableUseSessionTests.RunPortableCargo(resources.Towns, resources.Characters, resources.Archive, resources.ItemResources);
            return Task.CompletedTask;
        }),
        ("inventory-persistence", (_, _, resources) =>
        {
            Inventory.InventoryCommitTests.Run(resources.Towns, resources.Characters, resources.ItemResources);
            DungeonDropCollectionRepositoryTests.Run(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources);
            DatabaseSchemaTests.Run();
            return Task.CompletedTask;
        }),
        ("resource-survey", (_, _, resources) =>
        {
            DungeonResourceSurveyTests.RunResources(resources.Archive);
            return Task.CompletedTask;
        }),
        ("skills", async (options, root, resources) =>
        {
            SkillPointLedgerTests.Run(resources.Towns, resources.Archive, resources.Characters);
            await SkillProtocolTcpTests.RunAsync(options, root, resources);
            await SkillExtendedTcpTests.RunAsync(options, root, resources);
        }),
        ("skill-switch-inventory", SkillSwitchInventoryTcpTests.RunAsync),
        ("skill-slots", SkillQuickSlotsTcpTests.RunAsync),
        ("skill-material", SkillMaterialTcpTests.RunAsync),
        ("gm-progress", CharacterProgressTcpTests.RunAsync),
        ("gm-debug", FormalDungeonTcpTests.RunDebugAsync),
        ("special-containers", SpecialContainerTcpTests.RunAsync),
        ("guild-medals", GuildMedalTcpTests.RunAsync),
        ("guild-directory", GuildDirectoryTcpTests.RunAsync),
        ("guild-panel", GuildPanelTcpTests.RunAsync),
        ("guild-management", GuildManagementTcpTests.RunAsync),
        ("guild-applications", GuildApplicationTcpTests.RunAsync),
        ("guild-governance", GuildGovernanceTcpTests.RunAsync),
        ("guild-cargo", GuildCargoTcpTests.RunAsync),
        ("guild-economy", GuildEconomyTcpTests.RunAsync),
        ("guild-appearance", GuildAppearanceTcpTests.RunAsync),
        ("guild-npc-benefits", GuildNpcBenefitTcpTests.RunAsync),
        ("guild-contribution", GuildContributionTcpTests.RunAsync),
        ("guild-teleport", GuildTeleportTcpTests.RunAsync),
        ("guild-permit", GuildPermitTcpTests.RunAsync),
        ("guild-permit-sequential", GuildPermitSequentialTcpTests.RunAsync),
        ("currency", CurrencyTcpTests.RunAsync),
        ("currency-rules", (_, _, resources) =>
        {
            Inventory.CurrencyTests.Run(resources.Towns, resources.Characters, resources.ItemResources);
            return Task.CompletedTask;
        }),
        ("mileage", MileageTcpTests.RunAsync),
        ("mileage-shop", MileageShopTcpTests.RunAsync),
        ("token-cera", MailboxTcpTests.RunTokenCeraAsync),
        ("cargo", CargoTcpTests.RunAsync),
        ("cargo-purchase", CargoPurchaseTcpTests.RunAsync),
        ("shop", ShopTcpTests.RunAsync),
        ("cash-shop", CashShopTcpTests.RunAsync),
        ("cash-shop-promotions", CashShopTcpTests.PromotionsAsync),
        ("name-tag", NameTagTcpTests.RunAsync),
        ("character-slots", CharacterSlotsTcpTests.RunAsync),
        ("character-rename-card", CharacterRenameCardTcpTests.RunAsync),
        ("devil-contract", DevilContractTcpTests.RunAsync),
        ("devil-quest", (_, _, resources) =>
        {
            QuestDropTests.RunCore();
            QuestDropTests.Run(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources);
            return Task.CompletedTask;
        }),
        ("database-schema", (_, _, _) => { DatabaseSchemaTests.Run(); return Task.CompletedTask; }),
        ("settings-storage", (_, _, _) => { ClientSettingsStoreTests.Run(); return Task.CompletedTask; }),
        ("cera-package", CeraPackageTcpTests.RunAsync),
        ("premium-contract", PremiumContractTcpTests.RunAsync),
        ("growth-cube-contract", GrowthCubeContractTcpTests.RunAsync),
        ("enchantment", EnchantmentTcpTests.RunAsync),
        ("enhancement", EnhancementTcpTests.RunAsync),
        ("equipment-upgrade-announcements", EquipmentUpgradeAnnouncementTcpTests.RunAsync),
        ("amplification-options", AmplificationTcpTests.RunAsync),
        ("amplification-upgrade", AmplifyUpgradeTcpTests.RunAsync),
        ("creature-egg", CreatureEggTcpTests.RunAsync),
        ("creature-skin", CreatureSkinTcpTests.RunAsync),
        ("aura-skin", AuraSkinTcpTests.RunAsync),
        ("inventory-expansion", InventoryExpansionTcpTests.RunAsync),
        ("aura-action", AuraActionTcpTests.RunAsync),
        ("avatar-capacity", AvatarCapacityTcpTests.RunAsync),
        // 此专项也由avatar-package调用；独立工作目录避免同次全组选中时重用角色/数据库。
        ("gift-selection-tcp", (options, root, resources) =>
            AvatarPackageTcpTests.RunObservedSelectionsAsync(options, Path.Combine(root, "gift-selection-tcp"), resources)),
        ("sea-rage-weapon-gift", (options, root, resources) =>
            AvatarPackageTcpTests.RunSeaRageWeaponSelectionAsync(options, Path.Combine(root, "sea-rage-weapon-gift"), resources)),
        ("gift-selection-resources", (options, root, resources) =>
        {
            Inventory.AvatarPackageTests.RunResources(resources.Towns, resources.ItemResources, resources.ItemResources.AvatarPackages);
            Inventory.BoosterSelectionTests.Run(resources.Towns, resources.Characters, resources.ItemResources);
            Inventory.BoosterCreatureTests.RunResources(resources.Towns, resources.ItemResources);
            return Task.CompletedTask;
        }),
        ("creature-system", CreatureSystemTcpTests.RunAsync),
        ("creature-transport", CreatureTransportTcpTests.RunAsync),
        ("equipment-transfer-seal", EquipmentTransferSealTcpTests.RunAsync),
        ("creature-change", CreatureChangeTcpTests.RunAsync),
        ("creature-group", CreatureGroupTcpTests.RunAsync),
        ("creature-dungeon", FormalDungeonTcpTests.RunCreatureAsync),
        ("avatar-package", (options, root, resources) => AvatarPackageTcpTests.RunAsync(options, root, resources, resources.ItemResources.AvatarPackages)),
        ("emblem-compound", EmblemCompoundTcpTests.RunAsync),
        ("monster-card-bind", MonsterCardBindTcpTests.RunAsync),
        ("monster-card-upgrade", MonsterCardUpgradeTcpTests.RunAsync),
        ("guild-compound", GuildCompoundTcpTests.RunAsync),
        ("equipment-emblem", EquipmentEmblemTcpTests.RunAsync),
        ("avatar-compound", AvatarCompoundTcpTests.RunAsync),
        ("avatar-dye", AvatarDyeTcpTests.RunAsync),
        ("avatar-ability-change", AvatarAbilityChangeTcpTests.RunAsync),
        ("avatar-socket", AvatarSocketTcpTests.RunAsync),
        ("avatar-emblem", AvatarEmblemTcpTests.RunAsync),
        ("mailbox", MailboxTcpTests.RunAsync),
        ("gm-assets", DevelopmentAssetTcpTests.RunAsync),
        ("recharge-web", RechargeWebTcpTests.RunAsync),
        ("repair", RepairTcpTests.RunAsync),
        ("random-option", RandomOptionTcpTests.RunAsync),
        ("equipment-quality", EquipmentQualityTcpTests.RunAsync),
        ("texture-change", TextureChangeTcpTests.RunAsync),
        ("equipment-upgrade", EquipmentUpgradeTcpTests.RunAsync),
        ("echon-equipment", EchonEquipmentTcpTests.RunAsync),
        ("equipment-transmission", EquipmentTransmissionTcpTests.RunAsync),
        ("weapon-skin", WeaponSkinTcpTests.RunAsync),
        ("emoticon", EmoticonTcpTests.RunAsync),
        ("skin-recent", SkinRecentTcpTests.RunAsync),
        ("refinement", RefinementTcpTests.RunAsync),
        ("refinement-tickets", RefinementTicketTcpTests.RunAsync),
        ("disjoint", DisjointTcpTests.RunAsync),
        ("expert-job-learning", ExpertJobLearningTcpTests.RunAsync),
        ("expert-job-actions", ExpertJobActionTcpTests.RunAsync),
        ("expert-job-trade", ExpertJobTradeTcpTests.RunAsync),
        ("avatar-disjoint", AvatarDisjointTcpTests.RunAsync),
        ("avatar-preset", AvatarPresetTcpTests.RunAsync),
        ("npc-favor", NpcFavorTcpTests.RunAsync),
        ("fatigue-daily", FatigueDailyResetTcpTests.RunAsync),
        ("epic-book", EpicBookTcpTests.RunAsync),
        ("epic-piece-drops", FormalDungeonTcpTests.RunEpicPiecesAsync),
        ("material-exchange", MaterialExchangeTcpTests.RunAsync),
        ("recipe-craft", RecipeCraftTcpTests.RunAsync),
        ("upgrade-chronicle", UpgradeChronicleTcpTests.RunAsync),
        ("chronicle-enchant", ChronicleEnchantTcpTests.RunAsync),
        ("daily-retained-booster", DailyRetainedBoosterTcpTests.RunAsync),
        ("booster", BoosterTcpTests.RunAsync),
        ("magic-box", MagicBoxTcpTests.RunAsync),
        ("lottery", LotteryTcpTests.RunAsync),
        ("vending", VendingTcpTests.RunAsync),
        ("rune-box", RuneBoxTcpTests.RunAsync),
        ("limit-cube", LimitCubeTcpTests.RunAsync),
        ("booster-selection", BoosterSelectionTcpTests.RunAsync),
        ("booster-equipment", BoosterEquipmentTcpTests.RunAsync),
        ("authentication", AuthenticationTcpTests.RunAsync),
        ("native-login", NativeLoginTcpTests.RunAsync),
        ("native-channel", NativeChannelTcpTests.RunAsync),
        ("settings", ClientSettingsTcpTests.RunAsync),
        ("keepalive", KeepaliveTcpTests.RunAsync),
        ("emotion", EmotionTcpTests.RunAsync),
        ("chat", ChatTcpTests.RunAsync),
        ("chat-emoticon", ChatEmoticonTcpTests.RunAsync),
        ("packet-compatibility", (_, _, resources) => { PacketCompatibilityTests.Run(resources.Characters); return Task.CompletedTask; }),
        ("luke-scene-namespace", LukeSceneNamespaceTcpTests.RunAsync),
        ("raid-create", RaidCreateTcpTests.RunAsync),
        ("anton-raid", AntonRaidTcpTests.RunAsync),
        ("anton-raid-rejoin", AntonRaidRejoinTcpTests.RunAsync),
        ("anton-raid-rejoin-recover", AntonRaidRejoinTcpTests.RunRecoverAsync),
        ("anton-raid-rejoin-client-notify", AntonRaidRejoinTcpTests.RunClientNotifyGapAsync),
        ("anton-tcp-probe", AntonRaidTcpTests.RunProbeAsync),
        ("anton-raid-gap", AntonRaidGapTcpTests.RunAsync),
        ("anton-raid-phase1", AntonRaidGapTcpTests.RunPhaseOneAsync),
        ("anton-raid-card600-601", AntonRaidGapTcpTests.RunCard600601Async),
        ("anton-raid-reward-timeout", AntonRaidGapTcpTests.RunRewardTimeoutAsync),
        ("raid-start-minimum", AntonRaidTcpTests.RunMinimumAsync),
        ("luke-raid", LukeRaidTcpTests.RunAsync),
        ("luke-symbol103-handler", LukeSymbol103HandlerTests.RunAsync),
        ("luke-raid-original-gate", LukeRaidTcpTests.RunOriginalRuleGateAsync),
        ("luke-raid-start", LukeRaidStartTcpTests.RunAsync),
        ("luke-raid-run", LukeRaidStartTcpTests.RunDungeonsAsync),
        ("luke-raid-energy-objects", LukeRaidStartTcpTests.RunEnergyObjectsAsync),
        ("luke-raid-disconnect-candidates", LukeRaidStartTcpTests.RunDisconnectCandidatesAsync),
        ("luke-raid-facilities", LukeRaidStartTcpTests.RunFacilitiesAsync),
        ("luke-raid-fixed-route", LukeRaidStartTcpTests.RunFixedRouteAsync),
        ("luke-reward-restart", LukeRaidStartTcpTests.RunRewardRestartAsync),
        ("luke-raid-phases", LukeRaidStartTcpTests.RunPhasesAsync),
        ("luke-raid-production", LukeRaidRunTcpTests.ProductionAsync),
        ("luke-raid-recovery", LukeRaidRunTcpTests.RecoveryAsync),
        ("luke-raid-result600", LukeRaidStartTcpTests.RunResourceCompletionAsync),
        ("luke-raid-timers", LukeRaidStartTcpTests.RunTimersAsync),
        ("luke-raid-phase-timeout", LukeRaidStartTcpTests.RunPhaseTimeoutAsync),
        ("raid-channel-type", RaidChannelTypeTcpTests.RunAsync),
        ("raid-channel-landing", RaidChannelLandingTcpTests.RunAsync),
        ("raid-channel-events", RaidChannelLandingTcpTests.RunEventsAsync),
        ("special-channel-landing", RaidChannelLandingTcpTests.RunSpecialAsync),
        ("xian-village-door", RaidChannelLandingTcpTests.RunXianDoorAsync),
        ("presence", TownPresenceTcpTests.RunAsync),
        ("gm-web-accept", GmWebAcceptTcpTests.RunAsync)
    ];

    // 拆分后的旧组名作为别名，展开为原有全部内容，保持既有命令可用。
    static readonly Dictionary<string, string[]> Aliases = new(StringComparer.Ordinal)
    {
        ["dungeon-story-events"] = ["story-cinematic", "story-scenes", "story-quest-entry", "story-publication"],
        ["quests"] = ["quest-lifecycle", "quest-extras", "quest-chains", "quest-chain-settlement", "quest-metro"]
    };

    public static IEnumerable<string> GroupNames => Groups.Select(group => group.Name).Concat(Aliases.Keys);
    // 自测已有的定向入口和全量普查按需运行，完整 TCP 只执行网络集成。
    static readonly HashSet<string> TargetedOnly = new(StringComparer.Ordinal)
    {
        "otherverse-drop-rules",
        "fiendwar-reward-rules",
        "settings-snapshot-rules",
        "equipment-power-rules",
        "frame-crc-rules",
        "chat-rate-rules",
        "luke-scene-namespace",
        "luke-symbol103-handler",
        "luke-reward-restart",
        "class-change-rules",
        "levelup-ticket-rules",
        "levelup-table-rules",
        "experience-consumable-rules",
        "legacy-status-rules",
        "stackable-actions",
        "gift-selection-tcp", "gift-selection-resources", "sea-rage-weapon-gift", "dungeon-entry-plan", "assault-hunt-quest", "hosted-dungeon-eligibility", "quest-continuation", "quest-manual-skip", "quest-ticket", "quest-specified-ticket", "quest-awakening-ticket",
        "quest-play-only", "quest-auto-meet", "quest-npc-mapping", "quest-hidden", "quest-replay", "quest-initialization",
        "story-cinematic", "story-scenes", "story-quest-entry", "story-publication", "quest-core-rewards", "dungeon-settlement",
        "dungeon-drop-storage", "inventory-persistence", "resource-survey", "consumable-use-rules", "dungeon-entry-cost"
    };
    internal static IEnumerable<string> DefaultGroupNames => Groups.Select(group => group.Name).Where(name => !TargetedOnly.Contains(name));
    // 同一注册表供执行和机器可读清单使用；不是外部手填组表或覆盖证明。
    internal static string RegistryJson() => JsonSerializer.Serialize(new
    {
        SchemaVersion = 1,
        Purpose = "registration-only",
        Execution = "not-run",
        Runner = TcpTestReceipts.CaptureIdentity().Runner,
        Groups = Groups.Select(group => new
        {
            group.Name,
            Kind = group.Name == "resource-survey" ? "survey" : TargetedOnly.Contains(group.Name) ? "targeted" : "default"
        }).ToArray(),
        DefaultGroups = DefaultGroupNames.ToArray(),
        TargetedGroups = Groups.Where(group => TargetedOnly.Contains(group.Name) && group.Name != "resource-survey").Select(group => group.Name).ToArray(),
        SurveyGroups = Groups.Where(group => group.Name == "resource-survey").Select(group => group.Name).ToArray(),
        Aliases = Aliases.ToDictionary(alias => alias.Key, alias => alias.Value.ToArray(), StringComparer.Ordinal)
    });

    public static bool IsValidSelection(string selection)
    {
        string[] names = selection.Split(',');
        return names.Distinct(StringComparer.Ordinal).Count() == names.Length && names.All(GroupNames.Contains);
    }

    internal static string[] Expand(IEnumerable<string> names) =>
        names.SelectMany(name => Aliases.TryGetValue(name, out var members) ? members : [name]).Distinct(StringComparer.Ordinal).ToArray();

    public static async Task RunAsync(ServerOptions options, string? selection = null)
    {
        if (selection is not null && !IsValidSelection(selection))
            throw new InvalidDataException("未知或重复的 TCP 测试组");
        string[] selected = Expand(selection?.Split(',') ?? DefaultGroupNames);
        // 并行分片子进程：直接写入父轮次分配的目录，由父驱动统一汇总与清理。
        string? shardRoot = Environment.GetEnvironmentVariable(TcpTestShards.ShardRootVariable);
        if (!string.IsNullOrEmpty(shardRoot))
        {
            Environment.SetEnvironmentVariable(TcpTestShards.ShardRootVariable, null);
            string root = Path.GetFullPath(shardRoot);
            Directory.CreateDirectory(root);
            await RunGroupsAsync(options, root, selected);
            return;
        }
        int parallel = Math.Min(TcpTestShards.Parallelism(), selected.Length);
        await TcpTestArtifacts.RunAsync(async root =>
        {
            if (parallel > 1)
                await TcpTestShards.RunAsync(root, selected, parallel, TcpTestTimings.Load(TcpTestArtifacts.Parent));
            else
                await RunGroupsAsync(options, root, selected);
        });
    }

    static async Task RunGroupsAsync(ServerOptions options, string root, string[] selected)
    {
        using var resources = new TcpTestResources(options);
        string runId = Guid.NewGuid().ToString("N");
        var identity = TcpTestReceipts.CaptureIdentity();
        string durable = TcpTestReceipts.DurableDirectory(runId);
        var ordered = Groups.Where(group => selected.Contains(group.Name, StringComparer.Ordinal)).ToArray();
        string[] selection = ordered.Select(group => group.Name).ToArray();
        foreach (var group in ordered)
        {
            int before = TestCheck.Count;
            long loadBefore = resources.LoadMs;
            var startupBefore = TcpTestFixture.StartupTotals;
            long allocated = GC.GetTotalAllocatedBytes();
            var watch = Stopwatch.StartNew();
            using var coverage = new TcpTestReceipts.Group(group.Name, runId, identity, selection: selection);
            string localReceipt = Path.Combine(root, group.Name + ".result.json");
            string durableReceipt = Path.Combine(durable, group.Name + ".json");
            void Save(TcpTestReceipts.Receipt receipt)
            {
                // 完整本地记录写好后才发布独立小收据；成功现场回收不删除独立收据。
                TcpTestReceipts.WriteAtomic(localReceipt, receipt);
                TcpTestReceipts.WriteAtomic(durableReceipt, receipt);
            }
            Save(coverage.Snapshot("running"));
            try
            {
                await group.Run(options, root, resources);
                using var process = Process.GetCurrentProcess();
                Save(coverage.Snapshot("passed") with
                {
                    Assertions = TestCheck.Count - before, ElapsedMs = watch.ElapsedMilliseconds,
                    DriverPrivateBytes = process.PrivateMemorySize64, DriverWorkingSetBytes = process.WorkingSet64,
                    AllocatedBytes = GC.GetTotalAllocatedBytes() - allocated, SharedResourcesLoaded = resources.IsLoaded,
                    ResourceLoadMs = resources.LoadMs - loadBefore, FixtureStartMs = TcpTestFixture.StartupTotals.Milliseconds - startupBefore.Milliseconds,
                    FixtureStarts = TcpTestFixture.StartupTotals.Count - startupBefore.Count
                });
            }
            catch (Exception error)
            {
                try
                {
                    Save(coverage.Snapshot(error is OperationCanceledException ? "interrupted" : "failed", error.GetType().FullName) with
                    { Assertions = TestCheck.Count - before, ElapsedMs = watch.ElapsedMilliseconds });
                }
                catch (Exception saveError)
                { Console.Error.WriteLine("WARN test receipt write failed: " + saveError.GetType().Name); }
                throw;
            }
            Console.WriteLine($"PASS TCP {group.Name}: {TestCheck.Count - before} assertions, {watch.Elapsed.TotalSeconds:F1}s " +
                $"(resources {(resources.LoadMs - loadBefore) / 1000.0:F1}s, fixture starts {TcpTestFixture.StartupTotals.Count - startupBefore.Count} " +
                $"in {(TcpTestFixture.StartupTotals.Milliseconds - startupBefore.Milliseconds) / 1000.0:F1}s)");
        }
        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { Passed = true, Assertions = TestCheck.Count, Groups = selected, Runtime = Environment.Version.ToString(), OS = Environment.OSVersion.ToString() }));
        Console.WriteLine($"PASS {TestCheck.Count} C# TCP assertions; {root}");
    }
}
