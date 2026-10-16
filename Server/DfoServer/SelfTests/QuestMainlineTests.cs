using System.Buffers.Binary;
using System.Security.Cryptography;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 全新角色连续推进；不预填完成位、等级、任务材料或剧情进度。
internal static class QuestMainlineTests
{
    internal static bool MatchesReconnect(Character completed, Character restored, int firstDay, int lastDay, Location room)
    {
        if (restored.Position != room || restored.QuestCycleDay < firstDay || restored.QuestCycleDay > lastDay ||
            restored.QuestCycleDay < completed.QuestCycleDay) return false;
        bool rolled = restored.QuestCycleDay != completed.QuestCycleDay;
        // 只允许实际选角窗口内的日切、过期每日投影和固定次数保存；其余资产逐字段保持。
        var expired = rolled ? completed.QuestDailyClaims.Where(row => row.Value > 0 && row.Value < restored.QuestCycleDay)
            .Select(row => row.Key).ToHashSet() : [];
        var accountExpired = rolled ? completed.AccountQuestDailyClaims.Where(row => row.Value > 0 && row.Value < restored.QuestCycleDay)
            .Select(row => row.Key).ToHashSet() : [];
        var expected = completed with
        {
            Position = room, Return = restored.Return, Revision = completed.Revision + (rolled ? 2 : 1),
            QuestCycleDay = restored.QuestCycleDay,
            CompletedQuests = completed.CompletedQuests.Where(id => !expired.Contains(id)).ToArray(),
            QuestAnswers = completed.QuestAnswers.RemoveRange(expired), QuestDailyClaims = completed.QuestDailyClaims.RemoveRange(expired),
            AccountCompletedQuests = completed.AccountCompletedQuests.Where(id => !accountExpired.Contains(id)).ToArray(),
            AccountQuestAnswers = completed.AccountQuestAnswers.RemoveRange(accountExpired),
            AccountQuestDailyClaims = completed.AccountQuestDailyClaims.RemoveRange(accountExpired)
        };
        return QuestCompletionRepositoryTests.Same(expected,restored);
    }

    public static void Run(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,
        QuestCatalog? sharedQuests = null, SkillLearningCatalog? sharedSkills = null)
        => RunConfigured(towns,pvf,characters,items,Enumerable.Range(0,16).Select(job => ((byte)job,job == 0 ? 30 : 20)),sharedQuests,sharedSkills);

    public static void RunLater(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,
        QuestCatalog? sharedQuests = null, SkillLearningCatalog? sharedSkills = null)
        => RunConfigured(towns,pvf,characters,items,[(0,30)],sharedQuests,sharedSkills);

