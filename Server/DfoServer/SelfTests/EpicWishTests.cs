using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.EpicWish;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EpicWishTests
{
    public static void RunCore()
    {
        int start = Count;
        var wishes = new EpicWishList([5000, 0, 7000, 0, 0]);
        byte[] body = [5, 0x88, 0x13, 0, 0, 0, 0, 0, 0, 0x58, 0x1b, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        That(EpicWishRequests.Update(body).Same(wishes), "原生五槽保存保留中间空位和装备顺序");
        Bytes(EpicWishPackets.Info(wishes), body, "1292回放五槽不压缩空位，装备保持u32");
        That(EpicWishRequests.Update([0]).Same(EpicWishList.Empty), "显式零长度清单可清空全部祈愿");
        Bytes(EpicWishPackets.Info(EpicWishList.Empty), [0], "新角色发空清单以清除上一角色的祈愿状态");
        Bytes(EpicWishPackets.Updated(true), [1], "1354成功只写公共成功字节");
        Bytes(EpicWishPackets.Updated(false), [0, 0, 0], "1354失败保留公共失败格式，不伪造成功");
        foreach (byte[] invalid in new byte[][] { [], [1], [0, 0], new byte[25],
            [1, 255, 255, 255, 255], [2, 1, 0, 0, 0, 1, 0, 0, 0] })
            Reject<InvalidDataException>(() => EpicWishRequests.Update(invalid), "截断、尾随、负编号和重复非空装备整体拒绝");
        Reject<InvalidDataException>(() => new EpicWishList([1, 2, 3, 4, 5, 6]).Validate(), "祈愿窗口最多五槽");
        var allEmpty = new EpicWishList([0, 0, 0, 0, 0]);
        allEmpty.Validate();
        StorageAndSession(wishes, allEmpty);
        Console.WriteLine($"PASS {Count - start} 史诗祈愿协议、五槽、持久化、回滚、隔离及旧身份断言");
    }

    static void StorageAndSession(EpicWishList wishes, EpicWishList allEmpty)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-epic-wish-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "game.db");
        try
        {
            var repository = new CharacterRepository(path);
            Execute("INSERT INTO accounts(account_id,salt,password_hash,iterations,revision) VALUES('wish-a',X'01',X'02',100000,0),('wish-b',X'03',X'04',100000,0)");
            var owners = new AccountOwners();
            using var owner = owners.Acquire("wish-a", () => { });
            using var otherOwner = owners.Acquire("wish-b", () => { });
            var location = new Location(1, 0, 10, 10);
            Character first = repository.Create(owner, new("WishFirst", location, location));
            Character second = repository.Create(owner, new("WishSecond", location, location));
            Character other = repository.Create(otherOwner, new("WishOther", location, location));
            That(repository.SaveEpicWish(owner, first, wishes), "首次保存祈愿提交本人清单");
            Character restored = repository.LoadCharacter(owner, first.Slot);
            That(JsonSerializer.Serialize(restored) == JsonSerializer.Serialize(first with { EpicWishes = wishes }),
                "保存只改清单，不改角色revision、资产、任务和位置");
            That(repository.LoadCharacter(owner, second.Slot).EpicWishes.Items.IsEmpty &&
                repository.LoadCharacter(otherOwner, other.Slot).EpicWishes.Items.IsEmpty, "同账号其他角色和其他账号清单独立");
            Reject<InvalidOperationException>(() => repository.SaveEpicWish(otherOwner, restored, wishes), "不能借他人角色编号保存清单");
            Reject<InvalidOperationException>(() => repository.SaveEpicWish(owner, first, allEmpty), "同一revision的旧清单也不能覆盖新清单");
            Reject<InvalidOperationException>(() => repository.SaveEpicWish(owner, restored with { Revision = restored.Revision + 1 }, allEmpty),
                "资产版本已变化的角色快照拒绝");
            using (var external = Open())
            using (var writer = external.BeginTransaction(deferred: false))
                That(!repository.SaveEpicWish(owner, restored, wishes), "外部writer占用时同值操作只读返回");
            Execute("CREATE TRIGGER fail_wish BEFORE UPDATE ON character_epic_wishes BEGIN SELECT RAISE(ABORT,'wish failure'); END");
            Reject<SqliteException>(() => repository.SaveEpicWish(owner, restored, allEmpty), "SQL失败不确认保存且回滚旧清单");
            That(new CharacterRepository(path).LoadCharacter(owner, first.Slot).EpicWishes.Same(wishes), "重开数据库恢复原有五槽和顺序");
            Execute("DROP TRIGGER fail_wish");
            That(repository.SaveEpicWish(owner, restored, allEmpty) && repository.LoadCharacter(owner, first.Slot).EpicWishes.Same(allEmpty),
                "取消全部祈愿保存原生五个零槽");

            var map = new TownMap("wish.map", [new(new(0, 0, 20, 20), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(1, 0)] = new(1, 0, 1, [], [], [], location, map, [], []) },
                location, location, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var session = new GameSession(repository, towns, owner);
            var service = new EpicWishService(repository, id => id is 5000 or 7000);
            That(session.SaveEpicWish(service, wishes) is null, "选角前不能保存清单");
            session.Select(first.Slot);
            That(session.SaveEpicWish(service, wishes) is null, "未完成入城不能保存清单");
            session.MarkReady();
            That(session.SaveEpicWish(service, new([8000])) is null && session.Selected!.EpicWishes.Same(allEmpty),
                "目录外装备拒绝且保持当前清单");
            var receipt = session.SaveEpicWish(service, wishes)!;
            That(receipt is not null && session.CanPublishEpicWish(receipt), "目录通过后先持久化再产生当前角色发布回执");
            session.ReturnToRoster();
            That(!session.CanPublishEpicWish(receipt!), "退回名册不发送旧祈愿结果");
            session.Select(first.Slot);
            session.MarkReady();
            That(!session.CanPublishEpicWish(receipt!) && session.Selected!.EpicWishes.Same(wishes),
                "重选同角色恢复清单但拒绝旧epoch结果");
            session.ReturnToRoster();
            session.Select(second.Slot);
            session.MarkReady();
            That(!session.CanPublishEpicWish(receipt!) && session.Selected!.EpicWishes.Items.IsEmpty, "换角色不沿用上一角色的祈愿");
            Character deleted = repository.Create(owner, new("WishDeleted", location, location));
            That(repository.SaveEpicWish(owner, deleted, wishes), "删角样本先持久化本人祈愿");
            repository.Delete(owner, repository.LoadDeletionTarget(owner, deleted.Slot));
            That(Scalar($"SELECT count(*) FROM character_epic_wishes WHERE character_id={deleted.Id}") is 0L &&
                Scalar("SELECT count(*) FROM characters") is 3L &&
                repository.LoadCharacter(owner, first.Slot).EpicWishes.Same(wishes) &&
                repository.LoadCharacter(otherOwner, other.Slot).EpicWishes.Items.IsEmpty,
                "删角清理本人祈愿，保留同账号和跨账号角色存档");
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject<InvalidOperationException>(() => repository.SaveEpicWish(owner, restored, wishes), "旧generation不能写祈愿");
            Reject<InvalidOperationException>(() => session.CanPublishEpicWish(receipt!), "旧generation不能发布祈愿");

            Execute("DROP TABLE character_epic_wishes; PRAGMA user_version=189");
            _ = new CharacterRepository(path);
            That(Scalar("PRAGMA user_version") is long version && version == SqliteMigrations.CurrentVersion &&
                Scalar("SELECT count(*) FROM characters") is 3L && Scalar("SELECT count(*) FROM character_epic_wishes") is 0L,
                "v189升级仅新增空祈愿表，保留全部角色与旧数据");
            Execute("DROP TABLE character_epic_wishes; CREATE TABLE character_epic_wishes(character_id INTEGER); PRAGMA user_version=189");
            Reject<InvalidDataException>(() => new CharacterRepository(path), "同名未知结构不能冒充有效祈愿迁移");
            That(Scalar("PRAGMA user_version") is 189L && Scalar("SELECT count(*) FROM pragma_table_info('character_epic_wishes')") is 1L,
                "未知结构迁移失败回滚版本，不修补或删除旧表");
            Execute("DROP TABLE character_epic_wishes");
            _ = new CharacterRepository(path);
            That(Scalar("PRAGMA integrity_check") is "ok" && Scalar("SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                "祈愿保存、失败、清空与升级后数据库及外键完整");

            SqliteConnection Open()
            {
                var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True");
                db.Open();
                return db;
            }
            void Execute(string sql)
            {
                using var db = Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
            object? Scalar(string sql)
            {
                using var db = Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
        }
        finally { if (Directory.Exists(root)) TestFiles.DeleteDirectory(root, true); }
    }

    public static void RunResources(ItemResources resources)
    {
        var catalog = new EpicWishCatalog(resources.EpicPieces, resources.Items);
        int[] candidates = resources.EpicPieces.Entries.Select(entry => entry.EquipmentId).Where(catalog.Contains).Take(5).ToArray();
        That(candidates.Length == 5 && candidates.All(id => resources.Items.Get(id) is
            { Kind: ItemKind.Equipment, Rarity: 4, MinimumLevel: >= 70 }), "候选PVF提供真实图鉴内Lv70以上史诗供五槽祈愿");
        That(!catalog.Contains(0) && !catalog.Contains(-1) && !catalog.Contains(int.MaxValue), "目录拒绝空编号、负编号及未知装备");
    }
}
