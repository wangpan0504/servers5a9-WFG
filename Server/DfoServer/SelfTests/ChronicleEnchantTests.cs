using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Persistence;
using DfoServer.Game.Accounts;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer;

internal static class ChronicleEnchantTests
{
    public static void RunCore()
    {
        int start = Count;
        var body = Request(65, 1299, 0, 9, 9001, 0);
        var request = ChronicleEnchantRequests.Read(body);
        That(request is { MaterialSlot: 65, MaterialItemId: 1299, Target.List: InventoryListType.Main, Target.Index: 9, TargetItemId: 9001, Number: 0 },
            "370保留本版新增目标容器字节，不能套旧端13B请求");
        foreach (var bad in new[] { body[..^1], body.Concat(new byte[1]).ToArray(), Request(-1, 1299, 0, 9, 9001, 0) })
            Reject<InvalidDataException>(() => ChronicleEnchantRequests.Read(bad));
        Bytes(ChronicleEnchantPackets.Ack(InventoryResult.Reject(17), null), [0, 17, 0], "失败u16回执结束原生等待");
        Bytes(ChronicleEnchantPackets.Ack(new(true, 0), new(65, 1, 9, true, [])),
            [1, 65, 0, 1, 0, 1, 0, 9, 0, 0], "370成功10B包含目标容器、槽位和零返还数");
        Bytes(ChronicleEnchantPackets.Ack(new(true, 0), new(65, 0, 9, false, [new(121, 1234, 3)])),
            Build(w => { w.Write((byte)1); w.Write((short)65); w.Write((ushort)0); w.Write((byte)0); w.Write((byte)0);
                w.Write((short)9); w.Write((byte)1); w.Write((short)121); w.Write(1234); w.Write(3); }),
            "赋予失败仍是已提交操作，携带破坏目标及完整返还行");
        ResourceDeclarations();
        Plans();
        Console.WriteLine($"PASS {Count - start} 气息资源、协议与原子规划断言");
    }

