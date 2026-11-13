using System.Text.Json;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DisasterDungeonTests
{
    static void VerifyLowCrystalClaim(QuestSceneTests.Fixture fixture, FormalDungeonReceipt boss,
        DungeonCatalog dungeons, PvfArchive pvf, DungeonKillExperienceCatalog kills,
        CharacterGrowthCatalog growth, ItemResources items)
    {
        foreach (var monster in boss.Monsters.Monsters.Where(monster => monster.IsBlocking && monster.CanReportDeath))
        {
            // 死亡回执携带本次投影快照；石头控制器和Boss收尾可能已经清除其他演员，逐次读取当前存活表。
            if (fixture.Session.DungeonSnapshot?.LivingMonsters.Contains(monster.ObjectKey) != true) continue;
            var death = fixture.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(boss, monster), kills, growth);
            That(death.Outcome == DungeonRunOutcome.Accepted, "低档结算前清除仍存活的阻挡对象：" + monster.MonsterId);
        }
        int Boxes() => fixture.Read().Inventory.Items.Where(item => item.Core.ItemId == 490012200).Sum(item => item.Core.Count);
        That(Boxes() == 0, "低档实际Boss死亡不直发结晶盒，石头盒保持独立");
        var clear = global::DfoServer.Testing.SelfTestCatalogs.ClearExperience(pvf);
        var cards = new DungeonCardRewardPolicy(global::DfoServer.Testing.SelfTestCatalogs.DropRules(pvf),
            DungeonItemDropCatalog.Create([]), items.Items, items.GoldLimits, items.EpicPieces,
            prices: global::DfoServer.Testing.SelfTestCatalogs.CardPrices(pvf));
        var settled = fixture.Session.SettleFormalDungeon(fixture.Read().Id, 99, 99, [1, 2, 3], dungeons, clear, growth, cards);
        That(settled.Outcome == DungeonRunOutcome.Accepted && settled.Card?.Item is { ItemId: 490012200, Count: 1 } &&
            settled.Card.RuleId == "disaster-crystal-card-local-v2" && Boxes() == 0,
            "低档正式结算仅冻结本服一盒规则，不提前入包");
        var repeated = fixture.Session.SettleFormalDungeon(fixture.Read().Id, 99, 99, [1, 2, 3], dungeons, clear, growth, cards);
        That(repeated.Card == settled.Card && fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts") is 1L,
            "低档重复通关报告复用原牌面及唯一结算回执");
        fixture.Session.AdvanceFormalDungeonCards(false);
        That(fixture.Session.AdvanceFormalDungeonCards(true) is not null, "低档按既有结果页阶段开放翻牌");
        string before = JsonSerializer.Serialize(fixture.Read());
        fixture.Sql("CREATE TRIGGER disaster_card_fail BEFORE UPDATE OF claimed ON character_dungeon_card_rewards BEGIN SELECT RAISE(ABORT,'disaster card rollback'); END");
        Reject<SqliteException>(() => fixture.Session.SelectFormalDungeonFreeCard(0, 0), "低档领奖晚SQL失败整笔回滚");
        That(JsonSerializer.Serialize(fixture.Read()) == before && Boxes() == 0 &&
            fixture.Query("SELECT claimed FROM character_dungeon_card_rewards") is 0L,
            "低档失败保留原资产和未领取资格");
        fixture.Sql("DROP TRIGGER disaster_card_fail");
        That(fixture.Session.SelectFormalDungeonFreeCard(0, 0) is { Inventory.Accepted: true } && Boxes() == 1 &&
            fixture.Query("SELECT claimed FROM character_dungeon_card_rewards") is 1L,
            "低档原领取重试实际入库一盒并同事务设置领取位");
        string committed = JsonSerializer.Serialize(fixture.Read());
        That(fixture.Session.SelectFormalDungeonFreeCard(0, 0) is { Duplicate: true, Inventory: null } &&
            Boxes() == 1 && JsonSerializer.Serialize(fixture.Read()) == committed,
            "低档重复领取不多发结晶盒或重复金币");
    }
}
