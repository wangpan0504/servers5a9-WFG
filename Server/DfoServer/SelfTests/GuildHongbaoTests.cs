using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Guilds;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GuildHongbaoTests
{
    sealed class Clock(long unix) : TimeProvider
    {
        public long Unix = unix;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Unix);
    }

    public static void RunCore()
    {
        int before = Count;
        byte[] issue = [.. Enumerable.Repeat((byte)0xa7, 13), 101, 0, 0, 0, 6];
        That(GuildHongbaoRequests.TryIssue(issue, out uint item, out byte count) && item == 101 && count == 6 &&
            !GuildHongbaoRequests.TryIssue(issue[13..], out _, out _) &&
            !GuildHongbaoRequests.TryIssue([.. issue, 0], out _, out _) &&
            GuildHongbaoRequests.TryRecord([.. issue[..13], 1, 0, 0, 0], out uint id) && id == 1,
            "红包请求严格保留13B旧前缀，业务尾部不能冒充完整正文，前缀不要求全零");
        byte[] list = GuildHongbaoPackets.List(new(30, 5000, 2, [new(7, 101, "红", 6, 1700000000)]));
        That(list.Length == 59 && BitConverter.ToUInt32(list, 11) == 1 && BitConverter.ToUInt32(list, 15) == 7 &&
            BitConverter.ToUInt32(list, 19) == 101 && list[25] == 0 && list[53] == 6 && list[54] == 0 &&
            BitConverter.ToUInt32(list, 55) == 1700000000, "红包列表头及44B记录的ID、名称、份数、状态和到期时间按原生偏移排列");
        byte[] history = GuildHongbaoPackets.History([new(90, "甲", 17)]);
        That(history.Length == 41 && history[5] == 90 && history[8] == 0 &&
            BitConverter.ToUInt32(history, 36) == 17 && history[40] == 0,
            "领取历史严格36B，金额在行31偏移，末字节保留0");
        for (int parts = 5; parts <= 30; parts++)
            That(Enumerable.Range(0, parts).Sum(taken => GuildHongbaoService.Share(5000, parts, taken)) == 5000,
                "等额分配的整数余数守恒，不因份数产生或丢失货币");
        RunRepository();
        RunSchema();
        Console.WriteLine($"PASS guild hongbao transaction/parser assertions {Count - before}");
    }

    static void RunRepository()
    {
        string root = Path.Combine(Path.GetTempPath(), "guild-hongbao-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            var leases = Enumerable.Range(0, 8).Select(index => owners.Acquire("hongbao-core-" + index, static () => { })).ToArray();
            try
            {
                var room = new Location(0, 0, 100, 100);
                var towns = new TownCatalog(new Dictionary<(byte, byte), TownArea>(), room, room, new(0, [], []), [], new Dictionary<uint, uint[]>());
                var characters = leases.Select((owner, index) => repository.Create(owner, new("HongCore" + index, room, room)
                {
                    Inventory = index == 0 ? new([new(0, new(InventoryListType.Main, 65), new(101, ItemKind.Consumable, 3))]) : InventorySnapshot.Empty
                })).ToArray();
                GuildManagementIdentity Actor(int index) => new(new GameSession(repository, towns, leases[index]), leases[index],
                    characters[index].Id, characters[index].Name, 1, new(2, 10), () => leases[index].IsCurrent);
                Character Current(int index) => repository.LoadCharacter(leases[index], characters[index].Slot);
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True");
                db.Open();
                void Sql(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
                long Number(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
                Sql($"INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix) VALUES(1,'红包测试','hongbao-test',{characters[0].Id},1000)");
                foreach (var character in characters)
                    Sql($"INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{character.Id},4,1000)");
                var clock = new Clock(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                var catalog = new GuildHongbaoCatalog("[hongbao]\n101 201 101 95\n[/hongbao]\n[min guild member count]\n30\n[max total cera per day]\n50000\n[max take count per day]\n5");
                var service = new GuildHongbaoService(repository, catalog, clock);
                var original = Current(0);
                Sql("CREATE TRIGGER hongbao_issue_abort AFTER INSERT ON guild_hongbao_records BEGIN SELECT RAISE(ABORT,'issue rollback'); END");
                Reject<SqliteException>(() => service.Issue(Actor(0), original, 101, 6), "发行写账故障真实抛出");
                That(Current(0).Revision == original.Revision && Current(0).Inventory.Items.Single().Core.Count == 3 &&
                    Number("SELECT count(*) FROM guild_hongbao_records") == 0, "发行失败原子回滚背包扣除、UID、revision和账本");
                Sql("DROP TRIGGER hongbao_issue_abort");
                var issued = service.Issue(Actor(0), Current(0), 101, 6);
                That(issued is { Success: true, Id: > 0, Character: not null } && Current(0).Inventory.Items.Single().Core.Count == 2 &&
                    service.Read(leases[1], characters[1].Id) is { Rows.Length: 1 }, "发行一件真实红包与六份账本同事务生效，列表可重读");
                var eventService = new DfoServer.Game.Events.ServerEventService(new ServerEventRepository(path));
                eventService.Override(2378, false);
                That(!service.Issue(Actor(0), Current(0), 101, 6).Success &&
                    Current(0).Inventory.Items.Single().Core.Count == 2 && service.Read(leases[1], characters[1].Id) is { Rows.Length: 1 },
                    "红包关闭阻止新发放，不扣道具，原红包仍可查询");
                Sql("CREATE TRIGGER hongbao_take_abort BEFORE INSERT ON guild_hongbao_claims BEGIN SELECT RAISE(ABORT,'take rollback'); END");
                Reject<SqliteException>(() => service.Take(Actor(1), Current(1), issued.Id), "领取回执故障真实抛出");
                That(Current(1).Inventory.AccountWallet.Get(CurrencyKind.HappyTokenCera) == 0 &&
                    Number("SELECT taken FROM guild_hongbao_records") == 0 && Number("SELECT remaining FROM guild_hongbao_records") == 101,
                    "领取后段失败回滚钱包、剩余、份数与领取回执");
                Sql("DROP TRIGGER hongbao_take_abort");
                var first = service.Take(Actor(1), Current(1), issued.Id);
                That(first is { Success: true, Amount: 17, Taken: 1 } &&
                    Current(1).Inventory.AccountWallet.Get(CurrencyKind.HappyTokenCera) == 17 &&
                    !service.Take(Actor(1), Current(1), issued.Id).Success && Number("SELECT taken FROM guild_hongbao_records") == 1,
                    "首次领取真实入账号欢乐代币券，同账号重放不重复领，不增加次数");
                var alternate = repository.Create(leases[1], new("HongAlt", room, room));
                eventService.Override(2378, true);
                var alternateActor = Actor(1) with { CharacterId = alternate.Id, Name = alternate.Name };
                Sql($"INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{alternate.Id},4,1000)");
                That(!service.Take(alternateActor, repository.LoadCharacter(leases[1], alternate.Slot), issued.Id).Success,
                    "同账号换角色不能重复领取同一红包");
                for (int index = 2; index <= 5; index++)
                    That(service.Take(Actor(index), Current(index), issued.Id).Success, "不同账号分别领取一份真实红包");
                // 最后一份由两个独立owner竞争，SQLite条件更新与唯一回执共同守护。
                var race = Task.WhenAll(Enumerable.Range(6, 2).Select(index => Task.Run(() =>
                    service.Take(Actor(index), Current(index), issued.Id)))).GetAwaiter().GetResult();
                That(race.Count(result => result.Success) == 1 && race.Single(result => result.Success).Amount == 16 &&
                    Number("SELECT SUM(amount) FROM guild_hongbao_claims") == 101 &&
                    Number("SELECT remaining FROM guild_hongbao_records") == 0 &&
                    service.History(leases[0], characters[0].Id, issued.Id)?.Length == 6,
                    "最后一份并发只成功一人，全领取金额等于发行面额，历史由已提交回执恢复");
                That(!service.Take(Actor(0), Current(0), issued.Id).Success, "抢光后不扣资格、不发空额成功");
                var second = service.Issue(Actor(0), Current(0), 101, 5);
                var paid = service.Take(Actor(1), Current(1), second.Id);
                That(paid.Success && paid.Amount == 21, "第二个红包独立分配，不复用旧额度或旧记录");
                Sql($"INSERT INTO guild_hongbao_records(record_id,guild_id,sender_account,sender_name,item_id,total,remaining,portions,taken,created_at,expires_at) " +
                    $"SELECT 100+value,1,'fixture-sender','Fixture',101,1,0,5,1,{clock.Unix},{clock.Unix + 86400} " +
                    "FROM (SELECT 1 value UNION SELECT 2 UNION SELECT 3)");
                Sql($"INSERT INTO guild_hongbao_claims(record_id,account,character_id,name,level,amount,ordinal,day_start_unix,created_at) " +
                    $"SELECT 100+value,'hongbao-core-1',{characters[1].Id},'Daily',1,1,0,{GuildAttendanceDay.Start(clock.Unix)},{clock.Unix} " +
                    "FROM (SELECT 1 value UNION SELECT 2 UNION SELECT 3)");
                var third = service.Issue(Actor(0), Current(0), 101, 5);
                long remaining = Number($"SELECT remaining FROM guild_hongbao_records WHERE record_id={third.Id}");
                That(!service.Take(Actor(1), Current(1), third.Id).Success &&
                    Number($"SELECT remaining FROM guild_hongbao_records WHERE record_id={third.Id}") == remaining,
                    "账号当天已有五次领取，跨红包也不能再领，拒绝不减少剩余");
                Reject<InvalidOperationException>(() => service.Take(Actor(2), characters[2], third.Id),
                    "旧角色revision不能领取新红包或消费一次性份额");
                That(Number($"SELECT taken FROM guild_hongbao_records WHERE record_id={third.Id}") == 0,
                    "过期库存快照失败回滚领取前条件更新");
                clock.Unix += GuildHongbaoService.LifetimeSeconds;
                That(!service.Take(Actor(2), Current(2), third.Id).Success &&
                    service.Read(leases[0], characters[0].Id)!.Rows.Length == 0,
                    "到期边界立即拒绝领取，列表不显示失效红包，剩余仍保留待退");
                Sql("DELETE FROM guilds WHERE guild_id=1");
                Sql("CREATE TRIGGER hongbao_refund_abort BEFORE UPDATE OF refunded ON guild_hongbao_records WHEN NEW.refunded=1 BEGIN SELECT RAISE(ABORT,'refund rollback'); END");
                Reject<SqliteException>(() => service.Refund(Actor(0), Current(0)), "退款登记故障真实抛出");
                That(Current(0).Inventory.AccountWallet.Get(CurrencyKind.TokenCera) == 0 &&
                    Number("SELECT SUM(remaining) FROM guild_hongbao_records WHERE sender_account='hongbao-core-0'") == 181,
                    "解散后退款仍可恢复，后段失败回滚钱包和全部待退余额");
                Sql("DROP TRIGGER hongbao_refund_abort");
                var reopened = new CharacterRepository(path);
                var restoredService = new GuildHongbaoService(reopened, catalog, clock);
                var refunded = restoredService.Refund(Actor(0), Current(0));
                That(refunded is { Success: true, Amount: 181 } &&
                    Current(0).Inventory.AccountWallet.Get(CurrencyKind.TokenCera) == 181 &&
                    restoredService.Refund(Actor(0), Current(0)) is { Success: true, Amount: 0 } &&
                    Number("SELECT SUM(remaining) FROM guild_hongbao_records WHERE sender_account='hongbao-core-0'") == 0 &&
                    Current(1).Inventory.AccountWallet.Get(CurrencyKind.HappyTokenCera) == 38,
                    "重开恢复未领余额为发行账号代币券，重复不重退，也不覆盖已领取账号的钱包");
                That(Number("SELECT count(*) FROM pragma_foreign_key_check") == 0,
                    "红包账本在公会删除后保留必要审计，外键仍完整");
                using var replacement = owners.Acquire(leases[0].Account, static () => { });
                Reject<InvalidOperationException>(() => service.Refund(Actor(0), refunded.Character!),
                    "旧generation不能退款或发布新账号状态");
            }
            finally { foreach (var lease in leases) lease.Dispose(); }
        }
        finally { TestFiles.DeleteDirectory(root, true); }
    }

    static void RunSchema()
    {
        string root = Path.Combine(Path.GetTempPath(), "guild-hongbao-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (bool collision in new[] { false, true })
            {
                string path = Path.Combine(root, collision + ".db");
                _ = new CharacterRepository(path);
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open();
                void Sql(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
                long Number(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
                Sql("DROP TABLE guild_hongbao_claims; DROP TABLE guild_hongbao_records; PRAGMA user_version=139");
                if (collision)
                {
                    Sql("CREATE TABLE guild_hongbao_claims(account TEXT)");
                    Reject<SqliteException>(() => new CharacterRepository(path), "不覆盖同名未知领取结构");
                    That(Number("PRAGMA user_version") == 139 &&
                        Number("SELECT count(*) FROM sqlite_schema WHERE name='guild_hongbao_records'") == 0,
                        "领取表冲突使迁移全部回滚，保留旧版本");
                }
                else
                {
                    _ = new CharacterRepository(path);
                    _ = new CharacterRepository(path);
                    That(Number("PRAGMA user_version") == SqliteMigrations.CurrentVersion &&
                        Number("SELECT count(*) FROM guild_hongbao_claims") == 0, "真实139升级并重开保留空账本，不补不存在的历史");
                    Sql($"PRAGMA user_version={SqliteMigrations.CurrentVersion + 1}");
                    Reject<InvalidDataException>(() => new CharacterRepository(path), "红包库拒绝未来版本");
                }
            }
        }
        finally { TestFiles.DeleteDirectory(root, true); }
    }
}
