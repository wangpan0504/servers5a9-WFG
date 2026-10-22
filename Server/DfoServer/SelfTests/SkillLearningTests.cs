using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using PvfLib;

namespace DfoServer;

internal static class SkillLearningTests
{
    public static void RunCore()
    {
        int checks = Count;
        SkillLearningCommand Request(ushort id = 5, byte context = 0, byte mode = 0) => new(context, mode, [new(id, false, 1)]);
        var definitions = new Dictionary<ushort, SkillLearningDefinition>
        {
            [5] = Definition(5, [7, 11], 3, 1, 2),
            [6] = Definition(6, [5], 3, 1, 0, [new(5, 1)]),
            [7] = Definition(7, [0], 3, 1, 0),
            [8] = Definition(8, [0], 3, 1, 0, feature: true),
            [9] = Definition(9, [0], 3, 1, 0, combo: true),
            [10] = Definition(10, [0], 3, 1, 0, [new(5, 0)]),
            [11] = Definition(11, [0], 3, 1, 0, [new(99, 0)]),
            [12] = Definition(12, [3], 3, 1, 0, type: SkillType.Passive),
            [13] = Definition(13, [3], 3, 1, 0, type: SkillType.Passive, growtypeCount: 2),
            [14] = Definition(14, [3], 3, 1, 0, type: SkillType.Passive, skillClass: 4, growtypeCount: null),
            [15] = Definition(15, [0], 3, 1, 0, type: SkillType.Unknown),
            [16] = Definition(16, [0], 3, 1, 0, type: SkillType.Passive, skillClass: null),
            [200] = Definition(200, [0], 3, 1, 0, type: SkillType.Passive, skillClass: 4)
        };
        SkillLearningDefinition Resolve(byte job, ushort id) => job == 0 ? definitions[id] : throw new KeyNotFoundException();
        SkillLearningResult initialized = SkillLearning.Initialize(0, [new CharacterSkill(7, 1)], Resolve);
        That(initialized.Success && initialized.State!.Experience == 0 && initialized.State!.Trees.All(tree => tree.Sp == 0 && tree.Tp == 0),
            "Lv1 initialization grants no SP or TP");
        That(initialized.State!.Trees.Select(tree => tree.Context).SequenceEqual(new byte[] { 0, 1 }) &&
            initialized.State!.Trees.All(tree => tree.Skills.Single() == new LearnedSkill(0, 7, 1)), "two independent initial contexts bind actual skill in slot0");
        That(!ReferenceEquals(initialized.State!.Trees[0].Skills, initialized.State!.Trees[1].Skills), "initial trees do not share mutable list storage");
        That(SkillLearning.Initialize(0, [new CharacterSkill(9, 1)], Resolve).Failure == SkillLearningFailure.Unsupported,
            "combo six-slot selection remains unsupported rather than inventing binding");
        That(SkillLearning.Initialize(9, [], Resolve).Failure == SkillLearningFailure.Unsupported, "special job never receives fabricated initialized trees");
        That(SkillLearning.Initialize(0, [new CharacterSkill(5, 1), new CharacterSkill(5, 1)], Resolve).Failure == SkillLearningFailure.InvalidRequest,
            "duplicate initial IDs rejected");
        var mixedInitial = SkillLearning.Initialize(0, [new(14, 2), new(5, 1), new(12, 1), new(7, 1)], Resolve);
        That(mixedInitial.Success && mixedInitial.State!.Trees.All(tree =>
            tree.Skills.Single(skill => skill.Id == 5).Slot == 0 && tree.Skills.Single(skill => skill.Id == 7).Slot == 1 &&
            tree.Skills.Single(skill => skill.Id == 14) == new LearnedSkill(102, 14, 2) &&
            tree.Skills.Single(skill => skill.Id == 12).Slot == 103), "混排初始被动不占快捷槽且两树保留原等级");
        That(SkillLearning.Initialize(0, [new(15, 1)], Resolve).Failure == SkillLearningFailure.Unsupported,
            "未知type不冒充主动或被动初始绑定");
        That(SkillLearning.Initialize(0, [new(200, 1)], Resolve).State!.Trees[0].Skills.Single().Slot == 138,
            "特殊映射技能不使用普通初始槽");
        That(SkillLearning.Initialize(0, Enumerable.Range(3000, 37).Select(id => new CharacterSkill((ushort)id, 1)).ToArray(),
            (job, id) => Definition(id, [0], 1, 1, 0, type: SkillType.Passive, skillClass: 4)).Failure == SkillLearningFailure.Capacity,
            "初始被动区满时整批拒绝，不溢出到特殊预留槽");
        var source = State(30, [new(8, 7, 1)]);
        var dependent = State(18, [new(0, 5, 1), new(1, 6, 1)]);
        var free = new Dictionary<ushort, byte>();
        var deniedRefund = SkillLearning.Plan(0, 0, 10, dependent, new(0, 0, [new(5, true, 1)]), Resolve, free, 30);
        That(deniedRefund.Failure == SkillLearningFailure.Prerequisite && dependent.Trees[0].Sp == 18,
            "前置降至零而后继保留时整批失败，原余额和绑定不动");
        var allRefund = SkillLearning.Plan(0, 0, 10, dependent, new(0, 0, [new(6, true, 1), new(5, true, 1)]), Resolve, free, 30);
        That(allRefund.Success && allRefund.State!.Trees[0].Sp == 30 && allRefund.State.Trees[0].Skills.Count == 0 &&
            allRefund.State.Trees[1].Sp == 18, "同批退后继与前置可完整退款，不改另一树");
        SkillLearningResult result = SkillLearning.Plan(0, 0, 10, source, Request(), Resolve);
        That(result.Success && result.State!.Trees[0].Sp == 23 && result.State!.Trees[0].Skills.Single(skill => skill.Id == 5) == new LearnedSkill(0, 5, 1),
            "unlearned skill costs current0 entry and takes first vacant ordinary slot");
        That(source.Trees[0].Sp == 30 && source.Trees[0].Skills.Count == 1 && result.State!.Trees[1].Sp == 30,
            "plan does not mutate original or other context");
        That(result.Changes.SequenceEqual(new[] { new LearnedSkill(0, 5, 1) }), "ACK changes contain actual stable slot ID and new level");
        foreach ((ushort id, byte expectedSlot) in new[] { ((ushort)12, (byte)12), ((ushort)13, (byte)54), ((ushort)14, (byte)102) })
        {
            var passive = SkillLearning.Plan(0, 0, 10, source, Request(id), Resolve);
            That(passive.Success && passive.Changes.Single() == new LearnedSkill(expectedSlot, id, 1) &&
                passive.State!.Trees[0].Sp == 27 && passive.State!.Trees[1].Sp == 30,
                "新学被动按自身分组扣点和绑定 " + id);
        }
        That(SkillLearning.Plan(0, 0, 10, source, Request(16), Resolve).Failure == SkillLearningFailure.Unsupported,
            "缺少被动分组声明时不猜新槽位");
        That(SkillLearning.Plan(0, 0, 10, source, Request(200), Resolve).Changes.Single().Slot == 138,
            "特殊映射技能使用138起的固定槽位");
        var primaryFull = Enumerable.Range(0, 6).Select(index => new LearnedSkill((byte)index, (ushort)(1000 + index), 1)).ToArray();
        That(SkillLearning.Plan(0, 0, 10, State(30, primaryFull), Request(), Resolve).Changes.Single().Slot == 6,
            "普通主动六个主快捷槽满后使用扩展快捷槽");
        for (int slot = 6; slot < 12; slot++)
        {
            var filled = State(30, Enumerable.Range(0, slot).Select(index => new LearnedSkill((byte)index, (ushort)(1000 + index), 1)).ToArray());
            That(SkillLearning.Plan(0, 0, 10, filled, Request(), Resolve).Changes.Single().Slot == slot,
                "新学主动依次填实际扩展快捷栏 " + slot);
        }
        var manyInitial = SkillLearning.Initialize(0, Enumerable.Range(300, 13).Select(id => new CharacterSkill((ushort)id, 1)).ToArray(),
            (_, id) => Definition(id, [0], 3, 1, 0));
        That(manyInitial.Success && manyInitial.State!.Trees.All(tree => tree.Skills.Select(skill => skill.Slot)
            .SequenceEqual(Enumerable.Range(0, 12).Select(slot => (byte)slot).Append((byte)150))),
            "新角色或GM重建的主动预填整个12格，第13个进入普通主动区");
        var quickFull = primaryFull.Concat(Enumerable.Range(6, 6).Select(index => new LearnedSkill((byte)index, (ushort)(1000 + index), 1))).ToArray();
        That(SkillLearning.Plan(0, 0, 10, State(30, quickFull), Request(), Resolve).Changes.Single().Slot == 150,
            "主动快捷槽全满后进入主动组，不借用被动空槽");
        var passiveFull = State(30, Enumerable.Range(102, 36).Select(index => new LearnedSkill((byte)index, (ushort)(1000 + index), 1)).ToArray());
        That(SkillLearning.Plan(0, 0, 10, passiveFull, Request(14), Resolve).Failure == SkillLearningFailure.Capacity,
            "被动组满时不借用快捷槽、其他组或特殊预留槽");
        var oldLearning = State(30, [new(3, 14, 1)]);
        var oldCharacter = new Character(1, 0, "OldSkill", 7, new(38, 1, 0, 0, 0), null)
        {
            Skills = [new(14, 1)], Learning = oldLearning
        };
        var kept = SkillLearning.Initialize(oldCharacter, (_, _) => throw new InvalidOperationException("旧树不应重新读资源"));
        That(kept.Success && ReferenceEquals(kept.State, oldLearning), "已初始化旧角色不重建槽位或重置余额经验");
        var oldUpgrade = SkillLearning.Plan(0, 0, 10, oldLearning, Request(14), Resolve);
        That(oldUpgrade.Success && oldUpgrade.Changes.Single() == new LearnedSkill(3, 14, 2) && oldUpgrade.State!.Trees[0].Sp == 27,
            "已有旧绑定升级保持原槽，不附带存量迁移");
        SkillLearningResult again = SkillLearning.Plan(0, 0, 10, result.State!, Request(), Resolve);
        That(again.Success && again.State!.Trees[0].Sp == 12 && again.Changes.Single() == new LearnedSkill(0, 5, 2),
            "same packet again is another plus1 delta not duplicate identity or target level");
        SkillLearningResult third = SkillLearning.Plan(0, 0, 10, again.State!, Request(), Resolve);
        That(third.Success && third.State!.Trees[0].Sp == 1 && third.Changes.Single().Level == 3,
            "repeat-last cost applies current learned level index");
        That(SkillLearning.Plan(0, 0, 10, third.State!, Request(), Resolve).Failure == SkillLearningFailure.Mastered, "最终成长上限阻止继续学习");
        That(SkillLearning.Plan(0, 0, 1, source, Request(), Resolve).Success, "required level exact boundary admitted");
        That(SkillLearning.Plan(0, 0, 2, result.State!, Request(), Resolve).Failure == SkillLearningFailure.RequiredLevel,
            "next required level uses basis plus current times range");
        var contractSkill = Definition(21, [3], 1, 17, 0);
        SkillLearningDefinition ContractDefinition(byte job, ushort id) => id == 21 ? contractSkill : Resolve(job, id);
        That(SkillLearning.Plan(0, 0, 10, source, Request(21), ContractDefinition, contractLevels: 5).Failure == SkillLearningFailure.RequiredLevel &&
            SkillLearning.Plan(0, 0, 10, source, Request(21), ContractDefinition, contractLevels: 7).Success &&
            SkillLearning.Plan(0, 0, 10, source, Request(21), ContractDefinition, contractLevels: 0).Failure == SkillLearningFailure.RequiredLevel,
            "合成声明5与7分别参与真实SP资格，缺少加成0不回退为固定值");
        var contractTp = new SkillLearningDefinition(0, 22, "skill/contract-tp-fixture.skl", SkillType.Active,
            [0], Enumerable.Repeat(1, 6).ToArray(), Enumerable.Repeat(1, 6).ToArray(), 17, 0, [], new int[6], true, false, [],
            skillClass: 3, tpCosts: [1]);
        That(SkillLearning.Plan(0, 0, 10, source, Request(22), (_, _) => contractTp, contractLevels: 7).Failure == SkillLearningFailure.RequiredLevel,
            "TP技能不借用任何达人声明加成，仍按实际角色等级检查");
        var fixedContract = new SkillLearningDefinition(0, 23, "skill/contract-fixed-fixture.skl", SkillType.Active,
            [0], Enumerable.Repeat(20, 6).ToArray(), Enumerable.Repeat(20, 6).ToArray(), 1, 2, [], new int[6], false, false, [],
            fixedLevel: new(2, 1, true), skillFitnessGrowtypes: [0]);
        That(fixedContract.GetFixedLevel(10, 0, 0) == 5 && fixedContract.GetFixedLevel(10, 0, 5) == 7 &&
            fixedContract.GetFixedLevel(10, 0, 7) == 8 && fixedContract.GetFixedLevel(10, 0, int.MaxValue) == 20,
            "固定赠技使用声明加成与间隔计算，仍受资源maximum限制且大整数不溢出");
        That(SkillLearning.Plan(0, 0, 10, State(6, []), Request(), Resolve).Failure == SkillLearningFailure.InsufficientSp, "point shortage rejects all state");
        That(SkillLearning.Plan(0, 0, 10, State(7, []), Request(), Resolve).State!.Trees[0].Sp == 0, "exact SP cost admits zero remaining balance");
        That(SkillLearning.Plan(1, 0, 10, source, Request(), Resolve).Failure == SkillLearningFailure.MissingSkill, "wrong job list cannot borrow another job skill");
        That(SkillLearning.Plan(0, 0, 10, source, Request(99), Resolve).Failure == SkillLearningFailure.MissingSkill, "missing resource skill identified");
        That(SkillLearning.Plan(0, 1, 10, source, Request(), Resolve).Success, "普通转职索引允许匹配定义学习");
        That(SkillLearning.Plan(0, 0, 10, source, Request(8), Resolve).Failure == SkillLearningFailure.Unsupported, "feature TP skill excluded from SP branch");
        That(SkillLearning.Plan(0, 0, 10, source, Request(9), Resolve).Failure == SkillLearningFailure.Unsupported, "combo learning not ordinary any-slot assignment");
        That(SkillLearning.Plan(0, 0, 10, source, Request(6), Resolve).Failure == SkillLearningFailure.Prerequisite, "unmet prerequisite blocks learning");
        That(SkillLearning.Plan(0, 0, 10, source, Request(10), Resolve).Success,
            "存在资源的未学前置等级0可满足required0，不伪造missing getter");
        That(SkillLearning.Plan(0, 0, 10, source, Request(11), Resolve).Failure == SkillLearningFailure.Unsupported,
            "未知资源前置不能将null getter当level0放行");
        var batch = new SkillLearningCommand(0, 1, [new(5, false, 1), new(6, false, 1)]);
        SkillLearningResult batchResult = SkillLearning.Plan(0, 0, 10, source, batch, Resolve);
        That(batchResult.Success && batchResult.State!.Trees[0].Sp == 18 && batchResult.Changes.Select(skill => skill.Id).SequenceEqual(new ushort[] { 5, 6 }),
            "ordered distinct batch applies prerequisite from preceding accepted step");
        SkillLearningResult deniedBatch = SkillLearning.Plan(0, 0, 10, State(10, []), batch, Resolve);
        That(deniedBatch.Failure == SkillLearningFailure.InsufficientSp && deniedBatch.State is null && deniedBatch.Changes.Count == 0,
            "late batch failure exposes no partial state or ACK changes");
        var context1 = SkillLearning.Plan(0, 0, 10, source, Request(context: 1, mode: 1), Resolve);
        That(context1.Success && context1.State!.Trees[1].Sp == 23 && context1.State!.Trees[0].Sp == 30 && context1.State!.Trees[1].Tp == 4,
            "context1 and mode1 use selected SP tree without fabricating TP change");
        foreach (SkillLearningCommand invalid in new SkillLearningCommand[]
        {
            new(2, 0, [new(5, false, 1)]), new(0, 2, [new(5, false, 1)]), new(0, 0, []),
            new(0, 0, [new(5, false, 0)]),
            new(0, 0, [new(5, false, 1), new(5, false, 1)])
        })
            That(SkillLearning.Plan(0, 0, 10, source, invalid, Resolve).Failure == SkillLearningFailure.InvalidRequest,
                "request bounds or unsupported signed delta rejected");
        int[] activeSlots = Enumerable.Range(0, 12).Concat(Enumerable.Range(150, 48)).ToArray();
        var full = State(30, activeSlots.Select(index => new LearnedSkill((byte)index, (ushort)(1000 + index), 1)).ToArray());
        That(SkillLearning.Plan(0, 0, 10, full, Request(), Resolve).Failure == SkillLearningFailure.Capacity,
            "主动合法区域全满，其他组空闲也不能分配");
        var lastVacancy = State(30, activeSlots.Where(index => index != 197).Select(index => new LearnedSkill((byte)index, (ushort)(1000 + index), 1)).ToArray());
        That(SkillLearning.Plan(0, 0, 10, lastVacancy, Request(), Resolve).Changes.Single().Slot == 197,
            "最后主动组空槽197是合法绑定，不继续扫描204以上");
        That(SkillLearning.Plan(0, 0, 10, CharacterLearningState.Uninitialized, Request(), Resolve).Failure == SkillLearningFailure.Unsupported,
            "uninitialized old character is not given fake empty initialized trees");
        var mutable = new List<LearnedSkill> { new(8, 7, 1) };
        SkillLearningResult frozen = SkillLearning.Plan(0, 0, 10, State(30, mutable), Request(), Resolve);
        mutable.Clear();
        That(frozen.State!.Trees.All(tree => tree.Skills.Any(skill => skill.Id == 7)), "result copies both context collections into read-only snapshots");

        // 同一批先退满再新学：退点释放的槽位仍要出现在回执中，不能分配给新技能，否则成功回包因槽位重复抛异常断线。
        // 匹配资源的 TP 技能按被动组 1（54–101）绑定，这里同样使用被动声明。
        SkillLearningDefinition TpDefinition(ushort id) => new(0, id, "skill/slot-reuse-fixture.skl", SkillType.Passive,
            [0], Enumerable.Repeat(5, 6).ToArray(), Enumerable.Repeat(5, 6).ToArray(), 1, 0, [], new int[6], true, false, [],
            skillClass: 3, skillFitnessGrowtypeCount: 2, tpCosts: [1]);
        var tpDefinitions = new Dictionary<ushort, SkillLearningDefinition> { [31] = TpDefinition(31), [32] = TpDefinition(32) };
        SkillLearningDefinition TpResolve(byte job, ushort id) => job == 0 ? tpDefinitions[id] : throw new KeyNotFoundException();
        var refundThenLearn = SkillLearning.Plan(0, 0, 10, State(0, [new(54, 31, 5)]),
            new(0, 0, [new(31, true, 5), new(32, false, 5)]), TpResolve, new Dictionary<ushort, byte>(), totalTp: 10);
        SkillPackets.Success(0, 0, refundThenLearn.State!, refundThenLearn.Changes);
        That(refundThenLearn.Success &&
            refundThenLearn.Changes.SequenceEqual(new[] { new LearnedSkill(54, 31, 0), new LearnedSkill(55, 32, 5) }),
            "同批退满再新学：被释放的槽位不再分配，新技能落到下一个空槽且成功回包不抛异常");
        Console.WriteLine($"PASS {Count - checks} pure skill learning planning assertions");
    }

