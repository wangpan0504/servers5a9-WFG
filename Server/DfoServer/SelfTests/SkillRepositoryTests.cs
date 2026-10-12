using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using PvfLib;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;

namespace DfoServer;

internal static class SkillRepositoryTests
{
    // 仅隔离测试库使用该成长状态，不是生产赠点或 GM 准备接口。
    internal static NewCharacter Fixture(string name, TownCatalog towns, CharacterCatalog characters, SkillLearningCatalog catalog)
    {
        var skills = characters.Get(0).Skills.ToArray();
        var initialized = SkillLearning.Initialize(0, skills, catalog);
        if (!initialized.Success)
            throw new InvalidOperationException("测试初始技能资源无法初始化");
        return new(name, towns.Room, towns.InitialReturn)
        {
            Level = 3,
            Skills = skills,
            InitialLearning = new(2035, Array.AsReadOnly(initialized.State!.Trees.Select(tree => tree with { Sp = 60 }).ToArray()))
        };
    }

    internal static SkillLearningCommand Purchase(byte context = 0, ushort id = 5) =>
        new(context, 0, Array.AsReadOnly(new[] { new SkillLearningChangeRequest(id, false, 1) }));

    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters)
    {
        int before = TestCheck.Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-skill-repository-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var catalog = SkillLearningCatalog.Load(pvf, PremiumContractCatalog.Load(pvf).ExpertSkillLevels);
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("skill-repository", () => { });
            Character original = repository.Create(owner, Fixture("SkillRepo", towns, characters, catalog));
            var first = SkillLearning.Plan(0, 0, 3, original.Learning, Purchase(), catalog);
            TestCheck.That(first.Success, "真实鬼斩1到2学习计划成功");
            Character committed = repository.CommitLearning(owner, original, first.State!);
            var tree0 = committed.Learning.Trees.Single(tree => tree.Context == 0);
            var ghost = tree0.Skills.Single(skill => skill.Id == 5);
            TestCheck.That(tree0.Sp == 45 && ghost.Level == 2 && committed.Skills.Single(skill => skill.Id == 5).Level == 2 &&
                committed.Revision == original.Revision + 1 && committed.Learning.Experience == 2035,
                "同事务扣15SP、升级、主树投影和revision且经验不变");
            Character read = repository.List(owner).Single();
            TestCheck.That(Equal(read, committed), "重读恢复全部余额绑定等级与revision");
            var second = SkillLearning.Plan(0, 0, 3, committed.Learning, Purchase(1), catalog);
            Character two = repository.CommitLearning(owner, committed, second.State!);
            TestCheck.That(two.Learning.Trees.Single(tree => tree.Context == 1).Sp == 45 &&
                two.Learning.Trees.Single(tree => tree.Context == 1).Skills.Single(skill => skill.Id == 5).Level == 2 &&
                two.Learning.Trees.Single(tree => tree.Context == 0).Skills.SequenceEqual(tree0.Skills) &&
                two.Skills.SequenceEqual(committed.Skills), "context1学习不改context0及canonical技能");
            Reject<InvalidOperationException>(() => repository.CommitLearning(owner, committed, second.State!), "旧revision拒绝整笔写入");
            using var foreign = owners.Acquire("skill-foreign", () => { });
            Reject<InvalidOperationException>(() => repository.CommitLearning(foreign, two, two.Learning), "其他账号不能修改角色");

            // 失败发生在第二树绑定写入，前面的删除、点数和revision也必须回滚。
            Character rollbackCharacter = repository.Create(owner, Fixture("SkillRoll", towns, characters, catalog));
            Execute("CREATE TRIGGER reject_skill_tree BEFORE INSERT ON character_skill_bindings WHEN NEW.context=1 BEGIN SELECT RAISE(ABORT,'skill rollback'); END;");
            var rollback = SkillLearning.Plan(0, 0, 3, rollbackCharacter.Learning, Purchase(), catalog);
            TestCheck.That(rollback.Success, "用于SQL失败的真实鬼斩学习计划成功");
            Reject<SqliteException>(() => repository.CommitLearning(owner, rollbackCharacter, rollback.State!), "第二树SQL失败传播");
            Execute("DROP TRIGGER reject_skill_tree;");
            TestCheck.That(Equal(repository.List(owner).Single(character => character.Id == rollbackCharacter.Id), rollbackCharacter), "SQL失败回滚两树、主技能、点数、等级与revision");
            Reject<InvalidDataException>(() => repository.CommitLearning(owner, two, two.Learning with { Experience = 2036 }), "学习不能改XP");
            Reject<InvalidDataException>(() => repository.CommitLearning(owner, two, ChangePoints(two.Learning, 0, 46, 0)), "学习不能增SP");
            Reject<InvalidDataException>(() => repository.CommitLearning(owner, two, ChangePoints(two.Learning, 0, 45, 1)), "学习不能改TP");
            Character legacy = repository.Create(owner, new("SkillLegacy", towns.Room, towns.InitialReturn) { Skills = characters.Get(0).Skills.ToArray() });
            var initialization = SkillLearning.Initialize(0, legacy.Skills, catalog);
            Reject<InvalidDataException>(() => repository.CommitLearning(owner, legacy, ChangePoints(initialization.State!, 0, 1, 0)), "首次初始化禁止补点");
            Character initialized = repository.CommitLearning(owner, legacy, initialization.State!);
            TestCheck.That(initialized.Learning.Trees.All(tree => tree.Sp == 0 && tree.Tp == 0), "旧角色初始化两树只有零点数");
            Character gm = repository.UpdateProgress(owner, new(CharacterProgressField.Skill, owner.Account, initialized.Name, 5, 2));
            TestCheck.That(gm.Learning.Trees.All(tree => tree.Skills.Single(skill => skill.Id == 5).Level == 2 && tree.Sp == 0 && tree.Tp == 0),
                "GM覆盖已有绑定两树等级但不赠点");
            Reject<InvalidDataException>(() => repository.UpdateProgress(owner, new(CharacterProgressField.Skill, owner.Account, initialized.Name, 48, 1)),
                "已初始化角色GM不得新增没有资源资格的binding");
            TestCheck.That(Equal(repository.List(owner).Single(character => character.Id == gm.Id), gm), "拒绝GM新增不丢原技能事实");
            var oneBinding = new CharacterLearningState(0, Array.AsReadOnly(new CharacterSkillTree[]
            {
                new(0, 0, 0, Array.AsReadOnly(new LearnedSkill[] { new(0, 5, 1) })),
                new(1, 0, 0, Array.AsReadOnly(new LearnedSkill[] { new(0, 5, 1) }))
            }));
            Character last = repository.Create(owner, new("LastSkill", towns.Room, towns.InitialReturn)
            {
                Skills = [new(5, 1)], InitialLearning = oneBinding
            });
            Reject<InvalidDataException>(() => repository.UpdateProgress(owner, new(CharacterProgressField.Skill, owner.Account, last.Name, 5, 0)),
                "GM不可删除最后绑定导致重登空tree失败");
            TestCheck.That(Equal(repository.List(owner).Single(character => character.Id == last.Id), last), "最后绑定拒绝保留投影和余额");
            var staleGmPlan = SkillLearning.Plan(0, 0, 3, rollbackCharacter.Learning, Purchase(), catalog);
            Character gmChanged = repository.UpdateProgress(owner, new(CharacterProgressField.Skill, owner.Account, rollbackCharacter.Name, 5, 5));
            TestCheck.That(gmChanged.Revision == rollbackCharacter.Revision, "测试确认GM原合同不递增revision");
            Reject<InvalidOperationException>(() => repository.CommitLearning(owner, rollbackCharacter, staleGmPlan.State!),
                "相同revision的GM变更仍使旧学习计划失效");
            TestCheck.That(Equal(repository.List(owner).Single(character => character.Id == gmChanged.Id), gmChanged),
                "拒绝旧计划不覆盖GM等级或扣旧成本");
            using var fresh = owners.Acquire("skill-repository", () => { });
            Reject<InvalidOperationException>(() => repository.CommitLearning(owner, two, two.Learning), "旧generation拒绝学习写库");
            Character recovered = new CharacterRepository(path).List(fresh).Single(character => character.Id == two.Id);
            TestCheck.That(Equal(recovered, two) && recovered.Learning.Trees.All(tree => tree.Skills.Single(skill => skill.Id == 5).Slot == ghost.Slot),
                "新owner重登两树槽位稳定且未重扣费");
        }
        finally
        {
            Directory.Delete(root, true);
        }
        Console.WriteLine($"PASS {TestCheck.Count - before} 技能仓储事务断言");

        void Execute(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    private static CharacterLearningState ChangePoints(CharacterLearningState state, byte context, ushort sp, ushort tp) =>
        state with { Trees = Array.AsReadOnly(state.Trees.Select(tree => tree.Context == context ? tree with { Sp = sp, Tp = tp } : tree).ToArray()) };

    internal static bool Equal(Character left, Character right) => left.Revision == right.Revision &&
        left.Skills.SequenceEqual(right.Skills) && left.Learning.Experience == right.Learning.Experience &&
        left.Learning.Trees.Count == right.Learning.Trees.Count && left.Learning.Trees.Zip(right.Learning.Trees).All(pair =>
            pair.First.Context == pair.Second.Context && pair.First.Sp == pair.Second.Sp && pair.First.Tp == pair.Second.Tp &&
            pair.First.Skills.SequenceEqual(pair.Second.Skills));

}
