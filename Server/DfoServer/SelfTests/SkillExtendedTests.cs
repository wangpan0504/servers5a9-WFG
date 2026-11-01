using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer;

internal static class SkillExtendedTests
{
    public static void RunCore()
    {
        int start = TestCheck.Count;
        byte[] sample = [1, 3, 0, 3, 2, 3, 4];
        Check(SkillRequests.ParseCommands(sample).Single() == new SkillCommandChange(3, "020304"), "实收自定义记录计数与按键宽度");
        Check(SkillRequests.ParseCommands([1, 3, 0, 0]).Single().Command == "", "零字节恢复该技能默认");
        for (int i = 0; i < sample.Length; i++) Reject(() => SkillRequests.ParseCommands(sample[..i]), "截断指令拒绝");
        Reject(() => SkillRequests.ParseCommands([.. sample, 0]), "多余字节拒绝");
        var combo = SkillRequests.ParseCombo([0, 1, 118, 0, 2, 46, 0, 8, 0, 0]);
        Check(combo.Context == 0 && combo.Mode == 0 && combo.Combos.Single().Children.SequenceEqual(new ushort[] { 46, 8 }), "连招根、子技能及尾mode真实写宽");
        Reject(() => SkillRequests.ParseCombo([0, 1, 118, 0, 2, 46, 0, 8, 0]), "缺少尾mode不能误读最后ID高字节");
        Reject(() => SkillRequests.ParseCombo([0, 1, 118, 0, 7]), "连招超六子技能拒绝");
        var state = new CharacterLearningState(0, Array.AsReadOnly(new CharacterSkillTree[]
        {
            new(0, 0, 0, [new(0, 5, 1) { Command = "028004" }]), new(1, 0, 0, [new(0, 5, 1)])
        }));
        Check(Convert.ToHexString(SkillPackets.Initialized(state)).Contains("220402800104"), "field4为packed整数，高位按键不能直接写裸字节");
        var ack = SkillPackets.Success(0, 0, state, state.Trees[0].Skills);
        Check(ack.AsSpan(11, 5).SequenceEqual(new byte[] { 1, 3, 2, 128, 4 }), "升级ACK保留自定义hasExtra、数量和原字节");
        Check(SkillPacketsTests.DecodeInitialized(SkillPackets.Initialized(state))[0].Skills.Single().Command == "028004" &&
            SkillPacketsTests.DecodeSuccess(ack).Skills.Single().Command == "028004", "独立解码保留高位指令整数和ACK原字节");
        Console.WriteLine($"PASS {TestCheck.Count - start} 技能指令和连招协议边界断言");
    }

    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters)
    {
        int start = TestCheck.Count;
        var catalog = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, catalog);
        Check(catalog.TotalTp(49) == 0 && catalog.TotalTp(50) == 1 && catalog.TotalTp(60) == 11 && catalog.TotalTp(95) == 46,
            "匹配TP表逐等级累计，不能用当前级单项覆盖");
        var original = SkillLearning.Initialize(0, characters.Get(0).Skills, catalog).State! with { BonusSp = 137, BonusTp = 7 };
        var state = SkillPointLedger.Synchronize(0, 0, 95, original, catalog);
        Check(state.Trees.All(tree => tree.Tp == 53), "额外TP独立来源保留");
        var tp = PvfScript.List(pvf.ReadText("skill/swordmanskill.lst")).Keys.Select(id => catalog.Get(0, checked((ushort)id)))
            .First(skill => skill.SupportsTpLearning && skill.GetMaximum(0) >= 2 && skill.Prerequisites!.Count == 0);
        int cost = tp.GetPointCost(0, 0) + tp.GetPointCost(1, 0);
        var bought = SkillLearning.Plan(0, 0, 95, state, new(0, 0, [new(tp.SkillId, false, 2)]), catalog);
        Check(bought.Success && bought.State!.Trees[0].Tp == 53 - cost && bought.State.Trees[0].Sp == state.Trees[0].Sp &&
            bought.State.Trees[1].Tp == 53, "TP逐级扣费，SP与另一树保持");
        var refund = SkillLearning.Plan(0, 0, 95, bought.State!, new(0, 0, [new(tp.SkillId, true, 2)]), catalog);
        Check(refund.Success && refund.State!.Trees[0].Tp == 53 && !refund.State!.Trees[0].Skills.Any(skill => skill.Id == tp.SkillId), "TP全额退款删除零级绑定");
        var poor = state with { BonusTp = 0, Trees = Array.AsReadOnly(state.Trees.Select(tree => tree with { Tp = 0 }).ToArray()) };
        Check(SkillLearning.Plan(0, 0, 49, poor, new(0, 0, [new(tp.SkillId, false, 1)]), catalog).Failure == SkillLearningFailure.RequiredLevel, "TP角色等级门槛");
        Check(SkillLearning.Plan(0, 0, 50, poor, new(0, 0, [new(tp.SkillId, false, 2)]), catalog).Failure is SkillLearningFailure.RequiredLevel or SkillLearningFailure.InsufficientTp,
            "不足TP或逐级等级门槛不扣SP");
        var reset = SkillPointLedger.Reset(0, 0, 95, bought.State!, 0, catalog, 0);
        Check(reset.Success && reset.State!.Trees[0].Tp == 53 && reset.State.BonusSp == 137 && reset.State.BonusTp == 7, "整树重置派生SP/TP独立来源");
        var commands = SkillCustomization.Commands(0, state, [new(5, "020304")], false, catalog);
        Check(commands.Success && commands.State!.Trees[0].Skills.Single(skill => skill.Id == 5).Command == "020304" &&
            commands.State.Trees[1].Skills.All(skill => skill.Command == ""), "指令编辑仅主树不改变余额");
        var defaults = SkillCustomization.Commands(0, commands.State!, [], true, catalog);
        Check(defaults.Success && defaults.State!.Trees[0].Skills.All(skill => skill.Command == ""), "整树默认清空自定义字节");
        Check(!SkillCustomization.Commands(0, state, [new(174, "04")], false, catalog).Success, "被动不能配置施放指令");
        foreach (byte job in new byte[] { 9, 10 })
        {
            // 连招根的槽位按输入顺序分配；把根倒序喂入，使槽位顺序与技能ID相反，
            // 否则“按槽位排序”与“按技能ID排序”结果相同，回归用例无法区分修复是否生效。
            var initialSkills = characters.Get(job).Skills;
            var ordered = initialSkills.Where(skill => catalog.Get(job, skill.Id).HasComboDeclaration).Reverse()
                .Concat(initialSkills.Where(skill => !catalog.Get(job, skill.Id).HasComboDeclaration)).ToArray();
            var initialized = SkillLearning.Initialize(job, ordered, catalog);
            Check(initialized.Success, "特殊职业初始化 " + job);
            var special = SkillPointLedger.Synchronize(job, 0, 60, initialized.State!, catalog);
            Check(special.Trees.All(tree => tree.Sp == catalog.TotalSp(60) && tree.Tp == 11), "特殊职业免费基线与双树点数 " + job);
            ushort id = job == 9 ? (ushort)77 : (ushort)131;
            var learned = SkillLearning.Plan(job, 0, 60, special, new(0, 0, [new(id, false, 1)]), catalog);
            Check(learned.Success, "特殊职业技能等级与资源费用 " + job);
            // 黑暗武士新学主动只进下排排列区：取当前树 150–197 的首个空位，绝不占上排快捷栏。
            int expected = job == 9
                ? Enumerable.Range(150, 48).First(slot => special.Trees[0].Skills.All(skill => skill.Slot != slot))
                : -1;
            Check(job == 9 ? learned.Changes.Single().Slot == expected : learned.Changes.Single().Slot is >= 102 and < 138,
                "黑武新学主动填排列区首个空位，缔造被动使用分组 " + job);
            var character = new Character(1, 0, "Special", 1, towns.Room, towns.InitialReturn)
            { Job = job, Level = 49, Learning = initialized.State! with { Experience = growth.GetLevelStartExperience(49) } };
            var grown = CharacterGrowthPlan.AddExperience(character, growth.GetLevelExperienceSpan(49), growth);
            Check(grown.Success && grown.Plan!.Learning.Trees.All(tree => tree.Tp == 1), "特殊职业49升50正常经验成长 " + job);
            if (job == 9)
            {
                var replay = SkillPackets.Combos(special);
                var replayRoots = new List<ushort>();
                int cursor = 2;
                for (int page = 0; page < replay[1]; page++)
                {
                    byte replayContext = replay[cursor++];
                    int rootCount = replay[cursor++];
                    for (int i = 0; i < rootCount; i++)
                    {
                        if (replayContext == 0) replayRoots.Add((ushort)(replay[cursor] | replay[cursor + 1] << 8));
                        cursor += 3 + replay[cursor + 2] * 2;
                    }
                }
                var slotRoots = special.Trees.Single(tree => tree.Context == 0).Skills.OrderBy(skill => skill.Slot)
                    .Where(skill => catalog.Get(job, skill.Id).HasComboDeclaration).Select(skill => skill.Id).ToArray();
                Check(!slotRoots.SequenceEqual(slotRoots.OrderBy(id => id)), "连招夹具的槽位顺序与技能ID顺序不同");
                Check(replayRoots.SequenceEqual(slotRoots), "连招回放按根的快捷栏顺序而非技能ID");
                var combos = SkillCustomization.Combo(job, special, 0, [new(118, new ushort[] { 46, 8 }), new(119, new ushort[] { 46 })], catalog);
                Check(combos.Success && combos.State!.Trees[0].Combos.Single(combo => combo.Root == 118).Children.SequenceEqual(new ushort[] { 8 }), "跨组重复子技能保留最后位置");
                Check(!SkillCustomization.Combo(job, special, 0, [new(118, new ushort[] { 174 })], catalog).Success, "被动不能放连招");
                Check(SkillPackets.Combos(combos.State!)[0] == 0 && combos.State!.Trees[1].SameCombos(special.Trees[1]), "连招回放和另一树独立");
                Check(!SkillLayout.Move(job, special, new(0, 0, 198, -1, 0), catalog.Get).Success,
                    "连招根不能移出客户端支持的主六槽，双向交换仍校验根位置");
                // 自动组合的排列由客户端生成并提交；服务端只按该排列收走对应的上排技能。
                var quick = Enumerable.Range(7, 5).Select(value => (byte)value)
                    .Where(slot => special.Trees[0].Skills.All(skill => skill.Slot != slot)).ToArray();
                Check(quick.Length >= 2, "自动组合夹具存在空闲快捷槽");
                var arranged = special with { Trees = Array.AsReadOnly(special.Trees.Select(tree => tree.Context == 0
                    ? tree with { Skills = Array.AsReadOnly(tree.Skills.Select(skill => skill.Id == 46 ? skill with { Slot = quick[0] } :
                        skill.Id == 8 ? skill with { Slot = quick[1] } : skill).OrderBy(skill => skill.Slot).ToArray()) }
                    : tree).ToArray()) };
                var auto = SkillCustomization.Combo(9, arranged, 0, [new(118, new ushort[] { 46 })], catalog, autoCombo: true);
                Check(auto.Success && auto.RequiresSynchronization &&
                    auto.State!.Trees[0].Combos.Single(combo => combo.Root == 118).Children.SequenceEqual(new ushort[] { 46 }) &&
                    auto.State.Trees[0].Skills.Single(skill => skill.Id == 46).Slot is >= 150 and < 198 &&
                    auto.State.Trees[0].Skills.Single(skill => skill.Id == 8).Slot == quick[1] &&
                    auto.State.Trees[0].Skills.Count(skill => skill.Slot < 6) == 6,
                    "自动组合原样保存客户端排列，只收走排列包含的快捷技能并要求回放完整树");
                // 510阶段先按声明池收走快捷栏技能：客户端要等回执后才生成排列，重建必须提前。
                var prepared = SkillCustomization.PrepareAutoCombo(9, arranged, 0, catalog);
                Check(prepared.Success && prepared.RequiresSynchronization &&
                    prepared.State!.Trees[0].Skills.Single(skill => skill.Id == 46).Slot is >= 150 and < 198 &&
                    prepared.State.Trees[0].Skills.Single(skill => skill.Id == 8).Slot is >= 150 and < 198 &&
                    prepared.State.Trees[0].Skills.Where(skill => skill.Id is not (46 or 8))
                        .All(skill => skill.Slot == arranged.Trees[0].Skills.Single(value => value.Id == skill.Id).Slot),
                    "自动组合开始前按声明池收走快捷技能，其他槽位不变");
                var idle = SkillCustomization.PrepareAutoCombo(9, prepared.State!, 0, catalog);
                Check(idle.Success && !idle.RequiresSynchronization && ReferenceEquals(idle.State, prepared.State),
                    "快捷技能已全部收走后不重复回放完整树");
                var manual = SkillCustomization.Combo(9, arranged, 0, [new(118, new ushort[] { 46 })], catalog);
                Check(manual.Success && !manual.RequiresSynchronization &&
                    manual.State!.Trees[0].Skills.Single(skill => skill.Id == 46).Slot == quick[0],
                    "手工保存连招不改变快捷绑定也不触发完整树回放");
                var repeated = SkillCustomization.Combo(9, manual.State!, 0, [new(118, new ushort[] { 46, 8 })], catalog, autoCombo: true);
                Check(repeated.Success && repeated.RequiresSynchronization &&
                    repeated.State!.Trees[0].Combos.Single(combo => combo.Root == 118).Children.SequenceEqual(new ushort[] { 46, 8 }) &&
                    repeated.State.Trees[0].Skills.Single(skill => skill.Id == 46).Slot is >= 150 and < 198 &&
                    repeated.State.Trees[0].Skills.Single(skill => skill.Id == 8).Slot is >= 150 and < 198,
                    "自动组合保持客户端子技能顺序并将新收集的快捷技能移出上排");
            }
            else
            {
                var awakened = SkillPointLedger.Synchronize(job, 0x10, 60, special, catalog);
                Check(SkillLearning.Plan(job, 0x10, 60, awakened, new(0, 0, [new(273, false, 1)]), catalog).Success,
                    "特殊职业first0的自我觉醒secondary上限");
            }
        }
        Persist(towns, characters, catalog, commands.State!, bought.State!);
        Console.WriteLine($"PASS {TestCheck.Count - start} TP、指令、特殊职业资源及事务断言");
    }

    private static void Persist(TownCatalog towns, CharacterCatalog characters, SkillLearningCatalog catalog,
        CharacterLearningState commands, CharacterLearningState paid)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-skill-extended-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("extended", () => { });
            var profile = characters.Get(0);
            var character = repository.Create(owner, new NewCharacter("ExtTest", towns.Room, towns.InitialReturn)
            { Level = 95, Skills = profile.Skills, InitialLearning = commands });
            var paidCharacter = repository.Create(owner, new NewCharacter("TpTest", towns.Room, towns.InitialReturn)
            { Level = 95, Skills = profile.Skills, InitialLearning = commands });
            paidCharacter = repository.CommitLearning(owner, paidCharacter, paid, catalog.TotalSp(95) + 137, catalog.TotalTp(95) + 7);
            Check(repository.List(owner).Single(value => value.Id == paidCharacter.Id).Learning.Trees[0].Tp == paid.Trees[0].Tp,
                "TP消费、来源与技能等级同事务存档");
            var before = character;
            var update = SkillCustomization.Commands(0, character.Learning, [new(5, "04")], false, catalog);
            character = repository.CommitLearning(owner, character, update.State!);
            Check(repository.List(owner).Single(value => value.Id == character.Id).Learning.Trees[0].Skills.Single(skill => skill.Id == 5).Command == "04", "指令BLOB重读字节一致");
            Reject(() => repository.CommitLearning(owner, before, update.State!), "旧完整学习快照CAS拒绝");
            using var db = new SqliteConnection("Data Source=" + path);
            db.Open();
            using var trigger = db.CreateCommand();
            trigger.CommandText = "CREATE TRIGGER reject_command BEFORE INSERT ON character_skill_commands BEGIN SELECT RAISE(ABORT,'fixture'); END";
            trigger.ExecuteNonQuery();
            var failed = SkillCustomization.Commands(0, character.Learning, [new(5, "0304")], false, catalog);
            Reject(() => repository.CommitLearning(owner, character, failed.State!), "指令SQL失败不提前成功");
            Check(SkillRepositoryTests.Equal(character, repository.List(owner).Single(value => value.Id == character.Id)), "失败保留技能等级、费用、指令与revision");
            var specialProfile = characters.Get(9);
            var specialInitial = SkillLearning.Initialize(9, specialProfile.Skills, catalog).State!;
            // 互换根118/119的快捷槽位，使槽位顺序与root_id相反；存档表按root_id排序，
            // 读档必须按根槽位恢复，否则六列顺序与客户端不一致。
            var initialTree = specialInitial.Trees.Single(tree => tree.Context == 0);
            byte slot118 = initialTree.Skills.Single(skill => skill.Id == 118).Slot;
            byte slot119 = initialTree.Skills.Single(skill => skill.Id == 119).Slot;
            Check(slot118 != slot119, "连招根118/119占用不同快捷槽");
            var swapped = specialInitial with { Trees = Array.AsReadOnly(specialInitial.Trees.Select(tree => tree.Context == 0
                ? tree with { Skills = Array.AsReadOnly(tree.Skills.Select(skill => skill.Id == 118 ? skill with { Slot = slot119 } :
                    skill.Id == 119 ? skill with { Slot = slot118 } : skill).OrderBy(skill => skill.Slot).ToArray()) }
                : tree).ToArray()) };
            ushort[] slotOrder = swapped.Trees.Single(tree => tree.Context == 0).Skills.OrderBy(skill => skill.Slot)
                .Where(skill => catalog.Get(9, skill.Id).HasComboDeclaration).Select(skill => skill.Id).ToArray();
            Check(!slotOrder.SequenceEqual(slotOrder.OrderBy(id => id)), "存档夹具的槽位顺序与技能ID不同");
            var special = repository.Create(owner, new NewCharacter("ComboStore", towns.Room, towns.InitialReturn)
            { Job = 9, Skills = specialProfile.Skills, InitialLearning = swapped });
            var planned = SkillCustomization.Combo(9, special.Learning, 0, [new(118, new ushort[] { 46 }), new(119, new ushort[] { 8 })], catalog);
            Check(planned.Success && planned.State!.Trees.Single(tree => tree.Context == 0).Combos.Select(combo => combo.Root).SequenceEqual(slotOrder),
                "保存连招按根的快捷栏槽位排序，不按技能ID");
            special = repository.CommitLearning(owner, special, planned.State!);
            var stored = repository.List(owner).Single(value => value.Id == special.Id);
            Check(stored.Learning.Trees.Single(tree => tree.Context == 0).Combos.Select(combo => combo.Root).SequenceEqual(slotOrder),
                "读档按根的快捷栏槽位恢复连招列顺序");
            Check(stored.Learning.Trees.Zip(special.Learning.Trees).All(pair => pair.First.SameCombos(pair.Second)),
                "连招BLOB重读两树独立且根顺序稳定");
            trigger.CommandText = "CREATE TRIGGER reject_combo BEFORE INSERT ON character_skill_combos BEGIN SELECT RAISE(ABORT,'fixture'); END";
            trigger.ExecuteNonQuery();
            var rejectedCombo = SkillCustomization.Combo(9, special.Learning, 0, [new(118, new ushort[] { 8, 46 })], catalog);
            Reject(() => repository.CommitLearning(owner, special, rejectedCombo.State!), "连招SQL后段失败整笔回滚");
            var same = repository.List(owner).Single(value => value.Id == special.Id);
            Check(SkillRepositoryTests.Equal(special, same) && same.Learning.Trees.Zip(special.Learning.Trees).All(pair => pair.First.SameCombos(pair.Second)),
                "失败保留双树余额、绑定、连招、来源和revision");
            trigger.CommandText = "DROP TRIGGER reject_combo";
            trigger.ExecuteNonQuery();
            var deleted = repository.UpdateProgress(owner, new(CharacterProgressField.Skill, "extended", special.Name, 46, 0));
            Check(deleted.Learning.Trees.All(tree => tree.Skills.All(skill => skill.Id != 46) && tree.Combos.All(combo => !combo.Children.Contains((ushort)46))),
                "既有GM删除技能同事务移除连招引用，不留下损坏角色");
        }
        finally { SqliteConnection.ClearAllPools(); TestFiles.DeleteDirectory(root, recursive: true); }
    }

    private static void Check(bool value, string name) => TestCheck.That(value, "技能扩展 " + name);
    private static void Reject(Action action, string name)
    {
        try { action(); } catch (Exception error) when (error is InvalidDataException or InvalidOperationException or SqliteException) { Check(true, name); return; }
        throw new InvalidOperationException("FAIL " + name);
    }
}
