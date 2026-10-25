using System.Text.Json;
using System.Text.Json.Nodes;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class BoosterEpicExchangeTests
{
    const int SourceId = 490705251;
    const int SampleId = 101000783;
    const string OriginProperty = "EpicExchangeOrigin";

    public static void RunCore()
    {
        int checks = Count;
        var ordinary = new ItemCore(SampleId, ItemKind.Equipment, InventoryRewardGrantService.InitialQuality) { Durability = 45 };
        // 通过持久JSON构造，RED阶段不依赖尚未加入的生产类型，必须失败于来源丢失而非编译。
        var special = WithOrigin(ordinary, 1, SourceId);
        AssertOrigin(special, "来源记录由完整Core JSON恢复");
        special.Validate();
        That(special != ordinary && JsonWithoutOrigin(special) == JsonWithoutOrigin(ordinary),
            "同模板特殊来源与未记录实例不同，来源不占原有装备字段");
        That(JsonSerializer.Deserialize<ItemCore>(JsonSerializer.Serialize(special)) == special,
            "完整Core JSON重载保留版本化来源，旧JSON不反推来源");
        That(!HasOrigin(ordinary), "未记录来源的旧实例保持未知，不按泰波尔斯模板补标");
        That(special.Extension119 == 0, "旧来源记录不自动回填交换实例位");
        foreach (var (version, source) in new[] { (0, SourceId), (2, SourceId), (1, 0), (1, -1) })
            Reject<InvalidDataException>(() => WithOrigin(ordinary, version, source).Validate(),
                "来源未知版本或非正源编号不能进入合法Core");
        Reject<InvalidDataException>(() => WithOrigin(new(SourceId, ItemKind.Consumable, 1), 1, SourceId).Validate(),
            "禁史诗交换来源只属于装备实例，不能附加到新消耗品资产");
        RunMarkerDeclarations();

        byte[] legacy = AuctionPackets.ItemEntrySnapshot(ordinary);
        byte[] saved = AuctionPackets.ItemEntrySnapshot(special);
        That(legacy.Length == 123 && saved.Length == 135 && saved.AsSpan(0, 123).SequenceEqual(legacy),
            "拍卖仅在持久快照尾附12B来源，旧123B及原核心字节保持");
        That(AuctionPackets.ItemCoreFromEntry(legacy, ItemKind.Equipment) == ordinary &&
            AuctionPackets.ItemCoreFromEntry(saved, ItemKind.Equipment) == special,
            "拍卖持久快照同时恢复旧普通实例与新特殊来源实例");
        That(AuctionPackets.ItemBlock(saved, (int)ItemKind.Equipment).SequenceEqual(
            AuctionPackets.ItemBlock(legacy, (int)ItemKind.Equipment)) && Wire(special).SequenceEqual(Wire(ordinary)),
            "拍卖物品块与背包wire均不泄漏服务端来源trailer");
        var stamped = ordinary with { Extension119 = 0x01 };
        byte[] instanceEntry = AuctionPackets.ItemEntrySnapshot(stamped);
        byte[] stampedWire = Wire(stamped);
        byte[] plainWire = Wire(ordinary);
        That(instanceEntry.Length == 123 && AuctionPackets.ItemCoreFromEntry(instanceEntry, ItemKind.Equipment) == stamped,
            "交换实例位随123B拍卖快照往返");
        That(stampedWire.Length == plainWire.Length && stampedWire.Zip(plainWire).Count(pair => pair.First != pair.Second) == 1 &&
            stampedWire.Where((value, index) => plainWire[index] != value).Single() == 0x01,
            "交换实例位在背包wire上只改一字节，不移动其他字段");
        foreach (byte[] broken in new[] { legacy.Concat(new byte[1]).ToArray(), saved[..^1], saved.Concat(new byte[1]).ToArray() })
            Reject<InvalidDataException>(() => AuctionPackets.ItemCoreFromEntry(broken, ItemKind.Equipment),
                "非旧123B或完整135B快照拒绝，不截断丢来源");
        foreach (int offset in new[] { 123, 127, 131 })
        {
            byte[] broken = saved.ToArray();
            Array.Clear(broken, offset, 4);
            Reject<InvalidDataException>(() => AuctionPackets.ItemCoreFromEntry(broken, ItemKind.Equipment),
                "拍卖来源坏magic、版本零或源零拒绝");
        }
        var unknownVersion = saved.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(unknownVersion.AsSpan(127), 2);
        Reject<InvalidDataException>(() => AuctionPackets.ItemCoreFromEntry(unknownVersion, ItemKind.Equipment),
            "拍卖未知来源版本拒绝，不能降级普通实例");
        Console.WriteLine($"PASS {Count - checks} epic exchange origin core checks");
    }

    public static void RunResources(TownCatalog towns, ItemResources resources, DateTimeOffset now)
    {
        int checks = Count;
        var items = resources.Items;
        var service = new InventoryBoosterService(items, resources.Boosters, resources.GoldLimits, resources.EpicPieces);
        var definition = resources.Boosters.Get(SourceId);
        That(definition.Available && definition.Selection is { SelectionCount: 1, Categories.Length: 36 },
            "飞跃成长泰波尔斯盒真实epic声明可开，保留36分类和选择1");
        var sourceTemplate = items.Get(SourceId);
        That(sourceTemplate.Kind == ItemKind.Consumable && sourceTemplate.StackLimit >= 2 &&
            ItemSlotBoundService.Allows(InventorySnapshot.Empty, Bag(3), sourceTemplate.Kind) &&
            ItemSlotBoundService.Allows(InventorySnapshot.Empty, Bag(65), sourceTemplate.Kind),
            "源盒真实类型、堆叠上限与快捷/消耗品哨兵槽合法");
        RunMarkerRewardBoundary(towns, resources, now);
        RunShopSelection(towns, resources, now);
        var expected = Expected();
        var seen = new HashSet<int>();
        int rows = 0, empty = 0;
        foreach (var group in definition.Selection!.Categories)
        {
            int[] ids = expected.GetValueOrDefault((group.Category, group.Subcategory)) ?? [];
            That(group.Rewards.Select(row => row.ItemId).SequenceEqual(ids) &&
                group.Rewards.All(row => row.Count == 1 && row.Upgrade == 0 && row.Refinement == 0),
                "飞跃盒真实坐标与奖励原顺序、数量不缩减 " + group.Category + ":" + group.Subcategory);
            if (ids.Length == 0)
            {
                empty++;
                var rejected = service.Open(Role(), Select([SampleId], group.Category, group.Subcategory), out var receipt, now);
                That(!rejected.Accepted && rejected.Plan is null && receipt is null, "空分类不制造奖励或消费源");
            }
            foreach (int id in ids)
            {
                rows++;
                seen.Add(id);
                var template = items.Get(id);
                That(template.Kind == ItemKind.Equipment, "真实候选使用装备模板 " + id);
                for (byte job = 0; job < 16; job++)
                    foreach (byte level in new byte[] { 1, 95 })
                    {
                        var role = Role() with { Job = job, Level = level };
                        string before = JsonSerializer.Serialize(role);
                        var opened = service.Open(role, Select([id], group.Category, group.Subcategory), out var receipt, now);
                        That(opened.Accepted && opened.Plan is not null && receipt is not null,
                            "各职业与领取等级可领全部合法泰波尔斯候选 " + id + ":" + job + ":" + level);
                        var after = opened.Plan!.After;
                        var reward = after.Items.Single(item => item.Core.ItemId == id);
                        AssertOrigin(reward.Core, "每个真实奖励保留飞跃来源 " + id);
                        That(reward.Core.Extension119 == 0x01, "每个真实奖励投影交换资格位且次数为0 " + id);
                        That(JsonWithoutInstance(reward.Core) == JsonWithoutInstance(Core(id)) && reward.Uid == 0 &&
                            reward.Avatar is null && reward.Creature is null &&
                            after.Find(Bag(3)) is { Uid: 1, Core.Count: 1 } &&
                            after.Find(Bag(65)) == role.Inventory.Find(Bag(65)) && after.Items.Length == 3 && after.Gold == 12345 &&
                            receipt!.Rewards.SequenceEqual(new[] { new BoosterGrantedItem(id, 1) }) && JsonSerializer.Serialize(role) == before,
                            "仅追加来源，完整核心、源扣一次、哨兵/输入/钱包保持 " + id);
                    }
            }
        }
        That(rows == 78 && seen.Count == 74 && empty == 18, "飞跃盒保留78原行、74装备及18空分类");
        var first = Role();
        foreach (var bad in new[] { Select([]), Select([SampleId, SampleId]), Select([SourceId]), Select([SampleId], 255),
            Select([SampleId], 0, 255), new BoosterOpenRequest(3, 0, new(0, 0, [SampleId])) })
            RejectUnchanged(first, bad, "非法选择与数量整笔拒绝");
        foreach (var source in new[] { Core(SourceId, 2) with { EquipmentLockId = 1 },
            Core(SourceId, 2) with { ExpireTime = checked((int)now.ToUnixTimeSeconds()) } })
            RejectUnchanged(first with { Inventory = first.Inventory with { Items = first.Inventory.Items.SetItem(0, new(1, Bag(3), source)) } },
                Select([SampleId]), "源安全锁/准确到期保留全部资产");
        var range = ItemSlotBoundService.Range(ItemKind.Equipment, first.Inventory);
        var full = first with { Inventory = first.Inventory with { Items = first.Inventory.Items.AddRange(
            Enumerable.Range(range.Start, range.End - range.Start + 1).Select(slot => new InventoryItem(1000 + slot, Bag((short)slot), Core(16400)))) } };
        RejectUnchanged(full, Select([SampleId]), "装备页满不能只扣源或丢来源奖励");
        var oneFree = full with { Inventory = full.Inventory with { Items = full.Inventory.Items.RemoveAt(full.Inventory.Items.Length - 1) } };
        That(service.Open(oneFree, Select([SampleId]), out _, now).Accepted, "一个装备空格可领取单件完整实例");
        RunPersistence(towns, resources, service, now);
        Console.WriteLine($"PASS {Count - checks} Tayberrs growth selection checks");

        ItemCore Core(int id, int count = 1) => InventoryRewardGrantService.Create(items.Get(id), count, now);
        Character Role() => new(1, 0, "Growth", 0, towns.Room, towns.InitialReturn)
        { Inventory = new([new(1, Bag(3), Core(SourceId, 2)), new(2, Bag(65), Core(SourceId))], Gold: 12345) };
        void RejectUnchanged(Character role, BoosterOpenRequest request, string label)
        {
            string before = JsonSerializer.Serialize(role);
            var rejected = service.Open(role, request, out var receipt, now);
            That(!rejected.Accepted && rejected.Plan is null && receipt is null && JsonSerializer.Serialize(role) == before, label);
        }
    }

    const string Marker = "[set epic exchange instance type]\n";
    const string EquipmentBody = "[equipment]\n101000783 1\n[/equipment]";
    static string MarkerBox(string declaration, string body = EquipmentBody) => declaration +
        "[stackable type]\n`[booster selection]` 0\n[booster category num]\n2 4 9\n" +
        "[booster selection num]\n1\n[booster select category]\n0 0\n" + body + "\n[/booster select category]\n";

    static void RunMarkerDeclarations()
    {
        foreach (string text in new[]
        {
            MarkerBox(Marker), MarkerBox("") + Marker,
            MarkerBox("[outer]\n[/outer]\n" + Marker + "[outer]\n[/outer]\n"),
            MarkerBox("[name]\n`[set epic exchange instance type] [/set epic exchange instance type]`\n" + Marker),
            MarkerBox("[name]\n`[set epic exchange instance type] [/set epic exchange instance type]`\n")
        })
        {
            var definition = BoosterCatalog.Parse(text);
            That(definition.Available && definition.Selection!.Categories.Single().Rewards.Single() == new BoosterChoice(SampleId, 1),
                "单个根级空声明可用，完整闭合区间与字符串同名标签不误判或改变奖励");
        }
        foreach (string declaration in new[]
        {
            Marker + Marker, Marker + "1\n", Marker + "`value`\n",
            Marker + "[/set epic exchange instance type]\n", "[/set epic exchange instance type]\n",
            "[set epic exchange instance type\n", "[set epic exchange instance type extra]\n",
            "[ set epic exchange instance type]\n", "[outer]\n" + Marker + "[/outer]\n",
            "[outer]\n[outer]\n[/outer]\n" + Marker + "[/outer]\n",
            "[outer]\n[inner]\n" + Marker + "[/inner]\n[/outer]\n",
            "[outer]\n[inner]\n[/outer]\n" + Marker + "[/inner]\n"
        })
            That(!BoosterCatalog.Parse(MarkerBox(declaration)).Available,
                "重复、有值、残缺、多余闭标签、嵌套或交叉闭合的史诗交换声明拒绝");
        That(!BoosterCatalog.Parse(MarkerBox("", EquipmentBody + "\n" + Marker)).Available,
            "分类内的史诗交换声明不能当根级空声明");
        That(!BoosterCatalog.Parse(Marker + "[stackable type]\n`[booster]` 0\n[booster info]\n" +
            "[equipment]\n1 101000783 1 1\n[/equipment]\n[/booster info]\n").Available,
            "史诗交换声明不扩大到随机booster类型");
    }

    static void RunMarkerRewardBoundary(TownCatalog towns, ItemResources resources, DateTimeOffset now)
    {
        var archive = (PvfLib.PvfArchive)typeof(ItemCatalog).GetField("pvf",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(resources.Items)!;
        var isolated = new BoosterCatalog(archive, new Dictionary<int, string>());
        var definitions = (System.Collections.Concurrent.ConcurrentDictionary<int, BoosterDefinition>)typeof(BoosterCatalog)
            .GetField("definitions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(isolated)!;
        var service = new InventoryBoosterService(resources.Items, isolated, resources.GoldLimits, resources.EpicPieces);
        var source = new InventoryItem(1, Bag(3), InventoryRewardGrantService.Create(resources.Items.Get(SourceId), utcNow: now));
        var role = new Character(1, 0, "OriginBoundary", 0, towns.Room, towns.InitialReturn) { Inventory = new([source]) };
        string before = JsonSerializer.Serialize(role);
        definitions[SourceId] = BoosterCatalog.Parse(MarkerBox(Marker, "[etc]\n490705251 1\n[/etc]"));
        var rejected = service.Open(role, Select([SourceId]), out var rejectedReceipt, now);
        That(!rejected.Accepted && rejected.Plan is null && rejectedReceipt is null && JsonSerializer.Serialize(role) == before,
            "带史诗交换声明的实际非装备奖励经真实Open整包拒绝，源与全部输入保持");
        definitions[SourceId] = BoosterCatalog.Parse(MarkerBox("[name]\n`[set epic exchange instance type]`\n"));
        var ordinary = service.Open(role, Select([SampleId]), out var ordinaryReceipt, now);
        That(ordinary.Accepted && ordinaryReceipt is not null &&
            !HasOrigin(ordinary.Plan!.After.Items.Single(item => item.Core.ItemId == SampleId).Core) &&
            ordinary.Plan.After.Items.Single(item => item.Core.ItemId == SampleId).Core.Extension119 == 0 &&
            JsonSerializer.Serialize(role) == before,
            "字符串内标签不是实际声明，经真实Open发普通实例且不补来源或资格位");
        definitions[SourceId] = BoosterCatalog.Parse(MarkerBox(Marker + "[usable first growtype]\n3 4\n[/usable first growtype]\n"));
        foreach (byte branch in new byte[] { 0, 2 })
        {
            var ineligible = role with { GrowType = branch };
            var denied = service.Open(ineligible, Select([SampleId]), out var deniedReceipt, now);
            That(!denied.Accepted && denied.Plan is null && deniedReceipt is null && JsonSerializer.Serialize(role) == before,
                "史诗声明不绕过首转门禁，错误分支不扣源或发奖 " + branch);
        }
        foreach (byte branch in new byte[] { 3, 0x24 })
        {
            var eligible = role with { GrowType = branch };
            var opened = service.Open(eligible, Select([SampleId]), out var receipt, now);
            var expected = InventoryRewardGrantService.Create(resources.Items.Get(SampleId), utcNow: now) with
                { EpicExchangeOrigin = new(1, SourceId), Extension119 = 0x01 };
            That(opened.Accepted && receipt is { Rewards: [{ ItemId: SampleId, Count: 1 }] } &&
                opened.Plan!.After.Find(Bag(3)) is null &&
                opened.Plan.After.Items.Single(item => item.Core.ItemId == SampleId).Core == expected &&
                opened.Plan.CharacterPrecondition!.MatchesIdentity(eligible) &&
                !opened.Plan.CharacterPrecondition.MatchesIdentity(eligible with { GrowType = 2 }) &&
                JsonSerializer.Serialize(role) == before,
                "合法首转与觉醒分支领取完整来源装备，旧计划不绑定转职后的角色 " + branch);
        }
    }

    // 休亚在售的套装礼盒在 PVF 里没有交换声明，产物同样按“休亚处兑换”记资格。
    static void RunShopSelection(TownCatalog towns, ItemResources resources, DateTimeOffset now)
    {
        const int BoxId = 10305621;
        const int ProductId = 100200637;
        var items = resources.Items;
        var service = new InventoryBoosterService(items, resources.Boosters, resources.GoldLimits, resources.EpicPieces);
        That(resources.Shops.Stock(EpicExchangeCatalog.MerchantNpcId).Contains(BoxId) &&
            resources.Shops.Allows(EpicExchangeCatalog.MerchantNpcId, 247, BoxId) &&
            resources.Items.Get(BoxId).ShopPrice.Materials.Length == 2,
            "Shua stock sells the set selection box for two materials");
        var definition = resources.Boosters.Get(BoxId);
        That(!definition.SetsEpicExchangeInstanceType && definition.FromExchangeShop && definition.GrantsEpicExchangeOrigin &&
            definition.Available && definition.Selection is { SelectionCount: 1 } selection &&
            selection.Categories.Single() is { Category: 0, Subcategory: 0 } category &&
            category.Rewards.Select(row => row.ItemId).SequenceEqual(new[] { 100150662, 100050792, 100100693, 100200637, 100250656 }),
            "unmarked shop box grants exchange origin from merchant stock alone");
        That(!resources.Boosters.Get(SourceId).FromExchangeShop, "event growth box is not a shop-sold box");
        var box = new InventoryItem(1, Bag(3), InventoryRewardGrantService.Create(items.Get(BoxId), utcNow: now));
        // 真实礼盒模板 [minimum level] 95，角色等级不足会在开箱前被正常拒绝。
        var role = new Character(1, 0, "ShopBox", 0, towns.Room, towns.InitialReturn) { Level = 95, Inventory = new([box]) };
        var opened = service.Open(role, new BoosterOpenRequest(3, 1, new(0, 0, [ProductId])), out var receipt, now);
        var expected = InventoryRewardGrantService.Create(items.Get(ProductId), utcNow: now) with
            { EpicExchangeOrigin = new(1, BoxId), Extension119 = 0x01 };
        That(opened.Accepted && opened.Plan!.After.Find(Bag(3)) is null &&
            opened.Plan.After.Items.Single(item => item.Core.ItemId == ProductId).Core == expected &&
            receipt is { Rewards: [{ ItemId: ProductId, Count: 1 }] },
            "opening the shop box stamps the product with the exchange bit and the box as origin");
    }

    static void RunPersistence(TownCatalog towns, ItemResources resources, InventoryBoosterService service, DateTimeOffset now)
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-epic-origin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("epic-origin", () => { });
            repo.Create(owner, new("Growth", towns.Room, towns.InitialReturn)
            { Inventory = new([new(0, Bag(3), InventoryRewardGrantService.Create(resources.Items.Get(SourceId), utcNow: now))], Gold: 12345) });
            var session = new GameSession(repo, towns, owner);
            session.Select(0);
            session.MarkReady();
            var saved = session.Selected!;
            string before = Snapshot();
            var stale = service.Open(saved, Select([SampleId]), out _, now).Plan!;
            Query("CREATE TRIGGER fail_epic_origin BEFORE INSERT ON inventory_audit_log WHEN NEW.item_id_after=" + SampleId +
                " BEGIN SELECT RAISE(ABORT,'epic origin late audit rollback'); END");
            Reject<SqliteException>(() => Open(), "来源奖励晚审计失败必须真正回滚");
            That(ReferenceEquals(session.Selected, saved) && Snapshot() == before,
                "晚SQL失败回滚源、完整奖励来源、UID、revision与审计");
            Query("DROP TRIGGER fail_epic_origin");
            var committed = Open();
            That(committed.Accepted && committed.Receipt is not null && ReferenceEquals(committed.Receipt.Character, session.Selected),
                "来源与源消费同一Session提交后才产生回执");
            var current = session.Selected!;
            var reward = current.Inventory.Items.Single();
            AssertOrigin(reward.Core, "真实落库奖励保留来源");
            That(reward.Uid > 0 && reward.Core.ItemId == SampleId && reward.Core.Extension119 == 0x01 &&
                current.Inventory.Find(Bag(3)) is null && current.Inventory.Gold == 12345 &&
                JsonSerializer.Serialize(new CharacterRepository(path).List(owner).Single()) == JsonSerializer.Serialize(current),
                "完整来源重开Repository恢复且源只消费一次");
            Reject<InvalidOperationException>(() => repo.CommitInventory(owner, saved, stale), "旧revision来源计划不能二次发奖");
            before = Snapshot();
            var replay = Open();
            That(!replay.Accepted && replay.Receipt is null && Snapshot() == before, "已耗尽源重放不能复制来源实例");
            That(Query("PRAGMA integrity_check") is "ok", "私有临时库完整性");

            InventoryResult Open() => session.PlanAndCommitBooster(role => service.Open(role, Select([SampleId]), out _, now),
                service.UsePlaces(SourceId), SourceId);
            string Snapshot() => JsonSerializer.Serialize(new CharacterRepository(path).List(owner)) + ":" +
                Query("SELECT count(*) FROM inventory_audit_log") + ":" +
                Query("SELECT seq FROM sqlite_sequence WHERE name='character_inventory_items'");
            object? Query(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
        }
        finally { Directory.Delete(root, true); }
    }

    static Dictionary<(byte, byte), int[]> Expected() => new()
    {
        [(0, 0)] = [101030803, 101010927, 101000783, 101020736, 101040526],
        [(0, 1)] = [102000549, 102010612, 102020518, 102040584, 102030517],
        [(0, 2)] = [104000588, 104010572, 104020568, 104040588, 104030572],
        [(0, 3)] = [106000535, 106010566, 106020609, 106030639, 106040507],
        [(0, 4)] = [108000538, 108010526, 108020444, 108030547, 108040495],
        [(0, 5)] = [109000506, 109010463, 109030355, 109040337],
        [(0, 6)] = [101030803, 101010927, 101000783, 101020736],
        [(0, 7)] = [114000264, 114010246, 114020157, 114030157],
        [(0, 8)] = [116000169, 116010143, 116020145, 116030183],
        [(1, 0)] = [100100693, 100050792, 100150662, 100200637, 100250656],
        [(1, 1)] = [100110637, 100160578, 100260618, 100060651, 100210602],
        [(1, 2)] = [100170572, 100220572, 100270569, 100070605, 100120597],
        [(1, 3)] = [100230578, 100130610, 100280573, 100080629, 100180578],
        [(1, 4)] = [100140429, 100190400, 100240407, 100290420, 100090434],
        [(2, 0)] = [100312640, 100300946, 100322533], [(2, 1)] = [100312641, 100300947, 100322534],
        [(3, 0)] = [100344897, 100353080, 100390020], [(3, 1)] = [100344898, 100353081, 100390021]
    };

    static ItemCore WithOrigin(ItemCore core, int version, int source)
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(core))!.AsObject();
        json[OriginProperty] = new JsonObject { ["Version"] = version, ["SourceItemId"] = source };
        return JsonSerializer.Deserialize<ItemCore>(json.ToJsonString())!;
    }
    static bool HasOrigin(ItemCore core) => JsonSerializer.SerializeToElement(core).TryGetProperty(OriginProperty, out var origin) &&
        origin.ValueKind == JsonValueKind.Object;
    static void AssertOrigin(ItemCore core, string label)
    {
        var json = JsonSerializer.SerializeToElement(core);
        That(json.TryGetProperty(OriginProperty, out var origin) && origin.ValueKind == JsonValueKind.Object &&
            origin.GetProperty("Version").GetInt32() == 1 && origin.GetProperty("SourceItemId").GetInt32() == SourceId, label);
    }
    static string JsonWithoutOrigin(ItemCore core)
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(core))!.AsObject();
        json.Remove(OriginProperty);
        return json.ToJsonString();
    }
    // 来源与实例位是本次新增的两项差异，其余完整核心必须与普通创建逐字段相同。
    static string JsonWithoutInstance(ItemCore core)
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(core))!.AsObject();
        json.Remove(OriginProperty);
        json.Remove(nameof(ItemCore.Extension119));
        return json.ToJsonString();
    }
    static byte[] Wire(ItemCore core)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        ItemListProtocolWriter.Write(writer, 0, core);
        return stream.ToArray();
    }
    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static BoosterOpenRequest Select(int[] ids, byte category = 0, byte subcategory = 0) => new(3, 1, new(category, subcategory, ids));
}