    public static void Run(PvfArchive pvf, CharacterCatalog characters)
    {
        int checks = Count;
        SkillLearningCommand Request(ushort id = 5) => new(0, 0, [new(id, false, 1)]);
        var actual = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
        That(actual.Get(0, 5).GetBasePointCost(0) == 15, "matching resource hardattack base cost independently known");
        SkillLearningResult real = SkillLearning.Plan(0, 0, 1, State(15, []), Request(), actual.Get);
        That(real.Success && real.State!.Trees[0].Sp == 0 && real.Changes.Single().Level == 1, "real PVF ordinary hardattack learning consumes15 SP");
        CharacterProfile actualProfile = characters.Get(0);
        SkillLearningResult realInitial = SkillLearning.Initialize(0, actualProfile.Skills, actual);
        That(actualProfile.Skills.Length == 6 && realInitial.Success && realInitial.State!.Trees.All(tree => tree.Sp == 0 && tree.Tp == 0),
            "real job0 all six initial skills bind with zero points including mastered resource without range");
        That(realInitial.State!.Trees.All(tree => tree.Skills.Select(skill => new CharacterSkill(skill.Id, skill.Level)).SequenceEqual(actualProfile.Skills)) &&
            realInitial.State!.Trees.All(tree => tree.Skills.Select(skill => skill.Slot).SequenceEqual(new byte[] { 0, 1, 2, 102, 103, 104 })),
            "真实初始技能保留原ID等级，主动与被动槽位分别投影两树");
        That(actual.Get(0, 169).SupportsOrdinarySpLearning &&
            SkillLearning.Plan(0, 0, 1, realInitial.State!, Request(169), actual.Get).Failure == SkillLearningFailure.Mastered,
            "后跳沿默认间隔支持学习规则，初始已学一级仍由真实上限拒绝继续升级");
        for (byte job = 0; job < 16; job++)
        {
            var profile = characters.Get(job);
            var initial = SkillLearning.Initialize(job, profile.Skills, actual);
            var created = CharacterCreation.Prepare("Layout", profile, new(38, 0, 0, 0), new(38, 0, 0, 0), actual.Get);
            foreach (var tree in created.InitialLearning.Trees)
            {
                var source = initial.State!.Trees.Single(value => value.Context == tree.Context);
                var first = source.Skills.SingleOrDefault(skill => skill.Slot == 0);
                var second = source.Skills.SingleOrDefault(skill => skill.Slot == 1);
                bool swapped = first is not null && second is not null && first.Id != 169 && second.Id != 169;
                That((!swapped || tree.Skills.Single(skill => skill.Slot == 0).Id == second!.Id &&
                    tree.Skills.Single(skill => skill.Slot == 1).Id == first!.Id) &&
                    source.Skills.Where(skill => !swapped || skill.Slot > 1).All(skill => tree.Skills.Contains(skill)) &&
                    tree.Skills.Select(skill => (skill.Id, skill.Level)).OrderBy(skill => skill.Id)
                        .SequenceEqual(source.Skills.Select(skill => (skill.Id, skill.Level)).OrderBy(skill => skill.Id)) &&
                    tree.Combos.Count == source.Combos.Count && source.Combos.All(combo =>
                        tree.Combos.Any(value => value.Root == combo.Root && value.Children.SequenceEqual(combo.Children))),
                    "新建角色双树只交换前两个快捷技能，后跳、等级、被动和连招保留：" + job);
            }
            if (job is 9 or 10)
            {
                That(initial.Success && initial.State!.Trees.All(tree => tree.Skills.Count == profile.Skills.Length), "特殊职业完整初始化 " + job);
                That(initial.State!.Trees.All(tree => tree.Skills.Where(skill => actual.Get(job, skill.Id).Type == SkillType.Passive)
                    .All(skill => skill.Slot is >= 102 and < 138)), "特殊职业初始被动不占快捷栏 " + job);
                That(initial.State.Trees.All(tree => tree.Combos.Count == (job == 9 ? 6 : 0)), "黑暗武士六个默认连招根");
                continue;
            }
            That(initial.Success && initial.State!.Trees.All(tree => tree.Sp == 0 && tree.Tp == 0 &&
                tree.Skills.OrderBy(skill => skill.Id).Select(skill => new CharacterSkill(skill.Id, skill.Level)).SequenceEqual(profile.Skills)),
                "普通职业初始技能完整保留且不赠点 " + job);
            foreach (var tree in initial.State!.Trees)
            {
                That(tree.Skills.Where(skill => actual.Get(job, skill.Id).Type == SkillType.Active).Select(skill => skill.Slot)
                    .SequenceEqual(new byte[] { 0, 1, 2 }), "初始主动使用前三个快捷槽 " + job);
                That(tree.Skills.Where(skill => actual.Get(job, skill.Id).Type == SkillType.Passive).All(skill => skill.Slot is >= 102 and < 138),
                    "初始被动避开主扩展快捷槽与特殊预留槽 " + job);
            }
        }
        CommonSkills(actual, characters);
        Console.WriteLine($"PASS {Count - checks} resource skill learning assertions");
    }