    static void ResourceDeclarations()
    {
        var strings = new List<string>();
        PvfToken Tag(string name) { strings.Add("[" + name + "]"); return new(3, strings.Count - 1); }
        PvfToken Text(string text) { strings.Add(text); return new(6, strings.Count - 1); }
        PvfToken N(int number) => new(0, number);
        var table = new[] { Tag("3choro enchant"), Tag("check"), N(2), N(4), Text("amulet"), Tag("skill"), N(0),
            Text("[gunner]"), N(11), Text("[dungeon type]"), Text("[level]"), N(0), Text("%"), N(10), Tag("/skill"), Tag("/check"), Tag("/3choro enchant") };
        var source = new[] { Tag("import script"), Text("dimensionforce_r.stk"), Tag("type"), N(0), Tag("probability"), N(100), N(100) };
        var target = new[] { Tag("character item check"), N(2), N(4), Text("amulet"), Tag("/character item check") };
        var files = new Dictionary<string, PvfToken[]> { ["stackable/book.stk"] = source,
            ["stackable/dimensionforce_r.stk"] = table, ["equipment/neck.equ"] = target };
        var catalog = new ChronicleEnchantCatalog(path => files[path], index => strings[index],
            new Dictionary<int, string> { [1299] = "book.stk", [1254] = "book.stk" }, new Dictionary<int, string> { [9001] = "neck.equ" });
        var definition = catalog.Get(1299)!;
        That(definition is { Type: 0, FirstChance: 100, SecondChance: 100, EffectItemId: 1254 } &&
            catalog.Allows(9001, 2, 0x24, 19, definition, 0), "导入气息表，职业取原职业，转职取低四位，部位按客户端映射");
        That(!catalog.Allows(9001, 2, 1, 19, definition, 0) && !catalog.Allows(9001, 3, 4, 19, definition, 0) &&
            !catalog.Allows(9001, 2, 4, 19, definition, 1) && !catalog.Allows(9001, 2, 4, 12, definition, 0),
            "错误职业/转职/部位和未声明选项不能凭气息名称放行");
        var monitor = new[] { Tag("3choro enchant"), Tag("monitor"), N(13), N(1), N(2), Text("weapon"), Text("coat"),
            Tag("skill"), N(0), Text("[demonic lancer]"), N(1), Tag("/skill"), Tag("/monitor"), Tag("/3choro enchant") };
        var shared = ChronicleEnchantFile.Parse(monitor, index => strings[index]);
        That(shared.Skills.Count == 2 && shared.Skills.Keys.Select(key => key.Part).ToHashSet().SetEquals(new[] { "weapon", "coat" }),
            "monitor按声明数量展开多个部位，不把新职业共用表当作坏check或直接丢弃");
        PvfToken[] Supplement(int number) => [..table[..^1], Tag("check"), N(2), N(4), Text("amulet"),
            Tag("skill"), N(number), Text("[gunner]"), N(12), Tag("/skill"), Tag("/check"), Tag("/3choro enchant")];
        That(ChronicleEnchantFile.Parse(Supplement(1), index => strings[index]).Skills[new(2, 4, "amulet")].SetEquals(new byte[] { 0, 1 }),
            "相同职业/部位的后续check追加不同编号，不把分段声明误判为冲突");
        That(ChronicleEnchantFile.Parse(Supplement(0), index => strings[index]).Skills[new(2, 4, "amulet")].SetEquals(new byte[] { 0 }),
            "相同编号的多个效果段只形成一个选择，不凭效果段重复创造额外选项");
        var malformedMonitor = monitor.ToArray(); malformedMonitor[4] = N(3);
        Reject<InvalidDataException>(() => ChronicleEnchantFile.Parse(malformedMonitor, index => strings[index]));
        var independent = ChronicleEnchantFile.Parse([..target, Tag("type"), N(1000)], index => strings[index], equipment: true);
        That(independent.CharacterChecks.Length == 1, "装备的无关type声明不套气息材料type范围");
        Reject<InvalidDataException>(() => ChronicleEnchantFile.Parse([Tag("probability"), N(100), N(101)], index => strings[index]));
        Reject<InvalidDataException>(() => ChronicleEnchantFile.Parse([Tag("character item check"), N(2), N(4), Tag("/character item check")], index => strings[index]));
    }

