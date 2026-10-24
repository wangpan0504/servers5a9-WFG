using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class DungeonGrowthRepositoryTests
{
    static void RunSparseReads(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters)
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-growth-reads-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "game.db");
        try
        {
            var learning = Testing.SelfTestCatalogs.SkillLearning(pvf);
            var growth = Testing.SelfTestCatalogs.Growth(pvf);
            var pieces = new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]");
            var seeking = QuestEligibilityTests.Quest(29987) with { Condition = "[seeking]", IntData = [10165055, 2] };
            var quests = new QuestCatalog(new() { [29987] = seeking });
            var repository = new CharacterRepository(path, quests);
            using var owner = new AccountOwners().Acquire("growth-reads", () => { });
            var fixture = SkillRepositoryTests.Fixture("SparseGrowth", towns, characters, learning) with
                { ActiveMainQuests = [new(0, 3145, 1)], DungeonState = CharacterDungeonState.NewOrdinary };
            var current = repository.Create(owner, fixture);
            var (run, authority) = CreateRun(current);
            var pending = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity, 20, 1)).Pending!;
            var reward = new DungeonExperienceReward("fixture-only", DungeonKillExperienceCatalog.ResourcePath,
                pending.Monster.MonsterId, pending.Monster.CandidateLevel, 1);
            var empty = QuestDropPlan.Create(current, run.Entry, new(new Dictionary<int,StoryQuest>(),
                new Dictionary<int,ItemTemplate>(), pieces),
                pending.Death.EventId, pending.Death.Identity, pending.Result.Snapshot.CurrentMap.Id,
                pending.Monster.MonsterId, 1, false, _ => 0, DateTimeOffset.UtcNow);
            That(!empty.ReadsInventory && !empty.Inventory.Changed, "无匹配任务掉落的规划明确不依赖库存");

            // 临时改表名使任何完整背包加载立即报错；恢复后仍能独立重读，避免用源码搜索自证免读。
            Sql("ALTER TABLE character_inventory_items RENAME TO inventory_not_used_by_growth");
            DfoServer.Game.Dungeons.DungeonGrowthCommit committed;
            try
            {
                committed = repository.CommitDungeonGrowth(owner, current, run, pending, reward, growth, questDrops: empty);
                That(committed.Character.Learning.Experience == current.Learning.Experience + 1 &&
                    ReferenceEquals(current.Inventory, committed.Character.Inventory), "普通击杀在库存表不可查询时仍完成真实经验提交");
                Reject<SqliteException>(() => repository.CommitDungeonGrowth(owner, current, run, pending, reward, growth, questDrops: empty),
                    "原回执恢复保留完整读取，不拿旧在线快照冒充当前存档");
            }
            finally { Sql("ALTER TABLE inventory_not_used_by_growth RENAME TO character_inventory_items"); }
            That(JsonSerializer.Serialize(committed.Character) == JsonSerializer.Serialize(repository.LoadCharacter(owner, current.Slot)),
                "精简读取后的完整在线角色与独立读库一致");
            var replay = repository.CommitDungeonGrowth(owner, current, run, pending, reward, growth, questDrops: empty);
            That(replay.Duplicate && JsonSerializer.Serialize(replay.Character) == JsonSerializer.Serialize(committed.Character),
                "恢复库存查询后重复结算正常重读且不追加经验");
            run.ConfirmCommittedDeath(pending);

            current = committed.Character;
            var next = run.PrepareConfirmedDeath(authority.Death(run.Snapshot.Identity, 21, 2)).Pending!;
            // 同账号另一角色改变钱包时当前角色revision可不变，必须回退读取账号最新资产。
            Sql("INSERT INTO account_wallets(account,revision,mileage) VALUES('growth-reads',1,7) " +
                "ON CONFLICT(account) DO UPDATE SET revision=revision+1,mileage=7");
            var refreshed = repository.CommitDungeonGrowth(owner, current, run, next, reward, growth);
            That(refreshed.Character.Inventory.AccountWallet.Mileage == 7 &&
                JsonSerializer.Serialize(refreshed.Character) == JsonSerializer.Serialize(repository.LoadCharacter(owner, current.Slot)),
                "账号钱包版本改变触发完整回退，返回新资产而非覆盖为旧快照");

            var seeker = repository.Create(owner, fixture with { Name = "SeekingRead", ActiveMainQuests = [new(0,3145,1),new(1,29987,1)] });
            var (seekRun, seekAuthority) = CreateRun(seeker);
            var seekDeath = seekRun.PrepareConfirmedDeath(seekAuthority.Death(seekRun.Snapshot.Identity, 20, 1)).Pending!;
            var noDrop = QuestDropPlan.Create(seeker, seekRun.Entry, new(new Dictionary<int,StoryQuest>(),
                new Dictionary<int,ItemTemplate>(), pieces),
                seekDeath.Death.EventId, seekDeath.Death.Identity, seekDeath.Result.Snapshot.CurrentMap.Id, 1, 1, false, _ => 0, DateTimeOffset.UtcNow);
            Sql("ALTER TABLE character_inventory_items RENAME TO inventory_not_used_by_growth");
            try
            {
                Reject<SqliteException>(() => repository.CommitDungeonGrowth(owner, seeker, seekRun, seekDeath, reward, growth, questDrops: noDrop),
                    "寻物任务即使无新增掉落也必须核对库存，不能从空规划误入免读路径");
            }
            finally { Sql("ALTER TABLE inventory_not_used_by_growth RENAME TO character_inventory_items"); }
            That(JsonSerializer.Serialize(seeker) == JsonSerializer.Serialize(repository.LoadCharacter(owner, seeker.Slot)),
                "库存复验失败不留下经验、revision或任务半提交");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - before} 击杀精简读取、空规划、账号资产版本与回执恢复断言");

        void Sql(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
    }
}
