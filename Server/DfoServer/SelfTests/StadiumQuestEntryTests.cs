using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class StadiumQuestEntryTests
{
    // 真实6304整条五房路线；死亡正文为合成回归输入，不冒充客户端抓包或实机。
    internal static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items,
        QuestCatalog quests, SkillLearningCatalog skills)
    {
        int checks = Count;
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf,skills);
        var kills = global::DfoServer.Testing.SelfTestCatalogs.KillExperience(pvf);
        var dungeons = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
        var quest = quests.Get(6304);
        That(quest.MinimumLevel == 95 && quest.Prerequisites.SequenceEqual([6303]) &&
            QuestClearEligibility.IsSingleMap(quest) && quest.IntData.SequenceEqual([655514]) &&
            quest.Definition!.Dungeons.Any(row => row.DungeonId == 9609),"尼巫的请求绑定真实前置、9609和末图655514");
        var entrance = MiddleQuestEntryTests.Entrance(towns,quest,9609);
        foreach (byte job in Enumerable.Range(0,16).Select(value => (byte)value))
        {
            foreach (uint request in new uint[] {6304,0})
            {
            using var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,95,
                entrance.Completed,job:job,position:entrance.Position);
            That(f.Session.ChangeQuest(6304,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,
                "16职业沿真实31生命周期接取6304："+job);
                var before = f.Read();
                var acceptance = before.ActiveMainQuests.Single(row => row.QuestId == 6304);
                That(DungeonEntryPlan.TryCreate(dungeons,before,9609,request,0,out var planned,out var reason,
                    quest:request == 0 ? null : quest,quests:quests),"全职业显式/普通0规划："+reason);
                var entry = planned!;
                That(entry.Maze.SourceOrdinal == 2 && entry.RouteQuestId == 6304 && !entry.IsOrdinary &&
                    entry.Maze.MapSpecification!.Select(map => map.Id).SequenceEqual(Enumerable.Range(655510,5)) &&
                    entry.Maze.LayeredMaps.Count == 0 && QuestHuntProgress.Bound(acceptance,entry),
                    "保留五张原地图、任务布局与原接取，不套用9608竞技模式");
                ushort next = 100;
                foreach (var map in entry.Maze.MapSpecification!)
                {
                    var room = rooms.Get(map,requirePassiveDefinitions:false);
                    var plan = DungeonMonsterPlan.FromRoom(room,before.Id,next,entry.BasisLevel,playerTeam:entry.PlayerTeam);
                    That(plan.Monsters.Count == room.Monsters.Count + 1 &&
                        plan.Monsters.Take(room.Monsters.Count).Select(row => row.Source)
                            .SequenceEqual(room.Monsters) &&
                        plan.Monsters.Take(room.Monsters.Count).Select(row => row.SourceOrdinal)
                            .SequenceEqual(Enumerable.Range(0,room.Monsters.Count)) &&
                        room.AiCharacters is [{ Id:5409,Faction:"[character]",RankKind:"[normal]",Level:70 }] &&
                        plan.Monsters[^1] is { MonsterId:5409,SourceOrdinal:0,SpawnType:5,IsFriendly:true,IsBlocking:false } &&
                        plan.Monsters[^1].AiCharacter == room.AiCharacters[0] &&
                        plan.Monsters[^1].CandidateLevel == room.AiCharacters[0].Level,
                        "五房原怪物完整有序，另保留实际原生5409友方APC及其独立ordinal和等级");
                    next = checked((ushort)plan.NextObjectKey);
                    if (map.Id == 655514) CheckTerminal(entry with { StartMap=map },plan);
                }
                // 两端职业正式Session走完显式和普通0；其余职业覆盖同资源准入及完整规划。
                if (job is not (0 or 15)) continue;
                var opened = f.Session.EnterFormalDungeon(9609,request,0,dungeons,rooms,draw: _ => 0)
                    ?? throw new InvalidOperationException("中央竞技场正式入场失败");
                That(opened.Entry.RouteQuestId == 6304 && opened.Entry.QuestId == request &&
                    opened.Snapshot.Identity.InstanceId != Guid.Empty,"正式入场保留原路线及实际请求身份");
                var receipt = f.Session.CompleteFormalDungeonLoading()!;
                NativeDungeonDeathObservation? lastDeath = null;
                var retired = false;
                for (byte x = 0; x < 5; x++)
                {
                    That(receipt.Snapshot.CurrentMap.Id == 655510+x && f.Session.Phase == SessionPhase.Dungeon,
                        "沿实际五房路线加载，不直接跳末房");
                    if (x == 4)
                    {
                        CheckTerminal(receipt.Entry with { StartMap = receipt.Snapshot.CurrentMap }, receipt.Monsters);
                        CheckTerminal(receipt.Entry,receipt.Monsters);
                    }
                    if (x == 4 && job == 15)
                    {
                        var display = receipt.Monsters.Monsters.Single(row => row.MonsterId == 63821);
                        var unchanged = f.Read();
                        var ignored = f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt,display),kills,growth);
                        That(ignored.Outcome == DungeonRunOutcome.Accepted && ignored.Receipt is not null &&
                            !f.Session.DungeonSnapshot!.LivingMonsters.Contains(display.ObjectKey) &&
                            ignored.Reward is null && ignored.Drop is null && ignored.ClearFact is null &&
                            QuestCompletionRepositoryTests.Same(unchanged,f.Read()) &&
                            f.Session.Phase == SessionPhase.Dungeon && f.Session.EnableFormalDungeonSettlement() is null &&
                            f.Repository.LoadQuestClear(f.Owner,f.Read(),6304) is null,
                            "显示演员退场无成长、掉落、清图和结算许可，不替代真实敌人死亡");
                        retired = true;
                    }
                    foreach (var monster in receipt.Monsters.Monsters.Where(row => row.IsBlocking))
                    {
                        lastDeath = FormalDungeonSessionTests.Observation(receipt,monster);
                        var death = f.Session.ObserveFormalDungeonDeath(lastDeath,kills,growth);
                        That(death.Outcome == DungeonRunOutcome.Accepted && death.Receipt is not null,
                            "仅向真实战斗演员提交合成普通死亡");
                        var committed = f.Read();
                        That(f.Session.ObserveFormalDungeonDeath(lastDeath,kills,growth).Outcome == DungeonRunOutcome.Duplicate &&
                            QuestCompletionRepositoryTests.Same(committed,f.Read()),"普通死亡精确重试不重复奖励");
                        if (x == 4)
                            That(death.ClearFact is not null && monster.MonsterId == 109008549 &&
                                f.Session.EnableFormalDungeonSettlement()?.BossKey == monster.ObjectKey,
                                "真实芮兹死亡形成ClearFact，115引用其实际key而非63821控制演员");
                    }
                    if (x == 4) break;
                    That(f.Session.MoveFormalDungeon((byte)(x+1),0,0) is not null,"原门墙和清房允许进入下房");
                    receipt = f.Session.CompleteFormalDungeonLoading()!;
                }
                That((retired || job == 0) && f.Session.Phase == SessionPhase.Settlement &&
                    f.Read().ActiveMainQuests.Single(row => row.QuestId == 6304) is {ConditionState:0} complete &&
                    complete.AcceptanceId == acceptance.AcceptanceId && f.Repository.LoadQuestClear(f.Owner,f.Read(),6304) is not null,
                    "真实五房死亡形成原接取持久清图并达成6304，不要求63821死亡");
                That(f.Session.ReturnFromFormalDungeon() is not null &&
                    f.Session.ObserveFormalDungeonDeath(lastDeath!,kills,growth).Outcome == DungeonRunOutcome.Rejected,
                    "回城使旧实例死亡失效");
            }
        }
        Console.WriteLine($"PASS {Count-checks} 中央竞技场6304全职业32准入、完整五MAP及代表Session清图回归（非实机）");
    }

    static void CheckTerminal(DungeonEntryPlan entry,DungeonMonsterPlan plan)
    {
        That(plan.Monsters.Take(2).ToArray() is [
            {MonsterId:109008549,SourceOrdinal:0,SpawnType:0,IsBlocking:true,CanReportDeath:true,IsBoss:false,Source.ChampionRestricted:true},
            {MonsterId:63821,SourceOrdinal:1,SpawnType:3,Team:100,IsFriendly:false,IsBlocking:false,CanReportDeath:false,IsBoss:false,IsScriptedBoss:false}],
            "真敌与显示控制演员完整保留，显示品级仍3且不冒充dummy或战斗Boss");
        That(plan.Monsters[1].Source!.IsDisplayHuntDummy,"显示标记由原资源精确派生");
        var packet = DungeonPackets.StartMap(entry,plan,0x12345678);
        That(plan.Monsters.Count == 3 && plan.Monsters[2] is
            { MonsterId:5409,SourceOrdinal:0,SpawnType:5,IsFriendly:true,IsBlocking:false } &&
            plan.Monsters[2].AiCharacter == plan.Room.AiCharacters.Single(),
            "末房原两名怪物后保留累计候选原生5409，不将其改成敌人或追加死亡义务");
        That(packet[18] == 3 && packet.Length == 89,"末房NOTI29含三个完整22B演员记录");
        for (int index = 0; index < 2; index++)
        {
            var row = packet.AsSpan(19+22*index,22);
            That(BinaryPrimitives.ReadUInt16LittleEndian(row) == 0 &&
                BinaryPrimitives.ReadUInt32LittleEndian(row[2..]) == index &&
                BinaryPrimitives.ReadUInt16LittleEndian(row[6..]) == plan.Monsters[index].ObjectKey &&
                BinaryPrimitives.ReadUInt32LittleEndian(row[8..]) == (index == 0 ? 109008549u : 63821u) &&
                row[12] == 95 && row[13] == (index == 0 ? 0 : 3) && row[14] == 0 && row[15] == 0 &&
                row[16] == 255 && BinaryPrimitives.ReadUInt32LittleEndian(row[17..]) == 0 && row[21] == index,
                "逐字段独立预期：保留原ordinal/key/品级，只有显示演员清房排除位为1");
        }
        var apc = packet.AsSpan(63,22);
        That(BinaryPrimitives.ReadUInt16LittleEndian(apc) == 0 && BinaryPrimitives.ReadUInt32LittleEndian(apc[2..]) == 0 &&
            BinaryPrimitives.ReadUInt16LittleEndian(apc[6..]) == plan.Monsters[2].ObjectKey &&
            BinaryPrimitives.ReadUInt32LittleEndian(apc[8..]) == 5409 && apc[12] == 70 &&
            apc[13] == 5 && apc[14] == 0 && apc[15] == 0 && apc[16] == 255 &&
            BinaryPrimitives.ReadUInt32LittleEndian(apc[17..]) == 0 && apc[21] == 1,
            "5409独立APC行保原资源序号、等级、类型及友方清房排除位，完整22B字段不省略");
    }
}
