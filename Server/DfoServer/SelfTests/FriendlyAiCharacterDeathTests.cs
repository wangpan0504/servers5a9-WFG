using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class FriendlyAiCharacterDeathTests
{
    // 匹配3163资源和真实Session/文件库，贡献统计明确合成；不代替实机验收。
    public static void Run(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,
        QuestCatalog? sharedQuests = null, SkillLearningCatalog? sharedSkills = null)
    {
        int checks = Count;
        var quests = sharedQuests ?? QuestCatalog.Load(pvf); var skills = sharedSkills ?? SkillLearningCatalog.Load(pvf);
        var growth = CharacterGrowthCatalog.Load(pvf,skills); var kills = DungeonKillExperienceCatalog.Load(pvf);
        var dungeons = DungeonCatalog.Load(pvf); var rooms = DungeonRoomCatalog.Load(pvf);
        foreach (byte job in new byte[] { 0,14,15 })
        {
            using var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,20,[3155,3156,3161,3162],job: job);
            That(f.Session.LeaveRoom() && f.Session.ChangeQuest(3163,QuestLifecycleAction.Accept,quests,
                items.Items,items.GoldLimits,items.EpicPieces)?.Commit is not null,"真实资格正常接取天空之城任务：" + job);
            f.TravelTo(towns,new(40,3,800,250));
            var quest = quests.Get(3163);
            var opening = f.Session.EnterFormalDungeon(checked((uint)quest.DungeonIndex!.Value),3163,0,dungeons,rooms);
            That(opening is not null && opening.Snapshot.CurrentMap.Id == 57879,"资源声明的真实3163起始房间");
            var actor = opening!.Monsters.Monsters.Single(monster => monster.AiCharacter?.Faction == "[character]");
            That(actor.MonsterId == 65006 && actor.SpawnType == 5 && !actor.IsBlocking,
                "APC来源来自实际发布的友方65006，不从F111推定阵营");
            var enemies = opening.Monsters.Monsters.Where(monster => monster.IsBlocking).ToArray();
            var mixed = Source(opening,enemies[0],actor.ObjectKey,true);
            var initial = f.Session.Selected!;
            That(f.Session.ObserveFormalDungeonDeath(mixed,kills,growth).Outcome == DungeonRunOutcome.Rejected &&
                QuestCompletionRepositoryTests.Same(initial,f.Read()),"加载前友方来源不能授权死亡或落经验");
            var receipt = f.Session.CompleteFormalDungeonLoading()!;
            if (job == 0)
            {
                f.Sql("CREATE TRIGGER apc_growth_fail AFTER INSERT ON character_dungeon_growth_receipts BEGIN SELECT RAISE(ABORT,'APC late growth failure'); END");
                Reject<SqliteException>(() => f.Session.ObserveFormalDungeonDeath(mixed,kills,growth),"多来源死亡仍经真实成长事务，晚期故障整笔回滚");
                That(QuestCompletionRepositoryTests.Same(initial,f.Read()) && ReferenceEquals(initial,f.Session.Selected) &&
                    f.Session.DungeonSnapshot!.LivingMonsters.Contains(enemies[0].ObjectKey),"失败保留在线快照、任务与存活目标，可原事实重试");
                f.Sql("DROP TRIGGER apc_growth_fail");
            }
            var first = f.Session.ObserveFormalDungeonDeath(mixed,kills,growth);
            var committed = f.Read();
            That(first.Outcome == DungeonRunOutcome.Accepted && first.Reward is not null &&
                committed.Revision == initial.Revision+1 && committed.Learning.Experience == initial.Learning.Experience+first.Reward.Amount &&
                !f.Session.DungeonSnapshot!.LivingMonsters.Contains(enemies[0].ObjectKey),"多来源同一次提交经验和真实敌人死亡");
            That(f.Session.ObserveFormalDungeonDeath(mixed with { Participants = mixed.Participants.ToArray().ToImmutableArray() },kills,growth)
                    .Outcome == DungeonRunOutcome.Duplicate &&
                f.Session.ObserveFormalDungeonDeath(mixed with { Participants = mixed.Participants.Reverse().ToImmutableArray() },kills,growth)
                    .Outcome == DungeonRunOutcome.Rejected && QuestCompletionRepositoryTests.Same(committed,f.Read()),
                "新解析数组精确重复不再授奖，来源顺序篡改仍拒绝");
            That(f.Session.ObserveFormalDungeonDeath(Source(receipt,actor,actor.ObjectKey,false),kills,growth).Outcome == DungeonRunOutcome.Rejected &&
                QuestCompletionRepositoryTests.Same(committed,f.Read()),"友方可作为来源，仍不能作为敌人死亡目标");
            foreach (var monster in enemies.Skip(1))
                That(f.Session.ObserveFormalDungeonDeath(Source(receipt,monster,actor.ObjectKey,false),kills,growth).Outcome == DungeonRunOutcome.Accepted,
                    "原生仅APC来源也可结算当前真实敌人，不强制玩家贡献");
            var path = QuestStoryRouteTests.Path(opening.Entry.Maze.StaticTopology,opening.Entry.StartMap.Position,opening.Entry.BossMap.Position);
            var next = path[1];
            var moved = f.Session.MoveFormalDungeon(checked((byte)next.X),checked((byte)next.Y));
            That(moved is not null && moved.Monsters.Monsters.All(monster => monster.ObjectKey != actor.ObjectKey),
                "实际下一房没有重发起始APC行，跟随key仍属于同一run");
            var target = moved!.Monsters.Monsters.First(monster => monster.IsBlocking);
            var followed = Source(moved,target,actor.ObjectKey,false);
            That(f.Session.ObserveFormalDungeonDeath(followed,kills,growth).Outcome == DungeonRunOutcome.Rejected,
                "房间loading期间仍拒绝死亡");
            f.Session.CompleteFormalDungeonLoading();
            var beforeFollowed = f.Read();
            var applied = f.Session.ObserveFormalDungeonDeath(followed,kills,growth);
            That(applied.Outcome == DungeonRunOutcome.Accepted && applied.Reward is not null &&
                f.Read().Learning.Experience == beforeFollowed.Learning.Experience+applied.Reward.Amount &&
                f.Session.ObserveFormalDungeonDeath(mixed,kills,growth).Outcome == DungeonRunOutcome.Rejected,
                "已发布友方APC跨房击杀正常落经验，旧房目标仍不能在新房提交");
            var afterFollowed = f.Read();
            That(f.Session.ObserveFormalDungeonDeath(followed,kills,growth).Outcome == DungeonRunOutcome.Duplicate &&
                QuestCompletionRepositoryTests.Same(afterFollowed,f.Read()),"跨房死亡精确重放仍只保存一次");
            That(f.Session.ReturnFromFormalDungeon() is not null,"正常提前回城结束原run");
            var reopened = f.Session.EnterFormalDungeon(checked((uint)quest.DungeonIndex!.Value),3163,0,dungeons,rooms)!;
            var currentActor = reopened.Monsters.Monsters.Single(monster => monster.AiCharacter?.Faction == "[character]");
            var currentTarget = reopened.Monsters.Monsters.First(monster => monster.IsBlocking);
            f.Session.CompleteFormalDungeonLoading();
            var beforeStale = f.Read();
            That(currentActor.ObjectKey != actor.ObjectKey && reopened.Snapshot.Identity.InstanceId != opening.Snapshot.Identity.InstanceId &&
                f.Session.ObserveFormalDungeonDeath(Source(reopened,currentTarget,actor.ObjectKey,false),kills,growth).Outcome == DungeonRunOutcome.Rejected &&
                QuestCompletionRepositoryTests.Same(beforeStale,f.Read()),"新run不能借原APC key授权新敌人死亡");
            That(f.Session.ObserveFormalDungeonDeath(Source(reopened,currentTarget,currentActor.ObjectKey,false),kills,growth).Outcome == DungeonRunOutcome.Accepted,
                "新run实际发布友方身份正常可用");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(QuestCompletionRepositoryTests.Same(f.Session.Selected!,f.Read()) &&
                f.Read().Learning.Experience > initial.Learning.Experience && f.Read().ActiveMainQuests.Single().QuestId == 3163,
                "重选恢复真实击杀经验和未伪造的任务状态");
        }
        Console.WriteLine($"PASS {Count-checks} 真实天空之城APC、多职业、跨房来源与死亡事务断言（非实机）");
    }

    static NativeDungeonDeathObservation Source(FormalDungeonReceipt receipt,DungeonPlannedMonster target,ushort actor,bool mixed) =>
        FormalDungeonSessionTests.Observation(receipt,target) with
        {
            Participants = mixed ? [new(0xF111,actor,2,1),new(0x0111,receipt.Character.Id,7,1)] : [new(0xF111,actor,0,0)]
        };
}
