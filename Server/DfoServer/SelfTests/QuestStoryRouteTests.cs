using System.Buffers.Binary;
using System.Security.Cryptography;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers;
using DfoServer.Testing;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestStoryRouteTests
{
    public static void Run(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,
        QuestCatalog? sharedQuests = null, SkillLearningCatalog? sharedSkills = null)
    {
        int checks = Count;
        var quests = sharedQuests ?? QuestCatalog.Load(pvf); var skills = sharedSkills ?? SkillLearningCatalog.Load(pvf);
        var growth = CharacterGrowthCatalog.Load(pvf,skills); var parameters = QuestRewardParameterCatalog.Load(pvf);
        var dungeons = DungeonCatalog.Load(pvf); var rooms = DungeonRoomCatalog.Load(pvf); var kills = DungeonKillExperienceCatalog.Load(pvf);
        (ushort Id,byte Level,int[] Completed)[] cases =
        [
            (3147,5,[3145,4873,3146,3157,3054]),
            (3148,8,[3145,4873,3146,3157,3054,3147]),
            (3149,11,[3145,4873,3146,3157,3054,3147,3148,4875]),
            (3150,13,[3145,4873,3146,3157,3054,3147,3148,4875,3149]),
            (3151,15,[3145,4873,3146,3157,3054,3147,3148,4875,3149,3150]),
            (3163,18,[3162]), (3164,18,[3163]), (3168,19,[3167]), (3169,19,[3168]),
            (3170,20,[3169]), (3171,20,[3170])
        ];
        foreach (var scenario in cases)
        {
            bool skyCastle = scenario.Id >= 3163;
            using var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,scenario.Level,
                scenario.Completed.Concat(skyCastle ? new[] {3155,3156,3161} : [])
                    .Concat(scenario.Id >= 3168 ? new[] {3167} : []).Distinct().ToArray());
            var quest = quests.Get(scenario.Id);
            That(f.Session.LeaveRoom(), "新角色离开出生房间");
            var accept = f.Session.ChangeQuest(scenario.Id,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces);
            That(accept?.Commit is not null,$"实际Lv{scenario.Level}任务{scenario.Id}正常接取：{accept?.Reason}");
            That(f.Session.Travel(new(38,2,1085,249,5,38,0,0)), "真实剧情入口旅行");
            if (skyCastle)
            {
                var before = f.Read();
                That(f.Session.EnterFormalDungeon(checked((uint)quest.DungeonIndex!.Value),scenario.Id,0,dungeons,rooms) is null &&
                    QuestCompletionRepositoryTests.Same(before,f.Read()),"格兰之森入口不能借活动任务进入天空之城");
                f.TravelTo(towns,new(40,3,800,250));
            }
            var opening = f.Session.EnterFormalDungeon(checked((uint)quest.DungeonIndex!.Value),scenario.Id,0,dungeons,rooms);
            That(opening is not null && opening.Entry.RouteQuestId == scenario.Id && opening.Entry.BossMap.Id == quest.IntData[0],
                $"任务{scenario.Id}只选自身资源声明路线");
            var entry = opening!.Entry;
            var points = Path(entry.Maze.StaticTopology,entry.StartMap.Position,entry.BossMap.Position);
            var receipt = f.Session.CompleteFormalDungeonLoading()!;
            foreach (var point in points)
            {
                if (point == entry.BossMap.Position && entry.BossMap.Kind == "layered")
                {
                    var baseReceipt = receipt;
                    That(f.Session.MoveFormalDungeon(checked((byte)point.X),checked((byte)point.Y),1) is not null,
                        "终点基础地图未清除时允许资源声明的同坐标剧情换层");
                    receipt = f.Session.CompleteFormalDungeonLoading()!;
                    That(receipt.Snapshot.CurrentMap == entry.BossMap && f.Session.ObserveFormalDungeonDeath(
                        FormalDungeonSessionTests.Observation(baseReceipt,baseReceipt.Monsters.Monsters[0]),kills,growth).Outcome == DungeonRunOutcome.Rejected,
                        "换层使用真实目标地图并隔离基础层旧对象");
                }
                foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking).OrderBy(row => row.SpawnType == 3).ToArray())
                {
                    if (monster.IsScriptedBoss || receipt.Snapshot.CurrentMap.Kind == "layered")
                    {
                        byte[] body = new byte[74]; BinaryPrimitives.WriteUInt32LittleEndian(body,monster.ObjectKey);
                        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4),ushort.MaxValue);
                        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(42),0x11);
                        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(70),ClientBodyChecksum.Compute(body.AsSpan(0,70)));
                        That(DungeonRequests.TryParseScriptedDeath(body,out var parsed) && parsed!.ChecksumMatches && !DungeonRequests.TryParsePassiveDeath(body,out _),
                            "V31普通对象count=0正文与被动物件正文分别解析");
                        var observation = new NativeDungeonScriptedDeathObservation(monster.ObjectKey,parsed!.RewriteFlag,true,Convert.ToHexString(SHA256.HashData(body)));
                        var before = f.Read();
                        That(f.Session.ObserveFormalDungeonScriptedDeath(observation with { ChecksumMatches = false },kills,growth).Outcome == DungeonRunOutcome.Rejected &&
                            QuestCompletionRepositoryTests.Same(before,f.Read()), "剧情死亡校验失败不提交任何资产或进度");
                        That(f.Session.ObserveFormalDungeonScriptedDeath(observation,kills,growth).Outcome == DungeonRunOutcome.Accepted &&
                            f.Session.ObserveFormalDungeonScriptedDeath(observation,kills,growth).Outcome == DungeonRunOutcome.Duplicate,
                            "实际剧情对象只提交一次无参与者死亡，不伪造参与者");
                    }
                    else
                    {
                        That(f.Session.ObserveFormalDungeonScriptedDeath(new NativeDungeonScriptedDeathObservation(65536,0x11,true,new string('C',64)),kills,growth)
                            .Outcome == DungeonRunOutcome.Rejected, "无参与者报告仍拒绝当前实例不存在的对象");
                        That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt,monster),kills,growth).Outcome == DungeonRunOutcome.Accepted,
                            $"任务{scenario.Id}房间真实阻塞对象提交");
                    }
                }
                if (point == entry.BossMap.Position) break;
                var next = points[Array.IndexOf(points,point)+1];
                That(f.Session.MoveFormalDungeon(checked((byte)next.X),checked((byte)next.Y)) is not null, "实际门墙连接允许换房");
                receipt = f.Session.CompleteFormalDungeonLoading()!;
            }
            That(f.Session.ReadClearedQuest(scenario.Id)?.Clear.MapId == quest.IntData[0],
                $"任务{scenario.Id}清图凭据绑定实际末房与本次接取；阶段{f.Session.Phase}，条件{f.Read().ActiveMainQuests.Single().ConditionState}");
            var finish = f.Session.FinishQuest(scenario.Id,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
            That(finish.Outcome == QuestFinishOutcome.Committed && f.Read().CompletedQuests.Contains(scenario.Id), $"任务{scenario.Id}原子交付并保留完成记录");
            That(f.Session.ReturnFromFormalDungeon() is not null, "剧情完成正常回城");
        }
        Console.WriteLine($"PASS {Count-checks} 实际Lv5–20清图路线与V31剧情死亡断言（非实机）");
    }

    internal static DungeonPoint[] Path(DungeonStaticTopology topology,DungeonPoint start,DungeonPoint boss)
    {
        var from = new Dictionary<DungeonPoint,DungeonPoint?> { [start] = null }; var pending = new Queue<DungeonPoint>(); pending.Enqueue(start);
        while (pending.TryDequeue(out var point) && !from.ContainsKey(boss))
            foreach (var link in topology.Links!)
            {
                var next = link.First == point ? link.Second : link.Second == point ? link.First : (DungeonPoint?)null;
                if (next is { } target && from.TryAdd(target,point)) pending.Enqueue(target);
            }
        var path = new List<DungeonPoint> { boss };
        while (from[path[^1]] is { } parent) path.Add(parent);
        path.Reverse(); return path.ToArray();
    }
}
