using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
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
        var catalog = global::DfoServer.Testing.SelfTestCatalogs.SkillLearning(pvf);
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(pvf, catalog);
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
        AwakeningGrants(catalog);
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
        var ghost = catalog.Get(0, 5);
        int Cap(int extra)
        {
            int rank = Math.Min(ghost.GetMaximum(0), ghost.DeclaredMaximumLevels![0]);
            while (rank > 0 && ghost.GetOrdinaryNextRequiredCharacterLevel(rank - 1) > (long)level + extra) rank--;
            return rank;
        }
        int baseRank = state.Trees[0].Skills.Single(skill => skill.Id == 5).Level;
        int ordinaryCap = Cap(0), contractCap = Cap(catalog.ExpertContractLevels);
        var request = new SkillLearningCommand(0, 0, [new(5, false, checked((byte)(contractCap - baseRank)))]);
        var over = SkillLearning.Plan(0, 0, level, contract, request, catalog);
        Check(over.Success && over.Changes.Single().Level == contractCap, "达人按显式装配的声明放宽资格，正常费用与资源上限保留");
        var without = SkillLearning.Plan(0, 0, level, state, request, catalog);
        Check(contractCap > ordinaryCap ? without.Failure == SkillLearningFailure.RequiredLevel : without.Success,
            "无契约只有普通等级资格，零加成声明不制造额外放行");
        var expired = SkillPointLedger.Synchronize(0, 0, level, over.State! with { ExpertContractExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1 }, catalog);
        int ordinaryCost = Enumerable.Range(baseRank, ordinaryCap - baseRank).Sum(rank => ghost.GetPointCost(rank, 0));
        Check(expired.Trees[0].Skills.Single(skill => skill.Id == 5).Level == ordinaryCap && expired.Trees[0].Sp == total - ordinaryCost &&
            expired.BonusSp == bonus, "契约到期回落普通等级，退款并保留额外SP");
        // 契约只放宽资格；到期后 TP 与 SP 一样回退超门槛等级并返还点数。
        var tpGateCandidates = PvfScript.List(pvf.ReadText("skill/swordmanskill.lst")).Keys
            .Select(id => catalog.Get(0, checked((ushort)id)))
            .Where(skill => skill.SupportsTpLearning && skill.Prerequisites!.Count == 0 && skill.GetMaximum(0) > 0 && skill.GetPointCost(0, 0) > 0)
            .Select(skill => (Skill: skill, Gate: skill.GetOrdinaryNextRequiredCharacterLevel(0) - catalog.ExpertContractLevels))
            .Where(candidate => candidate.Gate is >= 1 and <= 95)
            .ToArray();
        Check(catalog.ExpertContractLevels > 0 && tpGateCandidates.Length > 0, "匹配PVF存在契约门槛附近的TP样本");
        var (tpContract, tpGateValue) = tpGateCandidates[0];
        byte tpGate = checked((byte)tpGateValue);
        // 当前PVF样例161首级需Lv50，达人提前到45时尚无等级TP；明确提供额外来源，不假定契约赠送TP。
        int tpBonus = tpContract.GetPointCost(0, 0) + 6;
        int tpTotal = catalog.TotalTp(tpGate) + tpBonus;
        var tpSource = SkillPointLedger.Synchronize(0, 0, tpGate, initial with { BonusSp = 0, BonusTp = tpBonus }, catalog) with
        {
            ExpertContractExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60
        };
        Check(SkillLearning.Plan(0, 0, tpGate, tpSource with { ExpertContractExpiresAt = 0 },
            new(0, 0, [new(tpContract.SkillId, false, 1)]), catalog).Failure == SkillLearningFailure.RequiredLevel,
            "有足够额外TP但无达人仍须拒绝未达普通等级门槛");
        var tpLearned = SkillLearning.Plan(0, 0, tpGate, tpSource, new(0, 0, [new(tpContract.SkillId, false, 1)]), catalog);
        Check(tpLearned.Success && tpLearned.State!.Trees[0].Tp == tpTotal - tpContract.GetPointCost(0, 0) &&
            tpLearned.State.BonusTp == tpBonus && tpLearned.State.Trees[1].Tp == tpTotal,
            "TP学习同一达人声明加成，正常扣TP");
        var tpExpired = SkillPointLedger.Synchronize(0, 0, tpGate,
            tpLearned.State! with { ExpertContractExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1 }, catalog);
        Check(tpExpired.Trees[0].Skills.All(skill => skill.Id != tpContract.SkillId) &&
            tpExpired.Trees.All(tree => tree.Tp == tpTotal) && tpExpired.BonusTp == tpBonus,
            "契约到期删除首级超门槛TP技能并全额退款，额外来源和另一树保持");
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
        { Job = 0, GrowType = 1, Level = level, Learning = add.State! with { SelectedTree = 1 } };
        var next = CharacterGrowthPlan.AddExperience(character, growth.GetLevelExperienceSpan(level), growth);
        Check(next.Success && next.Plan!.Learning.BonusSp == bonus &&
            next.Plan.Learning.SelectedTree == 1 &&
            next.Plan.Learning.Trees.Single(tree => tree.Context == 1).Sp == catalog.TotalSp(level + 1) + bonus &&
            next.Plan.Learning.Trees.Single(tree => tree.Context == 0).Skills.Single(skill => skill.Id == 5).Level == 3,
            "转职升级同样派生累计SP，保留额外来源、当前第二树与备用树加点");
        var progressReset = new CharacterProgressReset(characters, catalog, growth);
        Check(progressReset.Prepare(character, new(CharacterProgressField.Grow, "ledger", character.Name, 1, 0)).Learning.SelectedTree == 1 &&
            progressReset.Prepare(character, new(CharacterProgressField.Level, "ledger", character.Name, 50, 0)).Learning.SelectedTree == 1,
            "职业重建和等级重算保留已开通权益及当前第二方案");
        Persist(towns, characters, catalog, growth, state, add.State!);
        Console.WriteLine($"PASS {TestCheck.Count - before} SP扣除、退款、固有成长、契约和来源存档断言");
    }

    private static void AwakeningGrants(SkillLearningCatalog catalog)
    {
        ushort[] avenger = [119, 120, 122, 135];
        Check(catalog.FreeSkills(4, 4).All(skill => !avenger.Contains(skill.Id)) &&
            catalog.FreeSkills(4, 0x13).All(skill => skill.Id is not (119 or 120 or 135)),
            "未觉醒复仇者和其他分支不获得复仇者觉醒赠技");
        foreach (byte growth in new byte[] { 0x14, 0x24 })
            Check(avenger.All(id => catalog.FreeSkills(4, growth).Any(skill => skill.Id == id && skill.Level == 1)),
                "数字觉醒阶段读取完整赠技段并累计保留一觉赠技 " + growth);
        Check(catalog.FreeSkills(4, 0x13).All(skill => skill.Id != 222) &&
            new ushort[] { 89, 93, 222 }.All(id => catalog.FreeSkills(4, 0x23).Any(skill => skill.Id == id)),
            "驱魔一觉不提前获得二觉赠技，二觉累计两阶段资源");
        var complete = SkillLearning.Initialize(4, catalog.FreeSkills(4, 0x24), catalog).State! with
            { BonusSp = 137, BonusTp = 7 };
        complete = SkillPointLedger.Synchronize(4, 0x24, 95, complete, catalog);
        var paid = SkillLearning.Plan(4, 0x24, 95, complete, new(0, 0, [new(122, false, 15)]), catalog);
        Check(paid.Success, "觉醒赠技之上的付费等级沿正常学习规划");
        int legacyCharge = catalog.Get(4, 122).GetPointCost(0, 0x24);
        var legacy = paid.State! with { Trees = Array.AsReadOnly(paid.State.Trees.Select(tree => tree with
        {
            Sp = checked((ushort)(tree.Sp - (tree.Context == 0 ? legacyCharge : 0))),
            Skills = Array.AsReadOnly(tree.Skills.Where(skill => skill.Id is not (120 or 135))
                .Select(skill => skill.Id == 1 ? skill with { Command = "04" } : skill).ToArray())
        }).ToArray()) };
        var repaired = SkillPointLedger.Synchronize(4, 0x24, 95, legacy, catalog);
        Check(repaired.Trees.Zip(legacy.Trees).All(pair => pair.First.Sp == pair.Second.Sp + (pair.First.Context == 0 ? legacyCharge : 0) && pair.First.Tp == pair.Second.Tp &&
            pair.Second.Skills.All(skill => pair.First.Skills.Single(value => value.Id == skill.Id) == skill) &&
            pair.First.Skills.Count == pair.Second.Skills.Count + 2 &&
            pair.First.Skills.Any(skill => skill.Id == 120 && skill.Level == 1) &&
            pair.First.Skills.Any(skill => skill.Id == 135 && skill.Level == 1)) &&
            repaired.BonusSp == 137 && repaired.BonusTp == 7,
            "旧双树补齐审判与恶魔屏障并返还免费首级误扣费用，保留等级、槽位、指令和点数来源");
        Check(repaired.Trees.All(tree => tree.Skills.Single(skill => skill.Id == 119).Level == 10) &&
            ReferenceEquals(SkillPointLedger.Synchronize(4, 0x24, 95, repaired, catalog), repaired),
            "末日审判者仍按固定规则十级，重复同步不创建新状态");
        var reset = SkillPointLedger.Reset(4, 0x24, 95, repaired, 0, catalog, 0);
        Check(reset.Success && reset.State!.Trees[0].Skills.Single(skill => skill.Id == 122).Level == 1 &&
            reset.State.Trees[0].Skills.Single(skill => skill.Id == 120).Level == 1 &&
            reset.State.Trees[0].Skills.Single(skill => skill.Id == 135).Level == 1 &&
            reset.State.Trees[1].Skills.SequenceEqual(repaired.Trees[1].Skills),
            "洗点保留觉醒免费一级和独立备用树");
        var floor = SkillLearning.Plan(4, 0x24, 95, repaired, new(0, 0, [new(120, true, 1)]), catalog);
        Check(floor.Success && floor.State!.Trees[0].Skills.Single(skill => skill.Id == 120).Level == 1 &&
            floor.State.Trees[0].Sp == repaired.Trees[0].Sp &&
            SkillLearning.Plan(4, 0x24, 95, repaired, new(0, 0, [new(135, false, 1)]), catalog).Failure == SkillLearningFailure.Mastered,
            "觉醒赠技不能退款到零或突破子技能自身一级上限");
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
        finally { TestFiles.DeleteDirectory(root, true); }
    }

    private static void Check(bool condition, string message) => TestCheck.That(condition, message);
}
