using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BeastDungeonReservationTests
{
    public static void RunCore()
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-beast-reservation-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "game.db");
        var owners = new AccountOwners();
        using var alpha = owners.Acquire("beast-reserve-a", () => { });
        using var beta = owners.Acquire("beast-reserve-b", () => { });
        var leases = new[] { alpha, beta };
        var repository = new CharacterRepository(path);
        var at = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.FromHours(8));
        while (!BeastDungeonRotation.Contains(BeastDungeonRotation.ServiceDay(at), 310)) at = at.AddDays(1);
        var clock = new DailyBoosterTestClock(at);
        int day = BeastDungeonRotation.ServiceDay(at);
        try
        {
            var characters = leases.Select((owner, index) => repository.Create(owner,
                new NewCharacter("Reserve" + index, new(31, 1, 450, 234), null)
                {
                    DungeonState = CharacterDungeonState.NewOrdinary, CompletedQuests = [5927],
                    Inventory = InventorySnapshot.Empty with
                    {
                        Items = [new(0, new(InventoryListType.Main, 121), new(BeastDungeonTicket.Event, ItemKind.Material, 3)),
                            new(0, new(InventoryListType.Main, 122), new(BeastDungeonTicket.Ordinary, ItemKind.Material, 3))]
                    }
                })).ToArray();
            Guid run = Guid.NewGuid();
            var pending = characters.Select((character, index) => Reserve(index, character, run, 0)).ToArray();
            That(pending.All(row => Used(row) == 0 && TicketCount(row.Expected, BeastDungeonTicket.Event) == 3),
                "beast-first-room:持久预约不扣门票或次数");
            var initial = leases.Select(owner => repository.LoadCharacter(owner, 0)).ToArray();
            Reject<BeastDungeonAdmissionRejectedException>(() => repository.CommitBeastDungeonLoading(
                [pending[0] with { Identity = pending[0].Identity with { InstanceId = Guid.NewGuid() } }]),
                "beast-first-room:伪造run不能消费真实预约");
            Reject<BeastDungeonAdmissionRejectedException>(() => repository.CommitBeastDungeonLoading(
                [pending[0] with { Identity = pending[0].Identity with { Epoch = 2 } }]),
                "beast-first-room:错误加载epoch不能消费同run预约");
            clock.Advance(TimeSpan.FromDays(1));
            Reject<BeastDungeonAdmissionRejectedException>(() => repository.CommitBeastDungeonLoading(pending),
                "beast-first-room:首房跨日拒绝旧预约");
            clock.Advance(TimeSpan.FromDays(-1));
            Sql($"CREATE TRIGGER fail_beast_loading BEFORE INSERT ON character_beast_dungeon_entries WHEN NEW.character_id={characters[1].Id} BEGIN SELECT RAISE(ABORT,'beast loading rollback'); END");
            Reject<SqliteException>(() => repository.CommitBeastDungeonLoading(pending),
                "beast-first-room:第二成员晚期扣次失败");
            That(pending.Select((row, index) => (Row: row, Character: repository.LoadCharacter(row.Owner, 0), Initial: initial[index]))
                .All(value => value.Character.Revision == value.Initial.Revision &&
                    TicketCount(value.Character, BeastDungeonTicket.Event) == 3 && Used(value.Row) == 0),
                "beast-first-room:全队门票、次数、revision及预约状态完整回滚");
            Sql("DROP TRIGGER fail_beast_loading");
            var committed = repository.CommitBeastDungeonLoading(pending);
            That(committed.Select((character, index) => TicketCount(character, BeastDungeonTicket.Event) == 2 &&
                TicketCount(character, BeastDungeonTicket.Ordinary) == 3 && Used(pending[index]) == 1).All(value => value),
                "beast-first-room:首房全队一次提交活动票和次数，普通票不变");
            var replay = pending.Select((row, index) => row with { Expected = committed[index] }).ToArray();
            var repeated = repository.CommitBeastDungeonLoading(replay);
            That(repeated.Select((character, index) => character.Revision == committed[index].Revision && Used(replay[index]) == 1).All(value => value),
                "beast-first-room:相同首房确认持久幂等");
            repository.CancelBeastDungeonReservation(alpha.Account, committed[0].Id, run);
            That(Used(replay[0]) == 1 && TicketCount(repository.LoadCharacter(alpha, 0), BeastDungeonTicket.Event) == 2,
                "beast-first-room:断线清理不退款已提交事实");
            var cancelled = Reserve(0, committed[0], Guid.NewGuid(), 1);
            repository.CancelBeastDungeonReservation(alpha.Account, cancelled.Expected.Id, cancelled.Identity.InstanceId);
            Reject<BeastDungeonAdmissionRejectedException>(() => repository.CommitBeastDungeonLoading([cancelled]),
                "beast-first-room:被取消预约不能由迟到首房重新消费");
            var next = Reserve(0, cancelled.Expected, Guid.NewGuid(), 1);
            repository.CancelBeastDungeonReservation(alpha.Account, next.Expected.Id, cancelled.Identity.InstanceId);
            var second = repository.CommitBeastDungeonLoading([next])[0];
            That(Used(next) == 2 && TicketCount(second, BeastDungeonTicket.Event) == 1,
                "beast-first-room:旧run清理不释放新预约，第二次首房正常提交");
            Reject<InvalidOperationException>(() => Reserve(0, second, Guid.NewGuid(), 2),
                "beast-first-room:单图每日两次用尽后不能预约");
            Sql("PRAGMA user_version=145");
            _ = new CharacterRepository(path);
            That(Used(next) == 2 && TicketCount(repository.LoadCharacter(alpha, 0), BeastDungeonTicket.Event) == 1,
                "魔兽预约迁移回放及重开保留已提交资产和次数");
            Sql("DROP INDEX beast_pending_character");
            Reject<InvalidDataException>(() => new CharacterRepository(path),
                "缺少预约唯一约束时拒绝启动，不静默降低结构要求");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - before} beast first-room reservation assertions");

        BeastDungeonLoadingReservation Reserve(int index, Character character, Guid instance, int used)
        {
            var item = character.Inventory.Items.Single(row => row.Core.ItemId == BeastDungeonTicket.Event);
            var paid = character.Inventory with { Items = character.Inventory.Items.Replace(item, item with { Core = item.Core with { Value = item.Core.Count - 1 } }) };
            var admission = new BeastDungeonAdmission(day, used) { Ticket = new(character.Inventory, paid, "test_beast_ticket") };
            var identity = new DungeonRunIdentity(character.Id, 0, character.DungeonState!.LastAttempt + 1, instance, 1);
            var reserved = repository.CommitDungeonEntry(leases[index], character, character.DungeonState.NextObjectKey + 10,
                new(null, 0, identity, 59453) { DungeonId = 310, Beast = admission }).Character;
            return new(leases[index], reserved, identity, 310, admission, clock);
        }
        int Used(BeastDungeonLoadingReservation row) => repository.LoadBeastDungeonUsed(row.Owner, row.Expected.Id, day, 310);
        static int TicketCount(Character character, int id) => character.Inventory.Items.Where(row => row.Core.ItemId == id).Sum(row => row.Core.Count);
        void Sql(string sql)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
    }
}
