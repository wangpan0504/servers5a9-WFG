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
            var initialized = SkillLearning.Initialize(job, characters.Get(job).Skills, catalog);
            Check(initialized.Success, "特殊职业初始化 " + job);
            var special = SkillPointLedger.Synchronize(job, 0, 60, initialized.State!, catalog);
            Check(special.Trees.All(tree => tree.Sp == catalog.TotalSp(60) && tree.Tp == 11), "特殊职业免费基线与双树点数 " + job);
            ushort id = job == 9 ? (ushort)77 : (ushort)131;
            var learned = SkillLearning.Plan(job, 0, 60, special, new(0, 0, [new(id, false, 1)]), catalog);
            Check(learned.Success, "特殊职业技能等级与资源费用 " + job);
            Check(job == 9 ? learned.Changes.Single().Slot == 7 : learned.Changes.Single().Slot is >= 102 and < 138,
                "黑武新学主动填扩展空位，缔造被动使用分组 " + job);
            var character = new Character(1, 0, "Special", 1, towns.Room, towns.InitialReturn)
            { Job = job, Level = 49, Learning = initialized.State! with { Experience = growth.GetLevelStartExperience(49) } };
            var grown = CharacterGrowthPlan.AddExperience(character, growth.GetLevelExperienceSpan(49), growth);
            Check(grown.Success && grown.Plan!.Learning.Trees.All(tree => tree.Tp == 1), "特殊职业49升50正常经验成长 " + job);
            if (job == 9)
            {
                var combos = SkillCustomization.Combo(job, special, 0, [new(118, new ushort[] { 46, 8 }), new(119, new ushort[] { 46 })], catalog);
                Check(combos.Success && combos.State!.Trees[0].Combos.Single(combo => combo.Root == 118).Children.SequenceEqual(new ushort[] { 8 }), "跨组重复子技能保留最后位置");
                Check(!SkillCustomization.Combo(job, special, 0, [new(118, new ushort[] { 174 })], catalog).Success, "被动不能放连招");
                Check(SkillPackets.Combos(combos.State!)[0] == 0 && combos.State!.Trees[1].SameCombos(special.Trees[1]), "连招回放和另一树独立");
                Check(!SkillLayout.Move(job, special, new(0, 0, 198, -1, 0), catalog.Get).Success,
                    "连招根不能移出客户端支持的主六槽，双向交换仍校验根位置");
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
            var special = repository.Create(owner, new NewCharacter("ComboStore", towns.Room, towns.InitialReturn)
            { Job = 9, Skills = specialProfile.Skills, InitialLearning = SkillLearning.Initialize(9, specialProfile.Skills, catalog).State! });
            var planned = SkillCustomization.Combo(9, special.Learning, 0, [new(118, new ushort[] { 46 }), new(119, new ushort[] { 8 })], catalog);
            special = repository.CommitLearning(owner, special, planned.State!);
            Check(repository.List(owner).Single(value => value.Id == special.Id).Learning.Trees.Zip(special.Learning.Trees).All(pair => pair.First.SameCombos(pair.Second)),
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
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    private static void Check(bool value, string name) => TestCheck.That(value, "技能扩展 " + name);
    private static void Reject(Action action, string name)
    {
        try { action(); } catch (Exception error) when (error is InvalidDataException or InvalidOperationException or SqliteException) { Check(true, name); return; }
        throw new InvalidOperationException("FAIL " + name);
    }
}
