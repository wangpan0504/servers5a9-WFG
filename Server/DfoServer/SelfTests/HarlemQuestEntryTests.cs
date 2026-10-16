using System.Text.Json;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class HarlemQuestEntryTests
{
    // 复用本轮PVF和Session文件库夹具；只核当前入场及相邻移动，不冒充乘骑warp或客户端验收。
    internal static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills)
    {
        int checks = Count;
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, skills);
        var dungeons = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
        var quest = quests.Get(5934);
        That(QuestClearEligibility.IsSingleMap(quest) && quest.IntData[0] == 292104869 &&
            quest.Definition!.Dungeons.Any(row => row.DungeonId == 291100318),
            "前往哈林使用真实单地图清图声明和荒芜之地归属");
        var entrance = MiddleQuestEntryTests.Entrance(towns,quest,291100318);
        using var fixture = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,
            checked((byte)quest.MinimumLevel),entrance.Completed,position:entrance.Position);
        That(fixture.Session.ChangeQuest(5934,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
            "实际前置与等级按原生命周期接取哈林任务");
        var accepted = fixture.Read().ActiveMainQuests.Single(row => row.QuestId == 5934);
        DungeonWarpMap[] expectedWarps = [new(new(0,0),new(1,0)),new(new(0,0),new(2,0)),
            new(new(1,0),new(0,0)),new(new(1,0),new(2,0))];
        Guid previousInstance = Guid.Empty;
        DungeonEntryPlan? previousPlan = null;
        DungeonMonsterPlan[]? fullPlans = null;
        foreach (uint request in new uint[] { 5934,0 })
        {
            var before = fixture.Read();
            That(DungeonEntryPlan.TryCreate(dungeons,before,291100318,request,0,out var plan,out var reason,
                quest:request == 0 ? null : quest,quests:quests),
                "真实荒芜之地声明应能规划入场，不把未实现warp当完整修复："+reason);
            var entry = plan ?? throw new InvalidOperationException("哈林入场计划未建立");
            CheckBinding(entry,before,accepted,request);
            That(entry.Maze.ResourceError is null && entry.Maze.WarpMaps.SequenceEqual(expectedWarps) &&
                entry.Maze.BossEntrance is null && entry.Maze.ConditionalMaps.Count == 0 &&
                entry.Maze.LayeredMaps.Count == 0 && entry.Maze.StoryStages.Count == 0 &&
                entry.Maze.MapSpecification!.Select(map => map.Id).SequenceEqual([292104867,292104868,292104869]) &&
                entry.Maze.StaticTopology.Supported,
                "三张真实基础图和四条有向warp原序保留，无条件替换或叠层");
            if (previousPlan is null) fullPlans = CompletePlans(entry,rooms);
            else That(entry.Maze.SameLayout(previousPlan.Maze),"普通0与显式5934沿同一实际任务布局");
            previousPlan = entry;
            var opening = fixture.Session.EnterFormalDungeon(291100318,request,0,dungeons,rooms);
            var opened = opening ?? throw new InvalidOperationException("真实哈林Session入场被拒绝");
            CheckBinding(opened.Entry,before,accepted,request);
            That(opened.Generation == fixture.Owner.Generation && opened.Entry.CharacterRevision == before.Revision+1 &&
                opened.Snapshot.Identity.QuestId == request && opened.Snapshot.Identity.CharacterId == before.Id &&
                opened.Snapshot.Identity.InstanceId != Guid.Empty && opened.Snapshot.Identity.InstanceId != previousInstance &&
                opened.Snapshot.Identity.TaskAttempt == before.DungeonState!.LastAttempt+1 &&
                fixture.Session.CanPublishFormalDungeon(opened),
                "两种请求使用各自实际instance及attempt，原接取和owner不变");
            previousInstance = opened.Snapshot.Identity.InstanceId;
            var loaded = fixture.Session.CompleteFormalDungeonLoading() ?? throw new InvalidOperationException("哈林起点加载未完成");
            That(fixture.Session.Phase == SessionPhase.Dungeon &&
                loaded.Monsters.Monsters.Select(Shape).SequenceEqual(fullPlans![0].Monsters.Select(Shape)),
                "真实起点完成加载，四个友方演员均发布而非截断空房");
            var unchanged = fixture.Session.DungeonSnapshot;
            bool unsupportedMovesRejected = fixture.Session.MoveFormalDungeon(2,0,0) is null &&
                fixture.Session.MoveFormalDungeon(2,0,1) is null && fixture.Session.MoveFormalDungeon(1,0,1) is null;
            var afterRejected = fixture.Session.DungeonSnapshot;
            That(unsupportedMovesRejected && unchanged is not null && afterRejected is not null &&
                afterRejected.Identity == unchanged.Identity && afterRejected.CurrentMap == unchanged.CurrentMap &&
                afterRejected.Phase == unchanged.Phase && afterRejected.LivingMonsters.SequenceEqual(unchanged.LivingMonsters) &&
                fixture.Session.CanPublishFormalDungeon(loaded),
                "未实现的非相邻kind0及跨格kind1仍拒绝且不改变原场景，不伪造乘骑warp成功");
            var seenKeys = loaded.Monsters.Monsters.Select(row => row.ObjectKey).ToHashSet();
            for (byte x = 1; x <= 2; x++)
            {
                var moved = fixture.Session.MoveFormalDungeon(x,0,0);
                That(moved is not null && !fixture.Session.CanPublishFormalDungeon(loaded),
                    "当前支持的相邻通路沿原Run换房，旧回执失效");
                loaded = fixture.Session.CompleteFormalDungeonLoading() ?? throw new InvalidOperationException("哈林相邻房加载未完成");
                That(loaded.Snapshot.CurrentMap.Id == 292104867+x &&
                    loaded.Monsters.Monsters.Select(Shape).SequenceEqual(fullPlans![x].Monsters.Select(Shape)) &&
                    loaded.Monsters.Monsters.All(row => row.ObjectKey != before.Id && seenKeys.Add(row.ObjectKey)) &&
                    fixture.Session.Phase == SessionPhase.Dungeon,
                    "相邻路线保留完整中间房和终末敌人/APC；存活敌人不产生结算");
            }
            That(fixture.Session.EnableFormalDungeonSettlement() is null &&
                fixture.Repository.LoadQuestClear(fixture.Owner,fixture.Session.Selected!,5934) is null,
                "未报告死亡的入场及相邻移动没有清图事实或奖励许可");
            long retreatStarted = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            That(fixture.Session.ReturnFromFormalDungeon() is not null && !fixture.Session.CanPublishFormalDungeon(opened) &&
                !fixture.Session.CanPublishFormalDungeon(loaded), "未通关退出结束原Run与回执");
            long retreatFinished = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var after = fixture.Read();
            That(after.Inventory.StaminaUntil >= retreatStarted+600 && after.Inventory.StaminaUntil <= retreatFinished+600,
                "非训练房未清图回城沿正式规则写入十分钟虚弱期限");
            That(after.ActiveMainQuests.SequenceEqual(before.ActiveMainQuests) && after.CompletedQuests.SequenceEqual(before.CompletedQuests) &&
                JsonSerializer.Serialize(after.Inventory) == JsonSerializer.Serialize(
                    before.Inventory with { StaminaUntil = after.Inventory.StaminaUntil }) &&
                JsonSerializer.Serialize(after.Learning) == JsonSerializer.Serialize(before.Learning) &&
                fixture.Repository.LoadQuestClear(fixture.Owner,after,5934) is null,
                "真实Session入场、加载、相邻移动及退出不造死亡、清图或任务奖励");
        }
        Console.WriteLine($"PASS {Count-checks} 哈林真实三MAP完整演员及显式/普通0入场回归（跨格乘骑warp未实现，非客户端验收）");
    }

    static void CheckBinding(DungeonEntryPlan entry, Character character, ActiveMainQuest accepted,uint request)
    {
        That(entry.CharacterId == character.Id && entry.DungeonId == 291100318 && entry.QuestId == request && entry.RouteQuestId == 5934 &&
            entry.HasExplicitClearMap && !entry.IsOrdinary && entry.StartMap.Id == 292104867 && entry.BossMap.Id == 292104869 &&
            entry.QuestAcceptances.SequenceEqual(character.ActiveMainQuests) && QuestHuntProgress.Bound(accepted,entry),
            "布局、实际目标及原槽位/任务/AcceptanceId绑定完整");
        That(request == 0 ? entry.ActiveQuestSlot == byte.MaxValue && entry.QuestAcceptanceId == Guid.Empty :
            entry.ActiveQuestSlot == accepted.Slot && entry.QuestAcceptanceId == accepted.AcceptanceId,
            "请求0哨兵与实际活动接取分别保存，不伪造新接取");
    }

    static DungeonMonsterPlan[] CompletePlans(DungeonEntryPlan entry,DungeonRoomCatalog rooms)
    {
        ushort next = 100;
        var keys = new HashSet<ushort>();
        var plans = entry.Maze.MapSpecification!.Select(map =>
        {
            var room = rooms.Get(map,requirePassiveDefinitions:false);
            var plan = DungeonMonsterPlan.FromRoom(room,entry.CharacterId,next,entry.BasisLevel,playerTeam:entry.PlayerTeam);
            That(plan.Monsters.Count(row => row.Source is { SpecialChild:null }) == room.Monsters.Count &&
                plan.Monsters.Count(row => row.AiCharacter is not null) == room.AiCharacters.Count &&
                plan.SpecialObjects.Count == room.SpecialObjects.Count &&
                plan.Monsters.Select(row => row.ObjectKey).Concat(plan.SpecialObjects.Select(row => row.ObjectKey))
                    .All(key => key != entry.CharacterId && keys.Add(key)),
                "完整资源计划保留普通演员/APC/特殊父子并分配全路线唯一key");
            if (plan.Monsters.Count != 0 || plan.SpecialObjects.Count != 0) next = checked((ushort)plan.NextObjectKey);
            return plan;
        }).ToArray();
        That(plans[0].Monsters.Select(row => row.MonsterId).SequenceEqual([107010392,107012115,107012117,107012119]) &&
            plans[0].Monsters.All(row => row.IsFriendly && !row.IsBlocking) && plans[1].Monsters.Count == 0 &&
            plans.Select(plan => plan.Room.PassiveObjects.Count).SequenceEqual([5,3,2]) &&
            plans[2].Monsters is [{ MonsterId:107002770, IsBlocking:true, CanReportDeath:true },
                { MonsterId:15442, AiCharacter.Faction:"[character]", CanReportDeath:false }],
            "四个真实友方、中间空房、实际普通敌人和15442友方APC均完整保留");
        return plans;
    }

    static (int Id,int Level,byte Type,int Team,bool Friendly,bool Blocking,int? Ai) Shape(DungeonPlannedMonster actor) =>
        (actor.MonsterId,actor.CandidateLevel,actor.SpawnType,actor.Team,actor.IsFriendly,actor.IsBlocking,actor.AiCharacter?.Id);
}
