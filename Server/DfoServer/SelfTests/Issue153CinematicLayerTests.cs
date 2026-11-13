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

internal static class Issue153CinematicLayerTests
{
    internal static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources items, QuestCatalog quests,
        SkillLearningCatalog skills, CharacterGrowthCatalog growth, DungeonCatalog dungeons, DungeonRoomCatalog rooms,
        DungeonKillExperienceCatalog kills, DungeonClearExperienceCatalog clears, QuestRewardParameterCatalog parameters)
    {
        int checks = Count;
        Verify3452CinematicLayer(towns,characters,items,quests,skills,growth,dungeons,rooms,kills);
        Console.WriteLine($"PASS {Count-checks} #153 第6项：3452 换层（非实机）");
    }

    static void Verify3452CinematicLayer(TownCatalog towns, CharacterCatalog characters, ItemResources items, QuestCatalog quests,
        SkillLearningCatalog skills, CharacterGrowthCatalog growth, DungeonCatalog dungeons, DungeonRoomCatalog rooms,
        DungeonKillExperienceCatalog kills)
    {
        var (position, done) = TownAt(towns,156,54,3451,3452);
        using var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,54,done.ToArray(),position:position);
        That(f.Session.ChangeQuest(3452,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
            "#153 真实3452按Lv54及前置3451接取");
        var opening = f.Session.EnterFormalDungeon(156,3452,0,dungeons,rooms);
        That(opening is not null && opening.Entry.StartMap.Id == 70193 && opening.Entry.BossMap.Id == 70201 &&
            opening.Entry.Maze.LayeredMaps.Select(map => map.Id).SequenceEqual([70202,70351]),
            "#153 Q3452 进入 D156 两层起点：" + f.Session.LastFormalDungeonRejection);
        var flash = rooms.Get(opening!.Entry.Maze.LayeredMaps[0], requirePassiveDefinitions: false);
        That(flash.CinematicLayerReturn is { CinematicId: 2005, IsOnStartMap: true },
            "#153 70202 已识别 ON START MAP / CHANGE MAP，不把第二次 kind1 当成回基础格");
        f.Session.CompleteFormalDungeonLoading();
        That(f.Session.MoveFormalDungeon(0,4,1) is not null,"#153 第一次 kind1 进入 70202");
        var layered = f.Session.CompleteFormalDungeonLoading()!;
        That(layered.Snapshot.CurrentMap.Id == 70202 && layered.Monsters.Monsters.Any(row => row.IsBlocking),
            "#153 70202 仍有帝国士兵阻挡，不能靠清怪才换层");
        That(f.Session.MoveFormalDungeon(0,4,1) is not null,"#153 第二次 kind1 进入 70351，不要求清掉 70202");
        var next = f.Session.CompleteFormalDungeonLoading()!;
        That(next.Snapshot.CurrentMap.Id == 70351,"#153 第二层是 70351 对话/蘑菇层");
        That(f.Session.MoveFormalDungeon(0,4,1) is null,
            "#153 70351 未清图时第三次 kind1 不能返回 70193，不放宽其它地图");
        Clear(next);
        That(f.Session.MoveFormalDungeon(1,4,0) is not null,"真实3452清末层后直接进入第二房");
        var second = f.Session.CompleteFormalDungeonLoading()!;
        That(second.Snapshot.CurrentMap.Id == 70194,"第二房资源与现场复现相同");
        Clear(second);
        uint fatigue = f.Session.DungeonSnapshot!.ConsumedFatigue;
        for (int visit=0;visit<2;visit++)
        {
            var restored = f.Session.MoveFormalDungeon(0,4,0);
            That(restored is not null && restored.Snapshot.CurrentMap.Id == 70193 &&
                restored.Snapshot is { MapOperation:DungeonMapOperation.RestoreBase,MapPayload:DungeonMapPayload.Cached } &&
                restored.Snapshot.LivingMonsters.Count == 0,"真实3452折返显式恢复基础首房，不复用70351的怪物缓存");
            byte[] packet = DfoServer.Network.Builders.DungeonPackets.StartMap(restored!.Entry,restored.Monsters,
                restored.MapSeed,restored.Snapshot.MapOperation,restored.Snapshot.MapPayload,restored.PassiveItems);
            That(packet.Length == 16 && packet[2] == 2 && packet[13] == 0,
                "真实Session投影NOTI29使用基础缓存恢复合同且不重发已死怪物");
            That(f.Session.CompleteFormalDungeonLoading() is not null &&
                f.Session.DungeonSnapshot!.ConsumedFatigue == fatigue,"折返加载不重复扣疲劳");
            That(f.Session.ConfirmFormalDungeonLayerBase(0,4) && !f.Session.ConfirmFormalDungeonLayerBase(0,4) &&
                f.Session.MoveFormalDungeon(0,4,1) is null && f.Session.DungeonSnapshot!.CurrentMap.Id == 70193,
                "同坐标kind1仅确认一次，重复换层请求不重放剧情");
            That(f.Session.MoveFormalDungeon(1,4,0) is not null,"恢复首房后原真实门禁允许继续进入已清第二房");
            var revisited = f.Session.CompleteFormalDungeonLoading()!;
            That(revisited.Snapshot is { MapOperation:DungeonMapOperation.Current,MapPayload:DungeonMapPayload.Cached } &&
                revisited.Snapshot.LivingMonsters.Count == 0,"普通第二房仍保持自己的清房缓存");
        }
        void Clear(FormalDungeonReceipt receipt)
        {
            foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking))
                That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt,monster),kills,growth).Outcome == DungeonRunOutcome.Accepted,
                    "真实3452逐只提交清房死亡，不直接修改死亡集合或强开门");
        }
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
