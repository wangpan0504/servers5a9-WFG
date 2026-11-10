using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.BattlePass;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 资格与初始余额仅种入隔离库；恢复、资源规划、奖励/来源/进度事务及重放均走正式代码。
internal static class BattlePassSongRaidRecoveryTransactionTests
{
    internal static void Run(TcpTestResources resources)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-song-raid-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const int itemId = BattlePassUndefeatedSongSource.ItemId;
            var items = resources.ItemResources;
            var material = items.Items.Get(itemId).AccountMaterial
                ?? throw new InvalidDataException("匹配资源缺少不渝之咏账号材料定义");
            var now = DateTimeOffset.UtcNow;
            int raidWeek = AccountMaterialService.RaidWeek(now);
            var local = now.ToOffset(TimeSpan.FromHours(8));
            var thursday = new DateTimeOffset(local.Year, local.Month, local.Day, 6, 0, 0, TimeSpan.FromHours(8))
                .AddDays(-(((int)local.DayOfWeek - (int)DayOfWeek.Thursday + 7) % 7));
            if (thursday > local) thursday = thursday.AddDays(-7);
            var catalog = BattlePassCatalog.Load(resources.Archive);
            const string season = "song-raid-recovery-season";
            BattlePassMissionPolicy? policy = null;
            for (int week = 0; week < catalog.WeeklyMissionIds.Length / 4; week++)
            {
                var candidate = new BattlePassMissionPolicy(season, thursday.AddDays(-7 * week), catalog);
                if (candidate.Active(now).Any(row => BattlePassUndefeatedSongSource.Matches(row.Key.Category, row.Definition)))
                { policy = candidate; break; }
            }
            if (policy is null) throw new InvalidDataException("匹配资源未提供活跃周任务48");
            Scenario("normal-rollback", 0, 0, rollback: true);
            Scenario("pvf-p2", 0, 0, rollback: true, rewardCount: 100, phase: 1);
            Scenario("legacy-p2", 0, 0, rollback: true, rewardCount: 75, phase: 1,
                source: LukeResourceRewards.LegacySource);
            Scenario("pvf-p2-card3", 0, 0, rollback: true, rewardCount: 100, phase: 1, card: 3);
            Scenario("legacy-p2-card3", 0, 0, rollback: true, rewardCount: 75, phase: 1, card: 3,
                source: LukeResourceRewards.LegacySource);
            Scenario("holding-full", material.HoldingLimit, 0, rollback: false);
            Scenario("pvf-p2-holding-full", material.HoldingLimit, 0, rollback: false, rewardCount: 100, phase: 1);
            int weekly = new[] { material.WeeklyAccountLimit, material.WeeklyCharacterLimit }
                .Where(limit => limit > 0).DefaultIfEmpty(0).Min();
            if (weekly > 0)
            {
                Scenario("weekly-remaining", 0, Math.Max(0, weekly - 7), rollback: false);
                Scenario("pvf-p2-weekly-remaining", 0, Math.Max(0, weekly - 7), rollback: false,
                    rewardCount: 100, phase: 1, card: 3);
            }

