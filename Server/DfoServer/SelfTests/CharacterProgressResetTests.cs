using System.Text.Json;
using DfoServer.Development;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class CharacterProgressResetTests
{
    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters)
    {
        int start = Count;
        var skills = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, skills);
        var reset = new CharacterProgressReset(characters, skills, growth);
        var owners = new AccountOwners();
        string root = Path.Combine(Path.GetTempPath(), "S5A9-gm-reset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            using var owner = owners.Acquire("gm-reset", () => { });
            var roles = new List<Character>();
            for (byte job = 0; job < 16; job++)
            {
                var profile = characters.Get(job);
                var initial = SkillLearning.Initialize(job, profile.Skills, skills).State! with
                { BonusSp = 137, BonusTp = 7, Experience = growth.GetLevelStartExperience(95) };
                initial = SkillPointLedger.Synchronize(job, 0, 95, initial, skills);
                var role = repository.Create(owner, new NewCharacter("Reset" + job, towns.Room, towns.InitialReturn)
                { Job = job, Level = 95, AcceptedStoryLevel = 10, Skills = profile.Skills, InitialLearning = initial,
                    Inventory = DfoServer.Game.Inventory.Services.InventoryRewardGrantService.CreateStarter(profile) });
                roles.Add(role);
                var options = reset.GetOptions(job);
                That(options.BaseName.Length > 0 && options.GrowTypes.Any(option => option.Value == 0), "16职业基础分支来自CHR " + job);
                foreach (var option in options.GrowTypes)
                    for (int awakening = 0; awakening <= option.Awakenings.Count; awakening++)
                    {
                        byte grow = (byte)(option.Value | awakening << 4);
                        var plan = reset.Prepare(role, new(CharacterProgressField.Grow, owner.Account, role.Name, grow, 0));
                        var free = SkillPointLedger.FreeBaseline(job, grow, 95, skills);
                        That(plan.Learning.Trees.All(tree => tree.Sp == skills.TotalSp(95) + 137 && tree.Tp == skills.TotalTp(95) + 7 &&
                            tree.Skills.All(skill => skill.Command == "" && free[skill.Id] == skill.Level) && tree.Skills.Count == free.Count),
                            "每个实际分支和觉醒重建完整双树与点数 " + job + "/" + grow);
                    }
            }
            var before = roles[0];
            var bought = SkillLearning.Plan(0, 0, 95, before.Learning, new(0, 0, [new(5, false, 2)]), skills);
            That(bought.Success, "重置测试真实学习消耗SP");
            before = repository.CommitLearning(owner, before, bought.State!, bought.TotalSp, bought.TotalTp);
            var command = SkillCustomization.Commands(0, before.Learning, [new(5, "020304")], false, skills);
            before = repository.CommitLearning(owner, before, command.State!);
            var tree0 = before.Learning.Trees.Single(tree => tree.Context == 0);
            int spent = skills.TotalSp(95) + 137 - tree0.Sp;
            var ghost = tree0.Skills.Single(skill => skill.Id == 5);
            var plan50 = reset.Prepare(before, new(CharacterProgressField.Level, owner.Account, before.Name, 50, 0));
            var after = repository.ResetProgress(owner, plan50);
            That(spent > 0 && after.Level == 50 && after.Learning.Experience == growth.GetLevelStartExperience(50) && after.Revision == before.Revision + 1 &&
                after.Learning.Trees.All(tree => tree.Sp == skills.TotalSp(50) + 137 - (tree.Context == 0 ? spent : 0) && tree.Tp == skills.TotalTp(50) + 7),
                "改等级原子重算经验、两树SP/TP=新总额+额外来源−已学费用");
            That(after.Learning.Trees.Single(tree => tree.Context == 0).Skills.Contains(ghost) && ghost.Command == "020304" &&
                after.Position == before.Position &&
                after.Return == before.Return && after.AcceptedStoryLevel == 10 && JsonSerializer.Serialize(after.Inventory) == JsonSerializer.Serialize(before.Inventory) &&
                after.CompletedQuests.SequenceEqual(before.CompletedQuests), "改等级保留已学技能、槽位、指令，以及剧情、位置、任务和资产");
            Reject(() => reset.Prepare(after, new(CharacterProgressField.Level, owner.Account, after.Name, 1, 0)), "降到低于已学技能需求等级整笔拒绝");
            That(Same(after, repository.LoadCharacter(owner, after.Slot)), "拒绝降级不改存档");
            // 升级不做技能等级门限校验：存量里已学技能高于角色等级时，不能把升级一并锁死。
            var overLeveled = after with
            {
                Learning = after.Learning with
                {
                    Trees = Array.AsReadOnly(after.Learning.Trees.Select(tree => tree.Context != 0 ? tree
                        : tree with { Skills = Array.AsReadOnly(tree.Skills.Select(skill =>
                            skill.Id == ghost.Id ? skill with { Level = 20 } : skill).ToArray()) }).ToArray())
                }
            };
            var raised = reset.Prepare(overLeveled, new(CharacterProgressField.Level, owner.Account, overLeveled.Name, 60, 0));
            That(raised.Level == 60 && raised.Learning.Trees.Single(tree => tree.Context == 0).Skills
                .Single(skill => skill.Id == ghost.Id).Level == 20,
                "升级不受已学技能等级门限限制且保留技能等级");
            Reject(() => repository.ResetProgress(owner, plan50), "旧重置规划不能重复提交");
            var changed = repository.ResetProgress(owner, reset.Prepare(after, new(CharacterProgressField.Grow, owner.Account, after.Name, 33, 0)));
            That(changed.GrowType == 33 && changed.Level == 50 && changed.Learning.Experience == after.Learning.Experience &&
                changed.Learning.Trees.All(tree => tree.Sp == skills.TotalSp(50) + 137 && tree.Tp == skills.TotalTp(50) + 7), "转职及二觉使用新免费基线，经验和额外点数保留");
            Reject(() => reset.Prepare(changed, new(CharacterProgressField.Grow, owner.Account, changed.Name, 16, 0)), "普通未转职不能觉醒");
            Reject(() => reset.Prepare(changed, new(CharacterProgressField.Grow, owner.Account, changed.Name, 113, 0)), "未声明觉醒拒绝");
            That(DevelopmentRequest.Parse("grow " + changed.Name + " 1 2", owner.Account).Primary == 33, "分支和觉醒请求编码为同一grow字节");
            using var json = JsonDocument.Parse(DevelopmentGm.Execute(repository, owners, _ => new GameSession(repository, towns, owner),
                DevelopmentRequest.Parse("grow-options " + changed.Name, owner.Account), progress: reset)[3..]);
            That(json.RootElement.GetProperty("first").GetInt32() == 1 && json.RootElement.GetProperty("second").GetInt32() == 2 &&
                json.RootElement.GetProperty("options").GetProperty("growTypes")[1].GetProperty("awakenings").GetArrayLength() == 2, "GM查询返回当前分支和真实一二觉选项");

            var customized = SkillCustomization.Commands(changed.Job, changed.Learning, [new(5, "020304")], false, skills);
            changed = repository.CommitLearning(owner, changed, customized.State!);

            var session = new GameSession(repository, towns, owner);
            session.Select(changed.Slot); session.MarkReady();
            var selected = session.Selected!;
            using (var db = new SqliteConnection("Data Source=" + Path.Combine(root, "game.db") + ";Pooling=False"))
            {
                db.Open();
                using var sql = db.CreateCommand();
                sql.CommandText = "CREATE TRIGGER reject_gm_reset BEFORE INSERT ON character_skill_points WHEN NEW.context=1 BEGIN SELECT RAISE(ABORT,'late reset failure'); END";
                sql.ExecuteNonQuery();
                Reject(() => session.ResetProgress(new(CharacterProgressField.Level, owner.Account, selected.Name, 1, 0), reset), "后段双树写入失败回滚");
                var durable = repository.LoadCharacter(owner, selected.Slot);
                That(ReferenceEquals(selected, session.Selected) && Same(selected, durable), "后段失败保留SQL与在线等级、经验、点数和元数据");
                sql.CommandText = "DROP TRIGGER reject_gm_reset"; sql.ExecuteNonQuery();
            }
            var installed = session.ResetProgress(new(CharacterProgressField.Level, owner.Account, selected.Name, 1, 0), reset);
            That(ReferenceEquals(installed, session.Selected) && installed.Level == 1 && installed.Learning.Experience == 0 &&
                installed.Learning.Trees.All(tree => tree.Sp == skills.TotalSp(1) + 137 && tree.Tp == 7), "成功重置才安装实际在线快照");
            var idle = session.ResetProgress(new(CharacterProgressField.Level, owner.Account, roles[1].Name, 60, 0), reset);
            That(ReferenceEquals(installed, session.Selected) && idle.Level == 60 && roles[2].Level == repository.LoadCharacter(owner, roles[2].Slot).Level,
                "重置同账号其他角色不替换选中角色或影响第三角色");
            var stale = reset.Prepare(installed, new(CharacterProgressField.Level, owner.Account, installed.Name, 60, 0));
            repository.UpdateProgress(owner, new(CharacterProgressField.Skill, owner.Account, installed.Name, 5, 2));
            Reject(() => repository.ResetProgress(owner, stale), "同revision技能改变也拒绝旧计划");
            var dk = roles[9];
            var combo = SkillCustomization.Combo(9, dk.Learning, 0, [new(118, new ushort[] { 46 })], skills);
            dk = repository.CommitLearning(owner, dk, combo.State!);
            var dkLevel = repository.ResetProgress(owner, reset.Prepare(dk, new(CharacterProgressField.Level, owner.Account, dk.Name, 60, 0)));
            That(dkLevel.Level == 60 && dkLevel.Learning.Trees[0].Combos.Single(value => value.Root == 118).Children.SequenceEqual(new ushort[] { 46 }),
                "黑暗武士改等级保留自定义连招");
            var dkReset = repository.ResetProgress(owner, reset.Prepare(dkLevel, new(CharacterProgressField.Grow, owner.Account, dk.Name, dkLevel.GrowType, 0)));
            That(dkReset.Learning.Trees.All(tree => tree.Skills.Where(skill => skills.Get(9, skill.Id).HasComboDeclaration).All(skill => skill.Slot < 6) &&
                tree.Combos.Count == 6) && !dkReset.Learning.Trees[0].Combos.Single(value => value.Root == 118).Children.SequenceEqual(new ushort[] { 46 }),
                "黑暗武士重置恢复资源默认连招及根槽位");
            using var other = owners.Acquire("gm-legacy", () => { });
            var legacy = repository.Create(other, new("OldReset", towns.Room, towns.InitialReturn));
            var restored = repository.ResetProgress(other, reset.Prepare(legacy, new(CharacterProgressField.Level, other.Account, legacy.Name, 60, 0)));
            That(restored.Learning.Initialized && restored.Learning.BonusSp == 0 && restored.Learning.BonusTp == 0 &&
                restored.Learning.Trees.All(tree => tree.Sp == skills.TotalSp(60) && tree.Tp == skills.TotalTp(60)), "明确GM重置旧角色建立当前等级基线，不迁移历史布局");
            Reject(() => repository.ResetProgress(other, reset.Prepare(installed, new(CharacterProgressField.Level, other.Account, installed.Name, 60, 0))), "其他账号不能重置目标");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - start} GM等级、职业选项、双树重置和原子回滚断言");
    }

    private static bool Same(Character a, Character b) => a.Level == b.Level && a.GrowType == b.GrowType && a.Revision == b.Revision &&
        a.Learning.Experience == b.Learning.Experience && a.Learning.BonusSp == b.Learning.BonusSp && a.Learning.BonusTp == b.Learning.BonusTp &&
        a.Skills.SequenceEqual(b.Skills) && a.Learning.Trees.Zip(b.Learning.Trees).All(pair => pair.First.Sp == pair.Second.Sp &&
            pair.First.Tp == pair.Second.Tp && pair.First.Skills.SequenceEqual(pair.Second.Skills) && pair.First.SameCombos(pair.Second));

    private static void Reject(Action action, string message)
    {
        bool rejected = false;
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or SqliteException) { rejected = true; }
        That(rejected, message);
    }
}
