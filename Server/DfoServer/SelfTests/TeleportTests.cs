using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class TeleportTests
{
    const string PotionText = "[stackable type]\n`[teleport potion]` 0\n[minimum level]\n60\n[cool time]\n10000\n[int data]\n1 2\n[/int data]";
    const string AuraText = "[equipment type]\n`[aurora avatar]` 0\n[aura ability]\n`[solo teleport]` 420";

    public static void RunCore()
    {
        var potion = TeleportRequests.Potion(Convert.FromHexString("41004EAC27000026002F05EA0001"));
        That(potion == new PotionTeleportRequest(65, 2600014, 0, new(38, 0, 1327, 234, 1)), "原生237请求完整保留源槽、模板、模式和目的地");
        That(TeleportRequests.Solo(Convert.FromHexString("FFFFFFFF0700000027030F03CD0005")) ==
            new SoloTeleportRequest(-1, 7, new(39, 3, 783, 205, 5)), "原生470区分玩家ID与NPC ID而非把i32截断成u16");
        Reject<InvalidDataException>(() => TeleportRequests.Potion(new byte[13]), "237短包不能被当成功");
        Reject<InvalidDataException>(() => TeleportRequests.Solo(new byte[16]), "470未知追加字段拒绝");
        That(TeleportCatalog.ReadPotion(new(PotionText)) is { MinimumLevel: 60, CoolTime: 10000 } &&
            TeleportCatalog.ReadAura(new(AuraText)) == new SoloTeleportDefinition(420), "资源药剂毫秒与光环秒分别保留");
        That(TeleportCatalog.ReadAura(new(AuraText.Replace("[solo teleport]", "[none]"))) is null &&
            TeleportCatalog.ReadPotion(new(PotionText + "\n[need material]\n1 2")) is null,
            "无能力光环和附加费用未恢复的药剂不能获得传送资格");
        using var fixture = new Fixture();
        var session = fixture.Session;
        That(!session.TeleportPotion(fixture.Request, fixture.Service).Accepted, "未选角不能消费");
        session.Select(0); session.MarkReady();
        var before = fixture.Read();
        var onlineBefore = session.Selected;
        fixture.Sql("CREATE TRIGGER teleport_fail BEFORE INSERT ON character_teleport_cooldowns BEGIN SELECT RAISE(ABORT,'rollback'); END");
        Reject<SqliteException>(() => session.TeleportPotion(fixture.Request, fixture.Service), "扣药后冷却末段失败");
        That(System.Text.Json.JsonSerializer.Serialize(fixture.Read()) == System.Text.Json.JsonSerializer.Serialize(before) &&
            ReferenceEquals(session.Selected, onlineBefore) && session.Selected!.Revision == before.Revision &&
            fixture.Read().Inventory.Find(new(InventoryListType.Main, 65))!.Core.Count == 3, "真实SQL失败同时回滚位置、数量、冷却和在线快照");
        That(fixture.Scalar("SELECT count(*) FROM inventory_audit_log WHERE action_name='teleport_potion'") == 0, "失败扣药审计也回滚");
        fixture.Sql("DROP TRIGGER teleport_fail");
        var committed = session.TeleportPotion(fixture.Request, fixture.Service);
        var actual = fixture.Read();
        That(committed.Accepted && actual.Position == fixture.Request.Target && actual.Inventory.Find(new(InventoryListType.Main, 65))!.Core.Count == 2 &&
            actual.TeleportCooldowns.PotionUntil == fixture.Clock.Now + 10000 && session.CanPublishTravel(committed.Receipt!.Travel),
            "提交后真实UID库存、位置和冷却一致，回执属于已安装快照");
        That(!session.TeleportPotion(fixture.Request, fixture.Service).Accepted, "重复药剂请求冷却内不二次消费");
        session.ReturnToRoster(); session.Select(0); session.MarkReady();
        That(!session.CanPublishTravel(committed.Receipt!.Travel) && !session.TeleportPotion(fixture.Request, fixture.Service).Accepted,
            "重选旧回执失效且持久冷却保留");
        fixture.Clock.Now += 10000;
        That(session.TeleportPotion(fixture.Request, fixture.Service).Accepted, "冷却到期后同区域也可再次传送");
        var character = session.Selected!;
        That(fixture.Service.Potion(character, fixture.Request with { Target = new(3, 0, 10, 10) }, fixture.Clock.Now + 10000).Reason == "target-quest-required" &&
            fixture.Service.Potion(character with { Level = 59 }, fixture.Request, fixture.Clock.Now + 10000).Plan is null,
            "绕过普通邻接不绕过目的地任务与药剂等级");
        That(fixture.Service.Solo(character, new(-1, 999, new(2, 0, 10, 10)), null, fixture.Clock.Now).Plan is null &&
            fixture.Service.Solo(character, new(-1, 7, new(1, 0, 10, 10)), null, fixture.Clock.Now).Plan is null,
            "未知NPC与私有房间目标拒绝");
        var aura = session.TeleportSolo(new(-1, 7, new(2, 0, 30, 30)), fixture.Service, null);
        That(aura.Accepted && fixture.Read().TeleportCooldowns.AuraUntil == fixture.Clock.Now + 420000 &&
            TeleportPackets.Cooldown(aura.Receipt!.Travel.Character, fixture.Clock.Now).SequenceEqual(new byte[] { 1, 2, 164, 1, 0, 0 }),
            "光环7分钟持久冷却投影为type2及420秒");
        using var replacement = fixture.Owners.Acquire(fixture.Owner.Account, () => { });
        Reject<InvalidOperationException>(() => session.TeleportPotion(fixture.Request, fixture.Service), "旧generation不能传送");
        That(new CharacterRepository(fixture.Database).LoadCharacter(replacement, 0).TeleportCooldowns.AuraUntil == fixture.Clock.Now + 420000,
            "新仓储和新owner恢复已提交光环冷却");
    }

    sealed class Clock : TimeProvider
    {
        public long Now = 2000000000000;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Now);
    }
    sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "s5a9-teleport-" + Guid.NewGuid().ToString("N"));
        public string Database => Path.Combine(root, "game.db");
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease Owner { get; }
        public CharacterRepository Repository { get; }
        public GameSession Session { get; }
        public TeleportService Service { get; }
        public Clock Clock { get; } = new();
        public PotionTeleportRequest Request { get; } = new(65, 2600014, 0, new(2, 0, 20, 20, 1));
        public Fixture()
        {
            Directory.CreateDirectory(root); Owner = Owners.Acquire("teleport", () => { }); Repository = new(Database);
            TownArea Area(byte town, bool room, int[] quests) => new(town, 0, 1, quests, [], [], new(town, 0, 10, 10),
                new("test.map", [new(new(0, 0, 100, 100), [], [])], [], room, [new(7, 20, 20)]), [], []);
            var towns = new TownCatalog(new() { [(1, 0)] = Area(1, true, []), [(2, 0)] = Area(2, false, []), [(3, 0)] = Area(3, false, [42]) },
                new(1, 0, 10, 10), new(2, 0, 10, 10), new(0, [], []), [], new Dictionary<uint, uint[]>());
            var definitions = new TeleportCatalog(path => path.StartsWith("stackable", StringComparison.Ordinal) ? PotionText : AuraText,
                new Dictionary<int, string> { [101590011] = "aura.equ" }, new Dictionary<int, string> { [2600014] = "potion.stk" });
            Service = new(towns, definitions, id => new(id, ItemKind.Avatar, 9, 1, 0, 1, 0, 2, 1, "free", ushort.MaxValue, 0, "Aura"), Clock);
            Repository.Create(Owner, new("Teleport", new(2, 0, 10, 10), towns.InitialReturn)
            {
                Level = 60,
                Inventory = new([new(0, new(InventoryListType.Main, 65), new(2600014, ItemKind.Consumable, 3)),
                    new(0, new(InventoryListType.Equipment, 9), new(101590011, ItemKind.Avatar, 0)) { Avatar = new() }])
            });
            Session = new(Repository, towns, Owner);
        }
        public Character Read() => Repository.LoadCharacter(Owner, 0);
        public long Scalar(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + Database + ";Pooling=False"); db.Open();
            using var cmd = db.CreateCommand(); cmd.CommandText = sql; return (long)cmd.ExecuteScalar()!;
        }
        public void Sql(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + Database + ";Pooling=False"); db.Open();
            using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
        }
        public void Dispose() { Owner.Dispose(); Directory.Delete(root, recursive: true); }
    }
}
