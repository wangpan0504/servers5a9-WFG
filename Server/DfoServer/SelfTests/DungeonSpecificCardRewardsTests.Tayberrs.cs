using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
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
        var rooms = Testing.SelfTestCatalogs.Rooms(pvf);
        var monsterDrops = Testing.SelfTestCatalogs.MonsterDrops(pvf);
        var defaultDrops = new DungeonResourceDropPolicy(rules, items.DungeonDropItems, monsterDrops.Get, items.Items.Get);
        var customDropRules = System.Text.Json.JsonSerializer.Deserialize<TayberrsDropRules>(
            """{"AbyssInvitationRate":1000,"AbyssInvitationMinimumCount":2,"AbyssInvitationMaximumCount":2}""")!;
        var customDrops = new TayberrsDropPolicy(customDropRules, items.Items.Get);
        var clearLimits = ClearRewardLimitCatalog.Load(pvf);
        int draws = 0;
        var policy = new DungeonCardRewardPolicy(rules, items.DungeonDropItems, items.Items, items.GoldLimits,
            items.EpicPieces, _ => { draws++; return 0; }, prices,
            tayberrsRewards: new(prices, items.Boosters, items.Items, clearLimits));
        var withoutTable = new DungeonCardRewardPolicy(rules, items.DungeonDropItems, items.Items, items.GoldLimits, items.EpicPieces);
        var now = DateTimeOffset.Parse("2026-10-30T00:00:00Z");
        var membership = new CardMembership(now.AddDays(1).ToUnixTimeSeconds(), now.AddDays(1).ToUnixTimeSeconds());
        foreach (var (dungeonId, itemId, fragments, minimumWish, maximumWish) in new[]
            { (291100320, 10305620, 36, 12, 14), (291100322, 10306736, 29, 8, 10), (5349, 10308862, 22, 6, 8) })
        {
            var definition = dungeons.Get(dungeonId);
            byte difficulty = (byte)Enumerable.Range(0, 5).First(value => definition.AllowsDifficulty((byte)value, false));
            var maze = definition.Mazes.First(row => row.HellParty is null && row.QuestConnectionTokens.Count == 0);
            var bossMap = maze.MapSpecification!.Single(map => map.Kind == "boss");
            var entry = new DungeonEntryPlan(1, 0, checked((uint)dungeonId), 0, byte.MaxValue, difficulty, maze, bossMap, bossMap)
                { IsTayberrs = definition.IsTayberrs, BasisLevel = definition.BasisLevel };
            var boss = DungeonMonsterPlan.FromRoom(rooms.Get(bossMap, requirePassiveDefinitions: false), 1, 100,
                definition.BasisLevel).Monsters.Single(row => row.IsBoss &&
                    row.MonsterId is TayberrsDropPolicy.PartyBossMonsterId or TayberrsDropPolicy.SingleBossMonsterId);
            var defaultPlan = defaultDrops.PrepareEntry(entry)(boss);
            foreach (bool maximum in new[] { false, true })
            {
                var ground = defaultPlan.Generate(bound => maximum ? bound - 1 : 0, now);
                That(ground.Length == (maximum ? 3 : 1) && ground.All(row => row.GoldAmount == 0 &&
                    row.Item is { ItemId: TayberrsDropPolicy.CarbonCrystalId, Count: 1 }),
                    "泰波尔斯默认地面保留碳结晶体1至3个，不合成PVF没有领主来源的邀请函：" + dungeonId);
            }
            var customGround = customDrops.Prepare(entry, boss).Generate(_ => 0, now);
            That(customGround.Count(row => row.Item?.ItemId == TayberrsDropPolicy.AbyssInvitationId) == 2 &&
                customGround.All(row => row.Item is { Count: 1 }) &&
                customGround.Count(row => row.Item?.ItemId == TayberrsDropPolicy.CarbonCrystalId) == 1,
                "泰波尔斯显式JSON仍可配置邀请函概率与数量，不改变默认碳规则：" + dungeonId);
            That(string.Equals(definition.WarriorsDungeon, "Tayberrs", StringComparison.OrdinalIgnoreCase) &&
                prices.SpecificRewards.TryGetValue(dungeonId, out var rows) &&
                rows.SequenceEqual([new DungeonSpecificCardItem(itemId, 10000, 1)]) &&
                prices.SpecialClearRewards.TryGetValue(dungeonId, out var specialClear) &&
                specialClear == definition.SpecialClearReward &&
                specialClear.DungeonType == 71 &&
                specialClear.MailSender == "泰波尔斯奖励" &&
                specialClear.MailText == "物品栏空间不足， 泰波尔斯地下城奖励将通过邮件发送。" &&
                specialClear.Items.SequenceEqual([new SpecialDungeonClearRewardItem(10305821, 1_000_000, 2),
                    new SpecialDungeonClearRewardItem(10305820, 100_000, 1)]) &&
                specialClear.Roll(_ => 99_999).SequenceEqual([new SpecialDungeonClearRewardGrant(10305821, 2),
                    new SpecialDungeonClearRewardGrant(10305820, 1)]) &&
                specialClear.Roll(_ => 100_000).SequenceEqual([new SpecialDungeonClearRewardGrant(10305821, 2)]),
                "泰波尔斯三模式各保留类型71的独立固定奖励行与领主直入包灵石/原石声明：" + dungeonId);
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

        var simpleWire = DungeonPackets.SimpleClearRewardInfo([new(10305821, 2), new(10305820, 1)]);
        That(simpleWire.Length == 20 &&
            BinaryPrimitives.ReadUInt32LittleEndian(simpleWire) == 2 &&
            BinaryPrimitives.ReadUInt32LittleEndian(simpleWire.AsSpan(4)) == 10305821 &&
            BinaryPrimitives.ReadUInt32LittleEndian(simpleWire.AsSpan(8)) == 2 &&
            BinaryPrimitives.ReadUInt32LittleEndian(simpleWire.AsSpan(12)) == 10305820 &&
            BinaryPrimitives.ReadUInt32LittleEndian(simpleWire.AsSpan(16)) == 1,
            "NOTI 1610 按 count + (itemId,itemCount)*count 编码领主直入包奖励横幅");
        var storyDefinition = dungeons.Get(TayberrsDropPolicy.StoryDungeonId);
        That(storyDefinition.IsTayberrsStory && !storyDefinition.IsTayberrs && storyDefinition.SpecialClearReward is null,
            "泰波尔斯剧情地下城(291100321)标记为无特殊通关奖励");

        const int representative = 291100320;
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
        var (missions, clock) = DungeonBattlePassRegression.Schedule(pvf);
        using var fixture = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95,
            [.. done.Order()], position: position, items: [(10158964, 1)], itemCatalog: items.Items, timeProvider: clock);
        DungeonBattlePassRegression.Enable(fixture, missions, clock);
        var storyCharacter = fixture.Read() with { ActiveMainQuests = [new(0, 6040, 1) { AcceptanceId = Guid.NewGuid() }] };
        That(DungeonEntryPlan.TryCreate(dungeons, storyCharacter, TayberrsDropPolicy.StoryDungeonId, 6040, 0,
                out var storyPlan, out var storyReason, quest: quests.Get(6040), quests: quests) &&
            storyPlan!.IsTayberrsStory && storyPlan.DisableItemDrops,
            "泰波尔斯剧情地下城(291100321)入场计划标记为无地面掉落：" + storyReason);
        var (materialStart, materialEnd) = ItemSlotBoundService.Range(DfoServer.GameWorld.Items.ItemKind.Material, fixture.Read().Inventory);
        var fillerTemplate = items.Items.Get(10305616);
        var fillerCore = InventoryRewardGrantService.Create(fillerTemplate, fillerTemplate.StackLimit);
        var fullSlots = Enumerable.Range(materialStart, materialEnd - materialStart + 1)
            .Select(slot => InventoryRewardGrantService.CreateItem(fillerTemplate, new(InventoryListType.Main, (short)slot), fillerCore) with { Uid = (uint)slot })
            .ToArray();
        var fullInventoryCharacter = fixture.Read() with
        {
            Inventory = fixture.Read().Inventory with
            {
                Items = [.. fixture.Read().Inventory.Items.Where(row => row.Position.List != InventoryListType.Main ||
                    row.Position.Index < materialStart || row.Position.Index > materialEnd), .. fullSlots]
            }
        };
        var specialClearReward = prices.SpecialClearRewards[representative];
        var overflowGrant = InventoryRewardGrantService.TryGrantDirectItems(fullInventoryCharacter,
            [new(10305821, 2), new(10305820, 1)], items.Items, clock.GetUtcNow(), allowOverflowMail: true,
            reason: "special_dungeon_clear_reward", overflowMailSender: specialClearReward.MailSender,
            overflowMailText: specialClearReward.MailText);
        That(overflowGrant is { Accepted: true, Plan: { OverflowMailSender: "泰波尔斯奖励",
                OverflowMailText: "物品栏空间不足， 泰波尔斯地下城奖励将通过邮件发送。" } overflowPlan } &&
            overflowPlan.OverflowItems.Select(row => (row.Core.ItemId, row.Core.Count)).SequenceEqual([(10305821, 2), (10305820, 1)]),
            "泰波尔斯领主直入包奖励在物品栏满时按 [mail message] 转存邮件");
        byte entryDifficulty = (byte)Enumerable.Range(0, 5).First(value => dungeons.Get(representative).AllowsDifficulty((byte)value, false));
        var receipt = fixture.Session.EnterFormalDungeon(representative, 0, entryDifficulty, dungeons, rooms,
            drops: monsterDrops, items: items.Items, draw: _ => 0, pieces: items.EpicPieces)!;
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
                if (!monster.IsBoss)
                    That(death.Drops.IsEmpty && death.SpecialClearRewards.IsEmpty && death.SpecialClearRewardSlots.IsEmpty,
                        "泰波尔斯普通怪与绿名怪无任何地面掉落或特殊通关奖励：" + monster.MonsterId);
                else
                    That(!death.Drops.IsEmpty &&
                        death.Drops.All(drop => drop.Content.GoldAmount == 0 &&
                            drop.Content.Item?.ItemId == TayberrsDropPolicy.CarbonCrystalId) &&
                        death.SpecialClearRewards.SequenceEqual([new SpecialDungeonClearRewardGrant(10305821, 2),
                            new SpecialDungeonClearRewardGrant(10305820, 1)]) &&
                        death.SpecialClearRewardSlots.Length == 2 && !death.SpecialClearRewardMailOverflow &&
                        fixture.Read().Inventory.Items.Any(row => row.Core.ItemId == 10305821 && row.Core.Count == 2 && row.Uid > 0) &&
                        fixture.Read().Inventory.Items.Any(row => row.Core.ItemId == 10305820 && row.Core.Count == 1 && row.Uid > 0),
                        "泰波尔斯默认领主地面只掉碳结晶体，死亡同事务自动入包灵石x2与原石x1");
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
        Reject<InvalidOperationException>(() => fixture.Session.ResetRewardLimits(), "在线结算期间GM刷新仍拒绝，不绕开原会话门禁");
        That(fixture.Repository.LoadClearRewardLimitUsage(fixture.Owner, before.Id,
            clearLimits.FindDungeon(representative)!, ClearRewardLimitPolicy.Period(clock.GetUtcNow())) == new DungeonIncountUsage(1, 1),
            "泰波尔斯真实通关冻结即计入当日1次和当周1次，不等待选牌领取");
        string frozenJson = (string)fixture.Query("SELECT plan_json FROM character_dungeon_card_rewards")!;
        // 单独验证仓储合同：排除已冻结额度后，原牌面仍可沿正式结算与领取入口回放。
        That(fixture.Repository.ResetRewardLimits(fixture.Owner).ClearRewardRows == 1 &&
            fixture.Query("SELECT plan_json FROM character_dungeon_card_rewards") is string afterResetJson && afterResetJson == frozenJson,
            "仓储刷新单独排除次数，不改真实未领牌面的完整冻结JSON");
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
        var limitUsage = fixture.Repository.LoadClearRewardLimitUsage(fixture.Owner, afterClaim.Id,
            clearLimits.FindDungeon(representative)!, ClearRewardLimitPolicy.Period(clock.GetUtcNow()));
        That(limitUsage == default && fixture.Query("SELECT plan_json FROM character_dungeon_card_rewards") is string afterClaimJson && afterClaimJson == frozenJson,
            "刷新前的真实旧牌面领取与重复选牌不复活当日及当周次数，原JSON保持");
        That(policy.Prepare(dungeons.Get(representative), entryDifficulty, 1, utcNow: clock.GetUtcNow(),
            clearRewardUsage: limitUsage).Item is { ItemId: 10305616, Count: 36 }, "刷新后的下一份奖励计划重新冻结完整材料");
    }
}
