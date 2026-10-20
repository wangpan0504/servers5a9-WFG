using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class Issue153RemainingTests
{
    internal static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources items, QuestCatalog quests,
        SkillLearningCatalog skills, CharacterGrowthCatalog growth, DungeonCatalog dungeons, DungeonRoomCatalog rooms,
        DungeonClearExperienceCatalog clears, QuestRewardParameterCatalog parameters)
    {
        int checks = Count;
        Verify3446Settlement(towns,characters,items,quests,skills,growth,dungeons,rooms,clears,parameters);
        VerifySequentialEntry(towns,characters,items,quests,skills,growth,dungeons,rooms);
        Console.WriteLine($"PASS {Count-checks} #153 第3/8/9项：3446 物件结算、3466/3469 入场（非实机）");
    }

    static void Verify3446Settlement(TownCatalog towns, CharacterCatalog characters, ItemResources items, QuestCatalog quests,
        SkillLearningCatalog skills, CharacterGrowthCatalog growth, DungeonCatalog dungeons, DungeonRoomCatalog rooms,
        DungeonClearExperienceCatalog clears, QuestRewardParameterCatalog parameters)
    {
        var (position, done) = TownAt(towns,146,53,3445,3446);
        using var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,53,done.ToArray(),position:position);
        That(f.Session.ChangeQuest(3446,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
            "#153 真实3446按Lv53及前置3445接取");
        var opening = f.Session.EnterFormalDungeon(146,3446,0,dungeons,rooms);
        That(opening is not null && opening.Entry.StartMap.Id == 53626 && opening.Entry.BossMap.Id == 53627 &&
            opening.Entry.Maze.ClearConditions is [{ Type: "destroy object", TargetId: 80454, Count: 1 }],
            "#153 Q3446 进入 D146 物件终末：" + f.Session.LastFormalDungeonRejection);
        var loaded = f.Session.CompleteFormalDungeonLoading()!;
        That(loaded.Monsters.Monsters.All(row => !row.IsBlocking),
            "#153 53626 起点无阻挡，不要求击杀即可换房");
        That(f.Session.MoveFormalDungeon(0,0) is not null,"#153 沿真实连接进入 53627");
        loaded = f.Session.CompleteFormalDungeonLoading()!;
        That(loaded.Snapshot.CurrentMap.Id == 53627 &&
            loaded.Monsters.Room.PassiveObjects.Count(row => row.ObjectId == 80454) == 1 &&
            loaded.Monsters.Monsters.All(row => row.MonsterId != 107003988 || !row.IsBlocking) &&
            f.Session.EnableFormalDungeonSettlement() is null,
            "#153 终点赛丽亚不阻挡，加载不提前结算");
        That(f.Session.ObserveFormalPassiveDeath(80454,false,new string('A',64),rooms) is null &&
            f.Session.ObserveFormalPassiveDeath(80453,true,new string('A',64),rooms) is null,
            "#153 错误摘要或邻接模板不能借 80454 结算");
        var identity = f.Session.DungeonSnapshot!.Identity;
        var scene = f.Session.ObserveFormalPassiveDeath(80454,true,new string('A',64),rooms);
        That(scene?.Commit is { ProjectionCurrent: true } &&
            scene.Commit.Plan.SceneClear?.Result.ClearFact is { ConfirmedDeathEvents.Count: 0, PassiveSource.TemplateId: 80454 } &&
            f.Session.Phase == SessionPhase.Settlement && f.Session.DungeonSnapshot.Identity.Epoch == identity.Epoch + 1 &&
            f.Read().ActiveMainQuests.Single(row => row.QuestId == 3446).ConditionState == 0,
            "#153 80454 一次零击杀物件结算并归零 hunt enemy，不走 3447 的 69292/D7156：" +
            (scene is null ? f.Session.DiagnosePassiveEvent(80454,rooms) : "ok"));
        var enabled = f.Session.EnableFormalDungeonSettlement();
        That(enabled is { BossKey: null },"#153 3446 空31许可，不伪造 Boss key");
        f.Session.ConfirmFormalDungeonSettlementEnabled(enabled!.Value.Receipt);
        byte[] report = DungeonSettlementProtocolTests.Report(f.Read().Id,99);
        That(DungeonSettlementRequests.TryParsePlayResult(report,out var parsed),"#153 3446 沿正式46报告");
        var settled = f.Session.SettleFormalDungeon(parsed!.ActorId,parsed.RankPoint,parsed.MaximumRankPoint,report,dungeons,clears,growth);
        That(settled.Outcome == DungeonRunOutcome.Accepted && settled.Receipt is not null,
            "#153 3446 正式46保存通关");
        var finished = f.Session.FinishQuest(3446,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
        That(finished.Receipt?.Commit is { ConditionType: 6 } && f.Read().CompletedQuests.Contains(3446),
            "#153 3446 按 hunt enemy 交付，不把物件清图冒充 3447");
    }

    static void VerifySequentialEntry(TownCatalog towns, CharacterCatalog characters, ItemResources items, QuestCatalog quests,
        SkillLearningCatalog skills, CharacterGrowthCatalog growth, DungeonCatalog dungeons, DungeonRoomCatalog rooms)
    {
        var options7158 = towns.DungeonMaps.Values.SelectMany(map => map.Options.Where(row => row.DungeonId == 7158)).ToArray();
        var options160 = towns.DungeonMaps.Values.SelectMany(map => map.Options.Where(row => row.DungeonId == 160)).ToArray();
        That(options7158.Length > 0, "#153 D7158 出现在选图：" + string.Join(",", options7158.Select(row => row.QuestId + (row.InProgress ? "p" : "c"))));
        That(options160.Length > 0, "#153 D160 出现在选图：" + string.Join(",", options160.Select(row => row.QuestId + (row.InProgress ? "p" : "c"))));

        var (position7158, done7158) = TownAt(towns,7158,57,3465,3466,3474,3475);
        using (var blocked = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,57,done7158.ToArray(),position:position7158))
        {
            That(blocked.Session.ChangeQuest(3466,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
                "#153 真实3466接取");
            That(blocked.Session.EnterFormalDungeon(7158,3466,0,dungeons,rooms) is null &&
                blocked.Session.LastFormalDungeonRejection == "entry-plan:quest-maze-unavailable",
                "#153 会面 3474 未完成时父请求不进 D7158：" + blocked.Session.LastFormalDungeonRejection);
        }
        done7158.Add(3474);
        using (var ready = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,57,done7158.ToArray(),position:position7158))
        {
            That(ready.Session.ChangeQuest(3466,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
                "#153 3474 已完成时接取 3466");
            var opening = ready.Session.EnterFormalDungeon(7158,3466,0,dungeons,rooms);
            That(opening is not null && opening.Entry.StartMap.Id == 70312 && opening.Entry.BossMap.Id == 70320 &&
                opening.Entry.Maze.ConnectionQuestId == 3475,
                "#153 3474 完成后父 3466 进入 D7158：" + ready.Session.LastFormalDungeonRejection);
        }

        var (position160, done160) = TownAt(towns,160,57,3468,3469,3476,3477);
        using var chess = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,57,done160.ToArray(),position:position160);
        That(chess.Session.ChangeQuest(3469,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
            "#153 真实3469接取");
        var chessOpening = chess.Session.EnterFormalDungeon(160,3469,0,dungeons,rooms);
        That(chessOpening is not null && chessOpening.Entry.StartMap.Id == 70339 && chessOpening.Entry.BossMap.Id == 70347 &&
            chessOpening.Entry.Maze.ConnectionQuestId == 3476,
            "#153 接取 3469 后立即进入 D160：" + chess.Session.LastFormalDungeonRejection);
    }

    static (Location Position, HashSet<int> Done) TownAt(TownCatalog towns, int dungeonId, byte level, int prerequisite, params int[] exclude)
    {
        var area = towns.Areas.Values.First(row => row.Level <= level && row.DungeonWorldMap is { } world &&
            towns.DungeonMaps[world].Options.Any(option => option.DungeonId == dungeonId));
        var done = new HashSet<int>(area.Quests) { prerequisite };
        if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
        foreach (int id in exclude) done.Remove(id);
        foreach (var option in towns.DungeonMaps[area.DungeonWorldMap!.Value].Options)
            if (option.DungeonId == dungeonId && !option.InProgress && option.QuestId > 0) done.Add(option.QuestId);
        var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        return (new Location(area.Town,area.Area,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2))), done);
    }
}
