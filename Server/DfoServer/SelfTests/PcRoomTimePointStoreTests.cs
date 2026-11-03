using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.OnlineEvents;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PcRoomTimePointStoreTests
{
    public static void Run()
    {
        int before = Count;
        WatermarksAndClaims();
        RollbackAndGuards();
        Migration();
        Console.WriteLine($"PASS {Count - before} 累计在线账号水位、永久领取与同事务邮件断言");
    }

    static void WatermarksAndClaims()
    {
        using var f = new Fixture();
        int day = PcRoomTimePointDay.At(f.Now);
        var segment = Guid.NewGuid();
        string before = f.Snapshot();
        That(f.Repository.Read(f.Owner, day, f.Catalog) == new PcRoomTimePointSnapshot(day, 0, 0, 0, 0) && f.Snapshot() == before,
            "首次读取没有进度时不写库");
        var first = f.Update(segment, [new(day, 1000)], new(0, 0, 1), f.Items(2));
        That(first.Claimed && !first.Replayed && first.MailId > 0 && first.Snapshot is { Seconds: 1, DailyClaims: 1, QualifiedDays: 0 },
            "每日阈值达到后在一次提交创建领取事实");
        var mail = f.Repository.LoadMailbox(f.Owner, f.Character.Id, f.Now.ToUnixTimeSeconds()).Entries.Single();
        var item = mail.Attachments.Single().Item;
        That(mail.System && mail.Expires is null && mail.Gold == 0 && mail.SenderName == "累积在线得好礼" &&
            item.Uid > 0 && item.Core.Count == 2 && item.Core.ExpireTime == f.Now.ToUnixTimeSeconds() + 864000 && item.Core.Extension121 == 42,
            "复用永久系统邮件与真实物理UID，原附件数量期限及metadata保留");
        before = f.Snapshot();
        var replay = f.Update(segment, [new(day, 1000)], new(0, 0, 1), f.Items(2));
        var older = f.Update(segment, [new(day, 500)]);
        That(replay is { Claimed: false, Replayed: true } && replay.MailId == first.MailId &&
            older.Snapshot.Seconds == 1 && f.Snapshot() == before,
            "相同或较小绝对段水位零变化，重复领奖只返回原邮件");
        using (var external = f.Open())
        using (var writer = external.BeginTransaction(deferred: false))
        {
            var reading = Task.Run(() =>
            {
                var duplicate = f.Update(segment, [new(day, 1000)], new(0, 0, 1));
                var unchanged = f.Update(segment, [new(day, 500)]);
                var empty = f.Update(Guid.NewGuid(), []);
                return duplicate.Replayed && unchanged.Snapshot == replay.Snapshot && empty.Snapshot == replay.Snapshot;
            });
            bool completed;
            try { completed = reading.Wait(TimeSpan.FromSeconds(2)); }
            finally { writer.Rollback(); }
            That(reading.GetAwaiter().GetResult() && completed && f.Snapshot() == before,
                "外部writer保持时重复领取、旧水位与空段查询仍只读完成，不申请写锁");
        }
        var qualified = f.Update(segment, [new(day, 4500)]);
        That(qualified.Snapshot is { Seconds: 4, QualifiedDays: 1 }, "同段只加新增3500毫秒且达最后档只记一个合格日");
        var other = f.Repository.Create(f.Owner, new("OnlineSecond", f.Character.Position, null));
        var shared = f.Repository.Update(f.Owner, other, Guid.NewGuid(), [new(day, 500)], f.Now, f.Catalog);
        That(shared.Snapshot is { Seconds: 5, DailyClaims: 1, QualifiedDays: 1 }, "同账号换角继承时长及领取事实，新段只追加自身水位");
        var period = f.Repository.Update(f.Owner, other, Guid.NewGuid(), [], f.Now, f.Catalog, new(1, 0, 1), f.Items(3));
        That(period.Claimed && period.Snapshot.PeriodClaims == 1 && period.Snapshot.QualifiedDays == 1,
            "累计天数奖励使用账号合格日且独立于每日领取");
        var tomorrow = f.Now.AddDays(1);
        int nextDay = PcRoomTimePointDay.At(tomorrow);
        var cross = f.Repository.Update(f.Owner, other, segment, [new(day, 5000), new(nextDay, 4000)], tomorrow, f.Catalog);
        That(cross.Snapshot is { Seconds: 4, DailyClaims: 0, QualifiedDays: 2, PeriodClaims: 1 } &&
            f.Repository.Read(f.Owner, day, f.Catalog).Seconds == 5,
            "跨日段按服务日各自绝对水位保存，日领取重置而累计事实保留");
        f.Repository.ChangeMailState(f.Owner, f.Character.Id, [first.MailId], 0, f.Now.ToUnixTimeSeconds(), force: true);
        f.Sql("UPDATE server_events SET override_active=0,revision=2 WHERE event_id=228");
        var removed = f.Update(segment, [], new(0, 0, 1));
        That(removed.Replayed && removed.MailId == first.MailId && f.Query("SELECT count(*) FROM mailbox_messages") is 2L,
            "活动关闭且原邮件删除后仍重放永久领取事实，不重发邮件");
        f.Repository.Delete(f.Owner, f.Repository.LoadDeletionTarget(f.Owner, f.Character.Slot));
        That(f.Repository.Read(f.Owner, day, f.Catalog) is { DailyClaims: 1, QualifiedDays: 2, PeriodClaims: 1 },
            "删除领奖角色不清账号时长、累计天数与已领事实");
        f.Integrity();
    }

    static void RollbackAndGuards()
    {
        using var f = new Fixture();
        int day = PcRoomTimePointDay.At(f.Now);
        var segment = Guid.NewGuid();
        string before = f.Snapshot();
        f.Sql("CREATE TRIGGER online228_late_abort AFTER INSERT ON account_pcroom_timepoint_claims BEGIN SELECT RAISE(ABORT,'online reward rollback'); END");
        string withTrigger = f.Snapshot();
        Reject<SqliteException>(() => f.Update(segment, [new(day, 4000)], new(0, 0, 1), f.Items(2)),
            "真实邮件和UID写入后领取事实末段SQL失败");
        That(f.Snapshot() == withTrigger && f.Query("SELECT count(*) FROM sqlite_sequence WHERE name IN('mailbox_messages','mailbox_attachments','character_inventory_items') AND seq>0") is 0L,
            "晚失败回滚日进度、段水位、资格、邮件、附件、审计及UID序列");
        f.Sql("DROP TRIGGER online228_late_abort");
        That(f.Snapshot() == before, "移除故障夹具后原数据库逐表仍相同");
        f.Sql("UPDATE server_events SET override_active=0,revision=2 WHERE event_id=228");
        before = f.Snapshot();
        Reject<InvalidOperationException>(() => f.Update(segment, [new(day, 4000)], new(0, 0, 2), f.Items(2)), "关闭活动不能新领奖");
        That(f.Snapshot() == before, "拒绝领奖同时回滚该次未提交水位");
        That(f.Update(segment, [new(day, 2000)]).Snapshot.Seconds == 2, "关闭后仍允许保存之前合法段水位");
        f.Sql("UPDATE server_events SET override_active=1,revision=3 WHERE event_id=228");
        before = f.Snapshot();
        Reject<InvalidOperationException>(() => f.Update(segment, [new(day, 4000)], new(0, 0, 1), f.Items(2)), "开启新版本后旧活动revision领奖拒绝");
        Reject<InvalidDataException>(() => f.Update(segment, [new(day, 4000)], new(0, 0, 3), f.Items(1)), "附件总数量必须等于资源奖励");
        That(f.Snapshot() == before, "旧版本和错误附件不留水位或资格");
        var done = f.Update(segment, [new(day, 4000)], new(0, 0, 3), f.Items(2));
        That(done.Claimed && done.Snapshot is { Seconds: 4, QualifiedDays: 1, DailyClaims: 1 }, "同段原请求修正后重试正常只发一次");
        var original = f.Character;
        f.Character = f.Repository.Save(f.Owner, original, original.Position, original.Return);
        before = f.Snapshot();
        Reject<InvalidOperationException>(() => f.Repository.Update(f.Owner, original, Guid.NewGuid(), [new(day, 2000)], f.Now, f.Catalog),
            "实际角色revision变化时陈旧角色快照不写在线进度");
        That(f.Snapshot() == before, "陈旧角色拒绝不改原资产和已领事实");
        f.Integrity();
    }

    static void Migration()
    {
        using var f = new Fixture();
        string character = JsonSerializer.Serialize(f.Repository.LoadCharacter(f.Owner, f.Character.Slot));
        f.Sql("INSERT INTO account_skin_storage(account,state_json) VALUES('online-owner','{\"Owned\":[]}'); DROP TABLE account_pcroom_timepoint_claims; DROP TABLE account_pcroom_timepoint_segments; DROP TABLE account_pcroom_timepoint_days; PRAGMA user_version=190");
        string before = f.Snapshot();
        var upgraded = new CharacterRepository(f.Path);
        That(f.Query("PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion && f.Query("SELECT state_json FROM account_skin_storage WHERE account='online-owner'") is "{\"Owned\":[]}" &&
            JsonSerializer.Serialize(upgraded.LoadCharacter(f.Owner, f.Character.Slot)) == character && f.Snapshot() == before,
            "190到191只追加三个空活动表，保角色库存与旧账号皮肤数据");
        before = f.Snapshot();
        _ = new CharacterRepository(f.Path);
        That(f.Snapshot() == before && upgraded.Read(f.Owner, PcRoomTimePointDay.At(f.Now), f.Catalog).QualifiedDays == 0,
            "当前版本重开幂等且不补赠进度或奖励");
        int day = PcRoomTimePointDay.At(f.Now);
        var reward = f.Update(Guid.NewGuid(), [new(day, 4000)], new(0, 0, 1), f.Items(2));
        foreach (string table in new[] { "account_battle_pass_cera_orders", "account_battle_pass_cera_consumption",
            "account_battle_pass_certificate_orders", "account_battle_pass_source_events", "account_battle_pass_mission_claims",
            "account_battle_pass_mission_progress", "battle_pass_season_rules", "account_battle_pass_claims",
            "account_battle_pass_seasons", "character_epic_wishes" })
            f.Sql("DROP TABLE " + table);
        f.Sql("PRAGMA user_version=186");
        before = f.Snapshot();
        upgraded = new CharacterRepository(f.Path);
        That(f.Query("PRAGMA user_version") is long currentVersion && currentVersion == SqliteMigrations.CurrentVersion && f.Snapshot() == before &&
            upgraded.Read(f.Owner, day, f.Catalog) == reward.Snapshot,
            "旧228验证v186补跑正式战令和祈愿迁移，保在线水位、已领奖邮件及资产");
        f.Integrity();
    }

    sealed class Fixture : IDisposable
    {
        readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "S5A9-online228-" + Guid.NewGuid().ToString("N"));
        readonly AccountOwners owners = new();
        public string Path => System.IO.Path.Combine(root, "game.db");
        public AccountOwners.Lease Owner { get; }
        public CharacterRepository Repository { get; }
        public Character Character { get; set; }
        public DateTimeOffset Now { get; } = new(2026, 10, 28, 12, 0, 0, TimeSpan.FromHours(8));
        public PcRoomTimePointCatalog Catalog { get; } = new(new PcRoomTimePointFile(false, true, 1, 4,
            [new(1, 50001, 2, 1000, true, true), new(1, 50001, 1, 1000, true, true),
             new(1, 50001, 1, 1000, true, true), new(1, 50001, 1, 1000, true, true)],
            [new(1, 50001, 3, 1, true), new(2, 50001, 1, 1, true),
             new(3, 50001, 1, 1, true), new(4, 50001, 1, 1, true)], "ui/fixture.xui", []));
        public Fixture()
        {
            Owner = owners.Acquire("online-owner", () => { });
            Repository = new(Path);
            Character = Repository.Create(Owner, new("OnlineFirst", new(1, 0, 100, 100), null));
            Sql("INSERT INTO server_events(event_id,revision,starts_at,ends_at,override_active) VALUES(228,1,NULL,NULL,1)");
        }
        public ImmutableArray<InventoryItem> Items(int count) =>
            [new(0, new(InventoryListType.Main, 121), new(50001, ItemKind.Material, count)
                { ExpireTime = checked((int)Now.ToUnixTimeSeconds() + 864000), Extension121 = 42 })];
        public PcRoomTimePointCommit Update(Guid segment, IReadOnlyList<PcRoomTimePointElapsed> totals,
            PcRoomTimePointClaim? claim = null, ImmutableArray<InventoryItem> items = default) =>
            Repository.Update(Owner, Character, segment, totals, Now, Catalog, claim, items);
        public SqliteConnection Open()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open(); return db;
        }
        public void Sql(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public object? Query(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
        public string Snapshot()
        {
            using var db = Open(); using var command = db.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            var tables = new List<string>();
            using (var reader = command.ExecuteReader()) while (reader.Read()) tables.Add(reader.GetString(0));
            var rows = new List<object>();
            foreach (string table in tables)
            {
                command.CommandText = "SELECT * FROM [" + table + "] ORDER BY rowid";
                using var reader = command.ExecuteReader();
                while (reader.Read()) rows.Add(new { Table = table, Values = Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue).ToArray() });
            }
            return JsonSerializer.Serialize(rows);
        }
        public void Integrity() => That(Query("PRAGMA integrity_check") is "ok" && Query("SELECT count(*) FROM pragma_foreign_key_check") is 0L,
            "在线活动与邮件资产外键完整");
        public void Dispose() { Owner.Dispose(); TestFiles.DeleteDirectory(root, true); }
    }
}
