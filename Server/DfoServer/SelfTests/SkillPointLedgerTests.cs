using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer;

internal static class SkillPointLedgerTests
{
    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters)
    {
        int before = TestCheck.Count;
        var catalog = SkillLearningCatalog.Load(pvf);
        var growth = CharacterGrowthCatalog.Load(pvf, catalog);
        const byte level = 20;
        const int bonus = 137;
        int total = catalog.TotalSp(level) + bonus;
        var initial = SkillLearning.Initialize(0, characters.Get(0).Skills, catalog).State!;
        var state = initial with { Experience = growth.GetLevelStartExperience(level), BonusSp = bonus };
        state = SkillPointLedger.Synchronize(0, 0, level, state, catalog);
        Check(state.Trees.All(tree => tree.Sp == total), "累计等级SP加额外SP，初始与固定等级不扣费");
        var awakening = catalog.FixedSkills(0).Single(skill => skill.Path.EndsWith("/ultimatecrossslash.skl", StringComparison.Ordinal));
        Check(awakening.GetFixedLevel(95, 0, 0) == 0 && awakening.GetFixedLevel(95, 0x11, 0) == 0 &&
            awakening.GetFixedLevel(95, 0x15, 0) == 10, "觉醒固定技能读取secondary资格，不赠送给其他转职");
        var add = SkillLearning.Plan(0, 0, level, state, new(0, 0, [new(5, false, 2)]), catalog);
        Check(add.Success && add.State!.Trees[0].Sp == total - 30 && add.Changes.Single().Level == 3,
            "多级学习逐级计费，另一树独立，额外来源保留");
        var refund = SkillLearning.Plan(0, 0, level, add.State!, new(0, 0, [new(5, true, 1)]), catalog);
        Check(refund.Success && refund.State!.Trees[0].Sp == total - 15 && refund.Changes.Single().Level == 2,
            "降级按已学费用减少返还，不覆盖总来源");
        var floor = SkillLearning.Plan(0, 0, level, refund.State!, new(0, 0, [new(5, true, 255)]), catalog);
        Check(floor.Success && floor.Changes.Single().Level == 1 && floor.State!.Trees[0].Sp == total,
            "退到CHR免费基线，不能删除固有一级");
        var learned = SkillLearning.Plan(0, 0, level, state, new(0, 0, [new(12, false, 1)]), catalog);
        Check(learned.Success, "真实可移除技能已学习");
        var removed = SkillLearning.Plan(0, 0, level, learned.State!, new(0, 0, [new(12, true, 1)]), catalog);
        Check(removed.Success && removed.State!.Trees[0].Skills.All(skill => skill.Id != 12) &&
            SkillPackets.Success(0, 0, removed.State, removed.Changes)[10] == 0, "退到零删除绑定，ACK保留零级变更");
        var reset = SkillPointLedger.Reset(0, 0, level, add.State!, 0, catalog, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Check(reset.Success && reset.State!.Trees[0].Sp == total && reset.State.BonusSp == bonus &&
            reset.State.Trees[1].Skills.SequenceEqual(state.Trees[1].Skills), "整树重置保留额外SP、固有技能和另一树");
        Check(ReferenceEquals(SkillPointLedger.Reset(0, 0, level, reset.State!, 0, catalog, 0).State, reset.State),
            "重复无付费技能重置不产生新状态");
        var contract = state with { ExpertContractExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60 };
        var over = SkillLearning.Plan(0, 0, level, contract, new(0, 0, [new(5, false, 12)]), catalog);
        Check(over.Success && over.Changes.Single().Level == 13, "达人允许角色等级加五范围，按正常成本扣点");
        Check(SkillLearning.Plan(0, 0, level, state, new(0, 0, [new(5, false, 12)]), catalog).Failure == SkillLearningFailure.RequiredLevel,
            "无契约不允许超等级学习");
        var expired = SkillPointLedger.Synchronize(0, 0, level, over.State!, catalog);
        Check(expired.Trees[0].Skills.Single(skill => skill.Id == 5).Level == 10 && expired.Trees[0].Sp == total - 135 &&
            expired.BonusSp == bonus, "契约到期回落普通等级，退款并保留额外SP");
        foreach (byte job in Enumerable.Range(0, 16).Where(job => job is not (9 or 10)).Select(job => (byte)job))
        {
            var start = SkillLearning.Initialize(job, characters.Get(job).Skills, catalog).State!;
            foreach (byte first in Enumerable.Range(0, 6).Select(value => (byte)value))
            foreach (byte second in Enumerable.Range(0, first == 0 ? 1 : 3).Select(value => (byte)value))
            {
                byte profession = (byte)(first | second << 4);
                try { _ = catalog.FreeSkills(job, profession); }
                catch (NotSupportedException) { continue; }
                byte highLevel = 95;
                var synced = SkillPointLedger.Synchronize(job, profession, highLevel, start, catalog);
                Check(synced.Trees.All(tree => tree.Sp == catalog.TotalSp(highLevel)) && synced.BonusSp == 0,
                    "普通职业自动赠技与固有升级不占用SP " + job + "/" + profession);
                foreach (var fixedSkill in catalog.FixedSkills(job))
                    Check(synced.Trees.All(tree => !tree.Skills.Any(skill => skill.Id == fixedSkill.SkillId) ||
                        tree.Skills.Single(skill => skill.Id == fixedSkill.SkillId).Level >= fixedSkill.GetFixedLevel(highLevel, profession, 0)),
                        "固定等级资源同步 " + fixedSkill.Path);
            }
        }
        var character = new Character(1, 0, "GrowSource", 1, towns.Room, towns.InitialReturn)
        { Job = 0, GrowType = 1, Level = level, Learning = state };
        var next = CharacterGrowthPlan.AddExperience(character, growth.GetLevelExperienceSpan(level), growth);
        Check(next.Success && next.Plan!.Learning.BonusSp == bonus &&
            next.Plan.Learning.Trees.All(tree => tree.Sp == catalog.TotalSp(level + 1) + bonus),
            "转职升级同样派生累计SP，保留额外来源");
        Persist(towns, characters, catalog, growth, state, add.State!);
        Console.WriteLine($"PASS {TestCheck.Count - before} SP扣除、退款、固有成长、契约和来源存档断言");
    }

    private static void Persist(TownCatalog towns, CharacterCatalog characters, SkillLearningCatalog catalog,
        CharacterGrowthCatalog growth, CharacterLearningState state, CharacterLearningState learned)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var auth = new DfoServer.Game.Auth.AuthenticationService(new AuthenticationRepository(path));
            auth.Create("ledger", "fixture-password");
            using var owner = new AccountOwners().Acquire("ledger", () => { });
            var character = repository.Create(owner, new("LedgerTest", towns.Room, towns.InitialReturn)
            {
                Job = 0, Level = 20, Skills = characters.Get(0).Skills.ToArray(), InitialLearning = state
            });
            character = repository.CommitLearning(owner, character, learned, catalog.TotalSp(20) + 137);
            var reset = SkillPointLedger.Reset(0, 0, 20, character.Learning, 0, catalog, 0);
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=false");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_refund BEFORE INSERT ON character_skill_bindings WHEN NEW.context=1 BEGIN SELECT RAISE(ABORT,'refund rollback'); END";
            command.ExecuteNonQuery();
            try
            {
                repository.CommitLearning(owner, character, reset.State!, reset.TotalSp);
                throw new InvalidOperationException("退款SQL注入未拒绝");
            }
            catch (SqliteException) { Check(true, "退款第二树失败回滚全部余额与绑定"); }
            Check(SkillRepositoryTests.Equal(character, repository.List(owner).Single()), "回滚后额外来源与原存档保持");
            command.CommandText = "DROP TRIGGER reject_refund"; command.ExecuteNonQuery();
            character = repository.CommitLearning(owner, character, reset.State!, reset.TotalSp);
            var session = new GameSession(repository, towns, owner);
            session.Select(character.Slot); session.MarkReady();
            session.UpdateProgress(new(CharacterProgressField.BonusSp, owner.Account, character.Name, 777, 0));
            session.InitializeLearning(catalog);
            Check(session.Selected!.Learning.BonusSp == 777 && session.Selected.Learning.Trees.All(tree => tree.Sp == catalog.TotalSp(20) + 777),
                "GM额外SP独立持久，重新同步不会抹除");
            session.UpdateProgress(new(CharacterProgressField.ExpertContract, owner.Account, character.Name, 60, 0));
            Check(repository.List(owner).Single().Learning.ExpertContractExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                "账号契约到期时间持久保存");
            var peer = repository.Create(owner, new("LedgerPeer", towns.Room, towns.InitialReturn)
            { Job = 0, Level = 20, Skills = characters.Get(0).Skills.ToArray(), InitialLearning = state });
            session.UpdateProgress(new(CharacterProgressField.ExpertContract, owner.Account, peer.Name, 0, 0));
            Check(session.Selected!.Id == character.Id && session.Selected.Learning.ExpertContractExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() &&
                session.Selected.Learning.ExpertContractExpiresAt == repository.LoadCharacter(owner, peer.Slot).Learning.ExpertContractExpiresAt,
                "修改同账号其他角色的契约立即更新当前角色的账号状态，不覆盖其资产或额外SP");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Check(bool condition, string message) => TestCheck.That(condition, message);
}
