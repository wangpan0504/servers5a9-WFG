using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using DfoServer.Testing;

namespace DfoServer;

internal static class SessionPlanningTests
{
    static readonly SkillLearningCommand Purchase = new(0, 0, [new(5, false, 1)]);
    static readonly CharacterProfile Profile = new(0, new(1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), [new(5, 1)]);

    public static async Task RunAsync()
    {
        int checks = TestCheck.Count;
        Creation();
        Initialization();
        Learning();
        await CreationRacesAsync();
        await SkillRacesAsync();
        Console.WriteLine($"PASS {TestCheck.Count - checks} session planning/commit assertions");
    }

    static SkillLearningDefinition Resolve(byte job, ushort id) => id == 5
        ? new(job, id, "synthetic.skl", true, [5], [10, 10, 10, 10, 10, 10], [10, 10, 10, 10, 10, 10],
            1, 0, [], [0, 0, 0, 0, 0, 0], false, false, [])
        : throw new KeyNotFoundException();

    static void Creation()
    {
        using var f = new Fixture();
        var draft = CharacterCreation.Prepare("Draft", Profile, f.Towns.Room, f.Towns.InitialReturn, Resolve);
        TestCheck.That(draft.Job == 0 && draft.AcceptedStoryLevel == 1 && draft.ActiveMainQuests.Single() == new ActiveMainQuest(0, 3145, 1),
            "建角规则保留职业和初始主线");
        TestCheck.That(!ReferenceEquals(draft.Skills, Profile.Skills) && draft.Skills.SequenceEqual(Profile.Skills), "建角复制初始技能数组");
        TestCheck.That(draft.InitialLearning.Trees.All(tree => tree.Sp == 0 && tree.Tp == 0 && tree.Skills.Single() == new LearnedSkill(0, 5, 1)),
            "建角双树绑定原技能且不赠点");
        var special = CharacterCreation.Prepare("Special", Profile with { Job = 9 }, f.Towns.Room, f.Towns.InitialReturn, Resolve);
        TestCheck.That(!special.InitialLearning.Initialized && special.Skills.SequenceEqual(Profile.Skills), "特殊职业保持原技能和未初始化状态");
        var created = f.Session.CreateCharacter("Created", Profile, Resolve);
        TestCheck.That(created.Id > 0 && created.Learning.Initialized && f.Session.Selected is null && f.Repository.List(f.Owner).Length == 2,
            "建角返回持久结果且不隐式选择角色");
        Reject<SqliteException>(() => f.Session.CreateCharacter("Created", Profile, Resolve), "重名建角仍由唯一约束拒绝");
        TestCheck.That(f.Repository.List(f.Owner).Length == 2, "重名失败没有部分角色");
        f.Select();
        Reject<InvalidDataException>(() => f.Session.CreateCharacter("Selected", Profile, (_, _) => throw new Exception("不应解析")),
            "选角后建角在资源读取前拒绝");
    }

    static void Initialization()
    {
        using var f = new Fixture(initialized: false);
        f.Select(ready: false);
        var before = f.Session.Selected!;
        TestCheck.That(f.Session.InitializeLearning(Resolve) == SkillLearningFailure.None, "入城前允许初始化学习绑定");
        var current = f.Session.Selected!;
        TestCheck.That(!f.Session.Ready && current.Revision == before.Revision + 1 && current.Learning.Experience == 2035 &&
            current.Learning.Trees.All(tree => tree.Sp == 0 && tree.Tp == 0), "初始化只提交绑定，保持经验和选角阶段");
        TestCheck.That(Equal(current, f.Read()), "初始化回读实际提交快照");
        TestCheck.That(f.Session.InitializeLearning((_, _) => throw new Exception("不应解析")) == SkillLearningFailure.None &&
            ReferenceEquals(current, f.Session.Selected), "重复初始化不解析资源也不写库");

        var empty = before with { Skills = [] };
        TestCheck.That(SkillLearning.Initialize(empty, Resolve).Failure == SkillLearningFailure.Unsupported, "无技能旧角色不伪造初始化");
        TestCheck.That(SkillLearning.Initialize(before, (_, _) => throw new KeyNotFoundException()).Failure == SkillLearningFailure.MissingSkill &&
            !before.Learning.Initialized, "初始化缺资源保留原经验与未初始化状态");

        using var failed = new Fixture(initialized: false);
        failed.Select(ready: false);
        var snapshot = failed.Session.Selected;
        failed.FailSkillWrite();
        Reject<SqliteException>(() => failed.Session.InitializeLearning(Resolve), "初始化后段SQL失败向上传递");
        TestCheck.That(ReferenceEquals(snapshot, failed.Session.Selected) && Equal(snapshot!, failed.Read()), "初始化SQL失败整笔回滚且不替换Selected");
    }

