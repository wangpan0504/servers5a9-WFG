using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class MonsterCardUpgradeTests
{
    public static void RunCore()
    {
        int checks = Count;
        var request = MonsterCardUpgradeRequests.Read(Convert.FromHexString("00E9000200EA0000EB00"));
        That(request.TargetSlot == 233 && request.MaterialSlots.SequenceEqual([(short)234, (short)235]),
            "CMD603独立解析Main目标、材料数及每个Main材料槽");
        foreach (string invalid in new[] { "", "00E900", "00E90000", "00E9000500EA00", "01E9000100EA00",
            "00E9000101EA00", "0060010100EA00",
            "00E90001006001", "00E9000100EA0000", "00E9000100EA" })
            Reject<InvalidDataException>(() => MonsterCardUpgradeRequests.Read(Convert.FromHexString(invalid)),
                "CMD603拒绝缺项、尾字节、非Main容器及越界");
        That(MonsterCardUpgradeRequests.Read(Convert.FromHexString("00E9000200EA0000EA00")).MaterialSlots
                .SequenceEqual([(short)234, (short)234]) &&
            MonsterCardUpgradeRequests.Read(Convert.FromHexString("00E9000100E900")).MaterialSlots[0] == 233,
            "CMD603保留重复材料源槽及与目标同源的材料，张数交领域校验");
        That(Convert.ToHexString(MonsterCardUpgradePackets.Success(new(true, 10310789, 1))) == "010185549D0001" &&
            Convert.ToHexString(MonsterCardUpgradePackets.Success(new(false, 10310789, 0))) == "010085549D0000" &&
            Convert.ToHexString(MonsterCardUpgradePackets.Failure()) == "001600",
            "603提交成功与概率失败都携带实际结果，业务拒绝另走公共错误码");

        var fixture = new Fixture();
        var role = fixture.Role();
        var target = role.Inventory.Find(new(InventoryListType.Main, 233))!;
        var success = fixture.Service(79).Apply(role, request, out var receipt, out _);
        That(success.Accepted && receipt is { Upgraded: true, ItemId: 1001, Level: 1 } &&
            success.Plan!.After.Find(target.Position) == target with { Core = target.Core with { EnchantUpgrade = 1 } } &&
            success.Plan.After.Items.Length == 1 && success.Plan.After.Gold == role.Inventory.Gold,
            "两张材料80%边界命中：目标原UID与全部字段保留，仅卡片等级加一且不扣金币");
        var failed = fixture.Service(80).Apply(role, request, out var failedReceipt, out _);
        That(failed.Accepted && failedReceipt is { Upgraded: false, Level: 0 } &&
            failed.Plan!.After.Items.SequenceEqual([target]) && role.Inventory.Items.Length == 3,
            "80%边界外概率失败只扣各一张材料，保留目标和原始快照");
        var stack = fixture.Role(targetCount: 3);
        var split = fixture.Service(0).Apply(stack, request, out _, out _);
        That(split.Accepted && split.Plan!.After.Find(target.Position)?.Core is { Value: 2, EnchantUpgrade: 0 } &&
            split.Plan.After.Items.Count(row => row.Core is { ItemId: 1001, Value: 1, EnchantUpgrade: 1 }) == 1 &&
            split.Plan.After.Items.Single(row => row.Core.EnchantUpgrade == 1).Uid == 0,
            "堆叠只升级一张，旧等级余量保留原UID，新增实例等待提交分配UID");
        var repeated = fixture.Service(0).Apply(fixture.Role(materialCount: 3), new(233, [234, 234]), out _, out _);
        That(repeated.Accepted && repeated.Plan!.After.Find(new(InventoryListType.Main, 234))?.Core.Count == 1 &&
            repeated.Plan.After.Find(target.Position)?.Core.EnchantUpgrade == 1,
            "重复材料源槽扣实际两张，目标只升级一张");
        foreach (int count in new[] { 3, 4 })
        {
            var sameSource = fixture.Role(targetCount: count);
            var sameSuccess = fixture.Service(0).Apply(sameSource, new(233, [233, 233]), out _, out _);
            That(sameSuccess.Accepted && sameSuccess.Plan!.After.Items.Where(row => row.Core.ItemId == 1001)
                    .Sum(row => row.Core.Count) == count &&
                sameSuccess.Plan.After.Items.Count(row => row.Core.EnchantUpgrade == 1 && row.Core.Count == 1) == 1 &&
                sameSource.Inventory.Find(target.Position)?.Core.Count == count,
                "目标同源堆扣两张材料后升级一张，覆盖原槽仅余目标及需要分堆，原快照不变");
            var sameFailed = fixture.Service(80).Apply(sameSource, new(233, [233, 233]), out _, out _);
            That(sameFailed.Accepted && sameFailed.Plan!.After.Find(target.Position)?.Core is { EnchantUpgrade: 0 } remaining &&
                remaining.Count == count - 2,
                "目标同源堆概率失败仅扣两张材料，保留目标及其余未升级卡");
        }
        foreach (var invalidRequest in new MonsterCardUpgradeRequest[] { new(233, [234, 234]), new(233, [233]) })
            That(!fixture.Service(0).Apply(role, invalidRequest, out _, out var countReason).Accepted && countReason == "material-count",
                "不足重复材料或不能另留目标一张时整笔拒绝");
        var mergeRole = fixture.Role(targetCount: 3, materialCount: 2);
        var existing = new InventoryItem(999, new(InventoryListType.Main, 236), target.Core with { Value = 4, EnchantUpgrade = 1 });
        mergeRole = mergeRole with { Inventory = mergeRole.Inventory with { Items = mergeRole.Inventory.Items.Add(existing) } };
        var merged = fixture.Service(0).Apply(mergeRole, request, out _, out _);
        That(merged.Accepted && merged.Plan!.After.Find(existing.Position) == existing with { Core = existing.Core with { Value = 5 } } &&
            merged.Plan.After.Find(new(InventoryListType.Main, 234))?.Core.Count == 1 &&
            merged.Plan.After.Find(target.Position)?.Core.Count == 2,
            "成功优先合并同等级完整属性堆，保留既有UID，材料堆各只扣一张");
        var upgradedMaterial = role with { Inventory = role.Inventory with { Items = role.Inventory.Items.Select(row =>
            row.Position.Index == 234 ? row with { Core = row.Core with { EnchantUpgrade = 1 } } : row).ToImmutableArray() } };
        That(fixture.Service(0).Apply(upgradedMaterial, request, out _, out _).Accepted,
            "同稀有度已升级材料仍可消耗，不擅加客户端没有的等级限制");
        foreach (var core in new[] { target.Core with { EnchantUpgrade = 1 }, target.Core with { EquipmentLockId = 1 },
            target.Core with { ExpireTime = 1 }, target.Core with { Kind = ItemKind.Material }, target.Core with { ItemId = 1003 } })
        {
            var invalid = role with { Inventory = role.Inventory with { Items = role.Inventory.Items.Replace(target, target with { Core = core }) } };
            That(!fixture.Service(0).Apply(invalid, request, out var rejected, out _).Accepted && rejected is null,
                "满级、锁定、到期、类别不符及非卡片目标整笔拒绝");
        }
        var material = role.Inventory.Items[1];
        foreach (var core in new[] { material.Core with { ItemId = 1002 }, material.Core with { EquipmentLockId = 1 },
            material.Core with { ExpireTime = 1 }, material.Core with { ItemId = 1003 } })
        {
            var invalid = role with { Inventory = role.Inventory with { Items = role.Inventory.Items.Replace(material, material with { Core = core }) } };
            That(!fixture.Service(0).Apply(invalid, request, out _, out _).Accepted,
                "不同稀有度、锁定、过期、非卡片材料拒绝且无扣费计划");
        }
        var full = fixture.Role(targetCount: 2, materialCount: 2);
        var slots = Enumerable.Range(236, 53).Select(slot => new InventoryItem(slot, new(InventoryListType.Main, (short)slot),
            new(1003, ItemKind.Profession, 1)));
        full = full with { Inventory = full.Inventory with { Items = full.Inventory.Items.AddRange(slots) } };
        That(!fixture.Service(0).Apply(full, request, out _, out var reason).Accepted && reason == "card-capacity",
            "满卡片页且源堆仍有余量时整笔拒绝，不吞升级卡或材料");
        That(!fixture.Service(80).Apply(full, request, out _, out _).Accepted,
            "容量资格先于随机结果，不能无空间时只结算失败");
        That(fixture.Rates.CardUpgradeChance(6, 1) == 40 && fixture.Rates.CardUpgradeChance(6, 2) == 80 &&
            fixture.Rates.CardUpgradeChance(6, 3) == 100, "传说卡材料1/2/3张的概率为40/80/100");
        Reject<InvalidDataException>(() => fixture.Rates.CardUpgradeChance(6, 4), "零概率材料数不可冒充有效升级配置");
        foreach (int[] levels in new[] { new[] { 1 }, new[] { 0, 2 }, new[] { 0, 0 } })
        {
            var catalog = new EnchantmentCatalog(_ => "", new Dictionary<int, string> { [1001] = "card.stk" }, _ => Table(levels));
            Reject<InvalidDataException>(() => catalog.CardMaximumLevel(1001), "拒绝缺零级、跳级与重复等级资源");
        }
        SortLockMerge();
        SortLockMergePersistence();
        Console.WriteLine($"PASS {Count - checks} 卡片升级协议、概率、分堆与资产边界断言");
    }

    static void SortLockMerge()
    {
        var fixture = new Fixture(maximumLevel: 2);
        foreach (byte targetLock in new byte[] { 0, 1 })
        {
            var role = fixture.MergeRole(targetLock);
            var source = role.Inventory.Find(new(InventoryListType.Main, 233))!;
            var recipient = role.Inventory.Find(new(InventoryListType.Main, 234))!;
            var result = fixture.Service(0).Apply(role, new(233, [234]), out var receipt, out _);
            var plan = result.Plan!;
            That(result.Accepted && receipt is { Upgraded: true, Level: 2 } &&
                plan.After.Find(source.Position) == source with { Core = source.Core with { Value = 2 } } &&
                plan.After.Find(recipient.Position) == recipient && plan.After.Items.Length == role.Inventory.Items.Length &&
                plan.After.Find(new(InventoryListType.Main, 235)) == role.Inventory.Find(new(InventoryListType.Main, 235)) &&
                plan.MonsterCardUpgrade is { } fact && fact.Matches(plan, plan.After),
                "真实+1堆升级一张后合回异排序锁+2材料余堆，净数量、原UID和完整属性均保持，成功事实通过");
        }
    }

    static void SortLockMergePersistence()
    {
        // 合成资源与隔离SQLite覆盖活动战令提交，不依赖玩家PVF或角色库。
        string root = Path.Combine(Path.GetTempPath(), "S5A9-card-sortlock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool succeeded = false;
        try
        {
            string path = Path.Combine(root, "game.db");
            const string account = "card-sortlock", season = "card-sortlock-core";
            const string marker = "card sortlock inventory and both missions reached";
            var start = new DateTimeOffset(2026, 11, 5, 6, 0, 0, TimeSpan.FromHours(8));
            var clock = new UpgradeClock(start.AddHours(1));
            var policy = new BattlePassMissionPolicy(season, start, UpgradeMissionCatalog());
            var repository = new CharacterRepository(path);
            repository.ConfigureBattlePassMissions(policy, clock);
            using var owner = new AccountOwners().Acquire(account, () => { });
            var fixture = new Fixture(maximumLevel: 2);
            var seed = fixture.MergeRole(0);
            var role = repository.Create(owner, new NewCharacter("CardSortLock", seed.Position, seed.Return)
            {
                Level = seed.Level,
                Inventory = seed.Inventory with { Items = seed.Inventory.Items.Select(item => item with { Uid = 0 }).ToImmutableArray() }
            });
            var initial = ReadMissions(repository);
            That(initial.Active && initial.Season is { Revision: 0, TotalExperience: 0 } && initial.Slots.All(row => row.Count == 0) &&
                initial.Slots.Count(row => row.Instance.Key.Id is 45 or 70) == 2,
                "隔离活动战令真实激活周45与季70，所有进度与经验从零开始");
            var result = fixture.Service(0, clock).Apply(role, new(233, [234]), out var receipt, out _);
            That(result.Accepted && receipt is { Upgraded: true, Level: 2 } && result.Plan?.MonsterCardUpgrade is not null,
                "持久化回归使用真实服务的确定成功计划及原GUID");
            var plan = result.Plan!;
            var fact = plan.MonsterCardUpgrade!;
            var target = role.Inventory.Find(new(InventoryListType.Main, 233))!;
            var recipient = role.Inventory.Find(new(InventoryListType.Main, 234))!;
            string before = Snapshot();
            var alteredAfter = plan.After with { Items = plan.After.Items.Replace(recipient,
                recipient with { Core = recipient.Core with { SortLockFlag = 0 } }) };
            var altered = new InventoryPlan(plan.Before, alteredAfter, plan.Reason, plan.CharacterPrecondition)
                { MonsterCardUpgrade = fact with { After = alteredAfter } };
            bool rejectedAtFact = false;
            try { repository.CommitInventory(owner, role, altered); }
            catch (InvalidDataException error) when (error.Message == "怪物卡片升级来源与实际库存事务不一致") { rejectedAtFact = true; }
            That(rejectedAtFact && Snapshot() == before,
                "同步伪造计划和事实的原堆改锁仍在来源校验拒绝，库存、审计及战令整笔回滚");
            Sql($"""
                CREATE TRIGGER fail_card_sortlock AFTER UPDATE OF revision ON account_battle_pass_seasons
                WHEN NEW.account='{account}' AND NEW.season_key='{season}'
                BEGIN
                  SELECT CASE WHEN NEW.revision=1 AND NEW.total_experience=0
                    AND (SELECT count(*) FROM account_battle_pass_source_events
                      WHERE account=NEW.account AND season_key=NEW.season_key AND source_kind='monster_card_upgrade' AND source_id='{fact.Id:N}')=1
                    AND (SELECT count(*) FROM account_battle_pass_mission_progress
                      WHERE account=NEW.account AND season_key=NEW.season_key AND mission_id IN(45,70) AND current_count=1)=2
                    AND (SELECT count(*) FROM character_inventory_items i JOIN character_inventory_slots s ON s.item_uid=i.item_uid
                      WHERE i.account=NEW.account AND i.character_id={role.Id} AND s.list_type={(int)InventoryListType.Main}
                        AND ((i.item_uid={target.Uid} AND s.slot_index=233 AND json_extract(i.item_core,'$.EnchantUpgrade')=1
                          AND json_extract(i.item_core,'$.Value')=2 AND json_extract(i.item_core,'$.SortLockFlag')=0)
                        OR (i.item_uid={recipient.Uid} AND s.slot_index=234 AND json_extract(i.item_core,'$.EnchantUpgrade')=2
                          AND json_extract(i.item_core,'$.Value')=3 AND json_extract(i.item_core,'$.SortLockFlag')=1)))=2
                  THEN RAISE(ABORT,'{marker}') ELSE RAISE(ABORT,'card sortlock late prerequisites missing') END;
                END;
                """);
            bool reachedLateFailure = false;
            try { repository.CommitInventory(owner, role, plan); }
            catch (SqliteException error) when (error.SqliteErrorCode == 19 && error.Message == $"SQLite Error 19: '{marker}'.")
            { reachedLateFailure = true; }
            That(reachedLateFailure && Snapshot() == before,
                "异排序锁合堆通过来源校验并写完真实GUID、周季进度和季版本后，准确尾故障仍回滚全部资产与事实");
            Sql("DROP TRIGGER fail_card_sortlock");
            var committed = repository.CommitInventory(owner, role, plan);
            var missions = ReadMissions(repository);
            That(committed.Revision == role.Revision + 1 &&
                committed.Inventory.Items.OrderBy(item => item.Position.Index).SequenceEqual(plan.After.Items.OrderBy(item => item.Position.Index)) &&
                missions.Season is { Revision: 1, TotalExperience: 0 } &&
                missions.Slots.Where(row => row.Instance.Key.Id is 45 or 70).All(row => row.Count == 1 && !row.Ready && !row.Completed) &&
                Scalar("SELECT count(*) FROM account_battle_pass_source_events") is 1L &&
                Scalar("SELECT source_id FROM account_battle_pass_source_events") is string sourceId && sourceId == fact.Id.ToString("N"),
                "同一冻结计划重试仅提交一次真实合堆与两项进度，不重抽、不发XP或重复来源");
            string once = Snapshot();
            Reject<InvalidOperationException>(() => repository.CommitInventory(owner, role, plan), "原角色旧计划重放拒绝");
            That(Snapshot() == once, "旧计划重放不改变资产、审计、GUID、任务与版本");
            var reopened = new CharacterRepository(path);
            reopened.ConfigureBattlePassMissions(policy, clock);
            var restored = reopened.LoadCharacter(owner, role.Slot);
            var restoredMissions = ReadMissions(reopened);
            That(restored.Revision == committed.Revision &&
                restored.Inventory.Items.OrderBy(item => item.Position.Index).SequenceEqual(committed.Inventory.Items.OrderBy(item => item.Position.Index)) &&
                restoredMissions.Season is { Revision: 1, TotalExperience: 0 } &&
                restoredMissions.ToPayload().SequenceEqual(missions.ToPayload()) &&
                Scalar("PRAGMA integrity_check") is "ok" && Query("PRAGMA foreign_key_check").Length == 0,
                "重开仓储恢复真实UID、原排序锁、完整卡片与战令进度，SQLite及外键完整");
            succeeded = true;

            BattlePassMissionState ReadMissions(CharacterRepository source) => source.ReadBattlePassMissions(owner, clock.GetUtcNow())
                ?? throw new InvalidOperationException("卡片升级回归的活动战令未配置");
            SqliteConnection Open()
            {
                var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
                db.Open(); return db;
            }
            void Sql(string sql)
            {
                using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
            }
            object?[][] Query(string sql)
            {
                using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql;
                using var reader = command.ExecuteReader(); var rows = new List<object?[]>();
                while (reader.Read())
                {
                    var row = new object?[reader.FieldCount];
                    for (int i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    rows.Add(row);
                }
                return rows.ToArray();
            }
            object? Scalar(string sql) => Query(sql).Single().Single();
            string Snapshot() => JsonSerializer.Serialize(new[] {
                "SELECT * FROM characters ORDER BY id", "SELECT * FROM character_inventory_items ORDER BY item_uid",
                "SELECT * FROM character_inventory_slots ORDER BY character_id,list_type,slot_index",
                "SELECT * FROM inventory_avatar_details ORDER BY item_uid", "SELECT * FROM inventory_creature_details ORDER BY item_uid",
                "SELECT * FROM account_wallets ORDER BY account", "SELECT * FROM account_cargo ORDER BY account",
                "SELECT * FROM account_currency_balances ORDER BY account,currency_kind", "SELECT * FROM inventory_audit_log ORDER BY audit_id",
                "SELECT * FROM currency_audit_log ORDER BY audit_id", "SELECT * FROM account_battle_pass_seasons ORDER BY account,season_key",
                "SELECT * FROM account_battle_pass_source_events ORDER BY account,source_kind,source_id",
                "SELECT * FROM account_battle_pass_mission_progress ORDER BY account,season_key,category,period_key,mission_id",
                "SELECT * FROM account_battle_pass_mission_claims ORDER BY account,season_key,category,period_key,mission_id",
                "SELECT * FROM sqlite_sequence ORDER BY name"
            }.Select(sql => Query(sql)).ToArray());
        }
        finally
        {
            if (succeeded) TestFiles.DeleteDirectory(root, recursive: true);
            else Console.Error.WriteLine($"卡片异排序锁合堆回归失败，隔离现场保留于：{root}");
        }
    }

    static BattlePassCatalog UpgradeMissionCatalog()
    {
        int[] daily = [1, 2, 3, 4], weekly = [25, 26, 45, 28], season = [.. Enumerable.Range(51, 17), 70];
        var source = new List<object> { "[ui path]", "UI/CardUpgradeFixture.xui" };
        foreach (int id in daily.Concat(weekly).Concat(season))
            source.AddRange(new object[] { "[mission]", id, id == 45 ? 3 : id == 70 ? 30 : 1,
                id == 45 ? 1000 : id == 70 ? 1600 : 100, id is 45 or 70 ? 31 : 777, "卡片升级夹具", -1, "[/mission]" });
        source.Add("[daily mission]"); source.AddRange(daily.Cast<object>());
        source.AddRange(new object[] { "[/daily mission]", "[weekly mission]" }); source.AddRange(weekly.Cast<object>());
        source.AddRange(new object[] { "[/weekly mission]", "[season mission]" }); source.AddRange(season.Cast<object>());
        source.AddRange(new object[] { "[/season mission]", "[level reward]", 1, 1001, 1, 0, "[/level reward]",
            "[exp table]", 1, 0, "[/exp table]", "[tier table]", 1, 1, "[/tier table]", "[postal]", 1, 2, 3, 4, "[/postal]",
            "[package index]", 10, "[/package index]", "[certificate index]", 11, "[/certificate index]",
            "[buy restrict code]", 12, "[/buy restrict code]", "[golden mission exp ratio]", 3, "[cera consumption exp percentage]", 50 });
        var strings = new List<string>();
        PvfToken Token(object value)
        {
            if (value is int number) return new(0, number);
            string text = (string)value; strings.Add(text);
            return new(text.StartsWith('[') ? (byte)3 : (byte)6, strings.Count - 1);
        }
        return BattlePassCatalog.Parse(source.Select(Token).ToArray(), index => strings[index]);
    }

    sealed class UpgradeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public static void RunResources(ItemResources resources)
    {
        That(resources.Items.Get(10310789) is { IsMonsterCard: true, Rarity: 6 } &&
            resources.Enchantments.CardMaximumLevel(10310789) == 1, "报告四维100→150卡为传说且可升一级");
        var effects = resources.Enchantments.ReadEffects(10310789, 1);
        That(effects.Length == 4 && effects.All(row => row.IntegerValues.SequenceEqual([150])),
            "升级后的四维由正式卡片index1完整效果派生");
        That(resources.Enchantments.CardMaximumLevel(3620) == 10 &&
            resources.MonsterCardBinds.CardUpgradeChance(0, 1) == 100,
            "普通猫妖卡保留十级上限，普通卡一张材料必成");
    }

    static PvfTokenDocument Table(params int[] levels)
    {
        var strings = new[] { "[enchant table]", "[enchant index]", "[physical attack]", "[/enchant index]", "[/enchant table]" };
        var tokens = new List<PvfToken> { new(3, 0) };
        foreach (int level in levels)
            tokens.AddRange([new(3, 1), new(0, level), new(3, 2), new(0, 100 + level * 50), new(3, 3)]);
        tokens.Add(new(3, 4));
        return new(tokens.ToArray(), index => strings[index]);
    }

    sealed class Fixture
    {
        readonly ItemCatalog items;
        readonly EnchantmentCatalog enchantments;
        readonly GoldLimitCatalog limits = new("[gold limit from level]\n0 1000000\n[/gold limit from level]");
        readonly EpicPieceCatalog pieces = new("[piece list]\n50000 50001\n[/piece list]");
        public MonsterCardBindCatalog Rates { get; } = new("""
            [upgrade infos]
            [upgrade info]
            6 40 80 100 0 0
            [/upgrade infos]
            [bind result infos]
            [bind result info]
            0 1.00 5 1001 1
            [/bind result info]
            [/bind result infos]
            [bind shpere result infos]
            [bind result info]
            0 1.00 5 1001 1
            [/bind result info]
            [/bind shpere result infos]
            [bind shpere upgrade info]
            3 200
            [/bind shpere upgrade info]
            """);

        public Fixture(int maximumLevel = 1)
        {
            var paths = new Dictionary<int, string> { [1001] = "target.stk", [1002] = "mixed.stk", [1003] = "other.stk" };
            items = new(null!, new Dictionary<int, string>(), paths, 0);
            items.PrefillTemplates(paths.Keys.Select(id => new ItemTemplate(id, ItemKind.Profession, 0, 1, 0, 1000, 0,
                id == 1002 ? 3 : 6, 0, "[trade]", ushort.MaxValue, 0, "card", "[material expert job] 1")
                { IsMonsterCard = id != 1003 }).ToDictionary(row => row.Id));
            enchantments = new(_ => "", paths, _ => Table(Enumerable.Range(0, maximumLevel + 1).ToArray()));
        }
        public InventoryMonsterCardUpgradeService Service(int roll, TimeProvider? clock = null) => new(items, enchantments, Rates, limits, pieces, _ => roll, clock);
        public Character MergeRole(byte targetLock)
        {
            var role = Role(targetCount: 3, materialCount: 3);
            return role with { Inventory = role.Inventory with { Items = role.Inventory.Items.Select(item => item with
                { Core = item.Core with { EnchantUpgrade = item.Position.Index == 233 ? (byte)1 : item.Position.Index == 234 ? (byte)2 : (byte)0,
                    SortLockFlag = item.Position.Index == 234 ? (byte)(1 - targetLock) : targetLock, Marker16 = 3719 } }).ToImmutableArray() } };
        }
        public Character Role(int targetCount = 1, int materialCount = 1) => new(1, 0, "CardUpgrade", 0, new(1, 0, 10, 10), null)
        {
            Level = 95,
            Inventory = new(Enumerable.Range(233, 3).Select(slot => new InventoryItem(slot,
                new(InventoryListType.Main, (short)slot), new(1001, ItemKind.Profession, slot == 233 ? targetCount : materialCount)
                { Attr = 0x20, SortLockFlag = 1 })).ToImmutableArray())
        };
    }
}
