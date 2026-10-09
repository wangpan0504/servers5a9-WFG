using static DfoServer.Testing.TestCheck;
using System.Text.Json;
using System.Security.Cryptography;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Network.Parsers;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using DfoServer.Infrastructure;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using System.Collections.Immutable;

namespace DfoServer;

internal static class FormalDungeonSessionTests
{
    // 复用runner已打开的匹配PVF；观察明确为合成，不代表实机击杀或通知通过。
    public static Task RunAsync(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources itemResources)
    {
        int before = TestCheck.Count;
        var learning = SkillLearningCatalog.Load(pvf);
        var growth = CharacterGrowthCatalog.Load(pvf);
        var kills = DungeonKillExperienceCatalog.Load(pvf);
        var catalog = DungeonCatalog.Load(pvf);
        var rooms = DungeonRoomCatalog.Load(pvf);
        var quests = DfoServer.GameWorld.Quests.QuestCatalog.Load(pvf);
        Lifecycle(0);
        Lifecycle(14);
        OrdinaryJobs();
        OtherAdmissions();
        RejectedEntry();
        Reservation();
        UnknownOwner();
        Invalidation();
        RecordedNormalStatistics();
        FrozenLootTransaction();
        Console.WriteLine($"PASS {TestCheck.Count - before} 正式Session文件库、六房合成击杀、身份及回滚断言（非实机）");
        return Task.CompletedTask;

        void Lifecycle(byte job)
        {
            using var f = new Fixture("FormalJ" + job, towns, characters, learning, quests, job: job);
            Check(f.Created.DungeonState == CharacterDungeonState.NewOrdinary && f.Created.Learning.Initialized &&
                f.Created.Learning.Trees.Count == 2, "正常建角明确初始化副本owner与两树绑定");
            ulong startExperience = growth.GetCumulativeExperienceBoundary(1) - 104;
            f.Sql($"UPDATE characters SET experience={startExperience} WHERE id={f.Created.Id}");
            f.Sql("UPDATE character_dungeon_state SET capsule_experience=11,proficiency_level=2,proficiency_experience=33");
            f.SelectEntry();
            var opening = f.Session.BeginDungeonSelection();
            var initial = f.Session.Selected!;
            Check(f.Session.ReadClearedQuest(3145) is null, "未清图不能因客户端报告获取达成回执");
            var entry = Enter(f);
            var state = entry.Character.DungeonState!;
            Check(state.LastAttempt == 1 && state.NextObjectKey > 256 && entry.Character.Revision == initial.Revision + 1 &&
                ReferenceEquals(entry.Character, f.Session.Selected) && Same(entry.Character, f.Read()),
                "正式进场预留完整路线身份并安装实际提交角色");
            Check(f.Session.CurrentDungeonSelection is null, "正式入场成功消费原选图上下文");
            var preservedState = state;
            Check(f.Session.Phase == SessionPhase.Loading && f.Session.CanPublishFormalDungeon(entry) &&
                f.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms) is null, "Loading门禁防止重复预留");
            var notLoaded = Observation(entry, entry.Monsters.Monsters[0]);
            Check(f.Session.ObserveFormalDungeonDeath(notLoaded, kills, growth).Outcome == DungeonRunOutcome.Rejected && f.Count() == 0,
                "正式加载前报告不能签发或落账");
            var receipt = f.Session.CompleteFormalDungeonLoading() ?? throw new InvalidOperationException("FAIL 正式加载不能完成");
            Check(f.Session.Phase == SessionPhase.Dungeon && f.Session.CompleteFormalDungeonLoading() is null,
                "真实Session加载一次进入Dungeon，重复不初始化");
            Check(!f.Session.CanPublishFormalDungeon(entry), "加载epoch推进后旧初始化回执不能发布");
            var maps = new[] { 76121, 76122, 76123, 76124, 76125, 76126 };
            var targets = new (byte X, byte Y)[] { (1, 1), (2, 1), (2, 0), (3, 0), (4, 0) };
            var allKeys = new HashSet<ushort>();
            var allSpawnKeys = new HashSet<ushort>();
            ulong total = 0;
            NativeDungeonDeathObservation? oldRoomObservation = null;
            FormalDungeonReceipt? finalReceipt = null;
            for (int room = 0; room < maps.Length; room++)
            {
                Check(receipt!.Snapshot.CurrentMap.Id == maps[room] && receipt.Monsters.Monsters.Count > 0,
                    "真实3145六房逐房保留资源行与当前地图");
                if (oldRoomObservation is not null)
                    Check(f.Session.ObserveFormalDungeonDeath(oldRoomObservation, kills, growth).Outcome == DungeonRunOutcome.Rejected,
                        "新房拒绝旧房key与缓存报告");
                Check(f.Session.MoveFormalDungeon(0, 0) is null, "未清房不能过房");
                foreach (var monster in receipt.Monsters.Monsters)
                {
                    Check(allSpawnKeys.Add(monster.ObjectKey) && monster.ObjectKey != f.Created.Id &&
                        monster.ObjectKey >= 256 && monster.ObjectKey < state.NextObjectKey,
                        "所有真实房间怪key在进场预留范围内且全程唯一");
                    Check(allKeys.Add(monster.ObjectKey), "敌方死亡对象key全实例唯一");
                    var observation = Observation(receipt, monster);
                    if (!monster.IsBlocking)
                    {
                        long beforeFriendly = f.Count();
                        Check(f.Session.ObserveFormalDungeonDeath(observation, kills, growth).Outcome == DungeonRunOutcome.Rejected &&
                            f.Count() == beforeFriendly && f.Session.DungeonSnapshot!.LivingMonsters.Contains(monster.ObjectKey),
                            "任务友方保留生成对象，但没有击杀事实、经验或掉落资格");
                        continue;
                    }
                    if (monster.MonsterId == 107000903)
                    {
                        // 本版Boss实收：改写仅影响prefix，实际participant累计及创建HP仍独立核验。
                        byte[] body = Convert.FromHexString("1B0100000100000000000000000000000000CA220000000000001D0000000111010100010A0000000000000C005E0403010000000003360103000000B2080000000000003F03EF00A4030301150012002F00000020B11FCC");
                        Check(DungeonRequests.TryParseDeath(body, out var parsedBoss) && parsedBoss!.ObjectKey == monster.ObjectKey &&
                            parsedBoss.Reporter == receipt.Character.Id && parsedBoss.SinglePlayerStatistics is { ChecksumMatches: true },
                            "匹配Boss实收88B绑定当前模板key/本人，检查值独立匹配");
                        var boss = parsedBoss!.SinglePlayerStatistics!;
                        observation = new(parsedBoss.ObjectKey, parsedBoss.Reporter, boss.Participant.Kind, boss.Participant.ObjectKey,
                            boss.Participant.CumulativeHpDecrease, boss.Participant.PositiveHpDecreaseCount, boss.RewriteFlag,
                            boss.SpawnLevel, boss.SpawnHpSnapshot, boss.Unknown18, boss.Unknown26, boss.ChecksumMatches,
                            Convert.ToHexString(SHA256.HashData(body)));
                    }
                    var selectedBefore = f.Session.Selected!;
                    long count = f.Count();
                    if (job == 0 && monster.MonsterId == 107000903)
                    {
                        Guid acceptance = selectedBefore.ActiveMainQuests.Single().AcceptanceId;
                        f.Sql($"UPDATE character_active_main_quests SET acceptance_id='{Guid.NewGuid():N}'");
                        Reject<InvalidOperationException>(() => f.Session.ObserveFormalDungeonDeath(observation, kills, growth),
                            "同revision重新接取身份改变不能被旧实例清图推进");
                        Check(ReferenceEquals(selectedBefore, f.Session.Selected) && f.Count() == count &&
                            f.Read().ActiveMainQuests.Single().ConditionState == 1 &&
                            Convert.ToInt64(f.Query("SELECT count(*) FROM character_quest_clear_receipts")) == 0,
                            "旧接取拒绝不写经验SP或清图receipt");
                        f.Sql($"UPDATE character_active_main_quests SET acceptance_id='{acceptance:N}'");
                        f.Sql("CREATE TRIGGER clear_fail AFTER INSERT ON character_quest_clear_receipts BEGIN SELECT RAISE(ABORT,'late clear failure'); END");
                        Reject<SqliteException>(() => f.Session.ObserveFormalDungeonDeath(observation, kills, growth),
                            "末次死亡清图账本失败回滚经验、SP、任务条件和revision");
                        Check(ReferenceEquals(selectedBefore, f.Session.Selected) && Same(f.Read(), selectedBefore) &&
                            f.Count() == count && Convert.ToInt64(f.Query("SELECT count(*) FROM character_quest_clear_receipts")) == 0 &&
                            f.Read().ActiveMainQuests.Single().ConditionState == 1 && f.Session.Phase == SessionPhase.Dungeon,
                            "清图失败不提前置0或进入Settlement，原候选可精确重试");
                        f.Sql("DROP TRIGGER clear_fail");
                    }
                    if (count == 0)
                    {
                        Check(f.Session.ObserveFormalDungeonDeath(observation with { ChecksumMatches = false }, kills, growth).Outcome == DungeonRunOutcome.Rejected,
                            "Native正式入口拒绝检查失败，不借诊断签发");
                        f.Sql("CREATE TRIGGER formal_fail AFTER INSERT ON character_dungeon_growth_receipts BEGIN SELECT RAISE(ABORT,'formal late receipt failure'); END");
                        Reject<SqliteException>(() => f.Session.ObserveFormalDungeonDeath(observation, kills, growth), "晚期receipt触发器引起真实SQL整批回滚");
                        Check(ReferenceEquals(selectedBefore, f.Session.Selected) && Same(f.Read(), selectedBefore) &&
                            f.Count() == count && f.Session.DungeonSnapshot!.LivingMonsters.Contains(monster.ObjectKey),
                            "SQL失败保留在线角色、原死亡候选和活怪，不加经验SP或receipt");
                        f.Sql("DROP TRIGGER formal_fail");
                    }
                    var result = f.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                    Check(result.Outcome == DungeonRunOutcome.Accepted && result.Receipt is not null && result.MonsterKey == monster.ObjectKey,
                        "Session私有Native权威经过Prepare/SQL/Confirm发布实际回执");
                    finalReceipt = result.Receipt!;
                    uint expectedGain = monster.CandidateLevel == 5 ? 137u : selectedBefore.Level < 3 ? 104u : 93u;
                    Check(result.Reward!.Amount == expectedGain, "六房逐次使用击杀前等级及真实怪物等级计算经验");
                    total += expectedGain;
                    Check(f.Count() == count + 1 && Same(finalReceipt.Character, f.Read()) &&
                        finalReceipt.Character.DungeonState == preservedState && f.Session.CanPublishFormalDungeon(finalReceipt) &&
                        !f.Session.CanPublishFormalDungeon(receipt), "成长只落一条并保留副本附属owner，旧角色回执失效");
                    if (count == 0)
                        Check(Convert.ToInt64(f.Query("SELECT combat_sequence FROM character_dungeon_growth_receipts")) == 1 &&
                            finalReceipt.Character.Level == 2, "回滚重试保留第一战斗序号并完成真实等级边界升级");
                    var after = f.Session.Selected!;
                    Check(f.Session.ObserveFormalDungeonDeath(observation, kills, growth).Outcome == DungeonRunOutcome.Duplicate &&
                        ReferenceEquals(after, f.Session.Selected) && f.Count() == count + 1, "原生精确重复不重新发奖");
                    Check(f.Session.ObserveFormalDungeonDeath(observation with { BodySha256 = new string('B', 64) }, kills, growth).Outcome == DungeonRunOutcome.Rejected,
                        "同key变更正文指纹拒绝");
                    oldRoomObservation = observation;
                    if (room == maps.Length - 1 && result.ClearFact is not null)
                        Check(result.ClearFact.ConfirmedDeathEvents.Count == f.Count(), "Boss清图事实只包含已提交敌对击杀，不伪造任务友方死亡");
                }
                if (room < targets.Length)
                {
                    var next = f.Session.MoveFormalDungeon(targets[room].X, targets[room].Y);
                    Check(next is not null && f.Session.Phase == SessionPhase.Loading && !f.Session.CanPublishFormalDungeon(finalReceipt!),
                        "升级后清房连续过房，旧房回执失效");
                    Check(f.Session.ObserveFormalDungeonDeath(oldRoomObservation!, kills, growth).Outcome == DungeonRunOutcome.Rejected,
                        "新房Loading不处理旧报告");
                    receipt = f.Session.CompleteFormalDungeonLoading() ?? throw new InvalidOperationException("FAIL 新房加载不能完成");
                }
            }
            var final = f.Session.Selected!;
            Check(f.Session.Phase == SessionPhase.Settlement && final.Learning.Experience == startExperience + total &&
                (uint)allSpawnKeys.Count == preservedState.NextObjectKey - initial.DungeonState!.NextObjectKey &&
                final.Learning.Trees.All(tree => tree.Sp == initial.Learning.Trees.Single(t => t.Context == tree.Context).Sp + growth.GetSpDelta(1, final.Level)),
                "六房敌方实际等级经验及两树SP一致，剧情actor不要求死亡，Boss进入Settlement");
            Check(final.ActiveMainQuests.Single().ConditionState == 0 &&
                final.ActiveMainQuests.Single().AcceptanceId == initial.ActiveMainQuests.Single().AcceptanceId &&
                final.CompletedQuests.Length == 0 && Convert.ToInt64(f.Query("SELECT count(*) FROM character_quest_clear_receipts")) == 1 &&
                f.Query("SELECT acceptance_id FROM character_quest_clear_receipts") as string == final.ActiveMainQuests.Single().AcceptanceId.ToString("N"),
                "通关只达成本次接取条件，清图receipt唯一，未提前领奖或完成表");
            var clearReceipt = f.Session.ReadClearedQuest(3145);
            Check(clearReceipt is not null && f.Session.CanPublishQuestCondition(clearReceipt) &&
                clearReceipt.Clear.InstanceId == f.Session.DungeonSnapshot!.Identity.InstanceId &&
                clearReceipt.Clear.Revision == final.Revision && f.Session.ReadClearedQuest(3146) is null,
                "Settlement任务查询必须绑定同事务持久clear与实际本实例身份");
            Check(f.Session.ReturnFromFormalDungeon()?.Character == final && f.Session.Phase == SessionPhase.Town && f.Session.DungeonSnapshot is null &&
                !f.Session.CanPublishFormalDungeon(finalReceipt!), "回城清私有run，保留全部已提交成长");
            Check(!f.Session.CanPublishQuestCondition(clearReceipt!) && f.Session.ReadClearedQuest(3145) is { DungeonIdentity: null },
                "旧结算任务回执回城后失效，城镇从同一次接取持久清图恢复");
            Check(f.Session.ObserveFormalDungeonDeath(oldRoomObservation!, kills, growth).Outcome == DungeonRunOutcome.Rejected,
                "已回城拒绝旧死亡");
            Check(f.Session.Move(new(1084, 249, 5, 0, 0, 0)), "正式退出后可合法移动到新的入口位置");
            var moved = f.Session.Selected!;
            var reopenedSelection = f.Session.BeginDungeonSelection();
            Check(reopenedSelection.SelectionId > opening.SelectionId &&
                f.Session.CurrentDungeonSelection?.OriginPosition == moved.Position &&
                f.Session.CanPublishDungeonSelection(reopenedSelection) && !f.Session.CanPublishDungeonSelection(opening),
                "正式退出后再选图使用新的SelectionId和实际OriginPosition，不复用旧返回身份");
            Check(f.Session.ReturnFromDungeonSelection() is not null && f.Session.Phase == SessionPhase.Town &&
                f.Session.CurrentDungeonSelection is null && f.Session.Selected!.DungeonState == final.DungeonState &&
                f.Session.Selected.Learning.Experience == final.Learning.Experience && Same(f.Session.Selected, f.Read()),
                "重新选图仍可132返回且保留成长、attempt和对象key高水位");
            f.Session.ReturnToRoster();
            var reopened = new GameSession(new CharacterRepository(f.Path), towns, f.Owner);
            reopened.Select(f.Created.Slot);
            reopened.MarkReady();
            var reselected = reopened.Selected!;
            Check(Same(final with { Revision = reselected.Revision, Position = reselected.Position, Return = reselected.Return }, reselected),
                "仓储重开/重选只执行合法回房保存，成长与副本高水位恢复（非停服重启）");
            MoveToEntry(reopened);
            var second = reopened.EnterFormalDungeon(3, 3145, 0, catalog, rooms)!;
            Check(second is not null && second.Snapshot.Identity.TaskAttempt == 2 &&
                second.Snapshot.Identity.InstanceId != entry.Snapshot.Identity.InstanceId &&
                second.Monsters.Monsters.All(monster => monster.ObjectKey >= state.NextObjectKey),
                "重开再进场不重用旧attempt、instance或怪key");
            Check(new CharacterRepository(f.Path).LoadQuestClear(f.Owner, reopened.Selected!, 3145) == clearReceipt!.Clear,
                "真实清图允许再次挑战，同次接取仍只保留首个持久清图凭据");
        }