    static void Learning()
    {
        using var f = new Fixture();
        f.Select(ready: false);
        TestCheck.That(f.Session.LearnSkills(Purchase, (_, _) => throw new Exception("不应解析")).Failure == SkillLearningFailure.Unsupported,
            "未Ready时拒绝学习且不读取资源");
        f.Session.MarkReady();
        var before = f.Session.Selected!;
        var result = f.Session.LearnSkills(Purchase, Resolve);
        var receipt = result.Receipt!;
        TestCheck.That(result.Success && result.State!.Trees[0].Sp == 35 && result.Changes.Single() == new LearnedSkill(3, 5, 2),
            "学习扣一次费用并保留稳定槽位");
        TestCheck.That(ReferenceEquals(receipt.Character, f.Session.Selected) && ReferenceEquals(result.State, receipt.Character.Learning) &&
            receipt.Generation == f.Owner.Generation && receipt.SelectionEpoch == f.Session.SelectionEpoch && f.Session.CanPublishLearning(receipt),
            "成功回执绑定已安装状态和原generation/epoch");
        TestCheck.That(Equal(receipt.Character, f.Read()) && receipt.Character.Revision == before.Revision + 1, "回执为实际SQLite提交结果");
        var missing = f.Session.LearnSkills(new(0, 0, [new(99, false, 1)]), Resolve);
        TestCheck.That(!missing.Success && missing.Receipt is null && ReferenceEquals(receipt.Character, f.Session.Selected), "失败学习无回执且不安装快照");
        Reject<IOException>(() => f.Session.LearnSkills(Purchase, (_, _) => throw new IOException("资源读取失败")), "资源读取异常不提交");
        TestCheck.That(Equal(f.Session.Selected!, f.Read()) && f.Session.CanPublishLearning(receipt), "规划异常保持原发布资格");
        f.Session.UpdateProgress(new(CharacterProgressField.Level, f.Owner.Account, before.Name, 11, 0));
        TestCheck.That(f.Session.Selected!.Revision == receipt.Character.Revision && !f.Session.CanPublishLearning(receipt),
            "同revision的GM快照替换也使回执失效");
        result = f.Session.LearnSkills(Purchase, Resolve);
        f.Session.LearnSkills(Purchase, Resolve);
        TestCheck.That(!f.Session.CanPublishLearning(result.Receipt!), "后续提交使旧学习回执失效");
        result = f.Session.LearnSkills(Purchase, Resolve);
        f.Session.ReturnToRoster();
        TestCheck.That(!f.Session.CanPublishLearning(result.Receipt!), "返回列表后不能发布旧学习");
        f.Select();
        TestCheck.That(!f.Session.CanPublishLearning(result.Receipt!) && Equal(f.Session.Selected!, f.Read()), "同角色重选重读已提交数据且不复用旧回执");
        using var replacement = f.Owners.Acquire(f.Owner.Account, () => { });
        Reject<InvalidOperationException>(() => f.Session.CanPublishLearning(result.Receipt!), "旧generation不能发布学习回执");

        using var failed = new Fixture();
        failed.Select();
        var snapshot = failed.Session.Selected!;
        failed.FailSkillWrite();
        Reject<SqliteException>(() => failed.Session.LearnSkills(Purchase, Resolve), "学习后段SQL失败向上传递");
        TestCheck.That(ReferenceEquals(snapshot, failed.Session.Selected) && Equal(snapshot, failed.Read()), "学习后段SQL失败回滚点数、绑定和revision");
    }

    static async Task CreationRacesAsync()
    {
        using (var f = new Fixture())
        {
            await RaceAsync(f, definition => f.Session.CreateCharacter("Racing", Profile, definition), () =>
            {
                f.Select();
                f.Session.ReturnToRoster();
            });
            TestCheck.That(f.Session.Selected is null && f.Repository.List(f.Owner).Length == 1,
                "建角规划期间选角再回列表，旧epoch不得创建角色");
        }
        using (var f = new Fixture())
        {
            AccountOwners.Lease? replacement = null;
            try
            {
                await RaceAsync(f, definition => f.Session.CreateCharacter("Takeover", Profile, definition),
                    () => replacement = f.Owners.Acquire(f.Owner.Account, () => { }));
                TestCheck.That(f.Repository.List(replacement!).Length == 1, "建角规划期间接管不产生旧owner角色");
            }
            finally { replacement?.Dispose(); }
        }
    }

