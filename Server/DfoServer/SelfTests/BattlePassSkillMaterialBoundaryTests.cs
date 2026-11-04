using System.Text.Json;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.BattlePass;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Testing;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 任务5/11/15的窄边界：技能扣费只产生15来源，商城1743只产生11来源，经验领取不自动发奖励。
internal static class BattlePassSkillMaterialBoundaryTests
{
    public static void Run(TcpTestResources resources)
    {
        int before = Count;
        var start = new DateTimeOffset(2026, 10, 29, 6, 0, 0, TimeSpan.FromHours(8));
        var policy = new BattlePassMissionPolicy("skill-material-boundary", start, Catalog());
        var clock = new Clock(start.AddHours(1));
        var items = resources.ItemResources;
        var inventory = new InventoryService(items.Items, items.GoldLimits, items.EpicPieces, clock: clock);
        using var fixture = new QuestSceneTests.Fixture(resources.Towns, resources.Characters, resources.Learning,
            SelfTestCatalogs.Growth(resources.Archive, resources.Learning), resources.Quests, false, 95,
            timeProvider: clock, inventorySetup: character => inventory.Grant(character, [new(3037, 30)]).Plan!);
        fixture.Repository.ConfigureBattlePassMissions(policy);
        var initial = fixture.Repository.ReadBattlePassMissions(fixture.Owner, clock.GetUtcNow())!;
        That(initial.Slots.Any(row => row.Instance.Key is { Category: 0, Id: 5 }) &&
            initial.Slots.Any(row => row.Instance.Key is { Category: 0, Id: 11 }) &&
            initial.Slots.Any(row => row.Instance.Key is { Category: 0, Id: 15 }),
            "边界夹具同日同时装载任务5/11/15");

        var shop = fixture.Session.ClaimBattlePassMission(clock.GetUtcNow(), 0, 11, 1, 0);
        That(shop.Accepted && CountOf(shop.State!, 11) == 1 && CountOf(shop.State!, 5) == 0 &&
            CountOf(shop.State!, 15) == 0 && shop.State!.Season.TotalExperience == 0,
            "1743 action0商城报告只推进任务11，不提前发经验或串改5/15");

        var request = InventoryRequests.Delete(Convert.FromHexString("1000000010001A0A080210E60218DD172005200001"));
        for (int operation = 0; operation < 5; operation++)
            That(fixture.Session.DeleteInventory(request, inventory).Accepted, "五次真实技能材料支付保持成功");
        var paid = fixture.Repository.ReadBattlePassMissions(fixture.Owner, clock.GetUtcNow())!;
        That(CountOf(paid, 5) == 0 && CountOf(paid, 11) == 1 && CountOf(paid, 15) == 5 &&
            paid.Season.TotalExperience == 0 && fixture.Read().Inventory.AccountWallet.Get(CurrencyKind.ClearCube) == 5,
            "CMD18五次支付只完成任务15，任务5仍无来源且未自动加经验");

        string beforeClaims = JsonSerializer.Serialize(fixture.Read().Inventory);
        var claim11 = fixture.Session.ClaimBattlePassMission(clock.GetUtcNow(), 1, 11, 0, 0);
        var claim15 = fixture.Session.ClaimBattlePassMission(clock.GetUtcNow(), 1, 15, 0, 0);
        That(claim11.Accepted && claim15.Accepted && claim15.State!.Season.TotalExperience == 200 &&
            fixture.Query("SELECT count(*) FROM account_battle_pass_mission_claims") is 2L,
            "1743 action1分别领取11和15各100经验且claims恰为两笔");
        That(JsonSerializer.Serialize(fixture.Read().Inventory) == beforeClaims &&
            fixture.Query("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='colorless_skill'") is 5L,
            "任务经验领取不自动发等级奖励，五次技能来源与库存守恒");
        Console.WriteLine($"PASS battle-pass-skill-material-boundaries ({Count - before} checks)");
    }

    static long CountOf(BattlePassMissionState state, int id) =>
        state.Slots.Single(row => row.Instance.Key is { Category: 0 } && row.Instance.Key.Id == id).Count;

    static BattlePassCatalog Catalog()
    {
        var source = new List<object> { "[ui path]", "UI/MissionCoreBoundary.xui" };
        for (int id = 1; id <= 70; id++)
        {
            (int target, int raw, int parameter, string text) = id switch
            {
                5 => (1, 4, 190, "受身蹲伏"),
                11 => (1, 11, -1, "商城访问"),
                15 => (5, 15, 3037, "使用无色小晶块技能"),
                _ => (1, 777, -1, "边界夹具")
            };
            source.AddRange(new object[] { "[mission]", id, target, 100, raw, text, parameter, "[/mission]" });
        }
        source.Add("[daily mission]");
        for (int day = 0; day < 100; day++)
            source.AddRange(day == 0 ? new object[] { 5, 11, 15, 1 } : new object[] { 1, 2, 3, 4 });
        source.AddRange(new object[] { "[/daily mission]", "[weekly mission]" });
        for (int week = 0; week < 15; week++) source.AddRange(new object[] { 25, 26, 27, 28 });
        source.AddRange(new object[] { "[/weekly mission]", "[season mission]" });
        source.AddRange(Enumerable.Range(51, 18).Cast<object>());
        source.Add("[/season mission]");
        for (int level = 1; level <= 80; level++) source.AddRange(new object[] { "[level reward]", level, 999001, 1, 0, "[/level reward]" });
        source.Add("[exp table]");
        for (int level = 1; level <= 80; level++) source.AddRange(new object[] { level, level == 80 ? 0 : 1300 });
        source.AddRange(new object[] { "[/exp table]", "[tier table]", 1, 80, "[/tier table]", "[postal]", 1, 2, 3, 4, "[/postal]",
            "[package index]", 10, "[/package index]", "[certificate index]", 11, "[/certificate index]",
            "[buy restrict code]", 12, "[/buy restrict code]", "[golden mission exp ratio]", 3, "[/golden mission exp ratio]",
            "[cera consumption exp percentage]", 50, "[/cera consumption exp percentage]" });
        var strings = new List<string>();
        PvfToken Token(object value)
        {
            if (value is int number) return new(0, number);
            string text = (string)value; strings.Add(text);
            return new(text.StartsWith('[') ? (byte)3 : (byte)6, strings.Count - 1);
        }
        return BattlePassCatalog.Parse(source.Select(Token).ToArray(), index => strings[index]);
    }

    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
