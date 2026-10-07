using DfoServer.Infrastructure;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using PvfLib;
using DfoServer.GameWorld.Items;
using DfoServer.Testing.Inventory;

namespace DfoServer;

internal static class SelfTestRunner
{
    public static async Task RunCoreAsync()
    {
        CommandLineTests.Run();
        FrameCrcTests.Run();
        await BufferedLogTests.RunAsync();
        await GameSenderBudgetTests.RunAsync();
        await PacketCaptureTests.RunAsync();
        ClientNoticeTests.Run();
        DungeonRequestTests.Run();
        DungeonSelectionReturnTests.RunCore();
        AuthenticationTests.Run();
        AuthenticationServiceTests.Run();
        AuthenticationRepositoryTests.Run();
        await AccountCredentialStoreTests.RunAsync();
        NativeLoginTests.Run();
        await AccountConcurrencyTests.RunAsync();
        await AccountDatabaseConcurrencyTests.RunAsync();
        await TownPresenceConcurrencyTests.RunAsync();
        DatabaseSchemaTests.Run();
        RepositoryBoundaryTests.Run();
        CharacterReadTests.Run();
        await SessionPlanningTests.RunAsync();
        TownSelectionTests.Run();
        InventoryChangeTests.Run();
        InventoryProjectionTests.Run();
        InventoryWriteTests.Run();
        InventoryBatchTests.RunCore();
        DungeonDeathProtocolTests.Run();
        GrowthPacketsTests.Run();
        SkillRequestTests.Run();
        ItemRequestTests.Run();
        MailboxProtocolTests.Run();
        MailboxPersistenceTests.Run();
        await MailboxPublicationTests.RunAsync();
        DevelopmentAssetTests.RunCore();
        RentalTests.Run();
        SkillPacketsTests.Run();
    }

    public static async Task RunAsync(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources itemResources)
    {
        await RunCoreAsync();
        PacketCompatibilityTests.Run(characters);
        CharacterBoundaryTests.Run(towns, characters);
        SelfTests.Run(towns, characters);
        CharacterPreparationTests.Run(towns, pvf, characters);
        CharacterCreationTests.Run(towns, characters);
        await AssetPublicationTests.RunAsync(towns, pvf, characters, itemResources);
        await TownTravelPublicationTests.RunAsync(towns, characters, itemResources);
        InventorySelfTests.Run(towns, pvf, characters, itemResources);
        await MailboxTests.RunAsync(towns, characters, itemResources, pvf);
        DevelopmentAssetTests.RunResources(towns, itemResources);
        InventoryBatchTests.Run(itemResources);
        SpecialContainerTests.Run(towns, characters, itemResources);
        CurrencyTests.Run(towns, characters, itemResources);
        CargoTests.Run(towns, characters, itemResources);
        ShopTests.Run(towns, characters, itemResources);
        RepairTests.Run(towns, characters, itemResources);
        RefinementTests.Run(towns, characters, itemResources);
        await RefinementTicketTests.RunAsync(towns, characters, itemResources);
        DisjointTests.Run(towns, characters, itemResources);
        EpicPieceTests.Run(towns, characters, itemResources);
        MaterialExchangeTests.Run(towns, characters, itemResources);
        BoosterTests.Run(towns, characters, itemResources);
        BoosterSelectionTests.Run(towns, characters, itemResources);
        BoosterEquipmentTests.Run(towns, characters, itemResources);
        ItemTemplateImportTests.Run(towns, pvf, characters, itemResources);
        InventoryCommitTests.Run(towns, characters, itemResources);
        await InventoryReceiptTests.RunAsync(towns, characters, itemResources);
        await TownPresenceTests.Run(towns);
        await TownPresenceIndexTests.RunAsync(towns);
        DungeonCatalogTests.Run(pvf);
        DungeonGreedGridTests.Run(pvf);
        DungeonRoomCatalogTests.Run(pvf);
        DungeonResourceSurveyTests.Run(pvf);
        DungeonMonsterPlanTests.Run(pvf);
        DungeonMonsterPacketsTests.Run(pvf);
        DungeonProbeRunTests.Run(pvf);
        SinglePlayerDungeonRunTests.Run(pvf);
        DungeonDropPacketsTests.Run();
        await DungeonDeathProtocolTests.RunAsync(towns, pvf);
        DungeonEntryPlanTests.Run(pvf);
        await DungeonEntryProbeTests.RunAsync(towns, pvf, characters, itemResources);
        await DungeonSelectionReturnTests.RunAsync(towns, characters, itemResources);
        QuestCatalogTests.Run(pvf);
        StarterSkillTests.Run(pvf, characters);
        ItemCatalogTests.Run(pvf);
        StackableCatalogTests.Run(pvf);
        SkillCatalogTests.Run(pvf);
        CharacterGrowthCatalogTests.Run(pvf);
        CharacterGrowthTests.Run(towns, pvf);
        DungeonKillExperienceCatalogTests.Run(pvf);
        DungeonMonsterDropCatalogTests.Run(pvf);
        DungeonGrowthRepositoryTests.Run(towns, pvf, characters);
        SkillLearningCatalogTests.Run(pvf);
        SkillLearningTests.Run(pvf, characters);
        SkillRepositoryTests.Run(towns, pvf, characters);
        await SkillPublicationTests.RunAsync(towns, pvf, characters);
        DevelopmentGmTests.Run(towns);
        await ProtocolArchitectureTests.RunAsync(towns);
    }
}
