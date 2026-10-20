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

internal static class Issue153MermaidClearTests
{
    // 只验证真实资源路线和服务端脚本39提交链；合成报告不证明CMT2115会自然发包。
    internal static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources items, QuestCatalog quests,
        SkillLearningCatalog skills, CharacterGrowthCatalog growth, DungeonCatalog dungeons, DungeonRoomCatalog rooms,
        DungeonKillExperienceCatalog kills, DungeonClearExperienceCatalog clears, QuestRewardParameterCatalog parameters)
    {
        int checks = Count;
        var quest = quests.Get(3487);
        var entrance = MiddleQuestEntryTests.Entrance(towns,quest,162);
        foreach (uint request in new uint[] { 3487,0 })
        {
            using var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,59,
                entrance.Completed,position:entrance.Position);
            That(f.Session.ChangeQuest(3487,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
                "3487按真实Lv59及前置3486接取");
            var accepted = f.Read().ActiveMainQuests.Single(row => row.QuestId == 3487);
            var opening = f.Session.EnterFormalDungeon(162,request,0,dungeons,rooms);
            That(opening is not null && opening.Entry.RouteQuestId == 3487 && opening.Entry.StartMap.Id == 91541 &&
                opening.Entry.BossMap.Id == 91548 && opening.Entry.HasExplicitClearMap && opening.Entry.Maze.LayeredMaps.Count == 0,
                "3487显式/普通0入口保持真实91541至91548路线："+f.Session.LastFormalDungeonRejection);
            var path = QuestStoryRouteTests.Path(opening!.Entry.Maze.StaticTopology,opening.Entry.StartMap.Position,opening.Entry.BossMap.Position);
            int deaths = 0;
            FormalDungeonReceipt loaded = opening;
            for (int at = 0; at < path.Length; at++)
            {
                loaded = f.Session.CompleteFormalDungeonLoading()!;
                if (at == path.Length-1) break;
                foreach (var monster in loaded.Monsters.Monsters.Where(row => row.IsBlocking))
                {
                    That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(loaded,monster),kills,growth).Outcome == DungeonRunOutcome.Accepted,
                        "3487前序房间仅以实际演员key的合成死亡清房");
                    deaths++;
                }
                That(f.Session.MoveFormalDungeon(checked((byte)path[at+1].X),checked((byte)path[at+1].Y)) is not null,
                    "3487沿真实门墙走到下一房间");
            }
            var boss = loaded.Monsters.Monsters.Single(row => row.MonsterId == 62531);
            var ordinary = loaded.Monsters.Monsters.Where(row => row.Source is not null && row.MonsterId != 62531).ToArray();
            That(loaded.Snapshot.CurrentMap.Id == 91548 &&
                boss is { SourceOrdinal:0, SpawnType:3, IsBlocking:true, CanReportDeath:true, IsBoss:true, IsFriendly:false } &&
                boss.Source!.RawValues.SequenceEqual([62531,1,0,912,394,0,1,1]) && boss.Source.NpcParameter == 0 &&
                boss.Source.Modifiers.SequenceEqual(["[NPC]","[dummy]"]) &&
                ordinary.Select(row => row.MonsterId).SequenceEqual([69022,69022,65314,65314]) && ordinary.All(row => row.IsBlocking) &&
                loaded.Monsters.Monsters.Single(row => row.AiCharacter?.Id == 55430) is { CanReportDeath:false, IsBlocking:false },
                "91548保留NPC+dummy Boss、四只普通怪及友方APC，不删演员冒充清图");
            var before = f.Read();
            That(f.Session.Phase == SessionPhase.Dungeon && f.Session.EnableFormalDungeonSettlement() is null &&
                f.Session.FinishQuest(3487,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth).Outcome == QuestFinishOutcome.Rejected,
                "Boss报告前不能提前结算或34交付");
            That(f.Session.ObserveFormalDungeonScriptedDeath(new(boss.ObjectKey,0x22,false,new string('A',64)),kills,growth).Outcome == DungeonRunOutcome.Rejected &&
                QuestCompletionRepositoryTests.Same(before,f.Read()),"坏摘要的Boss脚本报告不能落库或清图");
            var death = f.Session.ObserveFormalDungeonScriptedDeath(new(boss.ObjectKey,0x22,true,new string('B',64)),kills,growth);
            That(death is { Outcome:DungeonRunOutcome.Accepted, ClearFact.ClearMap.Id:91548 } &&
                death.ClearFact.ConfirmedDeathEvents.Count == deaths+1 &&
                death.CulledMonsterKeys.Order().SequenceEqual(ordinary.Select(row => row.ObjectKey).Order()) &&
                f.Session.Phase == SessionPhase.Settlement,
                "62531脚本39沿原事务生成唯一终点，四小怪清退不冒充死亡");
            var clear = f.Session.ReadClearedQuest(3487)?.Clear;
            That(clear is { MapId:91548 } && clear.AcceptanceId == accepted.AcceptanceId,
                "3487通关凭据绑定原接取AcceptanceId");
            before = f.Read();
            That(f.Session.ObserveFormalDungeonScriptedDeath(new(boss.ObjectKey,0x22,true,new string('B',64)),kills,growth).Outcome == DungeonRunOutcome.Duplicate &&
                QuestCompletionRepositoryTests.Same(before,f.Read()),"重复62531报告不重复推进或发奖");
            foreach (var monster in ordinary)
                That(f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(loaded,monster),kills,growth).Outcome == DungeonRunOutcome.Duplicate,
                    "已清退小怪的后到39只去重，不补四份击杀奖励");
            That(QuestCompletionRepositoryTests.Same(before,f.Read()),"清退补报不改变完整角色状态");
            var enabled = f.Session.EnableFormalDungeonSettlement();
            That(enabled?.BossKey == boss.ObjectKey,"3487沿真实Boss key许可31");
            f.Session.ConfirmFormalDungeonSettlementEnabled(enabled!.Value.Receipt);
            byte[] report = DungeonSettlementProtocolTests.Report(f.Read().Id,99);
            That(DungeonSettlementRequests.TryParsePlayResult(report,out var parsed),"3487沿正式46解析");
            var settled = f.Session.SettleFormalDungeon(parsed!.ActorId,parsed.RankPoint,parsed.MaximumRankPoint,report,dungeons,clears,growth);
            That(settled is { Outcome:DungeonRunOutcome.Accepted, Receipt:not null },"3487正式46保存一次结算");
            That(f.Session.ReturnFromFormalDungeon() is not null,"3487结算后保留正式返回");
            f.Session.ReturnToRoster();
            f.Session.Select(0,resumeTown:true);
            f.Session.MarkReady();
            That(f.Session.ReadClearedQuest(3487)?.Clear is { MapId:91548 } restored && restored.AcceptanceId == accepted.AcceptanceId,
                "3487重选后保留同接取真实清图凭据");
            That(f.Session.FinishQuest(3487,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth).Receipt is not null &&
                f.Read().CompletedQuests.Contains(3487),"3487重选后正常34交付");
        }
        Console.WriteLine($"PASS {Count-checks} #153 第10项3487脚本报告、清图、46、34及重选回归（非实机）");
    }
}
