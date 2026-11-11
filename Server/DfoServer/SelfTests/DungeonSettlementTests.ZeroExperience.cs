using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Persistence;
using DfoServer.Testing;
using PvfLib;

namespace DfoServer;

internal static partial class DungeonSettlementTests
{
    internal static void RunZeroExperienceResources(PvfArchive pvf)
    {
        int before = TestCheck.Count;
        var dungeon = SelfTestCatalogs.Dungeons(pvf).Get(400000096);
        var catalog = SelfTestCatalogs.ClearExperience(pvf);
        for (byte difficulty = 0; difficulty < 5; difficulty++)
        {
            var reward = catalog.Calculate(dungeon, difficulty, 99);
            Check(reward is { BaseExperience: 0, RankExperience: 0, TotalExperience: 0 },
                "真实扭曲世界的次元显式零经验在各难度保留零奖励");
        }
        string root = Path.Combine(Path.GetTempPath(), "S5A9-zero-clear-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("zero-clear", () => { });
            var character = repository.Create(owner, Fixture("ZeroClear"));
            var (ordinary, ordinaryAuthority) = CreateRun(character);
            ordinary.AcceptConfirmedDeath(ordinaryAuthority.Death(ordinary.Snapshot.Identity, 20));
            Check(ordinary.PrepareSettlement(ordinary.ClearFact!, new byte[] { 1 }, catalog.Calculate(dungeon, 0, 99)).Outcome == DungeonRunOutcome.Rejected,
                "普通非零权重实例仍拒绝无依据的零奖励");
            var (run, authority) = CreateRun(character, entry => entry with { DungeonId = checked((uint)dungeon.Id),
                ExperienceWeight = DfoServer.GameWorld.Dungeons.DungeonKillExperienceCatalog.GetDungeonWeight(dungeon) });
            run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 20));
            var prepared = run.PrepareSettlement(run.ClearFact!, new byte[] { 1, 2, 3 }, catalog.Calculate(dungeon, 0, 99)).Pending!;
            var growth = SelfTestCatalogs.Growth(pvf);
            var committed = repository.CommitDungeonSettlement(owner, character, run, prepared, growth);
            Check(committed.Reward.TotalExperience == 0 && committed.Character.Learning.Experience == character.Learning.Experience &&
                committed.Character.Level == character.Level, "零经验仍提交完整结算凭证，保留真实等级与经验");
            var replay = repository.CommitDungeonSettlement(owner, character, run, prepared, growth);
            Check(replay.Duplicate && replay.Character.Revision == committed.Character.Revision,
                "零经验结算结果丢失重试读取持久凭证，不二次提交");
            Check(run.ConfirmCommittedSettlement(prepared) == DungeonRunOutcome.Accepted &&
                run.Return(run.Snapshot.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted,
                "真实零奖励经过清图、结算确认及正常返回，不因零经验中断");
            Check(new CharacterRepository(path).List(owner).Single().Learning.Experience == character.Learning.Experience,
                "重开文件库保留零经验结算后的角色");
            var benefits = new DfoServer.Game.Events.AdventurerBenefitService(new(pvf),
                DfoServer.Game.Events.AdventurerBenefitConfiguration.Default, _ => { });
            var returningCatalog = new DfoServer.GameWorld.Events.ReturningAdventurerCatalog(pvf);
            repository.ConfigureReturningAdventurers(returningCatalog, benefits, _ => 0);
            benefits.SetReturning(owner.Account, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var returning = repository.Create(owner, Fixture("ReturnClear") with { Level = 70,
                InitialLearning = Learning(growth.GetLevelStartExperience(70)) });
            var (returnRun, returnAuthority) = CreateRun(returning, entry => entry with { DungeonId = checked((uint)dungeon.Id),
                ExperienceWeight = DfoServer.GameWorld.Dungeons.DungeonKillExperienceCatalog.GetDungeonWeight(dungeon) });
            returnRun.AcceptConfirmedDeath(returnAuthority.Death(returnRun.Snapshot.Identity, 20));
            var returnPrepared = returnRun.PrepareSettlement(returnRun.ClearFact!, new byte[] { 1, 2, 3 }, catalog.Calculate(dungeon, 0, 99)).Pending!;
            var returnCommit = repository.CommitDungeonSettlement(owner, returning, returnRun, returnPrepared, growth);
            Check(repository.ReadReturningAdventurer(owner, returnCommit.Character).Missions.Single(row => row.Id == 4) is { Progress: 1, State: 1 },
                "真实零经验遗迹结算与回归第4任务进度同事务提交");
            repository.CommitDungeonSettlement(owner, returning, returnRun, returnPrepared, growth);
            Check(repository.ReadReturningAdventurer(owner, returnCommit.Character).Missions.Single(row => row.Id == 4).Progress == 1,
                "遗迹结算重放不会二次累计回归任务");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"PASS {TestCheck.Count - before} 真实零经验副本结算、恢复与返回断言");
    }
}
