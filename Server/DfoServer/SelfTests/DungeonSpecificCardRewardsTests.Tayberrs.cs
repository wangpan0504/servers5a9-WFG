using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;
using System.Buffers.Binary;
using DfoServer.Network.Builders;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonSpecificCardRewardsTests
{
    public static void RunTayberrsResources(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters,
        ItemResources items, QuestCatalog quests, SkillLearningCatalog skills)
    {
        var dungeons = Testing.SelfTestCatalogs.Dungeons(pvf);
        var prices = Testing.SelfTestCatalogs.CardPrices(pvf);
        var rules = Testing.SelfTestCatalogs.DropRules(pvf);
        int draws = 0;
        var policy = new DungeonCardRewardPolicy(rules, items.DungeonDropItems, items.Items, items.GoldLimits,
            items.EpicPieces, _ => { draws++; return 0; }, prices,
            tayberrsRewards: new(prices, items.Boosters, items.Items));
        var withoutTable = new DungeonCardRewardPolicy(rules, items.DungeonDropItems, items.Items, items.GoldLimits, items.EpicPieces);
        var now = DateTimeOffset.Parse("2026-10-30T00:00:00Z");
        var membership = new CardMembership(now.AddDays(1).ToUnixTimeSeconds(), now.AddDays(1).ToUnixTimeSeconds());
        foreach (var (dungeonId, itemId, fragments, minimumWish, maximumWish) in new[]
            { (291100320, 10305620, 36, 12, 14), (291100322, 10306736, 29, 8, 10), (5349, 10308862, 22, 6, 8) })
        {
            var definition = dungeons.Get(dungeonId);
            byte difficulty = (byte)Enumerable.Range(0, 5).First(value => definition.AllowsDifficulty((byte)value, false));
            That(string.Equals(definition.WarriorsDungeon, "Tayberrs", StringComparison.OrdinalIgnoreCase) &&
                prices.SpecificRewards.TryGetValue(dungeonId, out var rows) &&
                rows.SequenceEqual([new DungeonSpecificCardItem(itemId, 10000, 1)]),
                "泰波尔斯三模式各保留类型71的独立固定奖励行：" + dungeonId);
            foreach (int roll in new[] { 0, 9999 })
            {
                var reward = policy.Prepare(definition, difficulty, 1,
                    draw: bound => roll == 0 ? 0 : bound - 1, utcNow: now);
                That(reward.Item is { } core && core.ItemId == (roll == 0 ? 10305616 : 10309040) && core.Count == fragments &&
                    reward.TayberrsWish is { ItemId: 10305617 } wish && wish.Count == (roll == 0 ? minimumWish : maximumWish) &&
                    reward.Items().Count() == 2 && reward.RuleId == TayberrsCardRewardPolicy.Rule &&
                    reward.ResourceSource == DungeonCardPriceCatalog.ResourcePath + ";" + definition.Path,
                    "泰波尔斯直接冻结两组材料，保留模式数量、账号绑定分支和天空之愿权重首尾：" + dungeonId);
                var wire = DungeonSettlementPackets.CardReward(new(0, 0, 0, 0, "fixture", "fixture"), 0, reward);
                That(wire[156] == 3 && BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(165)) == reward.Item!.ItemId &&
                    BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(169)) == fragments &&
                    BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(173)) == 10305617 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(177)) == reward.TayberrsWish!.Count,
                    "NOTI35写金币占位及两组真实材料，不再显示name_奖励容器：" + dungeonId);
            }
            Reject<NotSupportedException>(() => withoutTable.Prepare(definition, difficulty, 1, draw: _ => 0),
                "泰波尔斯缺少专表时明确拒绝，不能生成普通装备：" + dungeonId);
            var options = policy.PrepareOptions(definition, difficulty, 1, membership, now)!;
            That(options.Paid.Item is { Count: 1 } paid && paid.ItemId == 10158700 &&
                options.PaidDisabled == definition.DisablePaidCard &&
                options.UseGoldCard == !definition.DisablePaidCard &&
                (definition.DisableBlackDiamondCardReward ? options.BlackDiamond is null :
                    options.BlackDiamond?.Item is { ItemId: 10305616 } diamond && diamond.Count == fragments &&
                    options.BlackDiamond.TayberrsWish is { ItemId: 10305617 }),
                "泰波尔斯金币牌独立抽时空石，实际黄金牌与黑钻禁用标记优先于会员：" + dungeonId);
        }

        const int representative = 291100320;
        var rooms = Testing.SelfTestCatalogs.Rooms(pvf);
        var growth = Testing.SelfTestCatalogs.Growth(pvf, skills);
        var kills = Testing.SelfTestCatalogs.KillExperience(pvf);
        var clear = Testing.SelfTestCatalogs.ClearExperience(pvf);
        var area = towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            towns.DungeonMaps[world].Options.Any(option => option.DungeonId == representative));
        var done = new HashSet<int>(area.Quests) { 6116 };
        if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
        foreach (var gate in area.Map(done).DungeonGates.Where(gate => gate.Conditions.Length == 0).Take(1))
            done.UnionWith(gate.Quests);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)),
            checked((ushort)(bounds.Y + bounds.Height / 2)));
        using var fixture = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95,
            [.. done.Order()], position: position, items: [(10158964, 1)]);
        byte entryDifficulty = (byte)Enumerable.Range(0, 5).First(value => dungeons.Get(representative).AllowsDifficulty((byte)value, false));
        var receipt = fixture.Session.EnterFormalDungeon(representative, 0, entryDifficulty, dungeons, rooms, pieces: items.EpicPieces)!;
        That(receipt is not null, "泰波尔斯翻牌回归沿真实入场票、前置任务和疲劳准入：" + fixture.Session.LastFormalDungeonRejection);
        var path = QuestStoryRouteTests.Path(receipt!.Entry.Maze.StaticTopology, receipt.Entry.StartMap.Position, receipt.Entry.BossMap.Position);
        for (int index = 0; index < path.Length; index++)
        {
            receipt = fixture.Session.CompleteFormalDungeonLoading()!;
            foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking && row.CanReportDeath && !row.IsFriendly)
                .OrderBy(row => row.IsBoss).ToArray())
            {
                var death = fixture.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt, monster), kills, growth);
                That(death is { Outcome: DungeonRunOutcome.Accepted, Receipt: not null },
                    "泰波尔斯真实路线死亡提交成功：" + monster.MonsterId);
                receipt = death.Receipt!;
            }
            if (index + 1 < path.Length)
                That(fixture.Session.MoveFormalDungeon((byte)path[index + 1].X, (byte)path[index + 1].Y) is not null,
                    "泰波尔斯沿资源拓扑进入下一房间");
        }
        var before = fixture.Read();
        var report = DungeonSettlementProtocolTests.Report(before.Id, 99);
        var settled = fixture.Session.SettleFormalDungeon(before.Id, 99, 99, report, dungeons, clear, growth, policy);
        int frozenDraws = draws;
        That(settled is { Outcome: DungeonRunOutcome.Accepted, Card.Item.ItemId: 10305616, Card.Item.Count: 36,
                Card.TayberrsWish.ItemId: 10305617, Card.TayberrsWish.Count: 12 } &&
            fixture.Read().Inventory.Items.SequenceEqual(before.Inventory.Items),
            "泰波尔斯真实通关冻结两组材料，选牌前不提前写入背包");
        var replay = fixture.Session.SettleFormalDungeon(before.Id, 99, 99, report, dungeons, clear, growth, policy);
        That(replay.Outcome == DungeonRunOutcome.Duplicate && replay.Card == settled.Card && draws == frozenDraws,
            "泰波尔斯重复通关复用同一专属牌面，不重抽通用装备");
        That(fixture.Session.AdvanceFormalDungeonCards(false) is not null && fixture.Session.AdvanceFormalDungeonCards(true) is not null,
            "泰波尔斯保留正常翻牌阶段");
        var beforeClaim = fixture.Read();
        fixture.Sql("CREATE TRIGGER tayberrs_claim_fail BEFORE UPDATE OF claimed ON character_dungeon_card_rewards BEGIN SELECT RAISE(ABORT,'tayberrs claim rollback'); END");
        Reject<SqliteException>(() => fixture.Session.SelectFormalDungeonFreeCard(0, 0), "泰波尔斯领取回执末段失败整笔回滚");
        That(QuestCompletionRepositoryTests.Same(beforeClaim, fixture.Read()) &&
            fixture.Query("SELECT claimed FROM character_dungeon_card_rewards") is 0L,
            "泰波尔斯领取失败不留下奖励包、金币或领取位");
        fixture.Sql("DROP TRIGGER tayberrs_claim_fail");
        var claim = fixture.Session.SelectFormalDungeonFreeCard(0, 0);
        var afterClaim = fixture.Read();
        That(claim?.Inventory is { Accepted: true } && afterClaim.Inventory.Items.Count(row =>
                row.Core.ItemId == 10305616 && row.Core.Count == 36 && row.Uid > 0) == 1 &&
            afterClaim.Inventory.Items.Count(row => row.Core.ItemId == 10305617 && row.Core.Count == 12 && row.Uid > 0) == 1 &&
            !afterClaim.Inventory.Items.Any(row => row.Core.ItemId == 10305620) &&
            fixture.Query("SELECT claimed FROM character_dungeon_card_rewards") is 1L && draws == frozenDraws,
            "泰波尔斯领取重试完整发放两组材料并创建真实UID，不发奖励包或重新随机");
        That(fixture.Session.SelectFormalDungeonFreeCard(0, 0) is { Duplicate: true, Inventory: null } &&
            QuestCompletionRepositoryTests.Same(afterClaim, fixture.Read()),
            "泰波尔斯重复选牌不再次增加物品、金币或角色版本");
    }
}
