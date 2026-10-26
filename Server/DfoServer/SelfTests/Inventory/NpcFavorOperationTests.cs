using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class NpcFavorOperationTests
{
    static readonly int[] Cubes = [3033, 3034, 3035, 3036, 3037, 3262];
    static readonly TimeProvider Clock = new ConsumableUseSessionTests.FakeClock();
    static readonly InventorySlot Slot = new(InventoryListType.Main, 65);
    static NpcFavorRules Rules => new(20, 100, 5,
        Cubes.ToImmutableDictionary(id => id, id => new NpcFavorGift(-1, id, 100, 100, 300)),
        [new(-1, 10157249, 1, 100, 100), new(17, 10157249, 1, 1000, 1000)], [0, 1, 47]);
    static NpcFavorCatalog Catalog => new(id => id is 17 or 18 ? new(17, 40000) : null, Rules);

    public static void RunCore()
    {
        var service = new NpcFavorService(Catalog, time: Clock, random: new EndpointRandom());
        var role = new Character(1, 0, "Gifts", 0, new(1, 0, 10, 10), null) { Level = 20 };
        foreach (int index in Enumerable.Range(0, 6))
        {
            var currency = CurrencyWallet.RewardKind(Cubes[index])!.Value;
            var input = role with { Inventory = CurrencyWallet.Set(role.Inventory, currency, 100) };
            var result = service.Plan(input, new(0, 18, 36, (ushort)(354 + index)));
            That(result.Accepted && CurrencyWallet.Get(result.Plan!.After, currency) == 0 &&
                result.Plan.After.NpcFavor is [{ NpcId: 17, Points: 300, GiftCount: 1 }], "六种晶块虚拟槽按账号钱包足额扣除并写共享owner");
        }
        var funded = role with { Inventory = CurrencyWallet.Set(role.Inventory, CurrencyKind.WhiteCube, 1000) };
        int day = ShopPurchaseCount.Today(Clock.GetUtcNow());
        var near = funded with { Inventory = funded.Inventory with { NpcFavor = [new(17, 39950) { GiftDay = day, GiftCount = 2 }] } };
        var capped = service.Plan(near, new(0, 17, 36, 355));
        That(capped.Accepted && capped.Plan!.After.NpcFavor is [{ Points: 40000, GiftCount: 3 }], "临界只加剩余点数且正常计次");
        foreach (var request in new NpcFavorRequest[] { new(0, 17, 0, 355), new(2, 17, 36, 355),
            new(0, 17, 36, 353), new(0, 17, 36, 360), new(0, 999, 36, 355), new(3, 17, 36, 355) })
            That(!service.Plan(funded, request).Accepted, "操作与容器不匹配、越界槽、未知NPC及操作拒绝");
        That(!service.Plan(role, new(0, 17, 36, 355)).Accepted &&
            !service.Plan(funded with { Level = 19 }, new(0, 17, 36, 355)).Accepted, "余额与等级不足不增好感");
        foreach (var row in new NpcFavorPoint[] { new(17, 40000), new(17, 1000) { GiftDay = day, GiftCount = 5 },
            new(17, 1000) { GiftDay = day + 1, GiftCount = 1 } })
            That(!service.Plan(funded with { Inventory = funded.Inventory with { NpcFavor = [row] } }, new(0, 17, 36, 355)).Accepted,
                "满点、当天满次数、墙钟倒退均不消费");
        var yesterday = funded with { Inventory = funded.Inventory with { NpcFavor = [new(17, 1000) { GiftDay = day - 1, GiftCount = 5 }] } };
        var reset = service.Plan(yesterday, new(0, 17, 36, 355));
        That(reset.Plan!.After.NpcFavor[0] is { GiftCount: 1 } && reset.Plan.After.NpcFavor[0].GiftDay == day,
            "新服务日重置次数但保留点数");
        var source = new InventoryItem(20, Slot, new(10157249, ItemKind.Consumable, 2) { SortLockFlag = 1 });
        var extra = role with { Inventory = new([source]) };
        var special = service.Plan(extra, new(2, 18, 0, 65));
        That(special.Accepted && special.Plan!.After.NpcFavor[0].Points == 1000 &&
            special.Plan.After.Find(Slot) is { Uid: 20, Core.Count: 1, Core.SortLockFlag: 1 }, "特定NPC礼物优先于通配项，保留剩余源完整核心");
        foreach (var core in new[] { source.Core with { EquipmentLockId = 1 }, source.Core with { ItemId = 999 },
            source.Core with { ExpireTime = (int)Clock.GetUtcNow().ToUnixTimeSeconds() } })
            That(!service.Plan(extra with { Inventory = new([source with { Core = core }]) }, new(2, 17, 0, 65)).Accepted,
                "锁定、非礼物及过期源拒绝");
        var friendly = role with { Inventory = role.Inventory with { NpcFavor = [new(17, 30000)] } };
        var dye = new NpcFavorRequest(1, 18, Colors: [new(0, 47), new(3, 1)]);
        var painted = service.Plan(friendly, dye);
        That(painted.Accepted && painted.Plan!.After.NpcFavor[0].Color(0) == 47 && painted.Plan.After.NpcFavor[0].Color(3) == 1,
            "关系二级允许四通道染色，按共享owner保存");
        foreach (var request in new[] { dye with { Colors = [new(4, 1)] }, dye with { Colors = [new(0, 255)] },
            dye with { Colors = [new(0, 1), new(0, 47)] }, dye with { Colors = [] } })
            That(!service.Plan(friendly, request).Accepted, "无效颜色、越界和重复通道不改持久颜色");
        That(!service.Plan(role, dye).Accepted, "好感不足不能通过伪造请求染色");
        var paintedRole = friendly with { Inventory = painted.Plan!.After };
        That(service.Plan(paintedRole, dye).Plan is { Changed: false }, "相同染色不写库或增加revision");
        Wire(day);
        ResourceParser();
        Repository(service, source, day);
    }

    static void Wire(int day)
    {
        That(NpcFavorRequests.Read([0, 17, 0, 0, 0, 36, 99, 1]) is { Operation: 0, NpcId: 17, SourceType: 36, SourceIndex: 355 },
            "CMD814原生赠礼8B字段独立预期");
        That(NpcFavorRequests.Read([1, 17, 0, 0, 0, 2, 0, 47, 3, 1]).Colors.SequenceEqual([new(0, 47), new(3, 1)]),
            "CMD814染色按变化对读取");
        foreach (var body in new byte[][] { [], [0, 17, 0, 0, 0, 36, 99], [0, 17, 0, 0, 0, 36, 99, 1, 0],
            [0, 255, 255, 255, 255, 36, 99, 1], [1, 17, 0, 0, 0, 0], [1, 17, 0, 0, 0, 2, 0, 1, 0, 2],
            [1, 17, 0, 0, 0, 1, 4, 1] })
            Reject<InvalidDataException>(() => NpcFavorRequests.Read(body), "截断、尾随、越界NPC和坏颜色列表拒绝");
        var row = new NpcFavorPoint(17, 1300) { GiftDay = day, GiftCount = 3 };
        Bytes(NpcFavorPackets.Gift(new(0, 17), row, 300), [1, 0, 17, 0, 0, 0, 44, 1, 20, 5, 0, 0, 3],
            "ACK814赠礼独立13B：本次增量、绝对点数、次数");
        var dye = new NpcFavorRequest(1, 17, Colors: [new(0, 47), new(3, 1)]);
        Bytes(NpcFavorPackets.Color(dye), [1, 1, 17, 0, 0, 0, 2, 0, 47, 3, 1], "ACK814染色不附加赠礼字段");
        Bytes(NpcFavorPackets.Snapshot([row.WithColor(0, 47).WithColor(3, 1)], Clock.GetUtcNow()),
            [1, 17, 0, 0, 0, 20, 5, 0, 0, 3, 4, 0, 47, 1, 0, 2, 0, 3, 1], "746恢复次数及四个颜色通道，无错位");
        Bytes(NpcFavorPackets.Snapshot([row], Clock.GetUtcNow().AddDays(1)), [1, 17, 0, 0, 0, 20, 5, 0, 0, 0, 0],
            "新日快照清除次数而非清除点数");
        That(JsonSerializer.Deserialize<NpcFavorPoint>("{\"NpcId\":17,\"Points\":1000}") == new NpcFavorPoint(17, 1000),
            "历史JSON点数对可直接读取，新增状态默认为零");
    }

    static void ResourceParser()
    {
        var strings = new List<string>();
        PvfToken Text(byte type, string value) { strings.Add(value); return new(type, strings.Count - 1); }
        PvfToken Tag(string value) => Text(3, "[" + value + "]");
        PvfToken[] tokens = [Tag("favor condition level"), new(0, 20), Tag("favor gift item count"), new(0, 100),
            Tag("favor gift limit"), new(0, 5), Tag("favor level point up"), new(0, 3034), new(0, 200), new(0, 600), Tag("/favor level point up"),
            Tag("extra favor gift"), new(0, -1), new(0, 10157249), new(0, 1), new(0, 100), new(0, 100), Text(6, "对话"), Tag("/extra favor gift"),
            Tag("favor npc name color"), new(0, 10), new(0, 212), new(0, 247), new(0, 90), Tag("/favor npc name color"),
            Tag("npc color map"), new(0, 0), new(0, 0), new(0, 255), new(0, 0), Text(6, "无"), Tag("/npc color map")];
        NpcFavorRules Parse(PvfToken[] input) => NpcFavorRules.Parse(new(input, index => strings[index]));
        That(Parse(tokens) is { MinimumLevel: 20, GiftLimit: 5 } rules && rules.Gifts[3034].Count == 100 &&
            rules.Extra(17, 10157249)?.Maximum == 100 && rules.Colors.SetEquals([0]), "普通、特殊赠礼及颜色来自结构化PVF配置");
        foreach (var bad in new PvfToken[][] { tokens[..^1], [..tokens, Tag("favor gift limit"), new(0, 2)],
            [..tokens[..8], new(0, 700), new(0, 600), ..tokens[10..]], [..tokens[..5], new(0, 256), ..tokens[6..]] })
            Reject<InvalidDataException>(() => Parse(bad), "坏结构、重复字段、倒置区间及越界日次数不能部分启用");
    }

    static void Repository(NpcFavorService service, InventoryItem source, int day)
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-npc-gifts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("gifts", () => { });
            var place = new DfoServer.GameWorld.Towns.Location(1, 0, 10, 10);
            var role = repository.Create(owner, new("Gifts", place, place) { Level = 20, Inventory = new([source with { Uid = 0 }]) });
            var inventory = CurrencyWallet.Set(role.Inventory, CurrencyKind.WhiteCube, 500) with { NpcFavor = [new(17, 30000)] };
            role = InventoryService.Commit(repository, owner, role, new(true, 0, new(role.Inventory, inventory, "test_favor_seed")));
            var gift = service.Plan(role, new(0, 17, 36, 355));
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
            db.Open();
            using var sql = db.CreateCommand();
            sql.CommandText = "CREATE TRIGGER fail_gift AFTER INSERT ON currency_audit_log WHEN NEW.action_name='npc_favor_gift' BEGIN SELECT RAISE(ABORT,'gift rollback'); END";
            sql.ExecuteNonQuery();
            Reject<SqliteException>(() => InventoryService.Commit(repository, owner, role, gift), "赠礼末段SQL失败");
            That(JsonSerializer.Serialize(role) == JsonSerializer.Serialize(repository.LoadCharacter(owner, 0)), "钱包、好感、日次数、revision一起回滚");
            sql.CommandText = "DROP TRIGGER fail_gift";
            sql.ExecuteNonQuery();
            var committed = InventoryService.Commit(repository, owner, role, gift);
            Reject<InvalidOperationException>(() => InventoryService.Commit(repository, owner, role, gift), "旧赠礼计划不能重扣");
            var dye = service.Plan(committed, new(1, 17, Colors: [new(0, 47)]));
            committed = InventoryService.Commit(repository, owner, committed, dye);
            var loaded = new CharacterRepository(path).LoadCharacter(owner, 0);
            That(loaded.Inventory.NpcFavor[0] is { Points: 30300, GiftCount: 1 } row && row.GiftDay == day && row.Color(0) == 47 &&
                loaded.Inventory.AccountWallet.Get(CurrencyKind.WhiteCube) == 400 && loaded.Revision == committed.Revision,
                "重开库恢复钱包、日次数、永久好感与NPC颜色");
            var pending = service.Plan(loaded, new(2, 17, 0, 65));
            using var replacement = owners.Acquire("gifts", () => { });
            Reject<InvalidOperationException>(() => InventoryService.Commit(repository, owner, loaded, pending), "接管后旧generation不能消费特殊礼物");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    sealed class EndpointRandom : Random
    {
        public override int Next(int minValue, int maxValue) => maxValue - 1;
    }
}
