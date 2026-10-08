using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using PvfLib;
using Microsoft.Data.Sqlite;
using DfoServer.Game.Session;

namespace DfoServer;

internal static class DungeonGrowthRepositoryTests
{
    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters)
    {
        int checks = 0;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-growth-repository-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var growth = CharacterGrowthCatalog.Load(pvf);
            var learning = SkillLearningCatalog.Load(pvf);
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("growth-repository", () => { });
            var fixture = SkillRepositoryTests.Fixture("GrowCommit", towns, characters, learning);
            fixture = fixture with
            {
                Level = 1,
                InitialLearning = new(0, Array.AsReadOnly(fixture.InitialLearning.Trees.Select(tree => tree with { Sp = 0 }).ToArray())),
                ActiveMainQuests = [new(0, 3145, 1)]
            };
            var initial = repository.Create(owner, fixture);
            // 测试显式给定1000；该数字只用于跨Lv2边界，不是怪物奖励规则。
            var (run, authority) = CreateRun(initial);
            var death = authority.Death(run.Snapshot.Identity, 20, 1);
            var pending = run.PrepareConfirmedDeath(death).Pending!;
            var first = repository.CommitDungeonGrowth(owner, initial, run, pending, Reward(1000, pending), growth);
            Check(!first.Duplicate && first.Character.Level == 2 && first.Character.Learning.Experience == 1000 &&
                first.Character.Revision == initial.Revision + 1 && first.Character.Learning.Trees.All(tree => tree.Sp == 30 && tree.Tp == 0),
                "经验、升级、两树SP和revision同事务提交");
            Check(run.IsPendingDeath(pending) && run.Snapshot.LivingMonsters.Count == 2 && CountReceipts() == 1,
                "数据库提交不抢先消费内存死亡，且保存一条稳定事实");
            var replay = repository.CommitDungeonGrowth(owner, initial, run, pending, Reward(1000, pending), growth);
            Check(replay.Duplicate && SkillRepositoryTests.Equal(first.Character, replay.Character) && CountReceipts() == 1,
                "提交后尚未确认内存时重放原事实，不重复加经验或SP");
            using (var writer = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                writer.Open();
                using var held = writer.BeginTransaction(deferred: false);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var readOnlyReplay = repository.CommitDungeonGrowth(owner, initial, run, pending, Reward(1000, pending), growth);
                Check(readOnlyReplay.Duplicate && watch.Elapsed < TimeSpan.FromSeconds(1),
                    "外部writer占用真实WAL库时，精确重放不申请写锁或等待writer");
                held.Rollback();
            }
            Reject<InvalidDataException>(() => repository.CommitDungeonGrowth(owner, initial, run, pending, Reward(1001, pending), growth),
                "同一事件ID修改奖励额度拒绝");
            Reject<InvalidDataException>(() => repository.CommitDungeonGrowth(owner, initial, run, pending,
                Reward(1000, pending) with { RuleId = "fixture-next-version" }, growth), "规则变更不能重放旧奖励ID");
            Reject<InvalidDataException>(() => repository.CommitDungeonGrowth(owner, initial, run, pending,
                Reward(1000, pending) with { ResourceSource = "monster/otherexp.tbl" }, growth), "资源身份改变不能重放旧奖励ID");
            Check(run.ConfirmCommittedDeath(pending).Outcome == DungeonRunOutcome.Accepted,
                "明确提交后才确认死亡");
            Reject<InvalidDataException>(() => repository.CommitDungeonGrowth(owner, first.Character, run, pending, Reward(1000, pending), growth),
                "已确认的旧候选不能再次进入写库入口");
            var current = repository.List(owner).Single();
            Check(current.Position == initial.Position && current.Return == initial.Return &&
                current.Skills.SequenceEqual(initial.Skills) && current.ActiveMainQuests.SequenceEqual(initial.ActiveMainQuests) &&
                current.CompletedQuests.SequenceEqual(initial.CompletedQuests) && current.Inventory.Gold == initial.Inventory.Gold &&
                current.Learning.Trees.Zip(initial.Learning.Trees).All(pair => pair.First.Skills.SequenceEqual(pair.Second.Skills)),
                "成长保留位置、任务、金币、技能等级和稳定slot");
            var failedDeath = authority.Death(run.Snapshot.Identity, 21, 2);
            var failedPending = run.PrepareConfirmedDeath(failedDeath).Pending!;
            Execute("CREATE TRIGGER fail_growth BEFORE INSERT ON character_dungeon_growth_receipts BEGIN SELECT RAISE(ABORT,'growth receipt failure'); END");
            Reject<SqliteException>(() => repository.CommitDungeonGrowth(owner, current, run, failedPending, Reward(1035, failedPending), growth),
                "末次死亡receipt写入故障触发真实SQLite回滚");
            Check(SkillRepositoryTests.Equal(repository.List(owner).Single(), current) && CountReceipts() == 1 &&
                run.IsPendingDeath(failedPending) && run.Phase == SinglePlayerDungeonPhase.Dungeon && run.Snapshot.LivingMonsters.Count == 1,
                "晚期SQL故障不留下经验、SP、revision、receipt或提前清图");
            Execute("DROP TRIGGER fail_growth");
            Check(run.CancelUncommittedDeath(failedPending), "明确回滚后释放原候选");
            var retry = run.PrepareConfirmedDeath(failedDeath).Pending!;
            var second = repository.CommitDungeonGrowth(owner, current, run, retry, Reward(1035, retry), growth);
            Check(second.Character.Level == 3 && second.Character.Learning.Experience == 2035 &&
                second.Character.Learning.Trees.All(tree => tree.Sp == 60) && CountReceipts() == 2 &&
                run.ConfirmCommittedDeath(retry).ClearFact is { ConfirmedDeathEvents.Count: 2 },
                "同死亡ID重试成功后才完成清图，每条奖励只提交一次");
            var restarted = new CharacterRepository(path).List(owner).Single();
            Check(SkillRepositoryTests.Equal(restarted, second.Character) && CountReceipts() == 2,
                "仓储重新打开保留经验、SP、revision与去重事实");
            var (tamperedRun, tamperedAuthority) = CreateRun(restarted);
            var tampered = tamperedRun.PrepareConfirmedDeath(tamperedAuthority.Death(tamperedRun.Snapshot.Identity, 20, 1, death.EventId)).Pending!;
            Reject<InvalidDataException>(() => repository.CommitDungeonGrowth(owner, restarted, tamperedRun, tampered, Reward(1000, tampered), growth),
                "另一实例复用事件ID不能领取原奖励");
            var (staleRun, staleAuthority) = CreateRun(restarted);
            var stale = staleRun.PrepareConfirmedDeath(staleAuthority.Death(staleRun.Snapshot.Identity, 20, 1)).Pending!;
            var moved = repository.Save(owner, restarted, restarted.Position, restarted.Return);
            Reject<InvalidOperationException>(() => repository.CommitDungeonGrowth(owner, restarted, staleRun, stale, Reward(1, stale), growth),
                "旧revision不能覆盖后续角色保存");
            Check(SkillRepositoryTests.Equal(repository.List(owner).Single(), moved) && CountReceipts() == 2,
                "revision拒绝不新增成长事实");
            var (gmRun, gmAuthority) = CreateRun(moved);
            var gmPending = gmRun.PrepareConfirmedDeath(gmAuthority.Death(gmRun.Snapshot.Identity, 20, 1)).Pending!;
            var edited = repository.UpdateProgress(owner, new(CharacterProgressField.Skill, owner.Account, moved.Name, 5, 2));
            Check(edited.Revision == moved.Revision, "GM前提变更不增加revision");
            Reject<InvalidOperationException>(() => repository.CommitDungeonGrowth(owner, moved, gmRun, gmPending, Reward(1, gmPending), growth),
                "同revision的技能前提变化也拒绝旧成长提交");
            using var outsider = owners.Acquire("growth-other", () => { });
            Reject<InvalidDataException>(() => repository.CommitDungeonGrowth(outsider, edited, gmRun, gmPending, Reward(1, gmPending), growth),
                "其他账号不能提交此角色成长");
            Reject<InvalidDataException>(() => repository.CommitDungeonGrowth(owner, edited, gmRun, new(gmPending.Death, gmPending.Monster, gmPending.Result), Reward(1, gmPending), growth),
                "重新构造候选不能取得成长写库资格");
            Reject<InvalidDataException>(() => repository.CommitDungeonGrowth(owner, edited, gmRun, gmPending, Reward(0, gmPending), growth),
                "零奖励不能创建成长receipt");
            Reject<InvalidDataException>(() => repository.CommitDungeonGrowth(owner, edited, gmRun, gmPending, Reward(1, gmPending) with { MonsterLevel = 4 }, growth),
                "奖励候选的怪物等级不符时拒绝");
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject<InvalidOperationException>(() => repository.CommitDungeonGrowth(owner, edited, gmRun, gmPending, Reward(1, gmPending), growth),
                "旧account generation不能写入成长");
            Check(SkillRepositoryTests.Equal(repository.List(replacement).Single(), edited) && CountReceipts() == 2,
                "owner接管与拒绝均不改变已提交经验");
            var localSeed = fixture with { Name = "GrowPolicy", InitialLearning = fixture.InitialLearning with { Experience = 950 } };
            var localCharacter = repository.Create(replacement, localSeed);
            var (localRun, localAuthority) = CreateRun(localCharacter);
            var localPending = localRun.PrepareConfirmedDeath(localAuthority.Death(localRun.Snapshot.Identity, 20, 1)).Pending!;
            var rewardTable = DungeonKillExperienceCatalog.Load(pvf);
            var localReward = DungeonKillExperiencePolicy.Plan(localRun, localPending, rewardTable);
            var localCommit = repository.CommitDungeonGrowth(replacement, localCharacter, localRun, localPending, localReward, growth);
            Check(localReward.RuleId == DungeonKillExperienceCatalog.RuleId && localReward.ResourceSource == DungeonKillExperienceCatalog.ResourcePath &&
                localReward.Amount == 72 && localReward.MonsterLevel == 3 && localCommit.Character.Level == 2 &&
                localCommit.Character.Learning.Experience == 1022 && localCommit.Character.Learning.Trees.All(tree => tree.Sp == 30),
                "本服真实Lv3表值72通过规则规划与原子事务，跨级自动发SP");
            Check((string?)Query("SELECT rule_id FROM character_dungeon_growth_receipts WHERE amount=72") == DungeonKillExperienceCatalog.RuleId,
                "持久receipt保留明确本服规则版本");
            localRun.ConfirmCommittedDeath(localPending);
            Reject<InvalidOperationException>(() => DungeonKillExperiencePolicy.Plan(localRun, localPending, rewardTable),
                "已确认死亡不能重新规划本服奖励");
            // 模拟已发布 v2 库；迁移只建空账本，不赠送经验、点数或事件。
            Execute("DROP TABLE character_dungeon_settlement_receipts; DROP TABLE character_dungeon_pickup_receipts");
            Execute("DROP TABLE character_dungeon_loot_receipts");
            Execute("DROP TABLE character_dungeon_state");
            Execute("DROP TABLE character_game_settings; DROP TABLE account_client_settings; DROP TABLE character_client_settings; DROP TABLE character_dungeon_growth_receipts; DROP TABLE mailbox_audit_log; DROP TABLE mailbox_attachments; DROP TABLE mailbox_sent_gold; DROP TABLE mailbox_messages");
            Execute("PRAGMA user_version=2");
            var migrated = new CharacterRepository(path).List(replacement).Single(character => character.Id == edited.Id);
            Check(SkillRepositoryTests.Equal(migrated, edited) && CountReceipts() == 0 &&
                migrated.DungeonState is null && Convert.ToInt64(Query("SELECT count(*) FROM character_dungeon_state")) == 0 &&
                Convert.ToInt64(Query("PRAGMA user_version")) == SqliteMigrations.CurrentVersion,
                "v2升级当前结构保持已有角色状态，新增账本为空");
            Execute("ALTER TABLE character_dungeon_growth_receipts RENAME COLUMN amount TO broken_amount");
            Reject<SqliteException>(() => new CharacterRepository(path), "当前版本的残缺成长账本初始化必须失败");
        }
        finally
        {
            Directory.Delete(root, true);
        }
        Console.WriteLine($"PASS {checks} 副本成长原子提交、去重、回滚与迁移断言");
        RunSessionPublication(towns, pvf, characters);

        void Check(bool value, string name)
        {
            checks++;
            if (!value)
                throw new InvalidOperationException("FAIL 副本成长: " + name);
        }
        void Reject<T>(Action action, string name) where T : Exception
        {
            try { action(); }
            catch (T) { Check(true, name); return; }
            throw new InvalidOperationException("FAIL expected " + typeof(T).Name + ": " + name);
        }
        DungeonExperienceReward Reward(uint amount, PreparedDungeonDeath prepared) =>
            new("fixture-only", DungeonKillExperienceCatalog.ResourcePath, prepared.Monster.MonsterId, prepared.Monster.CandidateLevel, amount);
        long CountReceipts() => Convert.ToInt64(Query("SELECT count(*) FROM character_dungeon_growth_receipts"));
        object? Query(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            return cmd.ExecuteScalar();
        }
        void Execute(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    private static void RunSessionPublication(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-growth-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        int checks = 0;
        try
        {
            var catalog = CharacterGrowthCatalog.Load(pvf);
            var rewards = DungeonKillExperienceCatalog.Load(pvf);
            var learning = SkillLearningCatalog.Load(pvf);
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("growth-session", () => { });
            var fixture = SkillRepositoryTests.Fixture("SessionGrow", towns, characters, learning) with
            {
                Level = 1,
                ActiveMainQuests = [new(0, 3145, 1)]
            };
            // 合成合法边界状态，只用于验证发布顺序，不作为正常攻击升级的实机证据。
            fixture = fixture with { InitialLearning = fixture.InitialLearning with
            {
                Experience = 950,
                Trees = Array.AsReadOnly(fixture.InitialLearning.Trees.Select(tree => tree with { Sp = 0 }).ToArray())
            } };
            var created = repository.Create(owner, fixture);
            var session = new GameSession(repository, towns, owner);
            session.Select(created.Slot);
            var before = session.Selected!;
            var (run, authority) = CreateRun(before);
            var pending = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity, 20, 1)).Pending!;
            var reward = DungeonKillExperiencePolicy.Plan(run, pending, rewards);
            long epoch = session.SelectionEpoch;
            Reject<InvalidOperationException>(() => session.CommitDungeonGrowth(epoch, before, run, pending, reward, catalog),
                "未Ready时不能提交成长或提前消费死亡");
            session.MarkReady();
            Reject<InvalidOperationException>(() => session.CommitDungeonGrowth(epoch + 1, before, run, pending, reward, catalog),
                "错误选角epoch不进入存储");
            Reject<InvalidOperationException>(() => session.CommitDungeonGrowth(epoch, before with { }, run, pending, reward, catalog),
                "同值角色副本不能替代原在线快照");
            Check(ReferenceEquals(session.Selected, before) && run.IsPendingDeath(pending) && Count() == 0,
                "资格拒绝保持在线状态、待提交死亡与账本");

            Sql("CREATE TRIGGER reject_growth BEFORE INSERT ON character_dungeon_growth_receipts BEGIN SELECT RAISE(ABORT,'growth session fixture'); END");
            Reject<SqliteException>(() => session.CommitDungeonGrowth(epoch, before, run, pending, reward, catalog),
                "真实SQL晚期失败向上传递，不伪装成功");
            Check(ReferenceEquals(session.Selected, before) && SkillRepositoryTests.Equal(repository.List(owner).Single(), before) &&
                run.IsPendingDeath(pending) && run.Snapshot.LivingMonsters.Contains((ushort)20) && Count() == 0,
                "晚期失败完整回滚，并保留原死亡ID与在线快照供查询恢复");
            Sql("DROP TRIGGER reject_growth");
            var result = session.CommitDungeonGrowth(epoch, before, run, pending, reward, catalog);
            Check(!result.Commit.Duplicate && result.Death.Outcome == DungeonRunOutcome.Accepted &&
                ReferenceEquals(session.Selected, result.Commit.Character) && session.Selected.Level == 2 &&
                session.Selected.Learning.Experience == 1022 && session.Selected.Learning.Trees.All(tree => tree.Sp == 30) &&
                session.Selected.Revision == before.Revision + 1 && Count() == 1,
                "SQL成功后才消费死亡并发布真实经验、等级、两树SP和revision");
            Check(!run.IsPendingDeath(pending) && !run.Snapshot.LivingMonsters.Contains((ushort)20) &&
                SkillRepositoryTests.Equal(repository.List(owner).Single(), session.Selected!),
                "领域死亡、在线角色与持久结果在提交后保持一致");
            Reject<InvalidOperationException>(() => session.CommitDungeonGrowth(epoch, session.Selected!, run, pending, reward, catalog),
                "已确认死亡不能再次进入成长提交");
            Check(Count() == 1 && repository.List(owner).Single().Learning.Experience == 1022,
                "重复调用不增发资产");

            // 模拟SQL成功但原调用方未安装在线结果，重读同一receipt后才确认原候选。
            var recoveryBefore = session.Selected!;
            var recoveryPending = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity, 21, 2)).Pending!;
            var recoveryReward = DungeonKillExperiencePolicy.Plan(run, recoveryPending, rewards);
            var saved = repository.CommitDungeonGrowth(owner, recoveryBefore, run, recoveryPending, recoveryReward, catalog);
            Check(ReferenceEquals(session.Selected, recoveryBefore) && run.IsPendingDeath(recoveryPending) && Count() == 2,
                "存储已提交但未发布的现场保持原候选");
            var restored = session.CommitDungeonGrowth(epoch, recoveryBefore, run, recoveryPending, recoveryReward, catalog);
            Check(restored.Commit.Duplicate && SkillRepositoryTests.Equal(saved.Character, session.Selected!) &&
                session.Selected!.Learning.Experience == 1094 && restored.Death.ClearFact is not null &&
                run.Phase == SinglePlayerDungeonPhase.Settlement && Count() == 2,
                "精确重放安装当前持久结果并确认唯一清图，不再次加经验或SP");

            var (oldRun, oldAuthority) = CreateRun(session.Selected!);
            var oldPending = oldRun.PrepareConfirmedDeath(oldAuthority.Death(oldRun.Snapshot.Identity, 20, 1)).Pending!;
            var oldReward = DungeonKillExperiencePolicy.Plan(oldRun, oldPending, rewards);
            var oldSelected = session.Selected!;
            session.ReturnToRoster();
            session.Select(created.Slot);
            session.MarkReady();
            var reselected = session.Selected!;
            Reject<InvalidOperationException>(() => session.CommitDungeonGrowth(epoch, oldSelected, oldRun, oldPending, oldReward, catalog),
                "返回选角并重选后旧epoch及快照不能发奖");
            Check(ReferenceEquals(session.Selected, reselected) && oldRun.IsPendingDeath(oldPending) && Count() == 2,
                "旧回调不覆盖重新选角的角色或消费旧候选");
            using var replacement = owners.Acquire("growth-session", () => { });
            Reject<InvalidOperationException>(() => session.CommitDungeonGrowth(session.SelectionEpoch, reselected, oldRun,
                oldPending, oldReward, catalog), "旧账号owner不能提交或发布成长");
            Check(ReferenceEquals(session.Selected, reselected) && Count() == 2 &&
                SkillRepositoryTests.Equal(new CharacterRepository(path).List(replacement).Single(), reselected),
                "账号接管及仓储重开保留已经提交的经验与两树点数");
        }
        finally
        {
            Directory.Delete(root, true);
        }
        Console.WriteLine($"PASS {checks} 副本成长在线发布、晚期失败及提交后恢复断言");

        void Check(bool value, string name)
        {
            checks++;
            if (!value) throw new InvalidOperationException("FAIL 副本成长发布: " + name);
        }
        void Reject<T>(Action action, string name) where T : Exception
        {
            try { action(); }
            catch (T) { Check(true, name); return; }
            throw new InvalidOperationException("FAIL expected " + typeof(T).Name + ": " + name);
        }
        long Count() => Convert.ToInt64(Sql("SELECT count(*) FROM character_dungeon_growth_receipts"));
        object? Sql(string text)
        {
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
            db.Open();
            using var query = db.CreateCommand();
            query.CommandText = text;
            return query.ExecuteScalar();
        }
    }

    private static (SinglePlayerDungeonRun Run, FixtureAuthority Authority) CreateRun(Character character)
    {
        var map = new DungeonMap("boss", new(0, 0), 76126, "map/growth-fixture.map");
        var maze = new DungeonMaze(0, 1, 1, "fixture-only", [map.Position], [map.Position], [map], []);
        var entry = new DungeonEntryPlan(character.Id, character.Revision, 3, 3145, 0, 0, maze, map, map);
        var sources = new ushort[] { 20, 21 }.Select(_ => new DungeonRoomMonster(1,
            Array.AsReadOnly(new[] { 1, 0, 3, 100, 100, 0, 1, 1 }), "[fixed]", "[normal]", "monster/growth-fixture.mob")).ToArray();
        var room = new DungeonRoomDefinition(map, Array.AsReadOnly(sources), [], []);
        var plan = new DungeonMonsterPlan(room, Array.AsReadOnly(sources.Select((source, index) =>
            new DungeonPlannedMonster(index, checked((ushort)(20 + index)), 1, 3, source)).ToArray()));
        var authority = new FixtureAuthority();
        if (!SinglePlayerDungeonRun.TryCreate(entry, 1, 1, new([plan], []), authority, out var run, out _))
            throw new InvalidOperationException("成长fixture实例未建立");
        run!.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        return (run, authority);
    }

    // 只为隔离仓储测试签发合成死亡，不是正式战斗实现。
    private sealed class FixtureAuthority : DungeonCombatAuthority
    {
        public ConfirmedDungeonMonsterDeath Death(DungeonRunIdentity identity, ushort key, long sequence, Guid? eventId = null)
            => ConfirmDeath(identity, eventId ?? Guid.NewGuid(), key, sequence);
    }
}