    static void RunConfigured(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items,IEnumerable<(byte Job,int Through)> cases,
        QuestCatalog? sharedQuests, SkillLearningCatalog? sharedSkills)
    {
        int checks = Count;
        var quests = sharedQuests ?? global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf); var skills = sharedSkills ?? global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, skills); var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(pvf);
        var dungeons = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf); var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
        var kills = global::DfoServer.Testing.SelfTestCatalogs.KillExperience(pvf); var clears = global::DfoServer.Testing.SelfTestCatalogs.ClearExperience(pvf);
        var drops = global::DfoServer.Testing.SelfTestCatalogs.MonsterDrops(pvf);
        foreach (var scenario in cases) RunCharacter(scenario.Job,scenario.Through);
        Console.WriteLine($"PASS {Count-checks} 全新角色连续主线、实际转职及分段重登断言（服务端，非实机）");

        void RunCharacter(byte job,int through)
        {
        using var f = new QuestSceneTests.Fixture(towns,characters,skills,growth,quests,false,1,[],job: job);
        uint lastDungeon = 3;
        bool retryChecked = false;
        var initial = f.Read();
        That(initial.Level == 1 && initial.CompletedQuests.Length == 0 && initial.ActiveMainQuests.Length == 0,
            "新角色没有预填任务、完成位或提升等级");
        ClearQuest(3145); Meeting(4873);
        ReachLevel(3); Accept(3146); Accept(3157); Gate(3);
        var parent = Enter(3,3146);
        That(f.Session.ObserveFormalPassiveTrigger(3157,new(0x10,0,0),new string('D',64),rooms)?.Commit is not null,
            "新角色父任务路线按匹配客户端右侧NPC对话33事件推进首个子任务，无普通死亡报告");
        Finish(3157); Accept(3054); Clear(parent); Finish(3054); Finish(3146); Return();
        ClearQuest(3147); ClearQuest(3148); Meeting(4875); ClearQuest(3149); ClearQuest(3150); ClearQuest(3151);
        Meeting(3152); Meeting(3153); Meeting(6929);
        var beforeTransfer = f.Read();
        var transfer = quests.All.Where(quest => quest.Condition == "[clear map]" && quest.IntData is [76196] && quest.MinimumLevel <= 20 &&
            QuestEligibility.Evaluate(beforeTransfer,quest,quests).Eligible).OrderBy(quest => quest.RewardType == "[grow type]" ? 0 : 1)
            .ThenBy(quest => quest.Rewards.FirstOrDefault().Value).First();
        ClearQuest(checked((ushort)transfer.Id));
        That(f.Read().GrowType == (job is 9 or 10 ? 0 : 1),$"职业{job}按实际定义转职；特殊职业保留本身职业状态，无GM重置");
        Meeting(3155); Range(3156,new(38,3,600,250)); Cinema(3158,new(39,0,300,250));
        Accept(3159); Condition(3159); Finish(3159);
        Range(3160,new(40,0,2300,250)); Meeting(3161); Range(3162,new(40,3,600,250));
        ClearQuest(3163); ClearQuest(3164); Meeting(3165); Meeting(3166); Meeting(3167);
        ClearQuest(3168); ClearQuest(3169); ClearQuest(3170); ClearQuest(3171); Meeting(3172);
        Accept(3173); Npc(29); Condition(3173,0x20); Finish(3173);
        That(f.Read().Inventory.Items.All(item => item.Core.ItemId is not (10164787 or 10164789)),
            "两条前置任务实际赠予的材料交付后消耗，无测试注入材料");
        Meeting(3174); Meeting(3175); Meeting(3176);
        // 完整入口共用job0前缀，但Lv20的全资产重登核对仍独立保留；后续从此实际角色继续。
        Reconnect(20);
        if (through >= 30)
        {
            ClearQuest(3177); ClearQuest(3178); ClearQuest(3179); Meeting(3180); Meeting(3181);
            Colorless(30); Accept(3182); Finish(3182);
            Meeting(3184); Range(3185,new(40,3,65,140)); Meeting(3186); Cinema(3187,new(39,1,450,250));
            ClearQuest(3188); ClearQuest(3189); ClearQuest(3190); ClearQuest(3191); Meeting(3192);
            Accept(3193); Npc(12); Condition(3193,0x20); Finish(3193);
            Meeting(3194); Meeting(3195); Meeting(3196); ClearQuest(3197); ClearQuest(3198);
            ClearQuest(3199); ClearQuest(3200); ClearQuest(3201); ClearQuest(3202);
            Hunt(3203); ClearQuest(3204); Hunt(3205); ClearQuest(3206); Meeting(3207); Meeting(3208);
            Colorless(100); Accept(3209); Finish(3209); Hunt(3210);
            Reconnect(through);
        }

        void Reconnect(int checkpoint)
        {
            var completed = f.Read();
            int firstDay = QuestCycle.Day(DateTimeOffset.UtcNow);
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            var restored = f.Read();
            int lastDay = QuestCycle.Day(DateTimeOffset.UtcNow);
            That(MatchesReconnect(completed,restored,firstDay,lastDay,towns.Room) &&
                QuestCompletionRepositoryTests.Same(f.Session.Selected!,restored) && restored.Level >= checkpoint && restored.ActiveMainQuests.Length == 0,
                $"Lv1–{checkpoint}整条主线与转职重登保留完整资产；游戏日{completed.QuestCycleDay}→{restored.QuestCycleDay}，revision{completed.Revision}→{restored.Revision}");
            Console.WriteLine($"职业{job}完成Lv1–{checkpoint}连续主线，转职路线{transfer.Id}，Lv{restored.Level}");
        }

        void Accept(ushort id)
        {
            var quest = quests.Get(id); ReachLevel(quest.MinimumLevel);
            var accepted = f.Session.ChangeQuest(id,QuestLifecycleAction.Accept,quests,items.Items,items.GoldLimits,items.EpicPieces);
            That(accepted?.Commit is not null,$"连续主线任务{id}正常接取：{accepted?.Reason}，Lv{f.Session.Selected!.Level}");
        }
        void Condition(ushort id,byte? channel = null)
        {
            var result = f.Session.ChangeQuest(id,QuestLifecycleAction.Condition,quests,items.Items,items.GoldLimits,items.EpicPieces,new(channel,0,0));
            That(result?.Commit is not null || result?.Outcome == QuestLifecycleOutcome.Unchanged,
                $"连续主线任务{id}当前场景推进：{result?.Reason}，{f.Session.Selected!.Position}");
        }
        void Finish(ushort id)
        {
            var result = f.Session.FinishQuest(id,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth);
            if (result.Outcome != QuestFinishOutcome.Committed)
            {
                var failed = f.Read();
                var preparation = QuestCompletionPlan.Prepare(failed,quests.Get(id),ushort.MaxValue,quests,parameters,
                    items.Items,items.GoldLimits,items.EpicPieces,growth,DateTimeOffset.UtcNow,f.Session.ReadClearedQuest(id)?.Clear);
                throw new InvalidOperationException($"连续主线{id}交付失败：{result.Outcome}/{preparation.Reason}，{failed.Position}");
            }
            var completed = f.Read();
            That(completed.CompletedQuests.Contains(id),$"连续主线任务{id}真实交付并写完成位");
            Console.WriteLine($"主线{id}完成，Lv{completed.Level}，EXP{completed.Learning.Experience}");
        }
        void Npc(int id)
        {
            // 导航使用会话已提交快照；交付、回滚与重登断言继续独立回读完整存档。
            var paths = f.TravelPaths(towns);
            var current = f.Session.Selected!;
            var done = QuestEligibility.Completed(current);
            var area = paths.Keys.Select(key => towns.Areas[key]).Where(area => area.Map(done).Npcs.Any(npc => npc.Id == id))
                .OrderBy(area => area.Town == current.Position.Town ? 0 : 1).ThenBy(area => area.Town).First();
            var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
            f.TravelTo(towns,new(area.Town,area.Area,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2))));
        }
        void Meeting(ushort id) { Accept(id); Npc(quests.Get(id).IntData[0]); Condition(id); Finish(id); }
        void Range(ushort id,Location position)
        {
            Accept(id); f.TravelTo(towns,position); Condition(id);
            Finish(id);
        }
        void Cinema(ushort id,Location position) { Accept(id); f.TravelTo(towns,position); Condition(id); Finish(id); }
        void Colorless(int count)
        {
            var disjoint = new InventoryDisjointService(items.Items,items.Disjoint,items.GoldLimits,items.EpicPieces,new Random(41));
            int attempts = 0;
            while (true)
            {
                var current = f.Session.Selected!;
                if (CurrencyWallet.Get(current.Inventory,CurrencyKind.ClearCube) >= count) break;
                That(++attempts <= 60,"实际任务装备分解材料在有界次数内完成");
                var equipment = current.Inventory.Items.Where(item => item.Position.List == InventoryListType.Main && item.Core.Kind == ItemKind.Equipment &&
                    !items.Items.Get(item.Core.ItemId).DisjointForbidden && !items.Items.Get(item.Core.ItemId).AttachType.Contains("[trade delete]",StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(item => items.Items.Get(item.Core.ItemId).DisjointValue).FirstOrDefault();
                if (equipment is null) throw new InvalidOperationException("正常主线实际装备不足以分解任务无色小晶块");
                That(f.Session.MutateInventory(character => disjoint.Disjoint(character,new(equipment.Position,-1,0),out _)).Accepted,
                    "任务材料由已领取装备的实际分解产物提供，不注入材料或完成位");
            }
        }
        void Hunt(ushort id)
        {
            Accept(id); uint dungeon = checked((uint)quests.Get(id).DungeonIndex!.Value);
            var before = f.Read();
            That(f.Session.FinishQuest(id,ushort.MaxValue,quests,parameters,items.Items,items.GoldLimits,items.EpicPieces,growth).Outcome == QuestFinishOutcome.Rejected &&
                QuestCompletionRepositoryTests.Same(before,f.Read()),"猎杀任务接取后不能绕过实际目标事件直接交付");
            int runs = 0;
            while (f.Read().ActiveMainQuests.Single(row => row.QuestId == id).ConditionState != 0)
            {
                That(++runs <= 12,"猎杀主线在有界实际副本循环中完成"); Gate(dungeon); Clear(Enter(dungeon,id));
                if (f.Read().ActiveMainQuests.Single(row => row.QuestId == id).ConditionState == 0 &&
                    QuestHuntProgress.TryTargets(quests.Get(id),out var targets) && targets.Any(target => target.EnemyType == 3))
                {
                    Finish(id); Return(); lastDungeon = dungeon; return;
                }
                Return();
            }
            if (quests.Get(id).CompleteNpc > 0) Npc(quests.Get(id).CompleteNpc);
            Finish(id); lastDungeon = dungeon;
        }
        void Gate(uint dungeon)
        {
            var paths = f.TravelPaths(towns);
            var current = f.Session.Selected!;
            var done = QuestEligibility.Completed(current);
            var area = paths.Keys.Select(key => towns.Areas[key]).Where(area => area.DungeonWorldMap is { } world &&
                towns.DungeonMaps[world].Error is null && towns.DungeonMaps[world].Options.Any(option => option.DungeonId == dungeon))
                .OrderBy(area => area.Town == current.Position.Town ? 0 : 1).ThenBy(area => area.Town).First();
            var map = area.Map(done);
            foreach (var gate in map.DungeonGates.Where(row => row.Bounds.Door && row.Quests.All(done.Contains) && row.Conditions.Length == 0).Select(row => row.Bounds))
            foreach (var walk in map.Bounds.Where(row => row.Quests.All(done.Contains) && row.Conditions.Length == 0).Select(row => row.Bounds))
            {
                int left = Math.Max(gate.X,walk.X), right = Math.Min(gate.X+gate.Width,walk.X+walk.Width);
                int top = Math.Max(gate.Y,walk.Y), bottom = Math.Min(gate.Y+gate.Height,walk.Y+walk.Height);
                if (left > right || top > bottom) continue;
                f.TravelTo(towns,new(area.Town,area.Area,checked((ushort)((left+right)/2)),checked((ushort)((top+bottom)/2))));
                return;
            }
            throw new InvalidOperationException($"副本{dungeon}入口与城镇{area.Town}/{area.Area}可行走区域没有交集");
        }
        FormalDungeonReceipt Enter(uint dungeon,uint quest)
        {
            var result = f.Session.EnterFormalDungeon(dungeon,quest,0,dungeons,rooms,drops,items.Items,pieces: items.EpicPieces);
            That(result is not null,$"连续主线当前Lv{f.Session.Selected!.Level}真实入场{dungeon}/{quest}，{f.Session.Selected.Position}");
            return f.Session.CompleteFormalDungeonLoading()!;
        }
        void ClearQuest(ushort id)
        {
            Accept(id); uint dungeon = checked((uint)quests.Get(id).DungeonIndex!.Value);
            Gate(dungeon); Clear(Enter(dungeon,id)); Finish(id); Return(); lastDungeon = dungeon;
        }
        void ReachLevel(int level)
        {
            int runs = 0;
            while (f.Session.Selected!.Level < level)
            {
                That(++runs <= 100,"普通副本的真实经验在有界次数内满足后继等级，不注入经验");
                Gate(lastDungeon);
                var current = f.Session.Selected!;
                var gate = towns.Areas[(current.Position.Town,current.Position.Area)];
                var done = QuestEligibility.Completed(current);
                var routine = towns.DungeonMaps[gate.DungeonWorldMap!.Value].Options.Where(option => option.QuestId == -1 ||
                    (option.InProgress ? current.ActiveMainQuests.Any(active => active.QuestId == option.QuestId) : done.Contains(option.QuestId)))
                    .Select(option => checked((uint)option.DungeonId)).Distinct().OrderBy(id => id == lastDungeon ? 0 : 1)
                    .First(id => DungeonEntryPlan.TryCreate(dungeons,current,id,0,0,out _,out _,quests: quests));
                var settled = Clear(Enter(routine,0));
                if (!retryChecked)
                {
                    var before = f.Read(); var selected = f.Session.Selected;
                    That(f.Session.RetryFormalDungeon(settled with { Character = before },dungeons,rooms,drops,items.Items,items.EpicPieces) is null &&
                        f.Session.CanPublishFormalDungeon(settled),"过期角色快照的重试保持原结算实例");
                    f.Sql("CREATE TRIGGER abort_quest_retry AFTER UPDATE ON character_dungeon_state BEGIN SELECT RAISE(ABORT,'retry failed'); END;");
                    Reject<SqliteException>(() => f.Session.RetryFormalDungeon(settled,dungeons,rooms,drops,items.Items,items.EpicPieces),
                        "重试预留身份的SQL失败不能提前销毁结算实例");
                    f.Sql("DROP TRIGGER abort_quest_retry");
                    That(f.Session.Phase == SessionPhase.Settlement && ReferenceEquals(selected,f.Session.Selected) &&
                        f.Session.CanPublishFormalDungeon(settled) && QuestCompletionRepositoryTests.Same(before,f.Read()),
                        "失败重试同时保留在线阶段、旧Run及数据库完整资产");
                    var retry = f.Session.RetryFormalDungeon(settled,dungeons,rooms,drops,items.Items,items.EpicPieces);
                    That(retry is not null && retry.Entry.QuestId == 0 && retry.Snapshot.Identity.InstanceId != settled.Snapshot.Identity.InstanceId &&
                        f.Read().DungeonState!.LastAttempt == before.DungeonState!.LastAttempt+1 && !f.Session.CanPublishFormalDungeon(settled),
                        "成功重试提交独立尝试并使旧结算身份失效");
                    Clear(f.Session.CompleteFormalDungeonLoading()!); retryChecked = true;
                }
                Return();
            }
        }
        void Return() => That(f.Session.ReturnFromFormalDungeon() is not null,"连续主线清图后正常回城");
        FormalDungeonReceipt Clear(FormalDungeonReceipt receipt)
        {
            var entry = receipt.Entry; var path = QuestStoryRouteTests.Path(entry.Maze.StaticTopology,entry.StartMap.Position,entry.BossMap.Position);
            foreach (var point in path)
            {
                if (point == entry.BossMap.Position && entry.BossMap.Kind == "layered")
                {
                    That(f.Session.MoveFormalDungeon(checked((byte)point.X),checked((byte)point.Y),1) is not null,"剧情终点按声明换层");
                    receipt = f.Session.CompleteFormalDungeonLoading()!;
                }
                if (through >= 30)
                    foreach (var active in f.Session.Selected!.ActiveMainQuests)
                        if (QuestHuntProgress.TryTargets(quests.Get(active.QuestId),out var targets))
                            for (int index = 0; index < targets.Length; index++)
                                if (targets[index].EnemyType == 3 && targets[index].DungeonId == entry.DungeonId &&
                                    rooms.Get(receipt.Snapshot.CurrentMap).PassiveObjects.Any(row => row.ObjectId == targets[index].MonsterId))
                                    That(f.Session.ObserveFormalPassiveTrigger(active.QuestId,new((byte)(0x10 << index),0,0),
                                        new string('E',64),rooms)?.Commit is not null,"真实任务所在MAP的目标被动演出事件推进猎杀，非普通战斗死亡");
                foreach (var monster in receipt.Monsters.Monsters.Where(row => row.CanReportDeath).OrderBy(row => row.IsBoss).ToArray())
                {
                    FormalDungeonDeathResult result;
                    if (monster.IsScriptedBoss || receipt.Snapshot.CurrentMap.Kind == "layered")
                    {
                        byte[] body = new byte[74]; BinaryPrimitives.WriteUInt32LittleEndian(body,monster.ObjectKey);
                        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4),12);
                        body[31] = 0x22;
                        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(70),ClientBodyChecksum.Compute(body.AsSpan(0,70)));
                        That(DungeonRequests.TryParseScriptedDeath(body,out var parsed) && parsed!.ChecksumMatches,"连续主线使用实际脚本死亡请求形状");
                        result = f.Session.ObserveFormalDungeonScriptedDeath(new(monster.ObjectKey,parsed!.RewriteFlag,true,
                            Convert.ToHexString(SHA256.HashData(body))),kills,growth);
                    }
                    else result = f.Session.ObserveFormalDungeonDeath(FormalDungeonSessionTests.Observation(receipt,monster),kills,growth);
                    That(result.Outcome == DungeonRunOutcome.Accepted,$"连续主线地图{receipt.Snapshot.CurrentMap.Id}真实死亡{monster.MonsterId}/{result.Outcome}");
                    if (through >= 30 && result.Drop is { } drop)
                        That(f.Session.PickFormalDungeonItem(drop.Reservation.GroundKey,items.Items,items.EpicPieces).Outcome == DungeonRunOutcome.Accepted,
                            "后续主线材料和装备从实例实际掉落拾取，保留真实UID与原子拾取回执");
                }
                if (point == entry.BossMap.Position) break;
                var next = path[Array.IndexOf(path,point)+1];
                That(f.Session.MoveFormalDungeon(checked((byte)next.X),checked((byte)next.Y)) is not null,"连续主线通过真实连接换房");
                receipt = f.Session.CompleteFormalDungeonLoading()!;
            }
            That(f.Session.Phase == SessionPhase.Settlement,"真实Boss死亡进入结算阶段");
            var settled = f.Session.SettleFormalDungeon(f.Session.Selected!.Id,99,99,[1,2,3,4],dungeons,clears,growth);
            That(settled.Outcome == DungeonRunOutcome.Accepted,"真实清图后正常提交通关经验，不用客户端报告创建死亡");
            return settled.Receipt!;
        }
        }
    }
}
