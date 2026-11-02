using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class Strongway2019MatchingEntryTests
{
    public static void RunCore()
    {
        int before = Count;
        using var f = new Fixture();
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var rows = f.Characters.Select((character, index) => new PartyDungeonReservation(f.Leases[index], character, 300,
            new(null, 0, new(character.Id, 0, 1, index < 4 ? first : second, 1), 400593)
            { DungeonId = 5360 })).ToArray();
        var result = f.Repository.CommitStrongway2019MatchingEntry(rows, f.Required);
        That(result.Length == 8 && result.All(row => row.Character.DungeonState?.LastAttempt == 1 &&
            row.Character.DungeonState.NextObjectKey == 300) &&
            result.Select((row, i) => row.Character.Revision == f.Characters[i].Revision + 1).All(value => value) &&
            result.Select(row => row.Character.Id).SequenceEqual(f.Characters.Select(row => row.Id)),
            "两支独立四人实例在一次writer事务内保存八名原角色的尝试号和实际revision");
        That(f.Snapshot() == JsonSerializer.Serialize(result.Select(row => row.Character)),
            "八名入场提交结果与各账号重读一致");
        Reject<InvalidOperationException>(() => f.Repository.CommitStrongway2019MatchingEntry(rows, f.Required),
            "旧八人入场计划不能重复预留尝试号");
        Reject<InvalidDataException>(() => f.Repository.CommitPartyDungeonEntry(rows),
            "普通组队入场合同仍限制单一一至四人实例");
        using var rollback = new Fixture();
        var pending = rows.Select((row, i) => row with
        {
            Owner = rollback.Leases[i], Expected = rollback.Characters[i],
            Charge = row.Charge with { Identity = row.Charge.Identity with { CharacterId = rollback.Characters[i].Id } }
        }).ToArray();
        string baseline = rollback.Snapshot();
        rollback.Sql($"CREATE TRIGGER fail_sw_eighth BEFORE UPDATE ON character_dungeon_state WHEN NEW.character_id={pending[^1].Expected.Id} BEGIN SELECT RAISE(ABORT,'sw-eighth'); END");
        Reject<SqliteException>(() => rollback.Repository.CommitStrongway2019MatchingEntry(pending, rollback.Required),
            "第八名成员晚期SQL失败传播");
        That(rollback.Snapshot() == baseline, "第八名失败回滚全部八人的revision、尝试号与对象游标");
        rollback.Sql("DROP TRIGGER fail_sw_eighth");
        rollback.Events.Override(312, false);
        Reject<InventoryEventRejectedException>(() => rollback.Repository.CommitStrongway2019MatchingEntry(pending, rollback.Required),
            "配对后活动revision或时段失效由同writer事务拒绝");
        That(rollback.Snapshot() == baseline, "活动失效不安装任何一侧队伍或消耗尝试号");
        foreach (var invalid in new[] { Array.Empty<PartyDungeonReservation>(), pending[..1],
            pending.Select((row, i) => row with { Charge = row.Charge with
                { Identity = row.Charge.Identity with { InstanceId = i < 5 ? first : second } } }).ToArray(),
            pending.Select((row, i) => i == 7 ? pending[0] : row).ToArray(), pending.Select(row => row with
                 { Charge = row.Charge with { Identity = row.Charge.Identity with { InstanceId = first } } }).ToArray(),
            pending.Select((row,i) => i == 7 ? row with { Charge = row.Charge with { DungeonId = 5359 } } : row).ToArray() })
            Reject<InvalidDataException>(() => rollback.Repository.CommitStrongway2019MatchingEntry(invalid, rollback.Required),
                "双队入口拒绝空队、缺少对手、单组超过四人、重复角色和混入普通图");
        SmallTeamEntry(1, 1);
        SmallTeamEntry(1, 4);
        SmallTeamEntry(4, 3);
        NormalEntryEventBoundary();
        Console.WriteLine($"PASS {Count - before} 首期强者之路双实例八人原子入场");
    }

    static void SmallTeamEntry(int firstCount, int secondCount)
    {
        using var f = new Fixture();
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var rows = f.Characters.Take(firstCount + secondCount).Select((character, index) =>
            new PartyDungeonReservation(f.Leases[index], character, 300,
                new(null, 0, new(character.Id, 0, 1, index < firstCount ? first : second, 1), 400593)
                { DungeonId = 5360 })).ToArray();
        string baseline = f.Snapshot();
        f.Sql($"CREATE TRIGGER fail_sw_last BEFORE UPDATE ON character_dungeon_state WHEN NEW.character_id={rows[^1].Expected.Id} BEGIN SELECT RAISE(ABORT,'sw-last'); END");
        Reject<SqliteException>(() => f.Repository.CommitStrongway2019MatchingEntry(rows, f.Required),
            "小队匹配最后成员SQL失败传播：" + firstCount + "v" + secondCount);
        That(f.Snapshot() == baseline, "小队匹配晚期失败回滚两队全部存档");
        f.Sql("DROP TRIGGER fail_sw_last");
        var committed = f.Repository.CommitStrongway2019MatchingEntry(rows, f.Required);
        That(committed.Length == rows.Length && committed.Select((row, index) =>
            row.Character.Id == rows[index].Expected.Id && row.Character.Revision == rows[index].Expected.Revision + 1 &&
            row.Character.DungeonState is { LastAttempt: 1, NextObjectKey: 300 }).All(value => value),
            "两支真实小队按实际成员数原子预留入场与revision：" + firstCount + "v" + secondCount);
        var reopened = new CharacterRepository(f.DatabasePath);
        That(committed.Select((row, index) => JsonSerializer.Serialize(reopened.LoadCharacter(f.Leases[index], row.Character.Slot)) ==
            JsonSerializer.Serialize(row.Character)).All(value => value),
            "重建仓储后读取小队持久入场与原提交一致");
    }

    static void NormalEntryEventBoundary()
    {
        using var fixture = new Fixture();
        var instance = Guid.NewGuid();
        var rows = fixture.Characters.Take(4).Select((character, index) =>
            new PartyDungeonReservation(fixture.Leases[index], character, 300,
                new(null, 0, new(character.Id, 0, 1, instance, 1), 400593)
                { DungeonId = 5359, RequiredEvent = fixture.Required })).ToArray();
        That(rows.All(row => !row.Charge.IsEmpty), "无费用首期活动入场仍携带正式writer前置条件");
        string baseline = fixture.Snapshot();
        fixture.Events.Override(312, false);
        Reject<InventoryEventRejectedException>(() => fixture.Repository.CommitDungeonEntry(rows[0].Owner,
            rows[0].Expected, rows[0].NextObjectKey, rows[0].Charge), "单人活动在规划后关闭由入场writer拒绝");
        That(fixture.Snapshot() == baseline, "单人活动跨界不预留尝试号、revision或游标");
        Reject<InventoryEventRejectedException>(() => fixture.Repository.CommitPartyDungeonEntry(rows),
            "普通四人首期活动在规划后关闭由同一入场writer拒绝");
        That(fixture.Snapshot() == baseline, "普通Party活动跨界整队无任何存档变化");
        var renewed = new InventoryEventPrecondition(fixture.Events.Override(312, true).Schedule, TimeProvider.System);
        var mixed = rows.Select((row, i) => row with
            { Charge = row.Charge with { RequiredEvent = i == 3 ? fixture.Required : renewed } }).ToArray();
        Reject<InventoryEventRejectedException>(() => fixture.Repository.CommitPartyDungeonEntry(mixed),
            "第四名活动快照过期回滚前三名已执行的入场写入");
        That(fixture.Snapshot() == baseline, "普通Party晚期活动快照失败仍回滚整队");
        var committed = fixture.Repository.CommitPartyDungeonEntry(rows.Select(row => row with
            { Charge = row.Charge with { RequiredEvent = renewed } }).ToArray());
        That(committed.Length == 4 && committed.All(row => row.Character.DungeonState?.LastAttempt == 1),
            "同一新活动快照可通过普通Party零费用入场原事务");
    }

    sealed class Fixture : IDisposable
    {
        readonly string directory = Path.Combine(Path.GetTempPath(), "S5A9-sw-eight-" + Guid.NewGuid().ToString("N"));
        readonly string path;
        public string DatabasePath => path;
        public CharacterRepository Repository { get; }
        public ServerEventService Events { get; }
        public AccountOwners.Lease[] Leases { get; }
        public Character[] Characters { get; }
        public InventoryEventPrecondition Required { get; }
        public Fixture()
        {
            Directory.CreateDirectory(directory); path = Path.Combine(directory, "game.db"); Repository = new(path);
            Events = new(new ServerEventRepository(path));
            Required = new(Events.Override(312, true).Schedule, TimeProvider.System);
            var owners = new AccountOwners();
            Leases = Enumerable.Range(0, 8).Select(i => owners.Acquire("sw-eight-" + i, () => { })).ToArray();
            Characters = Leases.Select((owner, i) => Repository.Create(owner, new("SWEight" + i, new(65, 1, 450, 234), null)
                { Level = 95, DungeonState = new(256, 0, 0, 0, 0) })).ToArray();
        }
        public string Snapshot() => JsonSerializer.Serialize(Characters.Select((character, i) => Repository.LoadCharacter(Leases[i], character.Slot)));
        public void Sql(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        public void Dispose() { foreach (var lease in Leases) lease.Dispose(); TestFiles.DeleteDirectory(directory, true); }
    }
}
