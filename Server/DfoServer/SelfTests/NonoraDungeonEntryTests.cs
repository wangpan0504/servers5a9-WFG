using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Testing;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// #542：复用#457的APC缺项修复，补真实Session准入；不将资源规划或合成入场当作客户端验收。
internal static class NonoraDungeonEntryTests
{
    public static void Run(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,
        QuestCatalog quests,SkillLearningCatalog skills,CharacterGrowthCatalog growth,DungeonCatalog dungeons,DungeonRoomCatalog rooms)
    {
        int checks = Count;
        var quest = quests.Get(3347);
        var entrance = MiddleQuestEntryTests.Entrance(towns,quest,61);
        var drops = SelfTestCatalogs.MonsterDrops(pvf);
        var policy = new DungeonResourceDropPolicy(SelfTestCatalogs.DropRules(pvf),items.DungeonDropItems,drops.Get,
            items.Items.Get,items.DungeonSpecialDrops,new EquipmentAmplificationDropPolicy(items.Amplifications.Rules));
        var branches = characters.Get(6).GrowthOptions.GrowTypes.Select(row => row.Value).Append((byte)0).Distinct().ToArray();
        That(branches.Length > 1,"暗夜使者资源包含实际转职分支，不猜死灵术士编号");
        foreach (byte grow in branches)
        {
            using var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,48,
                entrance.Completed,job:6,position:entrance.Position,growType:grow);
            var accepted = f.Session.ChangeQuest(3347,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces);
            That(accepted?.Commit is not null,"暗夜使者分支按真实前置接取3347："+grow+"/"+accepted?.Reason);
            var parent = f.Read().ActiveMainQuests.Single(row => row.QuestId == 3347);
            foreach (uint request in new uint[] { 3347,0 })
            {
                var opening = f.Session.EnterFormalDungeon(61,request,0,dungeons,rooms,drops,items.Items,pieces:items.EpicPieces,
                    prepareDrops:entry => policy.PrepareEntry(entry));
                That(opening is not null,"暗夜使者分支完整入图："+grow+"/"+request+"/"+f.Session.LastFormalDungeonRejection);
                That(opening!.Entry is { RouteQuestId:3347,StartMap.Id:76330,BossMap.Id:76331,Maze.ConnectionQuestId:3425 } &&
                    opening.Entry.QuestId == request && opening.Character.Job == 6 && opening.Character.GrowType == grow &&
                    opening.Entry.QuestAcceptances.SequenceEqual(f.Read().ActiveMainQuests) &&
                    f.Read().ActiveMainQuests.Single(row => row.QuestId == 3347) == parent &&
                    !f.Read().ActiveMainQuests.Any(row => row.QuestId is 3425 or 3426),
                    "只借用当前子任务布局，保留原父接取且不伪造隐藏子任务");
                That(request == 0 ? opening.Entry.QuestAcceptanceId == Guid.Empty : opening.Entry.QuestAcceptanceId == parent.AcceptanceId,
                    "显式请求绑定父任务接取；自动请求保留自身哨兵身份");
                var loaded = f.Session.CompleteFormalDungeonLoading();
                That(loaded is { Snapshot.CurrentMap.Id:76330 } && f.Session.Phase == SessionPhase.Dungeon,
                    "正式实例完成首房加载");
                That(f.Session.MoveFormalDungeon(1,0) is not null &&
                    f.Session.CompleteFormalDungeonLoading() is { Snapshot.CurrentMap.Id:76331 },"按真实门墙进入诺诺拉末房");
                That(f.Session.ReturnFromFormalDungeon() is not null && f.Session.Phase == SessionPhase.Town &&
                    f.Read().ActiveMainQuests.Single(row => row.QuestId == 3347) == parent,
                    "回城不提前完成或改写父任务进度及接取身份");
            }
        }
        Console.WriteLine($"PASS {Count-checks} 诺诺拉暗夜使者各资源分支正式入场断言（非实机）");
    }
}
