using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestScriptedSettlementTests
{
    // 真实3151资源与Session/文件库，死亡观察明确合成；不代表客户端实机验收。
    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog? sharedQuests = null, SkillLearningCatalog? sharedSkills = null)
    {
        int checks = Count;
        var quests = sharedQuests ?? QuestCatalog.Load(pvf); var skills = sharedSkills ?? SkillLearningCatalog.Load(pvf);
        var growth = CharacterGrowthCatalog.Load(pvf,skills); var kills = DungeonKillExperienceCatalog.Load(pvf);
        var dungeons = DungeonCatalog.Load(pvf); var rooms = DungeonRoomCatalog.Load(pvf);
        var clears = DungeonClearExperienceCatalog.Load(pvf);
        int[] completed = [3145,4873,3146,3157,3054,3147,3148,4875,3149,3150];
        foreach (byte job in new byte[] { 0,14,15 })
        {
            using var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,15,completed,job: job);
            That(f.Session.LeaveRoom(),"3151结算fixture正常离开出生房间");
            That(f.Session.ChangeQuest(3151,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
                "各职业通过实际等级与前置正常接取3151: " + job);
            That(f.Session.Travel(new(38,2,1085,249,5,38,0,0)),"真实格兰之森入口旅行");
            var quest = quests.Get(3151);
            var opening = f.Session.EnterFormalDungeon(checked((uint)quest.DungeonIndex!.Value),3151,0,dungeons,rooms);
            That(opening is not null && opening.Entry.BossMap.Id == 76187 && opening.Entry.BossMap.Position == new DungeonPoint(3,1) &&
                opening.Entry.BaseBossMap.Id == 76186,"实际3151终点是资源声明的安全层，不借用基础Boss地图");
            var entry = opening!.Entry;
            var path = QuestStoryRouteTests.Path(entry.Maze.StaticTopology,entry.StartMap.Position,entry.BossMap.Position);
            var receipt = f.Session.CompleteFormalDungeonLoading()!;
            ushort oldBaseKey = 0;
            NativeDungeonScriptedDeathObservation? terminalObservation = null;
            DungeonPlannedMonster? terminalMonster = null;
            foreach (var point in path)
            {
                if (point == entry.BossMap.Position)
                {
                    var previous = receipt;
                    oldBaseKey = previous.Monsters.Monsters.First(monster => monster.IsBoss).ObjectKey;
                    That(f.Session.MoveFormalDungeon(checked((byte)point.X),checked((byte)point.Y),1) is not null,
                        "按真实剧情关系从未清基础Boss图撤退换层");
                    receipt = f.Session.CompleteFormalDungeonLoading()!;
                    terminalMonster = receipt.Monsters.Monsters.Single(monster => monster.IsBlocking);
                    That(receipt.Snapshot.CurrentMap == entry.BossMap && terminalMonster.MonsterId == 75100 &&
                        terminalMonster.SpawnType == 0 && !terminalMonster.IsBoss && terminalMonster.CanReportDeath,
                        "安全层唯一阻挡演员75100是普通rank，不伪造Boss身份");
                    var before = f.Read();
                    That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(previous,previous.Monsters.Monsters.First(monster => monster.IsBoss)),
                        kills,growth).Outcome == DungeonRunOutcome.Rejected && QuestCompletionRepositoryTests.Same(before,f.Read()),
                        "基础层旧Boss key不能在新安全层签发死亡或推进任务");
                    That(f.Session.EnableFormalDungeonSettlement(terminalMonster.ObjectKey) is null,
                        "真实终点演员存活且无ClearFact时不能发布结算");
                }
                foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking).OrderBy(row => row.IsBoss).ToArray())
                {
                    FormalDungeonDeathResult result;
                    if (monster.IsScriptedBoss || receipt.Snapshot.CurrentMap.Kind == "layered")
                    {
                        var observation = new NativeDungeonScriptedDeathObservation(monster.ObjectKey,0x22,true,new string('E',64));
                        result = f.Session.ObserveFormalDungeonScriptedDeath(observation,kills,growth);
                        if (receipt.Snapshot.CurrentMap == entry.BossMap) terminalObservation = observation;
                    }
                    else result = f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt,monster),kills,growth);
                    That(result.Outcome == DungeonRunOutcome.Accepted,"真实路线阻挡对象提交合成权威死亡");
                }
                if (point == entry.BossMap.Position) break;
                var next = path[Array.IndexOf(path,point)+1];
                That(f.Session.MoveFormalDungeon(checked((byte)next.X),checked((byte)next.Y)) is not null,"真实门墙连接允许推进3151路线");
                receipt = f.Session.CompleteFormalDungeonLoading()!;
            }
            That(terminalObservation is not null && terminalMonster is not null && f.Session.Phase == SessionPhase.Settlement &&
                f.Session.ReadClearedQuest(3151)?.Clear.MapId == 76187 && f.Read().ActiveMainQuests.Single(row => row.QuestId == 3151).ConditionState == 0,
                "真实普通终点死亡同事务落ClearFact与任务归零");
            var target = terminalMonster!;
            var enabled = f.Session.EnableFormalDungeonSettlement(target.ObjectKey);
            That(enabled is { } candidate && candidate.BossKey == target.ObjectKey && candidate.Receipt.Snapshot.CurrentMap == entry.BossMap,
                "普通rank终点可发布115/31，引用本层真实死亡key");
            Bytes(DungeonSettlementPackets.BossAccepted(enabled!.Value.BossKey),[1,1,(byte)target.ObjectKey,(byte)(target.ObjectKey >> 8)],
                "115沿既有协议字段回真实对象key，不改造Boss");
            That(f.Session.EnableFormalDungeonSettlement(oldBaseKey)?.BossKey == target.ObjectKey &&
                f.Session.ConfirmFormalDungeonBoss(f.Session.Selected!.Id,target.ObjectKey,checked((uint)target.CandidateLevel),0) is null,
                "fallback只选当前终点真实死亡对象，普通rank不伪装117 Boss确认");
            f.Session.ConfirmFormalDungeonSettlementEnabled(enabled.Value.Receipt);
            That(f.Session.EnableFormalDungeonSettlement(target.ObjectKey) is null,"115/31开启确认后不再次发布结算入口");
            var killed = f.Read();
            byte[] report = DungeonSettlementProtocolTests.Report(killed.Id,99);
            That(DungeonSettlementRequests.TryParsePlayResult(report,out var parsed),"实际V31的46正文可独立解析");
            var settled = f.Session.SettleFormalDungeon(parsed!.ActorId,parsed.RankPoint,parsed.MaximumRankPoint,report,dungeons,clears,growth);
            var committed = f.Read();
            That(settled.Outcome == DungeonRunOutcome.Accepted && settled.Receipt is not null && settled.Reward is not null &&
                committed.Revision == killed.Revision+1 && committed.Learning.Experience == killed.Learning.Experience+settled.Reward.TotalExperience &&
                f.Session.FormalDungeonResultFocus() is not null,"未发送117即可Enable后完成46、保存一次结算并打开结果页");
            var duplicate = f.Session.SettleFormalDungeon(parsed.ActorId,parsed.RankPoint,parsed.MaximumRankPoint,report,dungeons,clears,growth);
            That(duplicate.Outcome == DungeonRunOutcome.Duplicate && duplicate.Reward == settled.Reward &&
                QuestCompletionRepositoryTests.Same(committed,f.Read()) &&
                f.Session.ObserveFormalDungeonScriptedDeath(terminalObservation!,kills,growth).Outcome == DungeonRunOutcome.Duplicate,
                "重复46和同一终点死亡均不二次授予经验或修改任务与资产");
            That(f.Session.ReturnFromFormalDungeon() is not null && f.Session.EnableFormalDungeonSettlement(target.ObjectKey) is null &&
                f.Session.ObserveFormalDungeonScriptedDeath(terminalObservation!,kills,growth).Outcome == DungeonRunOutcome.Rejected,
                "回城后旧终点key不能开启结算或提交迟到死亡");
        }
        Console.WriteLine($"PASS {Count-checks} 3151普通rank剧情终点、多职业结算与重复隔离断言（非实机）");
    }
}
