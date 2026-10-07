using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using PvfLib;

namespace DfoServer;

internal static class SkillLearningTests
{
    public static void Run(PvfArchive pvf, CharacterCatalog characters)
    {
        int checks = 0;
        void Check(bool value, string name)
        {
            checks++;
            if (!value)
                throw new InvalidOperationException("FAIL skill learning: " + name);
        }
        SkillLearningCommand Request(ushort id = 5, byte context = 0, byte mode = 0) => new(context, mode, [new(id, false, 1)]);
        var definitions = new Dictionary<ushort, SkillLearningDefinition>
        {
            [5] = Definition(5, [7, 11], 3, 1, 2),
            [6] = Definition(6, [5], 3, 1, 0, [new(5, 1)]),
            [7] = Definition(7, [0], 3, 1, 0),
            [8] = Definition(8, [0], 3, 1, 0, feature: true),
            [9] = Definition(9, [0], 3, 1, 0, combo: true),
            [10] = Definition(10, [0], 3, 1, 0, [new(5, 0)]),
            [11] = Definition(11, [0], 3, 1, 0, [new(99, 0)])
        };
        SkillLearningDefinition Resolve(byte job, ushort id) => job == 0 ? definitions[id] : throw new KeyNotFoundException();
        SkillLearningResult initialized = SkillLearning.Initialize(0, [new CharacterSkill(7, 1)], Resolve);
        Check(initialized.Success && initialized.State!.Experience == 0 && initialized.State!.Trees.All(tree => tree.Sp == 0 && tree.Tp == 0),
            "Lv1 initialization grants no SP or TP");
        Check(initialized.State!.Trees.Select(tree => tree.Context).SequenceEqual(new byte[] { 0, 1 }) &&
            initialized.State!.Trees.All(tree => tree.Skills.Single() == new LearnedSkill(0, 7, 1)), "two independent initial contexts bind actual skill in slot0");
        Check(!ReferenceEquals(initialized.State!.Trees[0].Skills, initialized.State!.Trees[1].Skills), "initial trees do not share mutable list storage");
        Check(SkillLearning.Initialize(0, [new CharacterSkill(9, 1)], Resolve).Failure == SkillLearningFailure.Unsupported,
            "combo six-slot selection remains unsupported rather than inventing binding");
        Check(SkillLearning.Initialize(9, [], Resolve).Failure == SkillLearningFailure.Unsupported, "special job never receives fabricated initialized trees");
        Check(SkillLearning.Initialize(0, [new CharacterSkill(5, 1), new CharacterSkill(5, 1)], Resolve).Failure == SkillLearningFailure.InvalidRequest,
            "duplicate initial IDs rejected");
        var source = State(30, [new(8, 7, 1)]);
        SkillLearningResult result = SkillLearning.Plan(0, 0, 10, source, Request(), Resolve);
        Check(result.Success && result.State!.Trees[0].Sp == 23 && result.State!.Trees[0].Skills.Single(skill => skill.Id == 5) == new LearnedSkill(0, 5, 1),
            "unlearned skill costs current0 entry and takes first vacant ordinary slot");
        Check(source.Trees[0].Sp == 30 && source.Trees[0].Skills.Count == 1 && result.State!.Trees[1].Sp == 30,
            "plan does not mutate original or other context");
        Check(result.Changes.SequenceEqual(new[] { new LearnedSkill(0, 5, 1) }), "ACK changes contain actual stable slot ID and new level");
        SkillLearningResult again = SkillLearning.Plan(0, 0, 10, result.State!, Request(), Resolve);
        Check(again.Success && again.State!.Trees[0].Sp == 12 && again.Changes.Single() == new LearnedSkill(0, 5, 2),
            "same packet again is another plus1 delta not duplicate identity or target level");
        SkillLearningResult third = SkillLearning.Plan(0, 0, 10, again.State!, Request(), Resolve);
        Check(third.Success && third.State!.Trees[0].Sp == 1 && third.Changes.Single().Level == 3,
            "repeat-last cost applies current learned level index");
        Check(SkillLearning.Plan(0, 0, 10, third.State!, Request(), Resolve).Failure == SkillLearningFailure.Unsupported, "primary cap外需另一OR分支证据，不伪装精通失败");
        Check(SkillLearning.Plan(0, 0, 1, source, Request(), Resolve).Success, "required level exact boundary admitted");
        Check(SkillLearning.Plan(0, 0, 2, result.State!, Request(), Resolve).Failure == SkillLearningFailure.RequiredLevel,
            "next required level uses basis plus current times range");
        Check(SkillLearning.Plan(0, 0, 10, State(6, []), Request(), Resolve).Failure == SkillLearningFailure.InsufficientSp, "point shortage rejects all state");
        Check(SkillLearning.Plan(0, 0, 10, State(7, []), Request(), Resolve).State!.Trees[0].Sp == 0, "exact SP cost admits zero remaining balance");
        Check(SkillLearning.Plan(1, 0, 10, source, Request(), Resolve).Failure == SkillLearningFailure.MissingSkill, "wrong job list cannot borrow another job skill");
        Check(SkillLearning.Plan(0, 0, 10, source, Request(99), Resolve).Failure == SkillLearningFailure.MissingSkill, "missing resource skill identified");
        Check(SkillLearning.Plan(0, 1, 10, source, Request(), Resolve).Failure == SkillLearningFailure.Unsupported, "unclosed advanced grow OR cap not guessed");
        Check(SkillLearning.Plan(0, 0, 10, source, Request(8), Resolve).Failure == SkillLearningFailure.Unsupported, "feature TP skill excluded from SP branch");
        Check(SkillLearning.Plan(0, 0, 10, source, Request(9), Resolve).Failure == SkillLearningFailure.Unsupported, "combo learning not ordinary any-slot assignment");
        Check(SkillLearning.Plan(0, 0, 10, source, Request(6), Resolve).Failure == SkillLearningFailure.Prerequisite, "unmet prerequisite blocks learning");
        Check(SkillLearning.Plan(0, 0, 10, source, Request(10), Resolve).Success,
            "存在资源的未学前置等级0可满足required0，不伪造missing getter");
        Check(SkillLearning.Plan(0, 0, 10, source, Request(11), Resolve).Failure == SkillLearningFailure.Unsupported,
            "未知资源前置不能将null getter当level0放行");
        var batch = new SkillLearningCommand(0, 1, [new(5, false, 1), new(6, false, 1)]);
        SkillLearningResult batchResult = SkillLearning.Plan(0, 0, 10, source, batch, Resolve);
        Check(batchResult.Success && batchResult.State!.Trees[0].Sp == 18 && batchResult.Changes.Select(skill => skill.Id).SequenceEqual(new ushort[] { 5, 6 }),
            "ordered distinct batch applies prerequisite from preceding accepted step");
        SkillLearningResult deniedBatch = SkillLearning.Plan(0, 0, 10, State(10, []), batch, Resolve);
        Check(deniedBatch.Failure == SkillLearningFailure.InsufficientSp && deniedBatch.State is null && deniedBatch.Changes.Count == 0,
            "late batch failure exposes no partial state or ACK changes");
        var context1 = SkillLearning.Plan(0, 0, 10, source, Request(context: 1, mode: 1), Resolve);
        Check(context1.Success && context1.State!.Trees[1].Sp == 23 && context1.State!.Trees[0].Sp == 30 && context1.State!.Trees[1].Tp == 4,
            "context1 and mode1 use selected SP tree without fabricating TP change");
        foreach (SkillLearningCommand invalid in new SkillLearningCommand[]
        {
            new(2, 0, [new(5, false, 1)]), new(0, 2, [new(5, false, 1)]), new(0, 0, []),
            new(0, 0, [new(5, true, 1)]), new(0, 0, [new(5, false, 0)]), new(0, 0, [new(5, false, 2)]),
            new(0, 0, [new(5, false, 1), new(5, false, 1)])
        })
            Check(SkillLearning.Plan(0, 0, 10, source, invalid, Resolve).Failure == SkillLearningFailure.InvalidRequest,
                "request bounds or unsupported signed delta rejected");
        var full = State(30, Enumerable.Range(0, 255).Select(index => new LearnedSkill((byte)index, (ushort)(1000 + index), 1)).ToArray());
        Check(SkillLearning.Plan(0, 0, 10, full, Request(), Resolve).Failure == SkillLearningFailure.Capacity, "all 255 ordinary bindings reject new slot without FF placeholder");
        var lastVacancy = State(30, Enumerable.Range(0, 254).Select(index => new LearnedSkill((byte)index, (ushort)(1000 + index), 1)).ToArray());
        Check(SkillLearning.Plan(0, 0, 10, lastVacancy, Request(), Resolve).Changes.Single().Slot == 254, "last ordinary slot254 is valid actual binding");
        Check(SkillLearning.Plan(0, 0, 10, CharacterLearningState.Uninitialized, Request(), Resolve).Failure == SkillLearningFailure.Unsupported,
            "uninitialized old character is not given fake empty initialized trees");
        var mutable = new List<LearnedSkill> { new(8, 7, 1) };
        SkillLearningResult frozen = SkillLearning.Plan(0, 0, 10, State(30, mutable), Request(), Resolve);
        mutable.Clear();
        Check(frozen.State!.Trees.All(tree => tree.Skills.Any(skill => skill.Id == 7)), "result copies both context collections into read-only snapshots");
        var actual = SkillLearningCatalog.Load(pvf);
        Check(actual.Get(0, 5).GetBasePointCost(0) == 15, "matching resource hardattack base cost independently known");
        SkillLearningResult real = SkillLearning.Plan(0, 0, 1, State(15, []), Request(), actual);
        Check(real.Success && real.State!.Trees[0].Sp == 0 && real.Changes.Single().Level == 1, "real PVF ordinary hardattack learning consumes15 SP");
        CharacterProfile actualProfile = characters.Get(0);
        SkillLearningResult realInitial = SkillLearning.Initialize(0, actualProfile.Skills, actual);
        Check(actualProfile.Skills.Length == 6 && realInitial.Success && realInitial.State!.Trees.All(tree => tree.Sp == 0 && tree.Tp == 0),
            "real job0 all six initial skills bind with zero points including mastered resource without range");
        Check(realInitial.State!.Trees.All(tree => tree.Skills.Select(skill => new CharacterSkill(skill.Id, skill.Level)).SequenceEqual(actualProfile.Skills)) &&
            realInitial.State!.Trees.All(tree => tree.Skills.Select(skill => skill.Slot).SequenceEqual(new byte[] { 0, 1, 2, 3, 4, 5 })),
            "real initial CHR identity and levels survive in both context slot bindings");
        Check(!actual.Get(0, 169).SupportsOrdinarySpLearning && realInitial.State!.Trees[0].Skills.Any(skill => skill.Id == 169),
            "initial fixed binding eligibility is separate from further learning eligibility");
        Console.WriteLine($"PASS {checks} pure skill learning planning assertions");
    }

    private static CharacterLearningState State(ushort sp, IReadOnlyList<LearnedSkill> skills) => new(123,
        Array.AsReadOnly(new CharacterSkillTree[] { new(0, sp, 4, skills), new(1, sp, 4, skills) }));

    private static SkillLearningDefinition Definition(ushort id, int[] costs, int maximum, int basis, int range,
        SkillPrerequisite[]? prerequisites = null, bool feature = false, bool combo = false) =>
        new(0, id, "skill/fixture.skl", true, costs, Enumerable.Repeat(maximum, 6).ToArray(),
            Enumerable.Repeat(maximum, 6).ToArray(), basis, range, prerequisites ?? [], new int[6], feature, combo, []);
}
