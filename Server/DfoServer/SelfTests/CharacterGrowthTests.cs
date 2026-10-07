using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using PvfLib;

namespace DfoServer;

internal static class CharacterGrowthTests
{
    public static void Run(TownCatalog towns, PvfArchive pvf)
    {
        int checks = 0;
        var catalog = CharacterGrowthCatalog.Load(pvf);
        var firstSkills = new List<LearnedSkill> { new(12, 5, 2) };
        var secondSkills = new List<LearnedSkill> { new(7, 8, 3) };
        var trees = new List<CharacterSkillTree> { new(1, 29, 11, secondSkills), new(0, 7, 9, firstSkills) };
        var character = new Character(123, 2, "Growth", 19, towns.Room, towns.InitialReturn)
        {
            Level = 2,
            Learning = new(1000, trees),
            Skills = [new(5, 2)],
            ActiveMainQuests = [new(0, 3145, 1)],
            Inventory = new([new(9, new(DfoServer.Game.Inventory.Models.InventoryListType.Equipment, 12), new(101010912, DfoServer.GameWorld.Items.ItemKind.Equipment, 42) { Durability = 45 })], Gold: 456)
        };
        var next = CharacterGrowthPlan.Create(character, 3, 2035, catalog);
        Check(next.Success && next.Plan!.LevelSpDelta == 30 && next.Plan.TargetLevel == 3 && next.Plan.Learning.Experience == 2035,
            "Lv2至3采用累计2035及等级贡献差30");
        Check(next.Plan!.Learning.Trees[0].Sp == 37 && next.Plan.Learning.Trees[1].Sp == 59 &&
            next.Plan.Learning.Trees[0].Tp == 9 && next.Plan.Learning.Trees[1].Tp == 11,
            "两树分别保留已消费SP及原TP，不重置为累计总贡献");
        Check(next.Plan.Learning.Trees[0].Skills.Single() == new LearnedSkill(12, 5, 2) &&
            next.Plan.Learning.Trees[1].Skills.Single() == new LearnedSkill(7, 8, 3), "技能等级和稳定slot保持");
        Check(next.Plan.CharacterId == 123 && next.Plan.Revision == 19 && next.Plan.PreviousLevel == 2 &&
            next.Plan.Before.Experience == 1000 && character.Level == 2 && character.Learning.Trees.Single(tree => tree.Context == 0).Sp == 7 &&
            character.Inventory.Gold == 456 && character.ActiveMainQuests.Single().QuestId == 3145,
            "计划冻结前提且不修改原角色、物品或任务");
        var sameLevel = CharacterGrowthPlan.Create(character, 2, 2034, catalog);
        Check(sameLevel.Success && sameLevel.Plan!.LevelSpDelta == 0 && sameLevel.Plan.Learning.Trees[0].Sp == 7,
            "同等级XP增加不额外发SP");
        var levelOne = character with { Level = 1, Learning = State(0, 0, 0), Skills = [new(5, 1)] };
        var levelTwo = CharacterGrowthPlan.Create(levelOne, 2, 1000, catalog).Plan!;
        Check(levelTwo.Learning.Trees.All(tree => tree.Sp == 30 && tree.Tp == 0), "新角色Lv1至2两树均加30，TP保持初值");
        var jump = CharacterGrowthPlan.Create(levelOne, 3, 2035, catalog);
        Check(jump.Success && jump.Plan!.LevelSpDelta == 60 && jump.Plan.Learning.Trees.All(tree => tree.Sp == 60),
            "跨等级贡献只按源与目标表差计算");
        var automaticLevel = CharacterGrowthPlan.AddExperience(character, 1035, catalog);
        Check(automaticLevel.Success && automaticLevel.Plan!.TargetLevel == 3 && automaticLevel.Plan.Learning.Experience == 2035 &&
            automaticLevel.Plan.Learning.Trees[0].Sp == 37 && automaticLevel.Plan.Learning.Trees[1].Sp == 59,
            "已核验奖励增量自动匹配累计等级边界，并保留两树消费余额");
        Check(CharacterGrowthPlan.AddExperience(levelOne, 2035, catalog).Plan!.TargetLevel == 3 &&
            CharacterGrowthPlan.AddExperience(character, 1034, catalog).Plan!.TargetLevel == 2 &&
            CharacterGrowthPlan.AddExperience(character, 0, catalog).Failure == CharacterGrowthFailure.InvalidTarget &&
            CharacterGrowthPlan.AddExperience(character, ulong.MaxValue, catalog).Failure == CharacterGrowthFailure.InvalidTarget,
            "同级、跨级和精确边界匹配，零增量及累计溢出拒绝");
        Check(CharacterGrowthPlan.Create(character, 2, 1000, catalog).Failure == CharacterGrowthFailure.InvalidTarget,
            "相同绝对经验候选拒绝，不提供稳定事件幂等");
        Check(CharacterGrowthPlan.Create(character, 2, 999, catalog).Failure == CharacterGrowthFailure.InvalidTarget &&
            CharacterGrowthPlan.Create(character, 1, 500, catalog).Failure == CharacterGrowthFailure.InvalidTarget,
            "降低经验或等级拒绝");
        Check(CharacterGrowthPlan.Create(character, 3, 2034, catalog).Failure == CharacterGrowthFailure.InvalidTarget &&
            CharacterGrowthPlan.Create(character, 2, 2035, catalog).Failure == CharacterGrowthFailure.InvalidTarget &&
            CharacterGrowthPlan.Create(character, 0, 2035, catalog).Failure == CharacterGrowthFailure.InvalidTarget &&
            CharacterGrowthPlan.Create(character, 96, 5434027680, catalog).Failure == CharacterGrowthFailure.InvalidTarget,
            "等级与经验边界不一致或支持范围外拒绝");
        var lastLevel = character with { Level = 95, Learning = State(catalog.GetLevelStartExperience(95), 15, 25) };
        Check(CharacterGrowthPlan.Create(lastLevel, 95, catalog.GetCumulativeExperienceBoundary(95) - 1, catalog).Success &&
            CharacterGrowthPlan.Create(lastLevel, 95, catalog.GetCumulativeExperienceBoundary(95), catalog).Failure == CharacterGrowthFailure.InvalidTarget,
            "Lv95明确上界，达到未知Lv96范围不截断或夹取");
        Check(CharacterGrowthPlan.Create(character with { Learning = State(0, 7, 29) }, 3, 2035, catalog).Failure == CharacterGrowthFailure.InvalidSnapshot,
            "GM高等级零XP不作为正常成长快照修复");
        Check(CharacterGrowthPlan.Create(character with { Learning = State(1000, ushort.MaxValue, 29) }, 3, 2035, catalog).Failure == CharacterGrowthFailure.PointOverflow &&
            CharacterGrowthPlan.Create(character with { Learning = State(1000, 7, ushort.MaxValue) }, 3, 2035, catalog).Failure == CharacterGrowthFailure.PointOverflow,
            "任一树u16余额溢出整计划拒绝");
        foreach (var unsupported in new[] { character with { Job = 9 }, character with { Job = 10 },
            character with { GrowType = 1 }, character with { Learning = CharacterLearningState.Uninitialized } })
            Check(CharacterGrowthPlan.Create(unsupported, 3, 2035, catalog).Failure == CharacterGrowthFailure.Unsupported,
                "特殊职业、转职及旧未初始化树不自动补资格");
        var malformed = character with { Learning = new(1000, Array.AsReadOnly(new[] { trees[0], trees[0] })) };
        Check(CharacterGrowthPlan.Create(malformed, 3, 2035, catalog).Failure == CharacterGrowthFailure.InvalidSnapshot,
            "重复上下文快照拒绝");
        firstSkills.Clear();
        secondSkills.Clear();
        trees.Clear();
        Check(next.Plan.Before.Trees[0].Skills.Single() == new LearnedSkill(12, 5, 2) &&
            next.Plan.Learning.Trees[1].Skills.Single() == new LearnedSkill(7, 8, 3), "原集合后续修改不污染计划或前提");
        Console.WriteLine($"PASS {checks} 普通成长纯计划、SP消费保留及边界拒绝");

        void Check(bool value, string name)
        {
            checks++;
            if (!value)
                throw new InvalidOperationException("FAIL 成长计划: " + name);
        }
    }

    private static CharacterLearningState State(ulong experience, ushort firstSp, ushort secondSp) => new(experience,
        Array.AsReadOnly(new CharacterSkillTree[]
        {
            new(0, firstSp, 0, Array.AsReadOnly(new[] { new LearnedSkill(0, 5, 1) })),
            new(1, secondSp, 0, Array.AsReadOnly(new[] { new LearnedSkill(0, 5, 1) }))
        }));
}
