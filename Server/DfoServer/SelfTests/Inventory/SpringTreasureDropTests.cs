using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class SpringTreasureDropTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.FromHours(8));

    public static void RunCore()
    {
        var rows = Enumerable.Range(0, 8).Select(slot => (Template(slot), 30179 + slot % 3)).ToArray();
        var catalog = new SpringTreasureAvatarCatalog(rows);
        var historical = new ItemTemplate(490705287, ItemKind.Consumable, 0, 1, 0, 1000, 0, 0, 0, "[account]", ushort.MaxValue, 0, "宝藏")
            { Expiration = new(1_584_568_800) };
        var policy = new SpringTreasureDropPolicy(catalog, historical);
        var character = Equipped(rows.Select(row => row.Item1));
        var entry = Entry();
        var boss = Boss();
        That(policy.HasOutfit(character, Now) && !policy.HasOutfit(character with { Level = 84 }, Now), "八槽混搭与85级实际门槛");
        foreach (int set in new[] { 30179, 30180, 30181 })
        {
            var single = new SpringTreasureAvatarCatalog(rows.Select(row => (row.Item1, set)));
            That(new SpringTreasureDropPolicy(single, historical).HasOutfit(character, Now), "三种原套均可八件 " + set);
        }
        for (short slot = 0; slot < 8; slot++)
        {
            var items = character.Inventory.Items;
            var item = items.Single(row => row.Position.Index == slot);
            foreach (var changed in new[]
            {
                items.Remove(item), items.Replace(item, item with { Position = new(InventoryListType.Avatar, slot) }),
                items.Replace(item, item with { Avatar = item.Avatar! with { ExpireTime = (int)Now.ToUnixTimeSeconds() } }),
                items.Replace(item, item with { Core = item.Core with { ItemId = 999999 } }),
                items.Replace(item, item with { Position = new(InventoryListType.Equipment, 8) }),
                items.Replace(item, item with { Position = new(InventoryListType.Equipment, 10) })
            }) That(!policy.HasOutfit(character with { Inventory = character.Inventory with { Items = changed } }, Now), "缺槽/背包/到期/借外观不补槽 " + slot);
        }
        var locked = character.Inventory.Items[0];
        That(policy.HasOutfit(character with { Inventory = character.Inventory with { Items = character.Inventory.Items.Replace(locked,
            locked with { Core = locked.Core with { EquipmentLockId = 1 } }) } }, Now), "穿戴锁只限制操作，不取消合法效果");
        That(!policy.HasOutfit(character with { Job = 1 }, Now), "非法职业的装配不能取得资格");
        foreach (int roll in new[] { 0, 99, 100, 999 })
        {
            int calls = 0;
            var result = policy.Generate(character, entry, boss, bound => { calls++; That(bound == 1000, "固定10%分母"); return roll; }, Now, true, false);
            That(result.Count == (roll < 100 ? 1 : 0) && calls == 1, "99命中/100不命中，单Boss一骰 " + roll);
            if (result.Count > 0) That(result[0].Item is { ItemId: 490705287, Count: 1, ExpireTime: ItemExpiration.MaximumUnixTime }, "当前日历按既有商城绝对期限开关创建原287");
        }
        int deniedCalls = 0;
        int Denied(int _) { deniedCalls++; return 0; }
        That(policy.Generate(character with { Level = 84 }, entry, boss, Denied, Now, true, false).Count == 0 &&
            policy.Generate(character, entry with { SpringTreasureRecommended = false }, boss, Denied, Now, true, false).Count == 0 &&
            policy.Generate(character, entry, boss with { IsFriendly = true }, Denied, Now, true, false).Count == 0 &&
            policy.Generate(character, entry, new(0, 2, 1, 85, null), Denied, Now, true, false).Count == 0 && deniedCalls == 0,
            "不合格/非Boss/友方不消耗活动骰");
        That(policy.Generate(character, entry, boss, _ => 0, Now, false, false).Count == 0, "绝对期限开关false保留原到期空结果");
        bool active = false;
        var gated = new SpringTreasureDropPolicy(catalog, historical, () => active);
        int gateCalls = 0;
        That(gated.Generate(character, entry, boss, _ => { gateCalls++; return 0; }, Now, true, false).Count == 0 && gateCalls == 0,
            "Disabled/Invalid活动门关闭时零活动骰零产物");
        active = true;
        That(gated.Generate(character, entry, boss, _ => { gateCalls++; return 0; }, Now, true, false).Count == 1 && gateCalls == 1,
            "仅同活动目录有效Active身份放行");
        var relative = new SpringTreasureDropPolicy(catalog, historical with { Expiration = new(0, 3) });
        That(relative.Generate(character, entry, boss, _ => 0, Now, true, false).Single().Item!.ExpireTime == Now.AddDays(3).ToUnixTimeSeconds(), "仅忽略绝对日期不抹相对三天");
        That(relative.Generate(character, entry, boss, _ => 0, Now, true, true).Single().Item!.ExpireTime == ItemExpiration.MaximumUnixTime,
            "相对期限仅由原独立usable-period开关明确控制");
        var daily = new SpringTreasureDropPolicy(catalog, historical with { Expiration = new(0, 3, 1) });
        That(daily.Generate(character, entry, boss, _ => 0, Now, true, true).Single().Item!.ExpireTime ==
            new DateTimeOffset(2026, 11, 1, 6, 0, 0, TimeSpan.FromHours(8)).ToUnixTimeSeconds(), "两个忽略开关仍保每日删除");
        That(SpringTreasureDropPolicy.ReservationCount(0, true) == 2 && SpringTreasureDropPolicy.ReservationCount(499, true) == 500 &&
            SpringTreasureDropPolicy.ReservationCount(500, false) == 500, "空普通计划及原500预留边界");
        bool overflow = false;
        try { _ = SpringTreasureDropPolicy.ReservationCount(500, true); } catch (InvalidDataException) { overflow = true; }
        That(overflow, "不能截断原500对象来容纳活动对象");
        var definition = new DungeonDefinition(310, "dungeon/normal.dgn", 87, 90, [entry.Maze], []);
        That(SpringTreasureDropPolicy.IsRecommended(definition, entry, 90) && !SpringTreasureDropPolicy.IsRecommended(definition, entry, 95) &&
            SpringTreasureDropPolicy.IsPotential(definition, entry), "严格推荐区间及队长不合格时仍预留个人成员容量");
        That(!SpringTreasureDropPolicy.IsRecommended(definition with { Id = 530 }, entry with { DungeonId = 530 }, 90), "已证黎明裂缝排除");
        foreach (var excluded in new[] { entry with { IsPractice = true }, entry with { IsTutorial = true }, entry with { IsQuestReplay = true },
            entry with { IsOrdinaryLuke = true }, entry with { IsOrdinaryPrey = true },
            entry with { IsCosmo = true }, entry with { IsInfernoParty = true }, entry with { IsTagEvildom = true },
            entry with { IsImperialArena = true }, entry with { IsBossRush = true }, entry with { QuestId = 123 },
            entry with { HellPartyMode = 1 }, entry with { IsAncientInfinite = true } })
            That(!SpringTreasureDropPolicy.IsPotential(definition, excluded), "独立模式不串入普通活动经济");
        foreach (var excludedDefinition in new[] { definition with { Id = SleepyHollowRewardCatalog.DungeonId },
            definition with { WarriorsDungeon = "Tayberrs" }, definition with { Path = "dungeon/towers/test.dgn" },
            definition with { RawSections = [new("evildom war dungeon", [])] },
            definition with { RawSections = [new("warriors dungeon", [])] },
            definition with { RawSections = [new("operation hope dungeon", [new(0, 1)])] },
            definition with { RawSections = [new("cosmo dungeon", [])] } })
            That(!SpringTreasureDropPolicy.IsPotential(excludedDefinition, entry) &&
                !SpringTreasureDropPolicy.IsRecommended(excludedDefinition, entry, 90), "已知特殊DGN的容量与个人资格共同拒绝");
        var explicitRange = definition with { RawSections = [new("recommended level", [new(0, 85), new(0, 95)])] };
        That(SpringTreasureDropPolicy.IsRecommended(explicitRange, entry, 85) && SpringTreasureDropPolicy.IsRecommended(explicitRange, entry, 95), "真实recommended闭区间优先");
        That(!SpringTreasureDropPolicy.IsPotential(explicitRange with { RawSections = [new("recommended level", [new(0, 85)])] }, entry), "残缺推荐字段不回退");
        That(!SpringTreasureDropPolicy.IsPotential(definition with { RawSections = [new("event dungeon", [])] }, entry), "现有显式特殊类排除");
        That(!SpringTreasureDropPolicy.IsPotential(definition with { MinimumLevel = 1, BasisLevel = 84 }, entry) &&
            SpringTreasureDropPolicy.IsPotential(definition with { MinimumLevel = 84, BasisLevel = 85 }, entry), "潜在推荐区间保85级交集边界");
        var higherRange = definition with { RawSections = [new("recommended level", [new(0, 96), new(0, 110)])], MinimumLevel = 1, BasisLevel = 84 };
        That(SpringTreasureDropPolicy.IsPotential(higherRange, entry) && SpringTreasureDropPolicy.IsRecommended(higherRange, entry, 100) &&
            !SpringTreasureDropPolicy.IsRecommended(higherRange, entry, 95), "显式推荐优先且潜在预留不擅自加95级上限");
        That(SpringTreasureDropPolicy.IsPotential(definition with { RawSections = [new("recommended level", [new(0, int.MaxValue), new(0, int.MaxValue)])] }, entry),
            "推荐存在等级查询不按范围长度循环或溢出");
        foreach (var malformed in new[]
        {
            explicitRange with { RawSections = [new("recommended level", [new(0, 95), new(0, 85)])] },
            explicitRange with { RawSections = [new("recommended level", [new(1, 85), new(0, 95)])] },
            explicitRange with { RawSections = [new("recommended level", [new(0, 85), new(0, 95)]), new("recommended level", [new(0, 85), new(0, 95)])] },
            definition with { MinimumLevel = 90, BasisLevel = 85 }
        }) That(!SpringTreasureDropPolicy.IsPotential(malformed, entry) && !SpringTreasureDropPolicy.IsRecommended(malformed, entry, 90),
            "畸形或重复推荐字段和倒置后备范围严格拒绝，不回退或预留");
        var partyDefinition = definition with { RawSections = [new("difficulty level", [new(0, 1), new(0, 1), new(0, 1), new(0, 1), new(0, 0)])] };
        That(DungeonEntryPlan.TryCreate(partyDefinition, character with { Level = 95 }, 310, 0, 0, out var leader, out _), "队长正常推荐外等级仍能普通入场");
        That(leader is { SpringTreasurePotential: true, SpringTreasureRecommended: false }, "共享路线按潜在资格预留，不随队长个人等级丢容量");
        That(DungeonEntryPlan.TryCreate(partyDefinition, character with { Id = 2, Level = 90 }, 310, 0, 0, out var participant, out _, partyRoute: leader),
            "实际partyRoute分支为本人的推荐等级规划");
        That(participant is { SpringTreasurePotential: true, SpringTreasureRecommended: true }, "实际party分支重算本人推荐，不复制队长false");
    }

    public static void RunResources(ItemResources resources)
    {
        var catalog = resources.SpringTreasureAvatars;
        That(catalog.Members.Count == 576 && catalog.SetCounts[30179] == 112 && catalog.SetCounts[30180] == 448 && catalog.SetCounts[30181] == 16,
            "原三套576成员完整typed目录，不扫描其它装备");
        foreach (var template in catalog.Members.Values)
            That(template.Kind == ItemKind.Avatar && template.EquipmentSlot < 8 &&
                resources.CharacterInvestmentMetadata.ReadSet(template.Id)?.SetId is 30179 or 30180 or 30181,
                "每个目录成员绑定原槽和真实套装 " + template.Id);
        var treasure = resources.Items.Get(490705287);
        var policy = new SpringTreasureDropPolicy(catalog, treasure);
        var expected = InventoryRewardGrantService.Create(treasure, 1, Now, shopExpiration: true, ignoreUsablePeriod: false);
        var expectedStrict = InventoryRewardGrantService.Create(treasure, 1, Now, shopExpiration: false, ignoreUsablePeriod: false);
        That(treasure.Expiration is { AbsoluteTime: 0, Days: 0, DailyDelete: 0, WeeklyDeleteDay: null } &&
            expected.ExpireTime == 0 && expectedStrict.ExpireTime == 0, "真实原287无期限声明，两种绝对期限设置均保持0永久");
        foreach (byte job in Enumerable.Range(0, 16).Select(value => (byte)value))
        {
            var templates = Enumerable.Range(0, 8).Select(slot => catalog.Members.Values
                .Where(row => row.EquipmentSlot == slot && row.AllowsUse(95, job)).OrderBy(row => row.Id).First()).ToArray();
            var character = Equipped(templates) with { Job = job, Level = 95 };
            That(policy.HasOutfit(character, Now), "全部职业完整八槽成员 " + job);
            var result = policy.Generate(character, Entry(), Boss(), _ => 0, Now, true, false);
            var strictResult = policy.Generate(character, Entry(), Boss(), _ => 0, Now, false, false);
            if (job == 0) Console.WriteLine("SPRING_TREASURE_EXPIRATION " + System.Text.Json.JsonSerializer.Serialize(new
            {
                treasure.Id, treasure.Expiration, Expected = expected, ExpectedStrict = expectedStrict,
                Actual = result.Select(row => row.Item).ToArray(), ActualStrict = strictResult.Select(row => row.Item).ToArray()
            }));
            That(result.Single().Item is { ItemId: 490705287, Count: 1 } core && core == expected && !core.IsExpired(Now.ToUnixTimeSeconds()),
                "当前日期真实287精确创建且永久或未到期 " + job);
            That(strictResult.Single().Item is { ItemId: 490705287, Count: 1 } strictCore && strictCore == expectedStrict &&
                !strictCore.IsExpired(Now.ToUnixTimeSeconds()), "无期限原287在false设置仍按原Create保永久 " + job);
        }
    }

    public static void RunPartyResources(ItemResources resources)
    {
        var avatars = resources.SpringTreasureAvatars;
        var policy = new SpringTreasureDropPolicy(avatars, resources.Items.Get(490705287));
        var templates = Enumerable.Range(0, 8).Select(slot => avatars.Members.Values.Where(item => item.EquipmentSlot == slot && item.AllowsUse(95, 0)).OrderBy(item => item.Id).First()).ToArray();
        int[] calls = [0, 0, 0];
        var results = new List<FrozenDungeonGroundDeath>();
        for (int member = 0; member < 3; member++)
        {
            var person = Equipped(templates) with { Id = (ushort)(member + 1), Level = (byte)(member == 2 ? 84 : 95) };
            var entry = Entry() with { CharacterId = person.Id };
            var bossMap = entry.BossMap with { Position = entry.StartMap.Position };
            entry = entry with { StartMap = bossMap, BossMap = bossMap, BaseBossMap = bossMap,
                Maze = new(0, 1, 1, "明确合成", [bossMap.Position], [bossMap.Position], [bossMap], []) };
            var source = new DungeonRoomMonster(1, [1, 0, 85, 100, 100, 0, 0, 0], "[fixed]", "[boss]", "monster/test.mob");
            var room = new DungeonRoomDefinition(bossMap, [source], [], []);
            var plan = DungeonMonsterPlan.FromRoom(room, person.Id, 100);
            var monster = plan.Monsters.Single();
            var authority = new SpringAuthority();
            That(SinglePlayerDungeonRun.TryCreate(entry, 1, 1, new([plan], []), authority, out var run, out string createReason),
                "个人冻结回归合成同Boss地面实例：" + createReason);
            That(run!.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted, "个人冻结实例完成加载");
            var prepared = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity, monster.ObjectKey)).Pending;
            var ground = new DungeonGroundState(run, [plan], [new(monster.ObjectKey, 200, entry.StartMap, 10, 10, 0)]);
            int index = member;
            IReadOnlyList<DungeonGroundContent> Factory() => policy.Generate(person, entry, monster, _ => { calls[index]++; return 0; }, Now, true, false);
            var frozen = ground.GetOrFreezeAssets(prepared!, Factory, SpringTreasureDropPolicy.Rule);
            person = member == 2 ? person with { Level = 95 } : person with { Inventory = InventorySnapshot.Empty };
            That(ReferenceEquals(frozen, ground.GetOrFreezeAssets(prepared!, Factory, SpringTreasureDropPolicy.Rule)), "同pending从无资格变合格或变装后仍返本人首次冻结，不重抽");
            results.Add(frozen);
        }
        That(calls.SequenceEqual(new[] { 1, 1, 0 }) && results[0].Grounds.Length == 1 && results[1].Grounds.Length == 1 && results[2].Grounds.IsEmpty,
            "两名合格本人各10%一骰，第三人未满85零骰，空批头也固定");
        That(results[0].Death.EventId != results[1].Death.EventId && results[0].Grounds[0].DeathIdentity.CharacterId != results[1].Grounds[0].DeathIdentity.CharacterId,
            "相同Boss模板不混成员死亡/领取归属");
    }

    internal static Character Equipped(IEnumerable<ItemTemplate> templates)
    {
        var items = templates.Select((template, index) =>
        {
            long uid = index + 1;
            return InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Equipment, template.EquipmentSlot),
                InventoryRewardGrantService.Create(template, utcNow: Now)) with { Uid = uid, Core = new(template.Id, ItemKind.Avatar, (int)uid) };
        }).ToImmutableArray();
        return new(1, 0, "Treasure", 0, new Location(38, 1, 1, 1), null) { Job = 0, Level = 85, Inventory = new(items) };
    }

    static ItemTemplate Template(int slot) => new(10000 + slot, ItemKind.Avatar, (byte)slot, 1, 0, 1, 0, 0, 0, "[account]", 1, 0, "活动装扮");
    internal static DungeonEntryPlan Entry()
    {
        var start = new DungeonMap("map", new(0, 0), 1, "map/start.map");
        var end = new DungeonMap("boss", new(1, 0), 2, "map/boss.map");
        var maze = new DungeonMaze(0, 2, 1, "BB EE", [start.Position], [end.Position], [start, end], []);
        return new(1, 0, 310, 0, byte.MaxValue, 0, maze, start, end)
            { SpringTreasurePotential = true, SpringTreasureRecommended = true };
    }
    internal static DungeonPlannedMonster Boss() => new(0, 2, 1, 85,
        new(1, [1, 85, 0, 100, 100, 0, 0, 0], "[fixed]", "[boss]", "monster/test.mob"));

    sealed class SpringAuthority : DungeonCombatAuthority
    {
        public ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity, ushort key) => ConfirmDeath(identity, Guid.NewGuid(), key, 1);
    }
}