        void OrdinaryJobs()
        {
            foreach (byte job in new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 11, 12, 13, 15 })
            {
                using var f = new Fixture("FormalJ" + job, towns, characters, learning, quests, job: job);
                Check(f.Created.Job == job && f.Created.GrowType == 0 && f.Created.Learning.Initialized &&
                    f.Created.Learning.Trees.Count == 2 && f.Created.DungeonState == CharacterDungeonState.NewOrdinary,
                    $"普通职业{job}真实建角初始化双树与副本owner");
                f.SelectEntry();
                var selection = f.Session.BeginDungeonSelection();
                Check(f.Session.Phase == SessionPhase.MapSelect && f.Session.CanPublishDungeonSelection(selection),
                    $"普通职业{job}经合法旅行进入选图阶段");
                var before = f.Session.Selected!;
                var entry = Enter(f);
                var loaded = f.Session.CompleteFormalDungeonLoading()!;
                var monster = loaded.Monsters.Monsters[0];
                var death = f.Session.ObserveFormalDungeonDeath(Observation(loaded, monster), kills, growth);
                Check(death.Outcome == DungeonRunOutcome.Accepted && f.Count() == 1 &&
                    f.Session.Selected!.Learning.Experience == before.Learning.Experience + 104 &&
                    Same(f.Session.Selected, f.Read()) && entry.Character.DungeonState!.LastAttempt == 1,
                    $"普通职业{job}正式入场加载及一次死亡成长成功");
                var final = f.Session.Selected;
                Check(f.Session.ReturnFromFormalDungeon()?.Character == final && f.Session.Phase == SessionPhase.Town &&
                    f.Session.DungeonSnapshot is null && Same(final!, f.Read()),
                    $"普通职业{job}退出保留已提交经验与双树，不留下实例");
            }
        }

        void OtherAdmissions()
        {
            foreach (var identity in new[] { (Job: (byte)9, Grow: 0), (Job: (byte)10, Grow: 0), (Job: (byte)14, Grow: 1), (Job: (byte)0, Grow: 37) })
            {
                using var f = new Fixture("EntryJ" + identity.Job, towns, characters, learning, quests, job: identity.Job);
                if (identity.Grow != 0)
                    f.Session.UpdateProgress(new(CharacterProgressField.Grow, f.Owner.Account, f.Created.Name, identity.Grow, 0));
                f.SelectEntry();
                f.Session.BeginDungeonSelection();
                var before = f.Session.Selected!;
                var entry = Enter(f);
                Check(f.Session.CompleteFormalDungeonLoading() is not null && f.Session.Phase == SessionPhase.Dungeon &&
                    entry.Character.Job == identity.Job && entry.Character.GrowType == identity.Grow &&
                    entry.Character.DungeonState!.LastAttempt == 1 && entry.Character.Revision == before.Revision + 1 &&
                    Same(before with { Revision = entry.Character.Revision, DungeonState = entry.Character.DungeonState }, entry.Character) &&
                    Same(entry.Character, f.Read()),
                    $"特殊职业/转职{identity.Job}/{identity.Grow}允许真实准入加载，保留原技能状态而不伪造初始化");
                Check(f.Session.ReturnFromFormalDungeon() is not null && f.Session.Phase == SessionPhase.Town &&
                    f.Session.DungeonSnapshot is null && f.Count() == 0 && Same(f.Session.Selected!, f.Read()),
                    "特殊职业/转职可退出且无击杀成长写入；其成长支持另行处理");
            }
        }

        void RejectedEntry()
        {
            using var ordinary = new Fixture("RejectRoute", towns, characters, learning, quests, job: 14);
            ordinary.SelectEntry();
            var current = ordinary.Session.BeginDungeonSelection();
            var selected = ordinary.Session.Selected!;
            foreach (var request in new[] { (Dungeon: 4u, Task: 3145u, Difficulty: (byte)0), (Dungeon: 3u, Task: 3146u, Difficulty: (byte)0),
                (Dungeon: 3u, Task: 3145u, Difficulty: (byte)1) })
                Check(ordinary.Session.EnterFormalDungeon(request.Dungeon, request.Task, request.Difficulty, catalog, rooms) is null &&
                    ReferenceEquals(selected, ordinary.Session.Selected) && Same(selected, ordinary.Read()) &&
                    ordinary.Session.CanPublishDungeonSelection(current) && ordinary.Session.DungeonSnapshot is null,
                    "放开普通职业仍保留副本/任务/难度资格，拒绝不预留attempt或key");
            foreach (bool missing in new[] { false, true })
            {
                using var f = new Fixture(missing ? "TaskMissing" : "TaskReached", towns, characters, learning, quests, job: 14);
                f.Sql(missing ? "DELETE FROM character_active_main_quests" : "UPDATE character_active_main_quests SET condition_state=0");
                f.SelectEntry();
                var selection = f.Session.BeginDungeonSelection();
                var context = f.Session.CurrentDungeonSelection;
                var before = f.Session.Selected!;
                Check(f.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms) is null &&
                    ReferenceEquals(before, f.Session.Selected) && Same(before, f.Read()) &&
                    ReferenceEquals(context, f.Session.CurrentDungeonSelection) && f.Session.CanPublishDungeonSelection(selection) &&
                    f.Session.DungeonSnapshot is null, "真实任务缺失或已达成仍拒绝，不改attempt/key/revision和返回身份");
            }
        }

        void Reservation()
        {
            using (var f = new Fixture("CreateAtomic", towns, characters, learning, quests))
            {
                var original = f.Read();
                var itemCount = f.Query("SELECT count(*) FROM character_inventory_items");
                f.Sql("CREATE TRIGGER create_owner_fail AFTER INSERT ON character_dungeon_state BEGIN SELECT RAISE(ABORT,'new owner failure'); END");
                Reject<SqliteException>(() => f.Session.CreateCharacter("OwnerFail", characters.Get(0), learning),
                    "新建owner写入失败回滚正常建角事务");
                Check(Convert.ToInt64(f.Query("SELECT count(*) FROM characters")) == 1 &&
                    Convert.ToInt64(f.Query("SELECT count(*) FROM character_dungeon_state")) == 1 &&
                    Equals(itemCount, f.Query("SELECT count(*) FROM character_inventory_items")) && Same(original, f.Read()),
                    "新建失败不留角色/owner/初始物品，不改既有角色");
                f.Sql("DROP TRIGGER create_owner_fail");
            }
            foreach (string table in new[] { "characters", "character_dungeon_state" })
            {
                using var f = new Fixture(table == "characters" ? "ReserveRole" : "ReserveOwner", towns, characters, learning, quests);
                f.SelectEntry();
                var selected = f.Session.Selected!;
                f.Sql($"CREATE TRIGGER reserve_fail AFTER UPDATE ON {table} BEGIN SELECT RAISE(ABORT,'reservation failure'); END");
                Reject<SqliteException>(() => Enter(f), "预留中途更新失败触发文件库回滚");
                Check(ReferenceEquals(selected, f.Session.Selected) && Same(selected, f.Read()) && f.Session.DungeonSnapshot is null &&
                    f.Session.Phase == SessionPhase.Town, "预留失败不留下角色revision、owner高水位或run");
                f.Sql("DROP TRIGGER reserve_fail");
                var success = Enter(f);
                Check(success.Character.DungeonState!.LastAttempt == 1, "解除故障后仅预留一次");
                Reject<InvalidOperationException>(() => f.Repository.ReserveDungeonAttempt(f.Owner, selected, success.Character.DungeonState.NextObjectKey + 1),
                    "旧revision及旧owner快照不能再次预留");
            }
            using (var f = new Fixture("ReserveCAS", towns, characters, learning, quests))
            {
                f.SelectEntry();
                var selected = f.Session.Selected!;
                f.Sql("UPDATE character_active_main_quests SET condition_state=0");
                Reject<InvalidOperationException>(() => f.Repository.ReserveDungeonAttempt(f.Owner, selected, 300),
                    "同revision任务前提改变也拒绝预留");
                Check(f.Read().DungeonState == selected.DungeonState && f.Read().Revision == selected.Revision,
                    "任务CAS拒绝不消费attempt或key");
            }
            using (var f = new Fixture("ReserveFull", towns, characters, learning, quests))
            {
                f.Sql("UPDATE character_dungeon_state SET next_object_key=65536");
                f.SelectEntry();
                var selected = f.Session.Selected!;
                Check(f.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms) is null && Same(selected, f.Read()),
                    "对象key耗尽拒绝完整路线，不回绕");
            }
            using (var f = new Fixture("ResAttempt", towns, characters, learning, quests))
            {
                f.Sql($"UPDATE character_dungeon_state SET last_attempt={long.MaxValue}");
                f.SelectEntry();
                var selected = f.Session.Selected!;
                Check(f.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms) is null && Same(selected, f.Read()),
                    "尝试号耗尽拒绝，不发生加法回绕");
            }
            using (var f = new Fixture("ReserveLast", towns, characters, learning, quests))
            {
                f.Sql("UPDATE character_dungeon_state SET next_object_key=65535");
                f.SelectEntry();
                var selected = f.Session.Selected!;
                Check(f.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms) is null,
                    "剩余单个key不能部分预留完整3145路线");
                Check(ReferenceEquals(selected, f.Session.Selected) && Same(selected, f.Read()) &&
                    f.Session.DungeonSnapshot is null && f.Session.Phase == SessionPhase.Town,
                    "锁外路线容量拒绝不留下角色revision、owner预留或run");
                var last = f.Repository.ReserveDungeonAttempt(f.Owner, selected, 65536);
                Check(last.DungeonState!.NextObjectKey == 65536 && last.DungeonState.LastAttempt == 1 && Same(last, f.Read()),
                    "仓储最后key区间可保存65536高水位哨兵");
                Reject<InvalidDataException>(() => f.Repository.ReserveDungeonAttempt(f.Owner, last, 1), "高水位不能回绕到1");
            }
        }

        void UnknownOwner()
        {
            using var f = new Fixture("LegacyFormal", towns, characters, learning, quests);
            var original = f.Read();
            DatabaseSchemaTests.RewindQuestSystemToV13(f.Path);
            f.Sql("DROP TABLE character_tp_sources; DROP TABLE character_skill_commands; DROP TABLE character_skill_combos; DROP TABLE character_skill_point_sources; DROP TABLE account_expert_contracts; DROP TABLE character_dungeon_settlement_receipts; DROP TABLE character_quest_completion_receipts; DROP TABLE character_quest_clear_receipts; DROP INDEX character_active_quest_acceptance; ALTER TABLE character_active_main_quests DROP COLUMN acceptance_id; DROP TABLE character_dungeon_pickup_receipts");
            f.Sql("DROP TABLE character_dungeon_loot_receipts");
            f.Sql("DROP TABLE character_dungeon_state");
            f.Sql("PRAGMA user_version=7");
            var legacy = new CharacterRepository(f.Path).LoadCharacter(f.Owner, f.Created.Slot);
            Check(legacy.DungeonState is null &&
                Same(original with { DungeonState = null, ActiveMainQuests = legacy.ActiveMainQuests, Learning = original.Learning with { BonusSp = null, BonusTp = null } }, legacy) &&
                original.ActiveMainQuests.Single().ConditionState == legacy.ActiveMainQuests.Single().ConditionState &&
                legacy.ActiveMainQuests.Single().AcceptanceId != Guid.Empty &&
                Convert.ToInt64(f.Query("SELECT count(*) FROM character_dungeon_state")) == 0,
                "真实v7到v8迁移旧角色保持unknown，不改资产或补aux/key");
            f.SelectEntry();
            var selection = f.Session.BeginDungeonSelection();
            var context = f.Session.CurrentDungeonSelection;
            var selected = f.Session.Selected!;
            Check(f.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms) is null && f.Session.DungeonSnapshot is null &&
                ReferenceEquals(selected, f.Session.Selected) && Same(selected, f.Read()) &&
                f.Session.Phase == SessionPhase.MapSelect && ReferenceEquals(context, f.Session.CurrentDungeonSelection) &&
                f.Session.CanPublishDungeonSelection(selection), "旧unknown角色拒绝不改角色、revision或原选图返回上下文");
            Check(f.Session.ReturnFromDungeonSelection() is not null, "缺owner拒绝后仍可返回城镇");
        }

        void Invalidation()
        {
            using var f = new Fixture("FormalEpoch", towns, characters, learning, quests);
            f.SelectEntry();
            var first = Enter(f);
            f.Session.ReturnToRoster();
            Check(f.Session.DungeonSnapshot is null && !f.Session.CanPublishFormalDungeon(first), "返回选角清正式实例并拒绝旧epoch回执");
            f.Session.Select(f.Created.Slot);
            f.Session.MarkReady();
            MoveToEntry(f.Session);
            var second = Enter(f);
            Check(second.SelectionEpoch != first.SelectionEpoch && second.Snapshot.Identity.InstanceId != first.Snapshot.Identity.InstanceId &&
                second.Snapshot.Identity.TaskAttempt == 2, "同角色重新选角建立新正式身份");
            Check(!f.Session.CanPublishFormalDungeon(second with { SelectionEpoch = first.SelectionEpoch }) &&
                !f.Session.CanPublishFormalDungeon(second with { Snapshot = second.Snapshot with { Identity = first.Snapshot.Identity } }),
                "伪造旧selection epoch或instance的回执不能发布");
            f.Session.CompleteFormalDungeonLoading();
            Check(f.Session.ObserveFormalDungeonDeath(Observation(first, first.Monsters.Monsters[0]), kills, growth).Outcome == DungeonRunOutcome.Rejected,
                "新实例加载后也拒绝旧实例key报告");
            using var replacement = f.Owners.Acquire(f.Owner.Account, () => { });
            Reject<InvalidOperationException>(() => f.Session.CanPublishFormalDungeon(second), "旧generation不能发布正式回执");
            Reject<InvalidOperationException>(() => f.Session.CompleteFormalDungeonLoading(), "旧generation不能完成新加载");
            f.Session.End();
            Check(new CharacterRepository(f.Path).LoadCharacter(replacement, f.Created.Slot).DungeonState == second.Character.DungeonState,
                "旧Session结束不回退已预留身份或释放新owner");
        }

        void RecordedNormalStatistics()
        {
            using var f = new Fixture("RecordedKill", towns, characters, learning, quests, secondCharacter: true);
            f.SelectEntry();
            var entry = Enter(f);
            Check(entry.Character.Id == 2 && entry.Monsters.Monsters.Select(monster => monster.ObjectKey).SequenceEqual(new ushort[] { 256, 257, 258, 259 }),
                "本版统计回归fixture明确绑定本人2及首房预留key");
            f.Session.CompleteFormalDungeonLoading();
            // 四种匹配版本的正常统计形状，含非零前缀和不同受击次数；不是合成死亡的实机奖励证据。
            string[] bodies =
            [
                "0201000002000000000000000000000000009A02000000000000020000000111010200140200000000000002000403DC000000000084670003000000F8010000000000008302D0006102ED00150001002500000040B77FF1",
                "0001000002000C0000000000000001000000EE0200000000000002000000011101020045020000000000000200A102E200000000006C3D0003000000F8010000000000003D02C5001B02E20015000100250000004818B0DC",
                "030100000200000000000000000000000000500300000000000004000000011101020010030000000000000400C602E0000000000049160003000000F80100000000000087020701A702E000150001002A000000B653753C",
                "0101000002000000000000000000000000006E02000000000000070000000111010200530200000000000007002C01EF00000000003C150003000000F8010000000000002D0218018902F400150001002A000000B0355B52"
            ];
            int index = 0;
            foreach (string body in bodies)
            {
                byte[] bytes = Convert.FromHexString(body);
                Check(DungeonRequests.TryParseDeath(bytes, out var parsed) && parsed!.SinglePlayerStatistics is { ChecksumMatches: true },
                    "实际统计形状的完整88B与MD5折叠匹配");
                var statistics = parsed!.SinglePlayerStatistics!;
                var part = statistics.Participant;
                var observation = new NativeDungeonDeathObservation(parsed.ObjectKey, parsed.Reporter, part.Kind, part.ObjectKey,
                    part.CumulativeHpDecrease, part.PositiveHpDecreaseCount, statistics.RewriteFlag, statistics.SpawnLevel,
                    statistics.SpawnHpSnapshot, statistics.Unknown18, statistics.Unknown26, statistics.ChecksumMatches,
                    Convert.ToHexString(SHA256.HashData(bytes)));
                var committed = f.Session.ObserveFormalDungeonDeath(observation, kills, growth);
                Check(committed.Outcome == DungeonRunOutcome.Accepted && committed.ClearFact is null &&
                    f.Count() == ++index && f.Session.Selected!.Learning.Experience == (ulong)index * 104,
                    "正式Native合同接受不同计数/前缀，每key仅落一次真实成长事务");
                Check(f.Session.ObserveFormalDungeonDeath(observation, kills, growth).Outcome == DungeonRunOutcome.Duplicate && f.Count() == index,
                    "不同真实形状的精确重放也不重复经验");
            }
            Check(f.Session.DungeonSnapshot!.LivingMonsters.Count == 0 && f.Session.Phase == SessionPhase.Dungeon &&
                f.Session.MoveFormalDungeon(1, 1) is not null && Same(f.Read(), f.Session.Selected!),
                "首房四怪提交后才可过房，未越过Boss清图资格");
        }

        void FrozenLootTransaction()
        {
            using var f = new Fixture("LootReceipt", towns, characters, learning, quests, job: 14);
            f.SelectEntry();
            f.Session.BeginDungeonSelection();
            var drops = DungeonMonsterDropCatalog.Load(pvf);
            int calls = 0;
            var entry = f.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms, drops, itemResources.Items,
                bound => { calls++; return 0; }) ?? throw new InvalidOperationException("FAIL 显式池准入失败");
            Check(entry.Character.DungeonState!.NextObjectKey > 285 && Same(entry.Character, f.Read()),
                "准入同事务预留完整怪物与最多一件地面key范围");
            f.Session.CompleteFormalDungeonLoading();
            var first = entry.Monsters.Monsters[0];
            var observation = Observation(entry, first) with
            {
                CumulativeHpDecrease = 696, PositiveHpDecreaseCount = 2, Unknown18 = 831, Unknown26 = 3
            };
            var changedObservation = observation with { Unknown18 = 832, BodySha256 = new string('B', 64) };
            var original = f.Session.Selected!;
            f.Sql("CREATE TRIGGER loot_fail AFTER INSERT ON character_dungeon_loot_receipts BEGIN SELECT RAISE(ABORT,'late loot failure'); END");
            Reject<SqliteException>(() => f.Session.ObserveFormalDungeonDeath(observation, kills, growth),
                "掉落账本晚期故障回滚同事务经验与成长receipt");
            Check(calls == 2 && f.Count() == 0 && Same(original, f.Read()) && ReferenceEquals(original, f.Session.Selected) &&
                Convert.ToInt64(f.Query("SELECT count(*) FROM character_dungeon_loot_receipts")) == 0,
                "冻结随机后SQL回滚不留经验或物品，也不提前发布地面");
            Check(f.Session.ObserveFormalDungeonDeath(changedObservation, kills, growth).Outcome == DungeonRunOutcome.Rejected &&
                calls == 2 && f.Count() == 0 && Same(original, f.Read()),
                "待提交控制死亡的相容改包不能换事件、重抽掉落或提前发经验");
            f.Sql("DROP TRIGGER loot_fail");
            var death = f.Session.ObserveFormalDungeonDeath(observation, kills, growth);
            var ground = death.Drop ?? throw new InvalidOperationException("FAIL 明确合成命中没有地面对象");
            Check(calls == 2 && death.Outcome == DungeonRunOutcome.Accepted && ground.Core.ItemId == 1000 && ground.Core.Count == 1 &&
                f.Count() == 1 && f.Read().Learning.Experience == 104 &&
                Convert.ToInt64(f.Query("SELECT count(*) FROM character_dungeon_loot_receipts")) == 1,
                "原事实重试只复用冻结结果，一次提交XP及完整地面核心");
            Check(f.Session.ObserveFormalDungeonDeath(observation, kills, growth).Outcome == DungeonRunOutcome.Duplicate &&
                f.Session.ObserveFormalDungeonDeath(changedObservation, kills, growth).Outcome == DungeonRunOutcome.Rejected && calls == 2 &&
                f.Count() == 1 && f.Read().Learning.Experience == 104 &&
                Convert.ToInt64(f.Query("SELECT count(*) FROM character_dungeon_loot_receipts")) == 1,
                "控制死亡提交后精确重复与相容改包均不再次发经验、抽奖或增发地面");
            uint key = ground.Reservation.GroundKey;
            Check(f.Session.PickFormalDungeonItem(key + 1000, itemResources.Items, itemResources.EpicPieces).Outcome == DungeonRunOutcome.Rejected,
                "客户端任意key不提供拾取资格");
            var beforePickup = f.Session.Selected!;
            f.Sql("CREATE TRIGGER pickup_fail AFTER INSERT ON character_dungeon_pickup_receipts BEGIN SELECT RAISE(ABORT,'late pickup failure'); END");
            Reject<SqliteException>(() => f.Session.PickFormalDungeonItem(key, itemResources.Items, itemResources.EpicPieces),
                "拾取receipt晚期故障整批回滚新UID、库存和revision");
            Check(Same(beforePickup, f.Read()) && ReferenceEquals(beforePickup, f.Session.Selected) &&
                f.Session.ReturnFromFormalDungeon() is null && f.Session.MoveFormalDungeon(1, 1) is null &&
                f.Session.ObserveFormalDungeonDeath(Observation(entry, entry.Monsters.Monsters[1]), kills, growth).Outcome == DungeonRunOutcome.Rejected,
                "存储异常保留拾取准备并拒绝新死亡或过房退出，不丢原地面");
            f.Sql("DROP TRIGGER pickup_fail");
            var pickup = f.Session.PickFormalDungeonItem(key, itemResources.Items, itemResources.EpicPieces);
            Check(pickup.Outcome == DungeonRunOutcome.Accepted && pickup.Commit is { Duplicate: false, ItemUid: > 0, ProjectionCurrent: true } &&
                f.Read().Inventory.Items.Single(item => item.Core.ItemId == 1000).Core == ground.Core && Same(f.Read(), f.Session.Selected!),
                "重试提交实际UID/槽和完整原核心，再确认地面消费");
            long uid = pickup.Commit!.ItemUid;
            Check(f.Session.PickFormalDungeonItem(key, itemResources.Items, itemResources.EpicPieces).Outcome == DungeonRunOutcome.Rejected &&
                Convert.ToInt64(f.Query("SELECT count(*) FROM character_dungeon_pickup_receipts")) == 1,
                "重复拾取无第二份资产或receipt");
            var second = f.Session.ObserveFormalDungeonDeath(Observation(entry, entry.Monsters.Monsters[1]), kills, growth);
            var merged = f.Session.PickFormalDungeonItem(second.Drop!.Reservation.GroundKey, itemResources.Items, itemResources.EpicPieces);
            Check(merged.Commit!.ItemUid == uid && f.Read().Inventory.Items.Single(item => item.Uid == uid).Core == (ground.Core with { Value = 2 }),
                "第二个真实来源核心只加一件，复用已有堆叠UID及全部属性");
            DungeonGroundObject? expired = null;
            foreach (var monster in entry.Monsters.Monsters.Skip(2))
                expired = f.Session.ObserveFormalDungeonDeath(Observation(entry, monster), kills, growth).Drop;
            Check(f.Session.MoveFormalDungeon(1, 1) is not null && f.Session.CompleteFormalDungeonLoading() is not null &&
                f.Session.PickFormalDungeonItem(expired!.Reservation.GroundKey, itemResources.Items, itemResources.EpicPieces).Outcome == DungeonRunOutcome.Rejected,
                "离房销毁未拾取地面，旧房key不能在新房授予资产");
            f.Session.ReturnToRoster();
            f.Session.Select(f.Created.Slot);
            f.Session.MarkReady();
            Check(f.Session.Selected!.Learning.Experience == 416 && f.Session.Selected.Inventory.Items.Single(item => item.Uid == uid).Core.Value == 2 &&
                f.Session.Selected.DungeonState!.NextObjectKey == entry.Character.DungeonState.NextObjectKey &&
                new CharacterRepository(f.Path).LoadCharacter(f.Owner, f.Created.Slot).Inventory.Items.Single(item => item.Uid == uid).Core.Value == 2,
                "重选/仓储重开保留拾取资产、XP和不回绕高水位；非实机重启替代");
            using var miss = new Fixture("LootMiss", towns, characters, learning, quests);
            miss.SelectEntry();
            int missCalls = 0;
            var missEntry = miss.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms, drops, itemResources.Items,
                bound => { missCalls++; return bound - 1; })!;
            miss.Session.CompleteFormalDungeonLoading();
            var missObservation = Observation(missEntry, missEntry.Monsters.Monsters[0]);
            Check(miss.Session.ObserveFormalDungeonDeath(missObservation, kills, growth).Drop is null && missCalls == 2 &&
                Convert.ToInt64(miss.Query("SELECT count(*) FROM character_dungeon_loot_receipts WHERE core IS NULL")) == 1 &&
                miss.Session.ObserveFormalDungeonDeath(missObservation, kills, growth).Outcome == DungeonRunOutcome.Duplicate && missCalls == 2,
                "未命中也保存冻结事实，精确重放不能改成物品或再次抽奖");
            using var full = new Fixture("LootFull", towns, characters, learning, quests);
            var empty = full.Read();
            var range = ItemSlotBoundService.Range(DfoServer.GameWorld.Items.ItemKind.Consumable, empty.Inventory.Expansion);
            var fullCore = InventoryRewardGrantService.Create(itemResources.Items.Get(1047), 1000);
            var occupied = Enumerable.Range(range.Start, range.End - range.Start + 1)
                .Select(slot => new InventoryItem(0, new(InventoryListType.Main, checked((short)slot)), fullCore)).ToImmutableArray();
            full.Repository.CommitInventory(full.Owner, empty, new(empty.Inventory,
                empty.Inventory with { Items = empty.Inventory.Items.AddRange(occupied) }, "explicit-full-bag-fixture"));
            full.SelectEntry();
            var fullEntry = full.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms, drops, itemResources.Items, bound => 0)!;
            full.Session.CompleteFormalDungeonLoading();
            var fullDeath = full.Session.ObserveFormalDungeonDeath(Observation(fullEntry, fullEntry.Monsters.Monsters[0]), kills, growth);
            var fullGround = fullDeath.Drop!;
            var fullBefore = full.Session.Selected!;
            Check(full.Session.PickFormalDungeonItem(fullGround.Reservation.GroundKey, itemResources.Items, itemResources.EpicPieces).Outcome == DungeonRunOutcome.Rejected &&
                Same(fullBefore, full.Read()) && Convert.ToInt64(full.Query("SELECT count(*) FROM character_dungeon_pickup_receipts")) == 0,
                "满包合法拒绝不提交UID/资产/revision，不消耗冻结地面");
            var stored = full.Read();
            var freed = stored.Inventory with { Items = stored.Inventory.Items.Where(item => item.Position.Index != range.Start || item.Position.List != InventoryListType.Main).ToImmutableArray() };
            full.Repository.CommitInventory(full.Owner, stored, new(stored.Inventory, freed, "explicit-free-slot-fixture"));
            // 模拟在线快照恢复；不重建Run或抽奖，合法原地面仍可取。
            var expected = full.Session.Selected!;
            Check(full.Session.PickFormalDungeonItem(fullGround.Reservation.GroundKey, itemResources.Items, itemResources.EpicPieces).Outcome == DungeonRunOutcome.Rejected,
                "陈旧满包快照仍拒绝，不覆盖已保存空槽");
            Check(full.Read().Inventory.Items.Length == stored.Inventory.Items.Length - 1 && ReferenceEquals(expected, full.Session.Selected),
                "旧库存冲突不造成第二份资产或自动重抽");
            full.Session.UpdateProgress(new(CharacterProgressField.Level, full.Owner.Account, full.Created.Name, expected.Level, 0));
            var resumedPickup = full.Session.PickFormalDungeonItem(fullGround.Reservation.GroundKey, itemResources.Items, itemResources.EpicPieces);
            var resumedItem = full.Read().Inventory.Find(new(InventoryListType.Main, range.Start));
            Check(resumedPickup.Outcome == DungeonRunOutcome.Accepted && resumedItem?.Core == fullGround.Core,
                $"测试显式恢复后拾取原地面；outcome={resumedPickup.Outcome},slot={resumedPickup.Commit?.Destination.Index},count={resumedItem?.Core.Count},phase={full.Session.Phase}");
        }

        FormalDungeonReceipt Enter(Fixture f) => f.Session.EnterFormalDungeon(3, 3145, 0, catalog, rooms)
            ?? throw new InvalidOperationException("FAIL 正式3145资源入口未建立");
        void MoveToEntry(GameSession session) => Check(session.LeaveRoom() && session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)),
            "重新选角后经合法出房旅行到左入口");
    }

    internal static NativeDungeonDeathObservation Observation(FormalDungeonReceipt receipt, DungeonPlannedMonster monster) =>
        new(monster.ObjectKey, receipt.Character.Id, 0x0111, receipt.Character.Id, 600, 3, 0,
            checked((uint)monster.CandidateLevel), 504, 620, 3, true, new string('A', 64));
    static bool Same(Character first, Character second) => JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second);
    static void Check(bool condition, string name) => TestCheck.That(condition, name);

    sealed class Fixture : IDisposable
    {
        readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "S5A9-formal-session-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public CharacterRepository Repository { get; }
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease Owner { get; }
        public GameSession Session { get; }
        public Character Created { get; }
        public Fixture(string name, TownCatalog towns, CharacterCatalog characters, SkillLearningCatalog learning, DfoServer.GameWorld.Quests.QuestCatalog quests, bool secondCharacter = false, byte job = 0)
        {
            Path = System.IO.Path.Combine(root, "game.db");
            Repository = new(Path, quests);
            Owner = Owners.Acquire("formal-session", () => { });
            Session = new(Repository, towns, Owner);
            if (secondCharacter) Session.CreateCharacter("RecordDummy", characters.Get(0), learning);
            Created = Repository.Create(Owner, CharacterCreation.Prepare(name, characters.Get(job), towns.Room, towns.InitialReturn, learning.Get)
                with { ActiveMainQuests = [new(0,3145,1)] });
        }
        public void SelectEntry()
        {
            Session.Select(Created.Slot);
            Session.MarkReady();
            Check(Session.LeaveRoom() && Session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)),
                "正常Session合法出房和左侧入口旅行");
        }
        public Character Read() => Repository.LoadCharacter(Owner, Created.Slot);
        public long Count() => Convert.ToInt64(Query("SELECT count(*) FROM character_dungeon_growth_receipts"));
        public object? Query(string sql)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }
        public void Sql(string sql)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        public void Dispose()
        {
            Owner.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