    private static void CommonSkills(SkillLearningCatalog catalog, CharacterCatalog characters)
    {
        ushort[] ids = [190,175,186,188,178,189];
        for (byte job = 0; job < 16; job++)
        {
            var profile = characters.Get(job);
            var initial = SkillLearning.Initialize(job, profile.Skills, catalog).State! with { BonusSp = 0, BonusTp = 0 };
            var growths = profile.GrowthOptions.GrowTypes.SelectMany(grow => Enumerable.Range(0, grow.Awakenings.Count + 1)
                .Select(awakening => checked((byte)(grow.Value | awakening << 4)))).Distinct();
            foreach (byte growth in growths)
            {
                var state = SkillPointLedger.Synchronize(job, growth, 95, initial, catalog);
                var allowed = ids.Where(id => catalog.Get(job,id).GetMaximum(growth) > 0).ToArray();
                var request = new SkillLearningCommand(0,0,allowed.Select(id => new SkillLearningChangeRequest(id,false,1)).ToArray());
                var result = SkillLearning.Plan(job,growth,95,state,request,catalog);
                That(result.Success && result.Changes.Select(skill => skill.Id).SequenceEqual(allowed) &&
                    result.State!.Trees[0].Sp == state.Trees[0].Sp - allowed.Sum(id => catalog.Get(job,id).GetPointCost(0,growth)) &&
                    result.State.Trees[1].Skills.SequenceEqual(state.Trees[1].Skills) && result.State.Trees[1].Sp == state.Trees[1].Sp,
                    "16职业合法成长的受身、跃翔及四类暴击混合学习，费用与另一树保留："+job+"/"+growth);
                That(result.Changes.Where(skill => catalog.Get(job,skill.Id).Type == SkillType.Active).All(skill => skill.Slot < 12 || skill.Slot is >= 150 and < 198) &&
                    result.Changes.Where(skill => catalog.Get(job,skill.Id).Type == SkillType.Passive).All(skill => skill.Slot is >= 102 and < 138),
                    "通用主动使用快捷栏或已满后的主动区，被动使用基础组："+job+"/"+growth);
                That(SkillLearning.Plan(job,growth,95,result.State!,new(0,0,[new(190,false,1)]),catalog).Failure == SkillLearningFailure.Mastered,
                    "受身蹲伏缺省间隔不放宽资源一级上限："+job+"/"+growth);
                foreach (ushort id in ids.Except(allowed))
                    That(SkillLearning.Plan(job,growth,95,state,new(0,0,[new(id,false,1)]),catalog).Failure == SkillLearningFailure.Mastered,
                        "职业资源零上限继续禁止学习："+job+"/"+growth+"/"+id);
            }
        }

        // 黑暗武士新学主动只进下排排列区（150–197），排满退普通技能区，绝不占上排快捷栏。
        SkillLearningDefinition Dark(ushort id, bool combo = false) =>
            new(9, id, "skill/fixture.skl", SkillType.Active, [0], Enumerable.Repeat(3, 6).ToArray(),
                Enumerable.Repeat(3, 6).ToArray(), 1, 0, [], new int[6], false, combo, [], 3, 6);
        That(SkillSlotAllocator.Allocate(Dark(5), []) == 150,
            "黑暗武士新学主动优先进入排列区150，不占上排快捷栏");
        That(SkillSlotAllocator.Allocate(Dark(5), Enumerable.Range(0, 12).Select(slot => (byte)slot).ToHashSet()) == 150,
            "快捷栏完全空闲时新学主动也不使用0–11");
        var arrangementFull = Enumerable.Range(150, 48).Select(slot => (byte)slot).ToHashSet();
        That(SkillSlotAllocator.Allocate(Dark(5), arrangementFull) == 12,
            "排列区排满后新学主动进入普通技能区12，不回退快捷栏");
        arrangementFull.UnionWith(Enumerable.Range(12, 126).Select(slot => (byte)slot));
        That(SkillSlotAllocator.Allocate(Dark(5), arrangementFull) == -1,
            "排列区与普通技能区都满时整笔拒绝，不伪造快捷栏绑定");
        That(SkillSlotAllocator.Allocate(Dark(5), arrangementFull, initial: true) == -1,
            "初始建角布局保持原规则，排列区满即失败");
        That(SkillSlotAllocator.Allocate(Dark(5, combo: true), []) == 0,
            "连招根仍按主动规则占用快捷栏");
    }

    private static CharacterLearningState State(ushort sp, IReadOnlyList<LearnedSkill> skills) => new(123,
        Array.AsReadOnly(new CharacterSkillTree[] { new(0, sp, 4, skills), new(1, sp, 4, skills) }));

    private static SkillLearningDefinition Definition(ushort id, int[] costs, int maximum, int basis, int range,
        SkillPrerequisite[]? prerequisites = null, bool feature = false, bool combo = false,
        SkillType type = SkillType.Active, int? skillClass = 3, int? growtypeCount = 6) =>
        new(0, id, "skill/fixture.skl", type, costs, Enumerable.Repeat(maximum, 6).ToArray(),
            Enumerable.Repeat(maximum, 6).ToArray(), basis, range, prerequisites ?? [], new int[6], feature, combo, [], skillClass, growtypeCount);
}