    static async Task SkillRacesAsync()
    {
        foreach (bool initialized in new[] { false, true })
        foreach (string change in new[] { "gm", "reselect", "takeover", "end", "commit" })
        {
            using var f = new Fixture(initialized);
            f.Select(ready: initialized);
            var original = f.Session.Selected!;
            AccountOwners.Lease? replacement = null;
            try
            {
                await RaceAsync(f, definition =>
                {
                    if (initialized)
                        f.Session.LearnSkills(Purchase, definition);
                    else
                        f.Session.InitializeLearning(definition);
                }, () =>
                {
                    switch (change)
                    {
                        case "gm":
                            f.Session.UpdateProgress(new(CharacterProgressField.Level, f.Owner.Account, original.Name, 11, 0));
                            break;
                        case "reselect":
                            f.Session.ReturnToRoster();
                            f.Select(ready: initialized);
                            break;
                        case "takeover":
                            replacement = f.Owners.Acquire(f.Owner.Account, () => { });
                            break;
                        case "end":
                            f.Session.End();
                            break;
                        case "commit":
                            if (initialized)
                                f.Session.LearnSkills(Purchase, Resolve);
                            else
                                f.Session.InitializeLearning(Resolve);
                            break;
                    }
                });
                var saved = f.Repository.LoadCharacter(replacement ?? f.Owner, original.Slot);
                TestCheck.That(saved.Revision == original.Revision + (change == "commit" ? 1 : 0), "过期规划不追加revision：" + initialized + "/" + change);
                TestCheck.That(saved.Level == (change == "gm" ? 11 : original.Level), "过期规划不覆盖GM等级：" + initialized + "/" + change);
                TestCheck.That(saved.Learning.Experience == original.Learning.Experience &&
                    (initialized ? saved.Learning.Trees[0].Sp == (change == "commit" ? 35 : 40) : saved.Learning.Initialized == (change == "commit")),
                    "过期规划不重复扣费或初始化：" + initialized + "/" + change);
            }
            finally { replacement?.Dispose(); }
        }
    }

    static async Task RaceAsync(Fixture f, Action<Func<byte, ushort, SkillLearningDefinition>> operation, Action mutate)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task work = Task.Run(() => operation((job, id) =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("资源规划未释放");
            return Resolve(job, id);
        }));
        Task competing = Task.CompletedTask;
        try
        {
            TestCheck.That(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))), "进入锁外资源规划");
            competing = Task.Run(() =>
            {
                using var unrelated = f.Owners.Acquire("unrelated", () => { });
                TestCheck.That(unrelated.Execute(() => true), "资源规划不阻塞其他账号owner操作");
                mutate();
            });
            await competing.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            // 即使断言失败也收回两项工作，不让迟到提交碰到下一用例或已删除的临时库。
            try { await Task.WhenAll(work, competing).WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (InvalidOperationException) { }
        }
        try
        {
            await work;
        }
        catch (InvalidOperationException)
        {
            TestCheck.That(true, "状态变化后拒绝原规划，不自动重跑");
            return;
        }
        throw new InvalidOperationException("过期规划未被拒绝");
    }

    static void Reject<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T)
        {
            TestCheck.That(true, name);
            return;
        }
        throw new InvalidOperationException("未拒绝：" + name);
    }

    static bool Equal(Character a, Character b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

    sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "S5A9-session-planning-" + Guid.NewGuid().ToString("N"));
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease Owner { get; }
        public CharacterRepository Repository { get; }
        public TownCatalog Towns { get; }
        public GameSession Session { get; }
        readonly ushort slot;

        public Fixture(bool initialized = true)
        {
            Directory.CreateDirectory(root);
            var room = new Location(1, 0, 10, 10);
            var street = new Location(2, 0, 10, 10);
            TownArea Area(Location point, bool isRoom, Location target) => new(point.Town, point.Area, 1, [], [], [], point,
                new("synthetic.map", [new(new(0, 0, 20, 20), [], [])], [new(new(0, 0, 20, 20), target.Town, target.Area, [], [])], isRoom, []), [], []);
            Towns = new(new() { [(1, 0)] = Area(room, true, street), [(2, 0)] = Area(street, false, room) },
                room, street, new(0, [], []), [], new Dictionary<uint, uint[]>());
            Repository = new(Path.Combine(root, "game.db"));
            Owner = Owners.Acquire("planning", () => { });
            CharacterLearningState learning = initialized
                ? new(2035, Array.AsReadOnly<CharacterSkillTree>([new(0, 40, 4, Array.AsReadOnly<LearnedSkill>([new(3, 5, 1)])),
                    new(1, 30, 2, Array.AsReadOnly<LearnedSkill>([new(7, 5, 1)]))]))
                : CharacterLearningState.Uninitialized with { Experience = 2035 };
            slot = Repository.Create(Owner, new("Fixture", room, street) { Level = 10, Skills = [new(5, 1)], InitialLearning = learning }).Slot;
            Session = new(Repository, Towns, Owner);
        }

        public void Select(bool ready = true)
        {
            Session.Select(slot);
            if (ready)
                Session.MarkReady();
        }

        public Character Read() => Repository.LoadCharacter(Owner, slot);

        public void FailSkillWrite()
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(root, "game.db") + ";Pooling=False");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_skill_write BEFORE INSERT ON character_skills BEGIN SELECT RAISE(ABORT,'skill write failure'); END";
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            Owner.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }
}
