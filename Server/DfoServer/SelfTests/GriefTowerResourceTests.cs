using DfoServer.GameWorld.Dungeons;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GriefTowerResourceTests
{
    public static void RunCore()
    {
        int checks = Count;
        object[] source = ["[enterable count]", 2, "[account enterable max count]", 1,
            "[min Level]", 90, "[top layer]", 2,
            "[use allow item list in dungeon]",10160849,10,10163185,-1,1234,0,"[/use allow item list in dungeon]",
            "[adventure group add enterable count]", 2, 6, 1, 7, 11, 2, 12, 99, 3, "[/adventure group add enterable count]",
            "[each layer matching dungeon]", 1, 5115, -1, 2, 6000, -1, "[/each layer matching dungeon]",
            "[each layer dungeon anti evil value]", 1, 4232, 2, 4300, "[/each layer dungeon anti evil value]",
            "[each layer dungeon clear reward]", 1, 1, "normal", 2, 2, "special", "[/each layer dungeon clear reward]"];
        var catalog = Parse(source);
        That(catalog.MinimumLevel == 90 && catalog.TopLayer == 2 && catalog.DeclaredEnterableCount == 2 &&
            catalog.DeclaredAccountMaximum == 1 && catalog.DeclaredAdventureBands is [{ MinimumLevel: 2, MaximumLevel: 6, AdditionalCount: 1 },
                { MinimumLevel: 7, MaximumLevel: 11, AdditionalCount: 2 },
                { MinimumLevel: 12, MaximumLevel: 99, AdditionalCount: 3 }], "悲叹次数配置保留原值与区间，不推导每日准入");
        That(catalog.FloorsByDungeon[6000] is { Layer: 2, DungeonId: 6000, AlternateDungeonId: -1, AntiEvil: 4300, RewardGroup: "special" } &&
            !catalog.FloorsByDungeon.ContainsKey(5116), "副本身份读取专表，不按5114加楼层猜测");
        foreach (var (tag, replacement) in new (string, object[])[]
        {
            ("[top layer]", [101]),
            ("[enterable count]", ["2"]),
            ("[use allow item list in dungeon]", [10160849,10,10160849,1]),
            ("[use allow item list in dungeon]", [10160849]),
            ("[account enterable max count]", [-1]),
            ("[each layer matching dungeon]", [1,5115,-1,2,5115,-1]),
            ("[each layer matching dungeon]", [1,5115,-1]),
            ("[each layer matching dungeon]", [1,5115]),
            ("[each layer dungeon anti evil value]", [1,4232,1,4300]),
            ("[each layer dungeon clear reward]", [1,2,"normal",2,2,"special"]),
            ("[each layer dungeon clear reward]", [1,1,"normal"]),
            ("[adventure group add enterable count]", [2,7,1,7,11,2])
        })
        {
            int start = Array.IndexOf(source, tag) + 1, end = start;
            while (end < source.Length && !(source[end] is string text && text.StartsWith('['))) end++;
            Reject<InvalidDataException>(() => Parse([..source[..start], ..replacement, ..source[end..]]), "悲叹拒绝非法或不完整资源 " + tag);
        }
        Reject<InvalidDataException>(() => Parse([..source,"[enterable count]",2]), "悲叹不以首字段掩盖重复配置");
        var itemUse = new GriefTowerItemUse(catalog);
        That(!itemUse.CanUse(999) && !itemUse.CanUse(1234),"未列入悲叹白名单和零上限物品均拒绝");
        for (int i = 0; i < 10; i++)
        {
            That(itemUse.CanUse(10160849),"悲叹专用药品在十次内允许使用");
            itemUse.CommitUse(10160849);
        }
        Reject<InvalidOperationException>(() => itemUse.CommitUse(10160849),"悲叹第十一次药品使用拒绝");
        for (int i = 0; i < 12; i++) itemUse.CommitUse(10163185);
        That(itemUse.CanUse(10163185) && new GriefTowerItemUse(catalog).CanUse(10160849),
            "负数上限不限次，新实例独立恢复额度，不继承上一次挑战消耗");
        var now = new DateTimeOffset(2026,10,23,6,0,0,TimeSpan.FromHours(8));
        var fresh = new GriefTowerEntryState(0,0,0,false,1);
        GriefTowerEntryDecision Decide(GriefTowerEntryState state, int adventure = 1, int level = 90,
            int party = 1, int dungeon = 5115, DateTimeOffset? at = null) =>
            GriefTowerEntryPolicy.Evaluate(catalog,state,dungeon,level,adventure,party,at ?? now);
        foreach (var (level, limit) in new[] { (1,1),(2,2),(6,2),(7,3),(11,3),(12,4),(99,4) })
        {
            var allowed = Decide(fresh,level);
            That(allowed is { Accepted: true, UsesInvitation: false, Day: 20261023 } && allowed.AccountLimit == limit,
                "账号基本日额度按冒险团区间计算: " + level);
            That(!Decide(fresh with { AccountDay = 20261023, AccountUsed = limit },level).Accepted,
                "同账号换角色不恢复用尽额度: " + level);
        }
        var failed = fresh with { AccountDay = 20261023, AccountUsed = 1, CharacterDay = 20261023 };
        That(Decide(failed) is { Accepted: true, UsesInvitation: true } && GriefTowerEntryDecision.InvitationItem == 10161065,
            "同角色失败后使用悲叹邀请函重试，不再要求账号基本额度，不使用绝望邀请函");
        That(Decide(failed with { CharacterCleared = true }).Reason == "grief-tower-already-cleared-today",
            "当天已通关不能用票重入");
        That(Decide(failed with { CharacterDay = 0 }).Reason == "grief-tower-account-quota-exhausted",
            "其它角色不能借用失败角色的付费重试资格");
        var yesterday = failed with { AccountDay = 20261022, CharacterDay = 20261022, CharacterCleared = true };
        That(!Decide(yesterday,at:now.AddTicks(-1)).Accepted && Decide(yesterday) is { Accepted: true, UsesInvitation: false },
            "06:00精确重置日额度和当日通关门禁，午夜不提前刷新");
        That(Decide(fresh,level:89).Reason == "grief-tower-level-too-low" &&
            Decide(fresh,party:2).Reason == "grief-tower-single-player-only" &&
            Decide(fresh,dungeon:6000).Reason == "grief-tower-floor-mismatch" &&
            Decide(fresh with { Layer = 2 },dungeon:6000).Accepted, "等级、单人和真实楼层身份分别核对");
        That(Decide(failed with { AccountDay = 20261024 }).Reason == "grief-tower-clock-before-state" &&
            !Decide(fresh with { AccountUsed = -1 }).Accepted, "时间回退和损坏计数不能恢复入场资格");
        object[] rewardRows = ["[cardslot specific reward table]", "[table]", "normal",
            "card 3",10160931,2850,1,"card 10",10160931,2800,1,"card 10",10303266,500,1,
            "[/table]", "[/cardslot specific reward table]"];
        var (rewardTokens,rewardResolve) = Decode(rewardRows);
        var rewardGroups = GriefTowerRewardTable.Parse(rewardTokens,rewardResolve);
        That(rewardGroups["normal"] is [{ Number: 3, Items.Length: 1 }, { Number: 10, Items.Length: 2 }] &&
            rewardGroups["normal"][1].Items.SequenceEqual(new DungeonSpecificCardItem[] { new(10160931,2800,1),new(10303266,500,1) }),
            "同槽多候选完整保留，缺少的card1/2不补造，数值不归一成必出");
        foreach (object[] malformed in new object[][]
        {
            [], [..rewardRows,..rewardRows],
            ["[cardslot specific reward table]","[table]","normal","card 3",1,2,"[/table]","[/cardslot specific reward table]"],
            ["[cardslot specific reward table]","[table]","normal","card 11",1,2,1,"[/table]","[/cardslot specific reward table]"],
            ["[cardslot specific reward table]","[table]","normal","card 3",1,-2,1,"[/table]","[/cardslot specific reward table]"]
        })
        {
            var (badTokens,badResolve) = Decode(malformed);
            Reject<InvalidDataException>(() => GriefTowerRewardTable.Parse(badTokens,badResolve),"悲叹奖励缺失/重复/残缺/非法行拒绝");
        }
        Console.WriteLine($"PASS {Count - checks} 悲叹专表身份、字段边界与覆盖");

        static GriefTowerCatalog Parse(object[] values)
        {
            var (tokens,resolve) = Decode(values);
            return new(tokens,resolve);
        }
    }
    static (PvfToken[] Tokens, Func<int,string> Resolve) Decode(object[] values)
    {
        var strings = new List<string>();
        var tokens = values.Select(value =>
        {
            if (value is int number) return new PvfToken(0, number);
            string text = (string)value;
            strings.Add(text);
            return new PvfToken(text.StartsWith('[') ? (byte)3 : (byte)6, strings.Count - 1);
        }).ToArray();
        return (tokens,id => strings[id]);
    }
    public static void Run(PvfArchive pvf)
    {
        int checks = Count;
        var catalog = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        var griefDefinition = catalog.Get(5115);
        That(catalog.GriefTower.ItemUseLimits.Count == 8 && catalog.GriefTower.ItemUseLimits[10160849] == 10 &&
            catalog.GriefTower.ItemUseLimits[10160850] == 10 && catalog.GriefTower.ItemUseLimits[10163185] == -1,
            "真实PVF悲叹八种可用道具及有限/无限次数原值");
        var normalRewards = catalog.GriefTower.RewardTables["tower_grief_reward_normal"];
        var specialRewards = catalog.GriefTower.RewardTables["tower_grief_reward_special"];
        That(normalRewards.Select(row => row.Number).SequenceEqual(Enumerable.Range(3,8)) &&
            specialRewards.Select(row => row.Number).SequenceEqual(Enumerable.Range(3,8)) &&
            normalRewards.Single(row => row.Number == 10).Items.SequenceEqual(new DungeonSpecificCardItem[] { new(10160931,2800,1),new(10303266,500,1) }) &&
            specialRewards.Single(row => row.Number == 10).Items is [{ ItemId: 10160930, Weight: 10000, Count: 1 }],
            "实际悲叹普通/特殊卡槽表按原数值读取，未把10000或2850解释为最终概率");
        var maximum = GriefTowerRewardPolicy.Prepare(catalog.GriefTower,griefDefinition.GriefFloor!,_ => 0);
        var specialFloor = catalog.GriefTower.FloorsByDungeon.Values.Single(row => row.Layer == 50);
        var special = GriefTowerRewardPolicy.Prepare(catalog.GriefTower,specialFloor,_ => 9999);
        That(maximum.Items.Length == 8 && maximum.Items.All(item => item.ItemId == 10160931 && item.Count == 1) &&
            special.Items is [{ Slot: 10, ItemId: 10160930, Count: 1 }],
            "当前八槽推定算法保留PVF原值，不套早期七材料上限，特殊层必得一份岁月");
        Reject<InvalidDataException>(() => GriefTowerRewardPolicy.Prepare(catalog.GriefTower,
            specialFloor with { RewardGroup = "tower_grief_reward_normal" },_ => 0),"楼层奖励组不能由调用者替换");
        That(catalog.GriefTower.MinimumLevel == 90 && catalog.GriefTower.TopLayer == 100 &&
            catalog.GriefTower.FloorsByDungeon.Count == 100 &&
            griefDefinition.GriefFloor is { Layer: 1, DungeonId: 5115, AntiEvil: 4232, RewardGroup: "tower_grief_reward_normal" },
            "悲叹首层使用实际专表，包含等级、抗魔和奖励身份");
        foreach (var floor in catalog.GriefTower.FloorsByDungeon.Values)
        {
            var tower = catalog.Get(floor.DungeonId);
            That(tower.GriefFloor == floor && tower.RequiresGriefEntryContract &&
                floor.DungeonId == 5114 + floor.Layer && floor.AlternateDungeonId == -1 &&
                floor.RewardGroup == (floor.Layer is 50 or 100 ? "tower_grief_reward_special" : "tower_grief_reward_normal"),
                "实际悲叹楼层必须携带专表身份及专用准入约束: " + floor.Layer);
            var towerMaze = catalog.ResolveOrdinaryMaze(tower,tower.Mazes.Single(),_ => throw new InvalidOperationException());
            That(towerMaze.IsGriefTower && towerMaze.GriefRoom is not null &&
                towerMaze.StaticTopology is { Supported: true, Rooms.Count: 1, Links.Count: 0 } &&
                towerMaze.MapSpecification is [var onlyRoom] && onlyRoom == towerMaze.GriefRoom &&
                !(towerMaze with { MapSpecification = [towerMaze.GriefRoom with { Id = int.MaxValue }] }).StaticTopology.Supported,
                "悲叹每层按唯一MAP归属生成单房，不允许替换已确认MAP: " + floor.Layer);
            var towerCharacter = new Character(98,0,"GriefFloor",0,new(38,2,1085,249),null) { Level = 90 };
            That(!DungeonEntryPlan.TryCreate(catalog,towerCharacter,(uint)floor.DungeonId,0,0,out _,out string refusal) &&
                refusal == "grief-tower-admission-required", "后续楼层不能误走普通入场: " + floor.Layer);
            var before = new GriefTowerProgress(new(0,0,0,false,floor.Layer),1,Guid.Empty,0);
            var decision = GriefTowerEntryPolicy.Evaluate(catalog.GriefTower,before.State,floor.DungeonId,90,1,1,
                new(2026,10,24,7,0,0,TimeSpan.FromHours(8)));
            That(DungeonEntryPlan.TryCreate(catalog,towerCharacter,(uint)floor.DungeonId,0,0,out var approved,out _,
                grief:new(before,decision,floor.DungeonId)) && approved is { EntryItem:null,StartFatigue:0,RequiresFatigueOwner:false } &&
                approved.GriefLayer==floor.Layer && approved.Maze.GriefRoom==approved.StartMap,
                "专用准入绑定实际层且不走普通费用或疲劳owner: " + floor.Layer);
        }
        Console.WriteLine($"PASS {Count - checks} 悲叹100层资源身份、单房拓扑与准入隔离");
    }
}
