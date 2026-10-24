using DfoServer.Game.Dungeons;
using DfoServer.Game.Currency;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class HarlemMaterialDropTests
{
    public static void RunResources(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills)
    {
        int before = Count;
        var dungeons = Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms = Testing.SelfTestCatalogs.Rooms(pvf);
        var growth = Testing.SelfTestCatalogs.Growth(pvf, skills);
        var kills = Testing.SelfTestCatalogs.KillExperience(pvf);
        var drops = DungeonMonsterDropCatalog.Load(pvf);
        var harlem = towns.DungeonMaps.Values.Where(map => map.HellDungeon && map.HellQuests.Contains(6080))
            .SelectMany(map => map.Options).Select(option => checked((uint)option.DungeonId)).ToArray();
        var rules = Testing.SelfTestCatalogs.DropRules(pvf);
        var epicCandidates = HarlemHellEpicDropTests.RunResources(pvf, items, harlem);
        foreach (var (id, content, count) in new[] { (10305940, 3330, 500), (10307527, 10305821, 20) })
        {
            var booster = items.Boosters.Get(id);
            That(booster.Available && booster.Pools.Length == 1 && booster.Pools[0].Draws == 1 &&
                booster.Pools[0].Rewards.Length == 1 && booster.Pools[0].Rewards[0].ItemId == content && booster.Pools[0].Rewards[0].Count == count,
                "当前PVF晶石与20灵石礼袋身份及可开启内容正确，不借50灵石升级礼袋替代");
        }
        var hellRules = DungeonHellDropRules.TryLoad(pvf)!;
        var hell = new DungeonHellRewardPolicy(hellRules, items.DungeonDropItems);
        Verify(291100308, false, 50);
        Verify(291100308, true, 50);
        Verify(291100308, false, 50, specialEpic: true);
        Verify(291100308, true, 50, specialEpic: true);
        Verify(291100308, true, 50, rareExtra: true);
        Verify(291100308, true, 50, rareExtra: true, rareLast: true);
        Verify(291100308, false, 50, bonus: true);
        Verify(291100308, true, 50, bonus: true);
        Verify(7191, false, 50, bonus: true);
        Verify(530, false, 0);
        Verify(530, false, 1000);
        Console.WriteLine($"PASS {Count - before} 哈林普通/星空及黎明材料正式死亡、拾取和回滚资源断言");

        void Verify(uint dungeonId, bool legend, int purgedPermille, bool specialEpic = false, bool rareExtra = false, bool rareLast = false, bool bonus = false)
        {
            bool cosmo = dungeonId == 7191;
            var epics = rareExtra ? new HarlemHellEpicDropPolicy(harlem, epicCandidates.Normal, epicCandidates.Weapons,
                epicCandidates.Tayberrs, epicCandidates.Cosmo, epicCandidates.Rules with { LegendRareEpicPermille = 1000 }, superPool: epicCandidates.Super) : epicCandidates;
            var area = towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
                towns.DungeonMaps[world].Options.Any(option => option.DungeonId == dungeonId));
            var done = new HashSet<int>(area.Quests) { 6030, 6080, 6081, 6082, 10112 };
            if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
            var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
            var position = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)),
                checked((ushort)(bounds.Y + bounds.Height / 2)));
            using var fixture = new QuestSceneTests.Fixture(towns, characters, skills, growth, quests, false, 95,
                done.Order().ToArray(), position: position,
                items: [(3330, cosmo ? 80 : 60), (HellPartyEntryCost.LegendStone, cosmo ? 8 : 4), (10161610, 40)]);
            var materials = new HarlemMaterialDropPolicy(new() { DawnPurgedPermille = purgedPermille }, harlem, items.Items.Get, epics);
            var resourcePolicy = new DungeonResourceDropPolicy(rules, items.DungeonDropItems, drops.Get,
                items.Items.Get, items.DungeonSpecialDrops, materials: materials, harlemEpics: epics,
                abyssBonuses: bonus ? new(new(), epics, items.Items.Get) : null);
            int drawCalls = 0;
            var receipt = Enter();
            That(receipt is not null, "材料回归沿真实准入进入副本: " + dungeonId + ":" + fixture.Session.LastFormalDungeonRejection);
            var target = receipt!.Entry.HellParty?.Room ?? receipt.Entry.BossMap.Position;
            var path = QuestStoryRouteTests.Path(receipt.Entry.Maze.StaticTopology, receipt.Entry.StartMap.Position, target);
            int materialDeaths = 0, collected = 0, epicDeaths = 0, epicCollected = 0, wishCollected = 0, rareCollected = 0;
            int expectedEpic = (cosmo ? epics.Tayberrs! : specialEpic || bonus ? epics.Weapons! : epics.Normal).Entries[^1].Template.Id;
            int rareEpic = epics.Tayberrs!.Entries[^1].Template.Id;
            int expectedRolls = dungeonId == 530 ? 0 : rooms.HellParty.RewardRollCount(cosmo ? (byte)1 : receipt.Entry.HellPartyMode);
            bool faulted = false;
            uint oldGroundKey = 0;
            int expectedId = dungeonId == 530 && purgedPermille == 1000 ? HarlemMaterialDropPolicy.PurgedId : HarlemMaterialDropPolicy.FragmentId;
            int expectedCount = dungeonId == 530 ? purgedPermille == 1000 ? 120 : 200 : cosmo ? 30 : legend ? 15 : 9;
            var bonusAmounts = new Dictionary<int, int> { [3330] = 2, [10158124] = 1, [10305940] = 1 };
            if (legend || cosmo) { bonusAmounts.Add(10305616, cosmo ? 10 : 2); bonusAmounts.Add(10307527, 1); }
            var bonusBefore = bonusAmounts.Keys.ToDictionary(id => id, id => ItemAmount(id));
            var bonusCollected = bonusAmounts.Keys.ToDictionary(id => id, _ => 0);
            int bonusDeaths = 0;
            uint oldBonusKey = 0;
            foreach (var point in path)
            {
                receipt = fixture.Session.CompleteFormalDungeonLoading()!;
                That(receipt is not null, "真实材料路线加载房间");
                // 逆序击杀证明奖励归实际末杀，而不是最后一个静态key。
                foreach (var monster in receipt!.Monsters.Monsters.Where(row => row.IsBlocking && row.CanReportDeath && !row.IsFriendly)
                    .OrderBy(row => rareLast && epics.IsLegendRare(receipt.Entry, row) ? 1 : 0)
                    .ThenByDescending(row => row.ObjectKey).ToArray())
                {
                    var observation = FormalDungeonSessionTests.Observation(receipt, monster);
                    if (dungeonId == 530 && monster.IsBoss)
                    {
                        var cached = resourcePolicy.PrepareEntry(receipt.Entry, hell);
                        That(!cached(monster with { IsFriendly = true }).ResourceSource.Contains(HarlemMaterialDropPolicy.Rule) &&
                            cached(monster).ResourceSource.Contains(HarlemMaterialDropPolicy.Rule) &&
                            !cached(monster with { Source = monster.Source! with { Modifiers = ["[dummy]"] } }).ResourceSource.Contains(HarlemMaterialDropPolicy.Rule),
                            "同模板同等级的友方、真Boss和脚本dummy不串用材料缓存");
                    }
                    bool lastHell = cosmo ? epics.IsCosmoBoss(receipt.Entry, monster) : monster.HellRewardRolls > 0 &&
                        (rareLast ? epics.IsLegendRare(receipt.Entry, monster) : monster.ObjectKey ==
                        receipt.Monsters.Monsters.Where(row => row.HellRewardRolls > 0).Min(row => row.ObjectKey));
                    if (!faulted && (rareExtra ? epics.IsLegendRare(receipt.Entry, monster) :
                        dungeonId == 530 && purgedPermille == 1000 && monster.IsBoss || lastHell))
                    {
                        var saved = fixture.Read();
                        fixture.Sql("CREATE TRIGGER fail_harlem_material AFTER INSERT ON character_dungeon_growth_receipts BEGIN SELECT RAISE(ABORT,'material death retry'); END");
                        Reject<SqliteException>(() => fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth),
                            "末杀地面写入故障不能部分提交死亡、经验、装备或材料");
                        That(QuestCompletionRepositoryTests.Same(saved, fixture.Read()), "材料死亡事务失败保留完整原角色快照");
                        fixture.Sql("DROP TRIGGER fail_harlem_material");
                        int frozenDraws = drawCalls;
                        var resumed = fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                        That(drawCalls == frozenDraws && resumed.Outcome == DungeonRunOutcome.Accepted,
                            "末杀原事件重试复用冻结分支，不重新抽装备或材料");
                        receipt = resumed.Receipt!;
                        Collect(resumed);
                        faulted = true;
                    }
                    else
                    {
                        var death = fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                        That(death.Outcome == DungeonRunOutcome.Accepted && death.Receipt is not null,
                            "真实演员死亡沿既有提交入口: " + monster.MonsterId);
                        receipt = death.Receipt!;
                        Collect(death);
                    }
                    var duplicate = fixture.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                    That(duplicate.Outcome == DungeonRunOutcome.Duplicate && duplicate.Drops.IsEmpty,
                        "重复死亡不重复发布材料地面对象");
                }
                if (point == target) break;
                var next = path[Array.IndexOf(path, point) + 1];
                receipt = fixture.Session.MoveFormalDungeon((byte)next.X, (byte)next.Y)!;
                That(receipt is not null, "沿真实地图移动到材料奖励源");
            }
            That(materialDeaths == 1 && collected == expectedCount && Amount() == expectedCount,
                "每次真实挑战只由一个合法死亡源发放完整材料，拾取后写入库存");
            That(wishCollected == (cosmo ? 10 : legend ? 2 : 0) && fixture.Read().Inventory.Items
                .Where(item => item.Core.ItemId == HarlemMaterialDropPolicy.SkyWishId).Sum(item => item.Core.Count) == wishCollected,
                "星空实际末杀仅一份天空之愿，死亡事务失败重试及重复包不多发，拾取保存完整数量");
            That(dungeonId == 530 ? epicDeaths == 0 : epicDeaths == 1 && epicCollected == expectedRolls && EpicAmount() == expectedRolls,
                "真实哈林普通/星空仅实际末杀掉落专表95史诗，装备骰数不变且拾取持久化；黎明不追加史诗");
            That(rareCollected == (rareExtra ? 1 : 0) && (cosmo || fixture.Read().Inventory.Items.Count(item => item.Core.ItemId == rareEpic) == rareCollected),
                "稀有怪先死或最后死均独立发一件测试奖励，故障重试和重复死亡不重发；默认关闭时不额外发奖");
            That(bonusDeaths == (bonus ? 1 : 0) && bonusAmounts.All(pair => bonusCollected[pair.Key] == (bonus ? pair.Value : 0) &&
                ItemAmount(pair.Key) == bonusBefore[pair.Key] + bonusCollected[pair.Key]),
                $"三种深渊仅合法末杀或终点发完整补全奖励，死亡故障重试、重复死亡及拾取均不多发：id={dungeonId},legend={legend},deaths={bonusDeaths}," +
                string.Join(';', bonusAmounts.Select(pair => $"{pair.Key}:expected={pair.Value},collected={bonusCollected[pair.Key]},before={bonusBefore[pair.Key]},after={ItemAmount(pair.Key)}")));
            That(fixture.Session.ReturnFromFormalDungeon() is not null && Amount() == expectedCount,
                "回城保留已拾材料，不在退场追加发奖");
            That(EpicAmount() == epicCollected, "回城保留已拾史诗，不在退场重发");
            var reentered = Enter();
            That(reentered is not null && Amount() == expectedCount &&
                fixture.Session.PickFormalDungeonItem(oldGroundKey, items.Items, items.EpicPieces, items.GoldLimits).Outcome == DungeonRunOutcome.Rejected,
                "新挑战不提前发材料，也不能消费旧实例的地面key");
            if (bonus) That(fixture.Session.PickFormalDungeonItem(oldBonusKey, items.Items, items.EpicPieces, items.GoldLimits)
                .Outcome == DungeonRunOutcome.Rejected, "新挑战不能拾取旧深渊补全奖励key");

            int ItemAmount(int id) => id == HarlemAbyssBonusDropPolicy.SoulId
                ? checked((int)fixture.Read().Inventory.AccountWallet.Get(CurrencyKind.EpicSoul))
                : fixture.Read().Inventory.Items.Where(item => item.Core.ItemId == id).Sum(item => item.Core.Count);
            int Amount() => fixture.Read().Inventory.Items.Where(item => item.Core.ItemId == expectedId).Sum(item => item.Core.Count);
            int EpicAmount() => fixture.Read().Inventory.Items.Count(item => item.Core.ItemId == expectedEpic);
            FormalDungeonReceipt? Enter()
            {
                if (legend)
                {
                    That(fixture.Session.BeginDungeonSelection() is not null, "星空材料回归建立选图上下文");
                    That(fixture.Session.PrepareLegendDungeonSelection(dungeons, _ => 2) == dungeonId, "星空材料沿冻结目标准入");
                }
                return fixture.Session.EnterFormalDungeon(dungeonId, 0, cosmo ? (byte)4 : dungeonId == 530 ? (byte)3 : (byte)0,
                    dungeons, rooms, drops, items.Items, bound =>
                    {
                        drawCalls++;
                        // 普通通道保持不命中；定向命中原末波史诗分支，验证专表候选的实际创建与拾取。
                        return dungeonId != 530 ? bound switch
                        { 1001 => 0, 1000000 => hellRules.Rarity(1)![3], 3 when bonus => 0,
                            1000 when specialEpic || bonus => 0, _ => bound - 1 } : bound - 1;
                    }, items.EpicPieces,
                    prepareDrops: entry => resourcePolicy.PrepareEntry(entry, hell), hellParty: dungeonId != 530, hellLegend: legend);
            }
            void Collect(FormalDungeonDeathResult death)
            {
                var extras = death.Drops.Where(row => !row.IsGold && bonusAmounts.ContainsKey(row.Core.ItemId)).ToArray();
                if (extras.Length > 0) bonusDeaths++;
                foreach (var ground in extras)
                {
                    That(bonus && ground.Core.Count == 1 && ground.ResourceIdentity.Contains(HarlemAbyssBonusDropPolicy.Rule),
                        "补全奖励逐个拥有地面key并保留本服规则来源");
                    oldBonusKey = ground.Reservation.GroundKey;
                    if (bonusCollected.Values.All(count => count == 0))
                    {
                        var saved = fixture.Read();
                        fixture.Sql("CREATE TRIGGER fail_bonus_pickup AFTER INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'bonus pickup retry'); END");
                        Reject<SqliteException>(() => fixture.Session.PickFormalDungeonItem(oldBonusKey, items.Items, items.EpicPieces, items.GoldLimits),
                            "补全奖励拾取故障回滚");
                        That(QuestCompletionRepositoryTests.Same(saved, fixture.Read()), "补全拾取故障不能部分写入背包");
                        fixture.Sql("DROP TRIGGER fail_bonus_pickup");
                    }
                    That(fixture.Session.PickFormalDungeonItem(oldBonusKey, items.Items, items.EpicPieces, items.GoldLimits)
                        .Outcome == DungeonRunOutcome.Accepted, "补全奖励沿原地面拾取事务持久化");
                    bonusCollected[ground.Core.ItemId]++;
                    That(fixture.Session.PickFormalDungeonItem(oldBonusKey, items.Items, items.EpicPieces, items.GoldLimits)
                        .Outcome == DungeonRunOutcome.Rejected, "补全奖励不能重复拾取");
                }
                foreach (var ground in death.Drops.Where(row => rareExtra && !row.IsGold && row.Core.ItemId == rareEpic))
                {
                    That(rareExtra && death.ReportedTarget is not null && ground.Core.Count == 1 &&
                        ground.ResourceIdentity.Contains(HarlemHellEpicDropPolicy.RareRule), "额外泰波尔斯保持自己的奖励来源");
                    That(fixture.Session.PickFormalDungeonItem(ground.Reservation.GroundKey, items.Items, items.EpicPieces, items.GoldLimits)
                        .Outcome == DungeonRunOutcome.Accepted, "稀有怪额外装备沿正式拾取入库");
                    rareCollected++;
                    That(fixture.Session.PickFormalDungeonItem(ground.Reservation.GroundKey, items.Items, items.EpicPieces, items.GoldLimits)
                        .Outcome == DungeonRunOutcome.Rejected, "稀有怪额外装备不能重复拾取");
                }
                foreach (var wish in death.Drops.Where(row => !row.IsGold && row.Core.ItemId == HarlemMaterialDropPolicy.SkyWishId))
                {
                    That(wish.Core.Count == 1, "天空之愿每个地面物品各1个");
                    var pickup = fixture.Session.PickFormalDungeonItem(wish.Reservation.GroundKey, items.Items, items.EpicPieces, items.GoldLimits);
                    That(pickup.Outcome == DungeonRunOutcome.Accepted && pickup.Commit is { Duplicate: false }, "天空之愿沿真实拾取事务入库");
                    wishCollected += wish.Core.Count;
                    That(fixture.Session.PickFormalDungeonItem(wish.Reservation.GroundKey, items.Items, items.EpicPieces, items.GoldLimits)
                        .Outcome == DungeonRunOutcome.Rejected, "天空之愿重复拾取不重发");
                }
                var equipment = death.Drops.Where(row => !row.IsGold && (!rareExtra || row.Core.ItemId != rareEpic) &&
                    items.Items.Get(row.Core.ItemId).Kind == GameWorld.Items.ItemKind.Equipment).ToArray();
                if (equipment.Length > 0)
                {
                    epicDeaths++;
                    That(equipment.Length == expectedRolls && equipment.All(row => row.Core.ItemId == expectedEpic &&
                        row.ResourceIdentity.Contains(HarlemHellEpicDropPolicy.Rule)),
                        "只有实际末杀一次生成完整装备骰，原等级过滤不再清空95专表");
                    foreach (var ground in equipment)
                    {
                        if (epicCollected == 0)
                        {
                            var saved = fixture.Read();
                            fixture.Sql("CREATE TRIGGER fail_harlem_epic_pickup AFTER INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'epic pickup retry'); END");
                            Reject<SqliteException>(() => fixture.Session.PickFormalDungeonItem(ground.Reservation.GroundKey,
                                items.Items, items.EpicPieces, items.GoldLimits), "史诗拾取写入故障回滚");
                            That(QuestCompletionRepositoryTests.Same(saved, fixture.Read()), "史诗拾取故障保留完整原角色快照");
                            fixture.Sql("DROP TRIGGER fail_harlem_epic_pickup");
                        }
                        var pickup = fixture.Session.PickFormalDungeonItem(ground.Reservation.GroundKey, items.Items, items.EpicPieces, items.GoldLimits);
                        That(pickup.Outcome == DungeonRunOutcome.Accepted && pickup.Commit is { Duplicate: false }, "专表史诗沿正式地面拾取保存");
                        epicCollected++;
                        That(fixture.Session.PickFormalDungeonItem(ground.Reservation.GroundKey, items.Items, items.EpicPieces, items.GoldLimits).Outcome == DungeonRunOutcome.Rejected,
                            "重复拾取不能重发专表史诗");
                    }
                }
                var material = death.Drops.Where(row => !row.IsGold && row.Core.ItemId is HarlemMaterialDropPolicy.FragmentId or HarlemMaterialDropPolicy.PurgedId).ToArray();
                if (material.Length == 0) return;
                materialDeaths++;
                That(material.All(row => row.Core.ItemId == expectedId && row.ResourceIdentity.Contains(HarlemMaterialDropPolicy.Rule)) &&
                    material.Sum(row => row.Core.Count) == expectedCount &&
                    (dungeonId == 530 || material.All(row => row.Core.Count == 1) &&
                        material.Select(row => row.Reservation.GroundKey).Distinct().Count() == expectedCount),
                    "深渊材料逐个占用独立地面key并保留完整数量及本服来源，黎明仍沿原互斥堆叠规则");
                foreach (var ground in material)
                {
                    oldGroundKey = ground.Reservation.GroundKey;
                    if (dungeonId == 530 && purgedPermille == 1000 && !faulted)
                    {
                        var saved = fixture.Read();
                        fixture.Sql("CREATE TRIGGER fail_harlem_pickup AFTER INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'material pickup retry'); END");
                        FormalDungeonPickupResult? attempt = null;
                        bool failed = false;
                        try { attempt = fixture.Session.PickFormalDungeonItem(oldGroundKey, items.Items, items.EpicPieces, items.GoldLimits); }
                        catch (SqliteException) { failed = true; }
                        That(failed, $"材料拾取必须走到事务故障点，outcome={attempt?.Outcome},capacity={attempt?.CapacityRejected},kind={ground.Core.Kind},count={ground.Core.Count},phase={fixture.Session.Phase}");
                        That(QuestCompletionRepositoryTests.Same(saved, fixture.Read()), "拾取故障保留库存原数量");
                        fixture.Sql("DROP TRIGGER fail_harlem_pickup");
                    }
                    var pickup = fixture.Session.PickFormalDungeonItem(oldGroundKey, items.Items, items.EpicPieces, items.GoldLimits);
                    That(pickup.Outcome == DungeonRunOutcome.Accepted && pickup.Commit is { Duplicate: false }, "原材料地面key实际拾取成功");
                    collected += ground.Core.Count;
                    That(fixture.Session.PickFormalDungeonItem(oldGroundKey, items.Items, items.EpicPieces, items.GoldLimits).Outcome == DungeonRunOutcome.Rejected,
                        "重复材料拾取不能再加数量");
                }
            }
        }
    }
}