            void Scenario(string name, int openingBalance, int earned, bool rollback,
                int rewardCount = 25, int phase = 0, byte card = 0, string source = LukeResourceRewards.Source)
            {
                string account = "song-raid-" + name;
                string path = Path.Combine(root, name + ".db");
                var repository = new CharacterRepository(path);
                repository.ConfigureBattlePassMissions(policy);
                var owners = new AccountOwners();
                using var owner = owners.Acquire(account, () => { });
                repository.Create(owner, new NewCharacter("SongRaid", resources.Towns.Room, resources.Towns.InitialReturn) { Level = 95 });
                var role = repository.LoadCharacter(owner, 0);
                string byCharacter = JsonSerializer.Serialize(earned == 0 ? ImmutableSortedDictionary<ushort, int>.Empty
                    : ImmutableSortedDictionary<ushort, int>.Empty.Add(role.Id, earned));
                // New characters read a default wallet; the persisted parent is created lazily by wallet writers.
                Sql("INSERT INTO account_wallets(account) VALUES($p0) ON CONFLICT(account) DO NOTHING", account);
                Sql("INSERT INTO account_material_balances(account,item_id,balance,raid_week,raid_earned,character_earned) VALUES($p0,$p1,$p2,$p3,$p4,$p5)",
                    account, itemId, openingBalance, earned == 0 ? 0 : raidWeek, earned, byCharacter);
                role = repository.LoadCharacter(owner, 0);
                var original = role;
                long originalGold = CurrencyWallet.Get(role.Inventory, CurrencyKind.Gold);
                Guid instance = Guid.NewGuid(), eventId = Guid.NewGuid();
                long anchor = now.ToUnixTimeSeconds();
                var offer = new RaidRewardOffer(1, 0, [new(itemId, rewardCount), new(0, 19)], []);
                var eligibility = new RaidPhaseEligibility(instance, RaidKind.Luke, phase, eventId, anchor,
                    role.Id, false, true, "fixture-qualified", source, raidWeek, [offer]);
                Sql($"INSERT INTO character_raid_runs(character_id,account,instance_id,raid_type,source,admitted_at,state,phase{phase}_event,phase{phase}_anchor,phase{phase}_receipt) VALUES($p0,$p1,$p2,1,$p3,$p4,1,$p5,$p4,$p6)",
                    role.Id, account, instance.ToString("N"), source, anchor, eventId.ToString("N"), JsonSerializer.Serialize(eligibility));
                // 重建仓储后仅从持久资格恢复，不能用当前策略重算旧75或新100。
                repository = new CharacterRepository(path);
                repository.ConfigureBattlePassMissions(policy);
                role = repository.LoadCharacter(owner, 0);
                original = role;
                _ = repository.ReadBattlePassMissions(owner, now);
                var target = new LukeRewardRecoveryTarget(role.Id, instance, phase, 1, 0);
                PreparedRaidReward? direct = null;
                InventoryPlan? directPlan = null;
                if (card != 0)
                {
                    // 正常选牌的资产writer入口；非零牌面的管理恢复是独立缺陷，不以此夹具冒充通过。
                    direct = repository.ReadLukeRewardRecovery(owner, role, target).Prepared with { Card = card };
                    var granted = InventoryRewardGrantService.TryGrantBatch(role, direct.Grants,
                        items.Items, items.GoldLimits, items.EpicPieces, utcNow: now, allowOverflowMail: true, raidReward: true);
                    That(granted.Accepted && granted.Plan is not null, "真实资源数量可在正常选牌入口规划");
                    var plan = granted.Plan!;
                    directPlan = new InventoryPlan(plan.Before, plan.After, "raid_reward", new(role), plan.OverflowItems)
                        { AccountMaterialRaidWeek = plan.AccountMaterialRaidWeek, AccountMaterialRaidDefinitions = plan.AccountMaterialRaidDefinitions };
                    That(repository.CommitRaidReward(owner, original, direct with { Card = 4 }, directPlan, now) is null,
                        "新旧正式来源仍拒绝超出0–3的牌面，零资产提交");
                }
                int expected = Math.Min(rewardCount, Math.Max(0, material.HoldingLimit - openingBalance));
                if (material.WeeklyAccountLimit != 0) expected = Math.Min(expected, Math.Max(0, material.WeeklyAccountLimit - earned));
                if (material.WeeklyCharacterLimit != 0) expected = Math.Min(expected, Math.Max(0, material.WeeklyCharacterLimit - earned));
                if (rollback && expected > 0)
                {
                    Sql("CREATE TRIGGER song_raid_rollback BEFORE UPDATE OF revision ON account_battle_pass_seasons BEGIN SELECT RAISE(ABORT,'injected song raid rollback'); END");
                    bool failed = false;
                    try { Claim(); }
                    catch (SqliteException error) when (error.SqliteErrorCode == 19) { failed = true; }
                    finally { Sql("DROP TRIGGER song_raid_rollback"); }
                    var restored = repository.LoadCharacter(owner, 0);
                    That(failed && restored.Revision == original.Revision &&
                        restored.Inventory.AccountWallet.Material(itemId).Same(original.Inventory.AccountWallet.Material(itemId)) &&
                        CurrencyWallet.Get(restored.Inventory, CurrencyKind.Gold) == originalGold &&
                        MissionCount() == 0 && Number("SELECT count(*) FROM character_raid_reward_receipts") == 0 &&
                        Number("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='raid_undefeated_song_acquisition'") == 0,
                        "后段赛季写失败回滚原材料、周额度、其它奖励、领取回执与任务来源");
                }
                var commit = Claim();
                role = commit.Character;
                var actual = repository.LoadCharacter(owner, 0);
                var actualBalance = actual.Inventory.AccountWallet.Material(itemId);
                bool limited = material.WeeklyAccountLimit != 0 || material.WeeklyCharacterLimit != 0;
                That(!commit.Duplicate && actual.Inventory.AccountWallet.Material(itemId).Balance == openingBalance + expected &&
                    (!limited || actualBalance.RaidWeek == raidWeek && actualBalance.EarnedIn(raidWeek) == earned + expected &&
                        actualBalance.EarnedBy(role.Id, raidWeek) == earned + expected &&
                        actualBalance.CharacterEarned.Count == (earned + expected > 0 ? 1 : 0)) &&
                    actual.Inventory.Items.All(item => item.Core.ItemId != itemId) &&
                    CurrencyWallet.Get(actual.Inventory, CurrencyKind.Gold) == originalGold + 19 && MissionCount() == expected &&
                    Number("SELECT count(*) FROM character_raid_reward_receipts") == 1 &&
                    Number($"SELECT count(*) FROM character_raid_reward_receipts WHERE card={card}") == 1 &&
                    Number("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='raid_undefeated_song_acquisition'") == (expected > 0 ? 1 : 0),
                    "恢复或正常选牌writer按冻结数量及实际额度计任务，满额仍发其它奖励：" + name);
                long revision = actual.Revision;
                var balance = actual.Inventory.AccountWallet.Material(itemId);
                var replay = direct is not null ? Claim() : LukeRewardRecovery.Claim(owner, role, target, eventId, anchor, repository,
                    () => throw new InvalidOperationException("重放不得重读资源或重做资产规划"), now);
                var repeated = repository.LoadCharacter(owner, 0);
                That(replay.Duplicate && repeated.Revision == revision && repeated.Inventory.AccountWallet.Material(itemId).Same(balance) &&
                    CurrencyWallet.Get(repeated.Inventory, CurrencyKind.Gold) == originalGold + 19 && MissionCount() == expected &&
                    Number("SELECT count(*) FROM character_raid_reward_receipts") == 1 &&
                    Number("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='raid_undefeated_song_acquisition'") == (expected > 0 ? 1 : 0),
                    "同原身份恢复重放不重复增加材料、周额度、其它奖励或任务来源：" + name);

                RaidRewardCommit Claim() => direct is not null
                    ? repository.CommitRaidReward(owner, original, direct, directPlan!, now)
                        ?? throw new InvalidDataException("正常选牌未通过原冻结权益复验")
                    : LukeRewardRecovery.Claim(owner, role, target, eventId, anchor, repository,
                        () => (items.Items, items.GoldLimits, items.EpicPieces), now);
                long MissionCount() => repository.ReadBattlePassMissions(owner, now)!.Slots
                    .Single(row => BattlePassUndefeatedSongSource.Matches(row.Instance.Key.Category, row.Instance.Definition)).Count;
                long Number(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
                void Sql(string sql, params object[] values)
                {
                    using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql;
                    for (int i = 0; i < values.Length; i++) command.Parameters.AddWithValue("$p" + i, values[i]);
                    command.ExecuteNonQuery();
                }
                SqliteConnection Open()
                {
                    var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
                    db.Open(); return db;
                }
            }
        }
        finally { TestFiles.DeleteDirectory(root, recursive: true); }
    }
}
