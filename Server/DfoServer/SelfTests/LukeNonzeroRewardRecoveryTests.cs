using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.BattlePass;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 仅在隔离库种入已冻结资格；资产规划、事务、管理恢复和历史发现均调用正式入口。
internal static class LukeNonzeroRewardRecoveryTests
{
    internal static void Run(TcpTestResources resources)
    {
        int checks = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-luke-nonzero-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool passed = false;
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            var items = resources.ItemResources;
            var now = DateTimeOffset.UtcNow;
            int week = AccountMaterialService.RaidWeek(now);
            var local = now.ToOffset(TimeSpan.FromHours(8));
            var reset = new DateTimeOffset(local.Year, local.Month, local.Day, 6, 0, 0, TimeSpan.FromHours(8))
                .AddDays(-(((int)local.DayOfWeek - (int)DayOfWeek.Thursday + 7) % 7));
            if (reset > local) reset = reset.AddDays(-7);
            var catalog = BattlePassCatalog.Load(resources.Archive);
            BattlePassMissionPolicy? policy = null;
            for (int index = 0; index < catalog.WeeklyMissionIds.Length / 4; index++)
            {
                var candidate = new BattlePassMissionPolicy("luke-nonzero-recovery", reset.AddDays(-7 * index), catalog);
                if (candidate.Active(now).Any(row => BattlePassUndefeatedSongSource.Matches(row.Key.Category, row.Definition)))
                { policy = candidate; break; }
            }
            if (policy is null) throw new InvalidDataException("匹配资源缺少活跃不渝之咏周任务");
            repository.ConfigureBattlePassMissions(policy);
            // 数量是历史冻结输入，不能因读取当前资源而把75补成100或重新抽取。
            foreach (var (source, amount) in new[] { (LukeResourceRewards.LegacySource, 75), (LukeResourceRewards.Source, 100) })
                foreach (byte card in new byte[] { 0, 1, 2, 3 }) Formal(source, amount, card);
            SquadGoldCodeFidelity();
            OtherSources();
            passed = true;
            Console.WriteLine($"LukeNonzeroRewardRecoveryTests SQLite: {Count - checks} passed");

            void Formal(string source, int amount, byte selectedCard)
            {
                string suffix = amount.ToString() + selectedCard;
                using var owner = owners.Acquire("luke-card-" + suffix, () => { });
                repository.Create(owner, new NewCharacter("LukeCard" + suffix, resources.Towns.Room, resources.Towns.InitialReturn) { Level = 95 });
                repository.Create(owner, new NewCharacter("LukeAlt" + suffix, resources.Towns.Room, resources.Towns.InitialReturn) { Level = 95 });
                var role = repository.LoadCharacter(owner, 0);
                var other = repository.LoadCharacter(owner, 1);
                var eligibility = new RaidPhaseEligibility(Guid.NewGuid(), RaidKind.Luke, 1, Guid.NewGuid(), now.ToUnixTimeSeconds(),
                    role.Id, true, true, "fixture-qualified", source, week,
                    [new(1, 0, [new(BattlePassUndefeatedSongSource.ItemId, amount), new(0, 19)], []), new(2, 0, [new(0, 5)], [])]);
                Seed(owner.Account, eligibility);
                // 重建仓储后仍只按原阶段资格恢复，不依赖在线Battle。
                repository = new CharacterRepository(path);
                repository.ConfigureBattlePassMissions(policy);
                _ = repository.ReadBattlePassMissions(owner, now);
                var target = new LukeRewardRecoveryTarget(role.Id, eligibility.Instance, 1, 1, selectedCard);
                string before = Facts();
                var saved = repository.ReadLukeRewardRecovery(owner, role, target);
                That(!saved.Claimed && saved.Prepared.Source == source && saved.Prepared.Card == selectedCard &&
                    saved.Prepared.Grants.SequenceEqual(eligibility.Offers[0].Grants) && Facts() == before,
                    "新旧正式0号冻结权益可按实际0至3号位置只读恢复，完整保存原来源、数量与其它奖励");
                Reject<InvalidDataException>(() => repository.ReadLukeRewardRecovery(owner, role, target with { Card = 4 }), "正式牌位4拒绝");
                Reject<InvalidDataException>(() => repository.ReadLukeRewardRecovery(owner, role, target with { Instance = Guid.NewGuid() }), "其它实例不能借用原资格");
                Reject<InvalidDataException>(() => repository.ReadLukeRewardRecovery(owner, other, target with { Character = other.Id }), "同账号另一角色没有原权益");
                Reject<InvalidDataException>(() => Claim(target, Guid.NewGuid(), eligibility.Anchor), "错误event不授权");
                Reject<InvalidDataException>(() => Claim(target, eligibility.Event, eligibility.Anchor + 1), "错误anchor不授权");
                That(Facts() == before, "所有无效目标不改资产、资格、邮件、周额度和任务来源");

                // 晚段任务提交失败发生在资产及领取回执写入之后，必须整笔回滚。
                Sql("CREATE TRIGGER luke_card_late_failure BEFORE UPDATE OF revision ON account_battle_pass_seasons BEGIN SELECT RAISE(ABORT,'luke card late failure'); END");
                try { Reject<SqliteException>(() => Claim(target, eligibility.Event, eligibility.Anchor), "非零恢复晚SQL失败传播"); }
                finally { Sql("DROP TRIGGER luke_card_late_failure"); }
                That(Facts() == before, "晚SQL故障回滚材料、金币、UID、邮件、回执、周额度及任务来源");

                var original = role;
                var commit = Claim(target, eligibility.Event, eligibility.Anchor);
                role = commit.Character;
                var actual = repository.LoadCharacter(owner, 0);
                var material = actual.Inventory.AccountWallet.Material(BattlePassUndefeatedSongSource.ItemId);
                That(!commit.Duplicate && material.Balance == amount && material.EarnedIn(week) == amount && material.EarnedBy(role.Id, week) == amount &&
                    CurrencyWallet.Get(actual.Inventory, CurrencyKind.Gold) == CurrencyWallet.Get(original.Inventory, CurrencyKind.Gold) + 19 &&
                    Number("SELECT count(*) FROM character_raid_reward_receipts WHERE character_id=$p0", role.Id) == 1 &&
                    Number("SELECT count(*) FROM account_battle_pass_source_events WHERE account=$p0 AND source_kind='raid_undefeated_song_acquisition'", owner.Account) == 1 &&
                    repository.ReadBattlePassMissions(owner, now)!.Slots.Single(row => BattlePassUndefeatedSongSource.Matches(row.Instance.Key.Category, row.Instance.Definition)).Count == amount,
                    "非零管理恢复按冻结数量一次到账，并原子保存原周额度、金币与任务来源");
                string delivered = Facts();
                var replay = LukeRewardRecovery.Claim(owner, role, target, eligibility.Event, eligibility.Anchor, repository,
                    () => throw new InvalidOperationException("原回执不得重读资源"), now);
                var page = repository.ListLukeRewardRecovery(owner, role);
                That(replay.Duplicate && page.Entries is [{ Claimed: [{}], AvailableOffers: [{ Kind: 2 }] }] &&
                    page.Entries[0].Claimed[0].Prepared.Card == selectedCard && page.Entries[0].Claimed[0].Prepared.Source == source &&
                    page.Entries[0].Claimed[0].Prepared.Grants.SequenceEqual(eligibility.Offers[0].Grants) &&
                    replay.MailIds.SequenceEqual(page.Entries[0].Claimed[0].MailIds) && Facts() == delivered,
                    "已领权益仅重放原回执，List保留真实来源与位置且同kind不再列为可领，其它kind仍保留");
                foreach (byte card in new byte[] { 0, 1, 2, 3, 4 })
                    if (card != selectedCard)
                        Reject<InvalidDataException>(() => Claim(target with { Card = card }, eligibility.Event, eligibility.Anchor), "已领后换牌或越界拒绝，不重复发奖");
                That(Facts() == delivered, "换牌拒绝零写入");

                string originalPlan = Scalar("SELECT plan_json FROM character_raid_reward_receipts WHERE character_id=$p0", role.Id);
                var prepared = saved.Prepared;
                foreach (var corrupt in new[] { prepared with { Source = "unknown-rule" }, prepared with { Instance = Guid.NewGuid() },
                    prepared with { Character = other.Id }, prepared with { Week = week + 1 }, prepared with { Grants = [new(0, 1)] } })
                {
                    Sql("UPDATE character_raid_reward_receipts SET plan_json=$p0 WHERE character_id=$p1", JsonSerializer.Serialize(corrupt), role.Id);
                    try
                    {
                        Reject<InvalidDataException>(() => repository.ReadLukeRewardRecovery(owner, role, target), "原计划来源/身份/数量损坏读取拒绝");
                        Reject<InvalidDataException>(() => repository.ListLukeRewardRecovery(owner, role), "原计划来源/身份/数量损坏发现拒绝");
                    }
                    finally { Sql("UPDATE character_raid_reward_receipts SET plan_json=$p0 WHERE character_id=$p1", originalPlan, role.Id); }
                }
                foreach (int card in new[] { (selectedCard + 1) % 4, 4 })
                {
                    Sql("UPDATE character_raid_reward_receipts SET card=$p0 WHERE character_id=$p1", card, role.Id);
                    try
                    {
                        Reject<InvalidDataException>(() => repository.ReadLukeRewardRecovery(owner, role, target), "SQL牌位与原选择计划不一致拒绝");
                        Reject<InvalidDataException>(() => repository.ListLukeRewardRecovery(owner, role), "SQL越界或不一致牌位发现拒绝");
                    }
                    finally { Sql("UPDATE character_raid_reward_receipts SET card=$p0 WHERE character_id=$p1", selectedCard, role.Id); }
                }
                That(Facts() == delivered, "损坏拒绝与精确复原后全部SQL事实不变");

                RaidRewardCommit Claim(LukeRewardRecoveryTarget selected, Guid eventId, long anchor) => LukeRewardRecovery.Claim(owner, role,
                    selected, eventId, anchor, repository, () => (items.Items, items.GoldLimits, items.EpicPieces), now);
            }

            void SquadGoldCodeFidelity()
            {
                int darkSourceId = items.Boosters.Get(10161941).Pools
                    .SelectMany(pool => pool.Rewards).Select(row => row.ItemId).Distinct().Single(id => id != 10159623);
                foreach (var (source, savedCode, selectedCard) in new[]
                {
                    (LukeResourceRewards.LegacySource, (byte)0, (byte)2),
                    (LukeResourceRewards.Source, (byte)0, (byte)1),
                    (LukeResourceRewards.Source, (byte)1, (byte)3)
                })
                {
                    string suffix = $"gold-{savedCode}-{selectedCard}-{(source == LukeResourceRewards.Source ? "cur" : "leg")}";
                    using var owner = owners.Acquire("luke-" + suffix, () => { });
                    repository.Create(owner, new NewCharacter("LkGold" + savedCode + selectedCard + (source == LukeResourceRewards.Source ? "C" : "L"),
                        resources.Towns.Room, resources.Towns.InitialReturn) { Level = 95 });
                    var role = repository.LoadCharacter(owner, 0);
                    var personalOffer = new RaidRewardOffer(1, 0, [new(BattlePassUndefeatedSongSource.ItemId, 100)], [])
                        { LukeResult = new(0, (uint)BattlePassUndefeatedSongSource.ItemId, 100, 0, 0, 0) };
                    var squadOffer = new RaidRewardOffer(0, 0, [new(darkSourceId, 4), new(10159623, 30)], [])
                    {
                        LukeResult = new(savedCode, checked((uint)darkSourceId), 4, 0, 0, 0),
                        LukeAdditionalResults = [new(savedCode, 10159623, 30, 0, 0, 0)]
                    };
                    var eligibility = new RaidPhaseEligibility(Guid.NewGuid(), RaidKind.Luke, 1, Guid.NewGuid(), now.ToUnixTimeSeconds(),
                        role.Id, true, true, "fixture-qualified", source, week, [personalOffer, squadOffer]);
                    Seed(owner.Account, eligibility);
                    var beforeClaim = repository.ListLukeRewardRecovery(owner, role).Entries.Single();
                    var restoredPersonal = beforeClaim.AvailableOffers.Single(offer => offer.Kind == 1);
                    var restoredSquad = beforeClaim.AvailableOffers.Single(offer => offer.Kind == 0);
                    That(restoredPersonal.LukeResult!.RawCode == 0 &&
                        restoredSquad.LukeResult!.RawCode == savedCode &&
                        restoredSquad.LukeAdditionalResults.Single().RawCode == savedCode &&
                        AntonRaidBattle.SameLukeOffers(eligibility.Offers, beforeClaim.Eligibility.Offers),
                        "持久化冻结资格恢复时严格保真已保存code（新冻结code=1仍为1，旧历史code=0不重抽或改写为1）");
                    var target = new LukeRewardRecoveryTarget(role.Id, eligibility.Instance, 1, 0, selectedCard);
                    var snapshot = repository.ReadLukeRewardRecovery(owner, role, target);
                    That(!snapshot.Claimed && snapshot.Prepared.Card == selectedCard &&
                        snapshot.Prepared.Grants.SequenceEqual(squadOffer.Grants),
                        "二阶段团队奖励0号冻结权益支持非零牌位只读恢复");
                    var commit = LukeRewardRecovery.Claim(owner, role, target, eligibility.Event, eligibility.Anchor, repository,
                        () => (items.Items, items.GoldLimits, items.EpicPieces), now);
                    role = commit.Character;
                    var afterClaim = repository.ListLukeRewardRecovery(owner, role).Entries.Single();
                    var savedSquadFromEntry = afterClaim.Eligibility.Offers.Single(offer => offer.Kind == 0);
                    var bound = LukeRewardResults.Bind(new(snapshot.Prepared, savedSquadFromEntry with { Card = selectedCard },
                        commit, new(true, 0), owner.Generation, 0), items.Items, now)!;
                    var packet = LukeRewardPackets.Result(bound);
                    var teamPacket = LukeRewardPackets.TeamResults([bound with { Card = 0 }]);
                    That(!commit.Duplicate && bound.Card == selectedCard && bound.Item.RawCode == savedCode &&
                        bound.AdditionalItems.Single().RawCode == savedCode &&
                        packet[6] == savedCode && packet[22] == savedCode &&
                        teamPacket[6] == savedCode && teamPacket[22] == savedCode,
                        "非零牌位领取及600结果/团队页重放完整保留原冻结code与全部展开道具");
                }
            }

            void OtherSources()
            {
                using var owner = owners.Acquire("luke-card-synthetic", () => { });
                repository.Create(owner, new NewCharacter("LukeExact", resources.Towns.Room, resources.Towns.InitialReturn) { Level = 95 });
                var role = repository.LoadCharacter(owner, 0);
                var eligibility = new RaidPhaseEligibility(Guid.NewGuid(), RaidKind.Luke, 0, Guid.NewGuid(), now.ToUnixTimeSeconds(),
                    role.Id, false, true, "fixture-qualified", "fixture-exact-card", week, [new(1, 7, [new(0, 19)], [])]);
                Seed(owner.Account, eligibility);
                var target = new LukeRewardRecoveryTarget(role.Id, eligibility.Instance, 0, 1, 7);
                Reject<InvalidDataException>(() => repository.ReadLukeRewardRecovery(owner, role, target with { Card = 3 }), "合成来源不借正式0号映射扩大权限");
                var commit = LukeRewardRecovery.Claim(owner, role, target, eligibility.Event, eligibility.Anchor, repository,
                    () => (items.Items, items.GoldLimits, items.EpicPieces), now);
                role = commit.Character;
                That(!commit.Duplicate && repository.ListLukeRewardRecovery(owner, role).Entries.Single().Claimed.Single().Prepared.Card == 7,
                    "其它来源继续允许原精确7号牌，不能收窄成正式0至3");
                var fiendwar = eligibility with { Instance = Guid.NewGuid(), RaidType = RaidKind.Fiendwar,
                    Source = "fixture-fiendwar", Offers = [new(1, 0, [new(0, 19)], [])] };
                Seed(owner.Account, fiendwar);
                That(repository.ReadLukeRewardRecovery(owner, role, new(role.Id, fiendwar.Instance, 0, 1, 3) { RaidType = RaidKind.Fiendwar }) is
                    { Claimed: false, Prepared: { Card: 3, RaidType: RaidKind.Fiendwar } }, "漩涡原0号授权映射与实际选择位置保持");
            }

            void Seed(string account, RaidPhaseEligibility eligibility)
            {
                string prefix = eligibility.Phase == 0 ? "phase0" : "phase1";
                Sql($"INSERT INTO character_raid_runs(character_id,account,instance_id,raid_type,source,admitted_at,state,{prefix}_event,{prefix}_anchor,{prefix}_receipt) VALUES($p0,$p1,$p2,$p3,$p4,$p5,1,$p6,$p5,$p7)",
                    eligibility.Character, account, eligibility.Instance.ToString("N"), (int)eligibility.RaidType, eligibility.Source,
                    eligibility.Anchor, eligibility.Event.ToString("N"), JsonSerializer.Serialize(eligibility));
            }
            string Facts() => string.Join("\n", new[] { "characters", "character_inventory_items", "account_wallets", "account_material_balances",
                "account_material_audit", "mailbox_messages", "mailbox_attachments", "character_raid_runs", "character_raid_reward_receipts",
                "account_battle_pass_seasons", "account_battle_pass_mission_progress", "account_battle_pass_source_events" }.Select(table =>
                {
                    using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = $"SELECT * FROM {table} ORDER BY rowid";
                    using var rows = cmd.ExecuteReader(); var values = new List<object[]>();
                    while (rows.Read()) { var row = new object[rows.FieldCount]; rows.GetValues(row); values.Add(row); }
                    return table + ":" + JsonSerializer.Serialize(values);
                }));
            long Number(string sql, params object[] values) => Convert.ToInt64(Scalar(sql, values));
            string Scalar(string sql, params object[] values)
            {
                using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql;
                for (int index = 0; index < values.Length; index++) cmd.Parameters.AddWithValue("$p" + index, values[index]);
                return Convert.ToString(cmd.ExecuteScalar())!;
            }
            void Sql(string sql, params object[] values)
            {
                using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql;
                for (int index = 0; index < values.Length; index++) cmd.Parameters.AddWithValue("$p" + index, values[index]);
                cmd.ExecuteNonQuery();
            }
            SqliteConnection Open()
            {
                var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
                db.Open(); return db;
            }
        }
        finally { if (passed) TestFiles.DeleteDirectory(root, recursive: true); }
    }
}