    static void Plans()
    {
        var source = new InventoryItem(101, new(InventoryListType.Main, 65), new(1299, ItemKind.Consumable, 2) { SortLockFlag = 1 });
        var target = new InventoryItem(102, new(InventoryListType.Main, 9), new(9001, ItemKind.Equipment, 1234567)
            { Durability = 18, Attr = 0xEB, EnchantCardId = 3175, GenuineUpgrade = 4, Rune = 5, SortLockFlag = 1 });
        Character role = new(1, 0, "Chronicle", 0, new(1, 0, 0, 0), null)
            { Job = 2, GrowType = 0x24, Level = 80, Inventory = new([source, target], Gold: 1234) };
        var book = new ItemTemplate(1299, ItemKind.Consumable, 0, 1, 0, 1000, 1, 2, 1, "[free]", ushort.MaxValue, 0, "Breath");
        var gear = new ItemTemplate(9001, ItemKind.Equipment, 19, 70, 18, 1, 1, 5, 72, "[trade]", 4, 0, "Neck");
        var definition = new ChronicleEnchantDefinition(0, 100, 11, ImmutableDictionary<ChronicleCategory, ImmutableHashSet<byte>>.Empty);
        var request = new ChronicleEnchantRequest(65, 1299, target.Position, 9001, 0);
        int rolls = 0, preparations = 0;
        InventoryResult Plan(Character character, out ChronicleEnchantReceipt? receipt, ChronicleEnchantDefinition? rule = null,
            ChronicleEnchantRequest? input = null, int draw = 100, bool valid = true, bool space = true) =>
            InventoryChronicleEnchantService.Plan(character, input ?? request, 1000, rule ?? definition, book, gear, valid,
                () => { rolls++; return draw; }, paid =>
                { preparations++; return space ? new(paid with { Items = paid.Items.Remove(paid.Find(target.Position)!) }, []) : null; }, out receipt);
        var result = Plan(role, out var receipt);
        var option = new ChronicleOption(1254, 2, 4, 19, 0);
        var changed = target with { Core = target.Core with { Chronicle0 = option } };
        That(result.Accepted && result.Plan!.After.Find(target.Position) == changed && receipt is { Succeeded: true, MaterialRemaining: 1 } &&
            result.Plan.After.Find(source.Position) == source with { Core = source.Core with { Value = 1 } } &&
            result.Plan.After.Gold == 1234 && preparations == 0, "首条100成功只扣一件，保留UID、完整核心、锁、钱包及金币");
        var once = role with { Inventory = result.Plan!.After };
        int beforeRolls = rolls;
        That(!Plan(once, out _).Accepted && rolls == beforeRolls, "同技能同种气息不重复赋予或抽签");
        var blue = definition with { Type = 1, SecondChance = 100 };
        var twice = Plan(once, out receipt, blue);
        That(twice.Accepted && twice.Plan!.After.Find(target.Position)!.Core == changed.Core with { Chronicle1 = option with { Id = 1255 } } &&
            twice.Plan.After.Find(source.Position) is null, "相同技能不同属性可作第二条，耗尽气息清槽");
        That(!Plan(role with { Inventory = twice.Plan!.After with { Items = twice.Plan.After.Items.Add(source) } }, out _, blue).Accepted,
            "第三条拒绝，不覆盖已有两条");
        var second = request with { Number = 1 };
        var failed = Plan(once, out receipt, input: second, draw: 12);
        That(failed.Accepted && receipt is { Succeeded: false } && failed.Plan!.After.Find(target.Position) is null &&
            failed.Plan.After.Find(source.Position) is null, "第二条11阈值抽12失败，源和目标在同一规划消费");
        That(Plan(once, out receipt, input: second, draw: 11).Accepted && receipt!.Succeeded,
            "randInt(100)包含两端，11边界仍成功");
        beforeRolls = rolls;
        That(Plan(once, out _, input: second, space: false).Error == 21 && rolls == beforeRolls,
            "失败返还装不下在抽签前整笔拒绝，不消耗或重抽");
        foreach (var invalid in new[] { role with { Inventory = new([source, target with { Core = target.Core with { EquipmentLockId = 1 } }]) },
            role with { Inventory = new([source, target with { Core = target.Core with { Durability = 17 } }]) },
            role with { Inventory = new([source with { Core = source.Core with { ExpireTime = 1000 } }, target]) } })
            That(!Plan(invalid, out _).Accepted, "锁、非满耐久及到期等号保持原资产");
        That(!Plan(role, out _, valid: false).Accepted && !Plan(role, out _, input: request with { Target = new(InventoryListType.Equipment, 19) }).Accepted,
            "资格不符或已穿戴目标不伪造成功");
        var clock = new ConsumableUseSessionTests.FakeClock();
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        var timed = role with { Inventory = role.Inventory with { Items = role.Inventory.Items.Replace(source,
            source with { Core = source.Core with { ExpireTime = checked((int)(now + 10)) } }) } };
        var guarded = InventoryChronicleEnchantService.Plan(timed, request, now, definition, book, gear, true, () => 100,
            _ => throw new InvalidOperationException("首条不应破坏"), out _, clock).Plan!.CharacterPrecondition!;
        That(guarded.Matches(timed) && !guarded.Matches(timed with { Job = 3 }) && !guarded.Matches(timed with { GrowType = 1 }),
            "提交前置绑定原职业、转职和注入时钟，不借旧规划跨角色资格");
        clock.Advance(TimeSpan.FromSeconds(10));
        That(!guarded.Matches(timed), "规划后气息到期等号拒绝，不能依赖主机系统日期继续提交");
        long refundNow = clock.GetUtcNow().ToUnixTimeSeconds();
        var limitedRefund = InventoryChronicleEnchantService.Plan(once, second, refundNow, definition, book, gear, true, () => 100,
            paid => new(paid with { Items = paid.Items.Remove(paid.Find(target.Position)!).Add(
                new(0, new(InventoryListType.Main, 121), new(70001, ItemKind.Material, 1) { ExpireTime = checked((int)(refundNow + 2)) })) }, []), out _, clock);
        That(limitedRefund.Accepted && limitedRefund.Plan!.CharacterPrecondition!.Matches(once), "有限期新返还在本次时钟截止前可提交");
        clock.Advance(TimeSpan.FromSeconds(2));
        That(!limitedRefund.Plan!.CharacterPrecondition!.Matches(once), "新返还到期同样拒绝整笔旧规划，不损失原装备或材料");
    }

