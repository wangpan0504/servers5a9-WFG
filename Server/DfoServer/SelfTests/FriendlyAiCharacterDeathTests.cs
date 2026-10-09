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
            var mixed = Source(opening,enemies[0],actor.ObjectKey,true) with { ReporterKey = ushort.MaxValue };
            var initial = f.Session.Selected!;
            That(f.Session.ObserveFormalDungeonDeath(mixed,kills,growth).Outcome == DungeonRunOutcome.Rejected &&
                QuestCompletionRepositoryTests.Same(initial,f.Read()),"加载前友方来源不能授权死亡或落经验");
            var receipt = f.Session.CompleteFormalDungeonLoading()!;
            var unknown = mixed with
            {
                MonsterKey = 65534, ReporterKey = 65533, BodySha256 = new string('B',64),
                Participants = [new(0xABCD,65532,0,0)]
            };
            var beforeEmpty = f.Session.DungeonSnapshot!;
            That(Ignored(f.Session.ObserveFormalDungeonDeath(unknown,kills,growth),65534) &&
                Ignored(f.Session.ObserveFormalDungeonScriptedDeath(new(65534,0x21,true,new string('C',64)),kills,growth),65534) &&
                QuestCompletionRepositoryTests.Same(initial,f.Read()) && ReferenceEquals(initial,f.Session.Selected) &&
                beforeEmpty.Identity == f.Session.DungeonSnapshot!.Identity &&
                beforeEmpty.LivingMonsters.SequenceEqual(f.Session.DungeonSnapshot!.LivingMonsters),
                "未知目标普通/count0报告只审计忽略，不响应、不写成长、任务或清图状态");
            That(Empty(f.Session.ObserveFormalDungeonDeath(Source(receipt,actor,actor.ObjectKey,false),kills,growth),actor.ObjectKey) &&
                !f.Session.DungeonSnapshot!.LivingMonsters.Contains(actor.ObjectKey) &&
                Empty(f.Session.ObserveFormalDungeonScriptedDeath(new(actor.ObjectKey,0x21,true,new string('C',64)),kills,growth),actor.ObjectKey) &&
                QuestCompletionRepositoryTests.Same(initial,f.Read()) && ReferenceEquals(initial,f.Session.Selected) &&
                beforeEmpty.Identity == f.Session.DungeonSnapshot!.Identity,
                "友方APC普通死亡退场与count0重放空响应，只移除存活视觉对象，不改revision、经验或任务");
            var retried = mixed;
            if (job == 0)
            {
                f.Sql("CREATE TRIGGER apc_growth_fail AFTER INSERT ON character_dungeon_growth_receipts BEGIN SELECT RAISE(ABORT,'APC late growth failure'); END");
                Reject<SqliteException>(() => f.Session.ObserveFormalDungeonDeath(mixed,kills,growth),"多来源死亡仍经真实成长事务，晚期故障整笔回滚");
                That(QuestCompletionRepositoryTests.Same(initial,f.Read()) && ReferenceEquals(initial,f.Session.Selected) &&
                    f.Session.DungeonSnapshot!.LivingMonsters.Contains(enemies[0].ObjectKey),"失败保留在线快照、任务与存活目标，可原事实重试");
                f.Sql("DROP TRIGGER apc_growth_fail");
                retried = mixed with
                {
                    ReporterKey = 65533, BodySha256 = new string('D',64),
                    Participants = [new(0x0211,65532,0,0),new(0xABCD,65531,0,0)]
                };
            }
            var first = f.Session.ObserveFormalDungeonDeath(retried,kills,growth);
            var committed = f.Read();
            That(first.Outcome == DungeonRunOutcome.Accepted && first.Reward is not null &&
                committed.Revision == initial.Revision+1 && committed.Learning.Experience == initial.Learning.Experience+first.Reward.Amount &&
                first.Receipt is not null && !f.Session.DungeonSnapshot!.LivingMonsters.Contains(enemies[0].ObjectKey),
                "FFFF与APC多来源正常提交；SQL回滚后换0211或未知来源仍只提交一次真实敌人经验");
            foreach (var replay in new[]
            {
                mixed with { Participants = mixed.Participants.ToArray().ToImmutableArray() },
                mixed with { Participants = mixed.Participants.Reverse().ToImmutableArray(), BodySha256 = new string('E',64) },
                mixed with { ReporterKey = 65533, Participants = [new(0xABCD,65532,0,0)], BodySha256 = new string('F',64) }
            })
                That(f.Session.ObserveFormalDungeonDeath(replay,kills,growth) is { Outcome: DungeonRunOutcome.Duplicate } duplicate &&
                    Ignored(duplicate,enemies[0].ObjectKey) && QuestCompletionRepositoryTests.Same(committed,f.Read()),
                    "已提交普通死亡换来源、顺序或正文仅审计，不重发响应或奖励");
            int sourceIndex = 0;
            foreach (var monster in enemies.Skip(1))
            {
                var report = Source(receipt,monster,actor.ObjectKey,false) with
                {
                    ReporterKey = ushort.MaxValue,
                    Participants = sourceIndex++ % 2 == 0 ? [new(0x0211,65532,0,0)] : [new(0xABCD,65531,0,0)]
                };
                That(f.Session.ObserveFormalDungeonDeath(report,kills,growth) is { Outcome: DungeonRunOutcome.Accepted, Reward: not null },
                    "0211与未知类型/key仅审计，不阻断当前真实敌人的奖励和死亡");
            }
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
                Ignored(f.Session.ObserveFormalDungeonDeath(mixed,kills,growth),enemies[0].ObjectKey),
                "已退场APC来源跨房仍可推进真实敌人，旧房目标只审计而不响应或发奖");
            var afterFollowed = f.Read();
            That(f.Session.ObserveFormalDungeonDeath(followed,kills,growth) is { Outcome: DungeonRunOutcome.Duplicate } repeated &&
                Ignored(repeated,target.ObjectKey) &&
                QuestCompletionRepositoryTests.Same(afterFollowed,f.Read()),"跨房死亡精确重放仍只保存一次");
            That(f.Session.ReturnFromFormalDungeon() is not null,"正常提前回城结束原run");
            var reopened = f.Session.EnterFormalDungeon(checked((uint)quest.DungeonIndex!.Value),3163,0,dungeons,rooms)!;
            var currentActor = reopened.Monsters.Monsters.Single(monster => monster.AiCharacter?.Faction == "[character]");
            var currentTarget = reopened.Monsters.Monsters.First(monster => monster.IsBlocking);
            f.Session.CompleteFormalDungeonLoading();
            var beforeStale = f.Read();
            That(Empty(f.Session.ObserveFormalDungeonScriptedDeath(new(currentActor.ObjectKey,0x21,true,new string('C',64)),kills,growth),currentActor.ObjectKey) &&
                !f.Session.DungeonSnapshot!.LivingMonsters.Contains(currentActor.ObjectKey) &&
                Empty(f.Session.ObserveFormalDungeonDeath(Source(reopened,currentActor,actor.ObjectKey,false),kills,growth),currentActor.ObjectKey) &&
                QuestCompletionRepositoryTests.Same(beforeStale,f.Read()),
                "新run友方APC先count0退场再普通报告重放，同样无revision、经验或任务变化");
            That(currentActor.ObjectKey != actor.ObjectKey && reopened.Snapshot.Identity.InstanceId != opening.Snapshot.Identity.InstanceId &&
                Ignored(f.Session.ObserveFormalDungeonDeath(mixed,kills,growth),enemies[0].ObjectKey) &&
                Ignored(f.Session.ObserveFormalDungeonDeath(Source(opening,actor,actor.ObjectKey,false),kills,growth),actor.ObjectKey) &&
                QuestCompletionRepositoryTests.Same(beforeStale,f.Read()) &&
                f.Session.DungeonSnapshot!.LivingMonsters.Contains(currentTarget.ObjectKey),
                "新run中旧目标只审计忽略，不响应、不发新奖励、不误删当前敌人");
            var oldSource = f.Session.ObserveFormalDungeonDeath(Source(reopened,currentTarget,actor.ObjectKey,false),kills,growth);
            var afterOldSource = f.Read();
            That(oldSource is { Outcome: DungeonRunOutcome.Accepted, Reward: not null, Receipt: not null } &&
                afterOldSource.Revision == beforeStale.Revision+1 &&
                afterOldSource.Learning.Experience == beforeStale.Learning.Experience+oldSource.Reward.Amount &&
                !f.Session.DungeonSnapshot!.LivingMonsters.Contains(currentTarget.ObjectKey),
                "旧run来源key仅审计，仍可推进当前run真实敌人且只提交本次奖励");
            That(Ignored(f.Session.ObserveFormalDungeonDeath(Source(reopened,currentTarget,currentActor.ObjectKey,false),kills,growth),currentTarget.ObjectKey) &&
                QuestCompletionRepositoryTests.Same(afterOldSource,f.Read()),"已提交普通目标换回新APC来源只审计，不响应或重复奖励");
            f.Session.ReturnToRoster();
            That(f.Session.ObserveFormalDungeonDeath(unknown,kills,growth).Outcome == DungeonRunOutcome.Rejected &&
                QuestCompletionRepositoryTests.Same(afterOldSource,f.Read()),"未选角仍拒绝死亡，来源审计不绕过会话owner与状态");
            f.Session.Select(0); f.Session.MarkReady();
            That(QuestCompletionRepositoryTests.Same(f.Session.Selected!,f.Read()) &&
                f.Read().Learning.Experience > initial.Learning.Experience && f.Read().ActiveMainQuests.Single().QuestId == 3163,
                "重选恢复真实击杀经验和未伪造的任务状态");
        }
        Console.WriteLine($"PASS {Count-checks} 多职业来源审计、无奖励退场、跨实例重放与死亡事务断言（非实机）");
    }

    static NativeDungeonDeathObservation Source(FormalDungeonReceipt receipt,DungeonPlannedMonster target,ushort actor,bool mixed) =>
        FormalDungeonSessionTests.Observation(receipt,target) with
        {
            Participants = mixed ? [new(0xF111,actor,2,1),new(0x0111,receipt.Character.Id,7,1)] : [new(0xF111,actor,0,0)]
        };

    static bool Empty(FormalDungeonDeathResult result,ushort key) =>
        result.Outcome is DungeonRunOutcome.Accepted or DungeonRunOutcome.Duplicate && result.Receipt is not null &&
        result.MonsterKey == key && result.Reward is null && result.ClearFact is null && result.Drop is null &&
        result.QuestDropSlots.Count == 0 && result.CulledMonsterKeys.Count == 0;

    static bool Ignored(FormalDungeonDeathResult result,ushort key) =>
        result.Outcome is DungeonRunOutcome.Accepted or DungeonRunOutcome.Duplicate && result.Receipt is null &&
        result.MonsterKey == key && result.Reward is null && result.ClearFact is null && result.Drop is null &&
        result.QuestDropSlots.Count == 0 && result.CulledMonsterKeys.Count == 0;
}
