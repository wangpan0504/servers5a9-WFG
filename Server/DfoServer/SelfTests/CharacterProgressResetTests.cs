using System.Text.Json;
using DfoServer.Development;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using System.Collections.Immutable;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class CharacterProgressResetTests
{
    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources resources)
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
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {Count - start} GM等级、职业选项、双树重置和原子回滚断言");
        RunProfessions(towns, pvf, characters, resources);
    }

    public static void RunProfessions(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources resources)
    {
        int start = Count;
        var skills = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, skills);
        var reset = new CharacterProgressReset(characters, skills, growth);
        var owners = new AccountOwners();
        string root = Path.Combine(Path.GetTempPath(), "S5A9-gm-profession-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string database = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(database);
            new DfoServer.Game.Auth.AuthenticationService(new AuthenticationRepository(database)).Create("profession", "test-password");
            using var owner = owners.Acquire("profession", () => { });
            var profile = characters.Get(0);
            var learning = SkillLearning.Initialize(0, profile.Skills, skills).State! with
            { BonusSp = 137, BonusTp = 7, Experience = growth.GetLevelStartExperience(95), ExpertContractExpiresAt = 123 };
            learning = SkillPointLedger.Synchronize(0, 0, 95, learning, skills);
            var starter = InventoryRewardGrantService.CreateStarter(profile);
            var worn = repository.Create(owner, new("WornJob", towns.Room, towns.InitialReturn)
            { Job = 0, Level = 95, Skills = profile.Skills, InitialLearning = learning, Inventory = starter });
            Reject(() => reset.PrepareProfession(worn, 1, 0, resources.Items, resources.Contracts), "基础职业不兼容穿戴明确拒绝，不隐式删除或卸装");
            That(JsonSerializer.Serialize(worn.Inventory) == JsonSerializer.Serialize(repository.LoadCharacter(owner, worn.Slot).Inventory), "穿戴拒绝保留全部物品");
            var inventory = starter with { Gold = 12345,
                Items = starter.Items.Select((item, index) => item with { Position = new(InventoryListType.Main, (short)(9 + index)) }).ToImmutableArray() };
            var before = repository.Create(owner, new("ChangeJob", towns.Room, towns.InitialReturn)
            { Job = 0, Level = 95, Skills = profile.Skills, InitialLearning = learning, Inventory = inventory });
            before = repository.CommitSkillSwitchInventory(owner, new(before, new(5, []), null, "profession_test"));
            void Sql(string sql, object? value = null)
            {
                using var db = new SqliteConnection("Data Source=" + database + ";Pooling=False"); db.Open();
                using var command = db.CreateCommand(); command.CommandText = sql;
                if (value is not null) command.Parameters.AddWithValue("$value", value);
                command.ExecuteNonQuery();
            }
            Sql("UPDATE characters SET weapon_skin_state=$value WHERE id=" + before.Id,
                JsonSerializer.Serialize(new SkinStorageState([123], 123) { PartyFrames = [new(20001, 0)], SelectedPartyFrame = 20001 }));
            Sql("INSERT INTO account_expert_contracts(account,expires_at) VALUES($value,123)", owner.Account);
            Sql("INSERT INTO character_pvp_training_skills VALUES(" + before.Id + ",$value,'" + Guid.NewGuid().ToString("N") + "'," + before.Revision + ")",
                JsonSerializer.Serialize(new DfoServer.Game.Pvp.PvpTrainingSkills(before.Learning, 0, Guid.Empty)));
            before = repository.LoadCharacter(owner, before.Slot);
            var plan = reset.PrepareProfession(before, 1, 0, resources.Items, resources.Contracts);
            That(plan.Job == 1 && plan.Before.Job == 0, "目标基础职业与前置快照分别保留");
            Sql("CREATE TRIGGER reject_profession BEFORE INSERT ON character_skill_points WHEN NEW.context=1 BEGIN SELECT RAISE(ABORT,'profession rollback'); END");
            Reject(() => repository.ResetProgress(owner, plan), "基础职业双树后段失败整笔回滚");
            var failed = repository.LoadCharacter(owner, before.Slot);
            That(Same(before, failed) && failed.Job == before.Job && failed.SkillSwitchInventory.Same(before.SkillSwitchInventory) &&
                JsonSerializer.Serialize(failed.PvpTrainingSkills) == JsonSerializer.Serialize(before.PvpTrainingSkills), "回滚保留职业、技能、绑定及训练缓存");
            Sql("DROP TRIGGER reject_profession");
            var after = repository.ResetProgress(owner, plan);
            var free = SkillPointLedger.FreeBaseline(1, 0, 95, skills);
            That(after.Job == 1 && after.GrowType == 0 && after.Level == before.Level && after.Revision == before.Revision + 1 &&
                after.Learning.Experience == before.Learning.Experience && after.Learning.ExpertContractExpiresAt == 123 &&
                after.Learning.Trees.All(tree => tree.Sp == skills.TotalSp(95) + 137 && tree.Tp == skills.TotalTp(95) + 7 &&
                    tree.Skills.Count == free.Count && tree.Skills.All(skill => free[skill.Id] == skill.Level && skill.Command == "")),
                "跨基础职业原子重建双树与目标免费技能，保留经验、等级及额外SP/TP来源");
            That(after.SkillSwitchInventory.Same(SkillSwitchInventoryState.Empty) && after.PvpTrainingSkills is null,
                "基础职业改变即使分支相同也清除旧职业绑定与训练缓存");
            That(JsonSerializer.Serialize(after.Inventory) == JsonSerializer.Serialize(before.Inventory) && after.Position == before.Position &&
                after.CompletedQuests.SequenceEqual(before.CompletedQuests), "成功切换完整保留物品、皮肤、金币、位置及既有完成历史");
            Reject(() => repository.ResetProgress(owner, plan), "职业旧revision规划不能再次提交");
            using var other = owners.Acquire("other-profession", () => { });
            Reject(() => repository.ResetProgress(other, reset.PrepareProfession(after, 2, 0, resources.Items, resources.Contracts)), "基础职业变更拒绝跨账号提交");
            Reject(() => reset.PrepareProfession(after, 16, 0, resources.Items), "超界基础职业拒绝");
            Reject(() => reset.PrepareProfession(after, 2, 16, resources.Items), "未转职觉醒按正式职业目录拒绝");
            Reject(() => reset.PrepareProfession(after with { PvpPrivate = new(7) }, 2, 0, resources.Items), "决斗场专用角色身份不允许基础职业变化");
            That(Same(after, repository.LoadCharacter(owner, after.Slot)), "全部拒绝路径不改存档");
            foreach (byte targetJob in reset.Jobs)
            {
                var target = reset.PrepareProfession(after, targetJob, 0, resources.Items, resources.Contracts);
                That(target.Job == targetJob && target.Learning.Trees.All(tree => tree.Skills.All(skill =>
                    skills.Get(targetJob, skill.Id) is not null)), "正式资源全部基础职业可建立目标技能 " + targetJob);
            }
            string catalogText = DevelopmentGm.Execute(repository, owners, _ => null,
                DevelopmentRequest.Parse("profession-options", owner.Account), progress: reset);
            using var catalog = JsonDocument.Parse(catalogText[3..]);
            That(catalog.RootElement.GetProperty("baseJobChange").GetBoolean() && catalog.RootElement.GetProperty("jobs").GetArrayLength() == characters.Jobs.Count(),
                "完整职业目录不依赖账号或角色名册");
            That(DevelopmentRequest.Parse("profession ChangeJob 2 1 2", owner.Account) is { Primary: 2, Secondary: 33, Writes: true }, "基础职业命令完整保留job与grow");
            foreach (var command in new[] { "profession ChangeJob 16 0 0", "profession ChangeJob 1 6 0", "profession ChangeJob 1 0 3", "profession ChangeJob -1 0 0" })
                Reject(() => DevelopmentRequest.Parse(command, owner.Account), "基础职业非法命令拒绝 " + command);
            string occupied = DevelopmentGm.Answer("profession ChangeJob 2 0 0", owner.Account, repository, owners, _ => null, items: resources.Items, progress: reset);
            That(occupied.StartsWith("ERR ") && Same(after, repository.LoadCharacter(owner, after.Slot)), "即便在线查询无会话也不抢占已有账号租约");
            string live = DevelopmentGm.Answer("profession ChangeJob 2 0 0", owner.Account, repository, owners,
                _ => new GameSession(repository, towns, owner), items: resources.Items, progress: reset);
            That(live.StartsWith("ERR ") && live.Contains("退出该账号"), "已连接账号明确要求离线");
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {Count - start} GM基础职业目录、原子切换及拒绝断言");
    }

    private static bool Same(Character a, Character b) => a.Job == b.Job && a.Level == b.Level && a.GrowType == b.GrowType && a.Revision == b.Revision &&
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