    internal static byte[] Request(short material, int materialId, byte list, short target, int targetId, byte number) => Build(writer =>
    { writer.Write(material); writer.Write(materialId); writer.Write(list); writer.Write(target); writer.Write(targetId); writer.Write(number); });

    internal static void RunDestruction(TcpTestResources resources, int gearId)
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-chronicle-destruction-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            using var owner = new AccountOwners().Acquire("chronicle-destruction", () => { });
            var items = resources.ItemResources;
            var target = InventoryRewardGrantService.Create(items.Items.Get(gearId)) with { Chronicle0 = new(1255, 12, 1, 19, 0) };
            var role = repository.Create(owner, new("ChrFail", resources.Towns.Room, resources.Towns.InitialReturn)
                { Job = 12, GrowType = 1, Level = 80, Inventory = new([new(0, new(InventoryListType.Main, 9), target),
                    new(0, new(InventoryListType.Main, 65), InventoryRewardGrantService.Create(items.Items.Get(1254), 2))]) });
            var service = new InventoryChronicleEnchantService(items.Items, items.ChronicleEnchants, items.Disjoint,
                items.AmplifyUpgrades, items.GoldLimits, items.EpicPieces, new MaximumRandom());
            var request = new ChronicleEnchantRequest(65, 1254, new(InventoryListType.Main, 9), gearId, 0);
            var planned = service.Plan(role, request, out var receipt);
            That(planned.Accepted && receipt is { Succeeded: false } && planned.Plan!.After.Find(request.Target) is null,
                "真实普通气息按第二条阈值失败，预先冻结共享分解返还和销毁");
            string before = Snapshot();
            Sql("CREATE TRIGGER fail_chronicle_destroy BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='chronicle_enchant' BEGIN SELECT RAISE(ABORT,'destruction rollback'); END");
            Reject<SqliteException>(() => InventoryService.Commit(repository, owner, role, planned));
            That(Snapshot() == before, "真实破坏、源消费、返还UID及审计在末段SQL失败时一起回滚");
            Sql("DROP TRIGGER fail_chronicle_destroy");
            // 新的显式尝试重新规划，不把SQL失败自动重放为原随机操作。
            var next = service.Plan(role, request, out receipt);
            var committed = InventoryService.Commit(repository, owner, role, next);
            var restored = repository.LoadCharacter(owner, role.Slot);
            That(restored.Inventory.Find(request.Target) is null && restored.Inventory.Find(new(InventoryListType.Main, 65))?.Core.Count == 1 &&
                JsonSerializer.Serialize(restored.Inventory) == JsonSerializer.Serialize(committed.Inventory) && receipt is { Succeeded: false },
                "重读文件库保留真实销毁、一次源扣除及全部实际返还，不复生装备");

            string Snapshot() => JsonSerializer.Serialize(repository.LoadCharacter(owner, role.Slot));
            void Sql(string text)
            {
                using var db = new SqliteConnection("Data Source=" + Path.Combine(root, "game.db") + ";Pooling=False");
                db.Open(); using var command = db.CreateCommand(); command.CommandText = text; command.ExecuteNonQuery();
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    sealed class MaximumRandom : Random
    {
        public override int Next(int maxValue) => maxValue == 0 ? 0 : maxValue - 1;
    }
}
