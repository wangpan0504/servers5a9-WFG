using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Marriage;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Marriage;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class MarriageCoupleTests
{
    public static void RunCore()
    {
        int before = Count;
        TravelRules();
        FurnitureMoves(new MarriageResourceTests.Source().Parse());
        byte[] comment = [7, 0, 0, 0, 4, 0, 0, 0, 0xd6, 0xd0, 0xb9, 0xfa];
        That(MarriageCoupleRequests.CommentUpdate(comment) == new MarriageCommentUpdateRequest(7, "中国"),
            "留言正文按CP936字节数解析且没有结构前缀");
        Reject<InvalidDataException>(() => MarriageCoupleRequests.CommentUpdate([.. comment, 0]), "留言多余尾字节拒绝");
        Reject<InvalidDataException>(() => MarriageCoupleRequests.CommentUpdate([7, 0, 0, 0, 1, 0, 0, 0, 0x81]), "截断CP936双字节拒绝");
        byte[] move = [.. new byte[13], 1, 2, 0, 0x2d, 1, 0, 0, 0, 1, 0, 0x2d, 1, 0, 0];
        var parsed = MarriageCoupleRequests.MoveItem(move);
        That(parsed.Source == new MarriageCoupleSlot(1, 2, 301) && parsed.Target == new MarriageCoupleSlot(0, 1, 301),
            "摆放请求两端保留同一家具编号，额外13字节前缀不误作字段");
        Reject<InvalidDataException>(() => MarriageCoupleRequests.MoveItem(move.AsSpan(13)), "遗漏结构前缀拒绝");
        Reject<InvalidDataException>(() => MarriageCoupleRequests.MoveItem([.. move, 0]), "移动请求多余字节拒绝");
        move[13] = 2;
        Reject<InvalidDataException>(() => MarriageCoupleRequests.MoveItem(move), "未知物品容器拒绝");
        Reject<InvalidDataException>(() => MarriageCoupleRequests.OpenClose([.. new byte[13], 2]), "开关不是布尔值拒绝");
        Reject<InvalidDataException>(() => MarriageCoupleRequests.Empty([0]), "无正文请求不吞掉额外字段");

        var record = Record() with { Storage = ImmutableDictionary<ushort, MarriageFurnitureStack>.Empty.Add(3, new(301, 2)) };
        var qualified = new Character(1, 0, "婚姻资格", 0, new(38, 1, 450, 234), null) { Marriage = record };
        var qualification = new InventoryCharacterPrecondition(qualified, requireMarriageState: true);
        That(qualification.MatchesIdentity(qualified) &&
            !qualification.MatchesIdentity(qualified with { Marriage = null }) &&
            !qualification.MatchesIdentity(qualified with { Marriage = record with { State = 1 } }) &&
            qualification.MatchesIdentity(qualified with { Marriage = record with { Revision = record.Revision + 1 } }),
            "婚姻购买资格复验关系与阶段，不因单纯心意点或家具revision变化拒绝");
        That(new InventoryCharacterPrecondition(qualified).MatchesIdentity(qualified with { Marriage = null }),
            "普通库存业务不额外绑定婚姻资格");
        var decorated = record with { Furniture = ImmutableDictionary<ushort, MarriageFurnitureStack>.Empty
            .Add(1, new(301, 3)).Add(2, new(301, 1)).Add(3, new(302, 1)) };
        That(MarriageAppearance.DecorationScore(decorated, new Dictionary<int, uint> { [301] = 200, [302] = 7 }) == 407 &&
            MarriageAppearance.DecorationScore(decorated with { State = 0 }, new Dictionary<int, uint>()) == 0,
            "远端装饰分按每个摆放条目累加材料数量，不乘堆叠、不去重，解除后清零");
        var removed = MarriageCoupleRules.Delete(record, 1, 3, 301, 1);
        That(removed.Success && removed.Record.Storage[3].Count == 1 && removed.Record.Revision == 5 &&
            record.Storage[3].Count == 2 && removed.Items.IsEmpty, "删除共用物品只产生不可变新版本，不触碰旧快照");
        var stale = MarriageCoupleRules.Delete(record, 1, 3, 302, 1);
        That(!stale.Success && ReferenceEquals(stale.Record, record) && stale.Items.IsEmpty, "错物品请求拒绝且无资产副作用");
        That(!MarriageCoupleRules.Delete(record with { State = 1 }, 1, 3, 301, 1).Success,
            "订婚阶段不能操作婚后共用财产");
        var gifted = MarriageCoupleRules.GiveCapsule(record with
        {
            Members = record.Members.SetItem(1, new() { CapsulesOwned = 2, CapsuleDay = 4 })
        }, 1, 5);
        That(gifted.Success && gifted.Record.Members[1].CapsulesOwned == 1 && gifted.Record.Members[2].CapsulesPending == 1 &&
            gifted.Record.Members[1].CapsuleDay == 5, "赠送在同一婚姻快照内转移给离线配偶，持有量不按日重置");
        That(!MarriageCoupleRules.GiveCapsule(gifted.Record, 1, 4).Success, "日期倒退不能重置每日额度");
        That(!MarriageCoupleRules.GiveCapsule(record, 99, 1).Success, "非关系成员不能赠送胶囊");
        var posted = MarriageCoupleRules.AddComment(record, 3, "来宾", "祝福", 8, 99, 2);
        That(posted.Success && posted.Record.Comments.Single().AuthorId == 3 && record.Comments.IsEmpty,
            "来宾留言形成不可变记录，由调用方验证进入权限");
        That(!MarriageCoupleRules.AddComment(record, 3, "来宾", new string('中', 128), 8, 99, 2).Success,
            "留言上限按CP936字节而非字符计数");
        That(!MarriageCoupleRules.AddComment(posted.Record, 3, "来宾", "重复编号", 8, 99, 2).Success,
            "留言编号重复不能覆盖旧记录");
        That(MarriageCoupleRules.DeleteComment(posted.Record with { State = 3 }, 1, 8).Success,
            "婚礼双方能在婚礼完成前管理来宾留言");

        byte[] update = MarriageCouplePackets.MoveUpdate(0, new(1, 301, 2, 0), new(0, 301, 1, 1));
        That(update.Length == 23 && update[1] == 1 && BinaryPrimitives.ReadInt32LittleEndian(update.AsSpan(2)) == 301 &&
            BinaryPrimitives.ReadUInt16LittleEndian(update.AsSpan(6)) == 2 && update[12] == 0,
            "移动通知固定23字节，字段顺序按消费者而非请求布局");
        var seedTypes = new Dictionary<int, int> { [7] = 1 };
        var treeState = new MarriageTreeState { Seed = 7, Expiry = 8, Growth = 9, Waters = 2 };
        byte[] tree = MarriageCouplePackets.TreeInformation(treeState, seedTypes);
        That(tree.SequenceEqual(new byte[] { 1, 0, 0, 0, 8, 0, 0, 0, 9, 0, 0, 0, 2 }),
            "树状态13字节按种子映射树index，不把STK编号发给客户端");
        That(MarriageCouplePackets.TreeInformation(treeState, new Dictionary<int, int>()).SequenceEqual(new byte[13]) &&
            treeState.Seed == 7, "模板失效只投影空树，不发送未知index或清理原存档");
        byte[] list = MarriageCouplePackets.CommentsAck(0, 7, posted.Record.Comments);
        That(list.Length == 59 && list[0] == 1 && BinaryPrimitives.ReadInt32LittleEndian(list.AsSpan(9)) == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(list.AsSpan(51)) == 4,
            "留言回执包含42字节定长行头及独立CP936正文长度");
        That(MarriageCouplePackets.CapsuleInformation(new() { CapsuleGauge = 8, CapsulesOwned = 2, CapsulesTaken = 3 }, false)
            .SequenceEqual(new byte[] { 8, 0, 0, 0, 2, 3, 0 }), "胶囊通知持有与领取字段不颠倒");
        That(MarriageCouplePackets.CapsuleAck(1054, 0, new() { CapsuleGauge = 2, CapsulesOwned = 3 }).Length == 10 &&
            MarriageCouplePackets.CapsuleAck(1055, 0, new() { CapsulesOwned = 3 }).Length == 6 &&
            MarriageCouplePackets.CapsuleAck(1056, -6, new()).Length == 5, "三种胶囊回执按命令及结果写入不同宽度");
        var ticket = new ItemTemplate(9000, ItemKind.Consumable, 0, 1, 0, 1000, 0, 0, 0, "[trade]", ushort.MaxValue, 0, "传送券");
        var raw = new StackableFile(9000, "partner.stk", new("[waste]", [0]), 1000, null, null, 1, null, [])
        {
            ActionDeclaration = new("[teleport to partner]", [1]), DeclaredActionUsablePlaces = ["[village]", "[seria room]"],
            DeclaredNeededMaterials = [7000, 5]
        };
        var file = new RecoveryConsumableFile(9000, "partner.stk", raw, 1000, null, null, null, ["cool time"], []);
        var definition = PartnerTeleportDefinition.Parse(file, ticket);
        string[] symbols = ["[stackable type]", "[waste]", "[action type]", "[teleport to partner]", "[action usable place]", "[village]", "[need material]"];
        PvfToken[] duplicate = [new(3, 0), new(6, 1), new(0, 0), new(3, 0), new(6, 1), new(0, 0),
            new(3, 2), new(6, 3), new(0, 1), new(3, 4), new(6, 5), new(3, 6), new(0, 7000), new(0, 5)];
        That(PartnerTeleportDefinition.Parse(9000, "partner.stk", duplicate, id => symbols[id], ticket) is not null,
            "原生券两次同值waste声明可解析，不放宽通用重复字段规则");
        duplicate[5] = new(0, 1);
        That(PartnerTeleportDefinition.Parse(9000, "partner.stk", duplicate, id => symbols[id], ticket) is null,
            "伴侣券重复类型参数冲突拒绝，不能用首条或末条掩盖冲突");
        duplicate[5] = new(2, 0);
        That(PartnerTeleportDefinition.Parse(9000, "partner.stk", duplicate, id => symbols[id], ticket) is null,
            "重复类型的错误token种类拒绝，不能把浮点零当整数零");
        That(definition is { Source.CoolTime: 1000, Source.CoolGroup: -9000 } && definition.PurchaseMaterials.Single() == new ItemMaterialCost(7000, 5),
            "伴侣传送券解析原生动作、资源冷却与独立商店价格");
        var source = new InventoryItem(77, new(InventoryListType.Main, 65), new(9000, ItemKind.Consumable, 2));
        var character = new Character(1, 0, "传送者", 0, new(38, 0, 400, 300), null) { Level = 1, Inventory = new([source]) };
        var consumed = InventoryActionConsumableService.Plan(character, source.Position, definition!.Source, "[village]",
            TimeProvider.System, DateTimeOffset.FromUnixTimeSeconds(1000));
        That(consumed.Accepted && consumed.Plan!.After.Find(source.Position)!.Core.Count == 1 &&
            consumed.Plan.After.ConsumableStates.Single().UntilMilliseconds == 1001000,
            "缺少商店兑换材料仍能使用已有券，只扣源券一张且计划带实际冷却");
        That(PartnerTeleportDefinition.Parse(file with { Stackable = raw with { ActionDeclaration = new("[teleport to partner]", [2]) } }, ticket) is null &&
            PartnerTeleportDefinition.Parse(file with { Stackable = raw with { DeclaredNeededMaterials = [7000] } }, ticket) is null,
            "未知传送参数与损坏购买价格拒绝，不能猜成已支持定义");
        Console.WriteLine($"PASS {Count - before} marriage couple core assertions");
    }

    static void FurnitureMoves(MarriageCatalog catalog)
    {
        var stored = Record() with { Storage = ImmutableDictionary<ushort, MarriageFurnitureStack>.Empty.Add(0, new(301, 2)) };
        var placed = MarriageCoupleRules.Move(stored, 1, new(1, 0, 301), new(0, 1, 301), catalog);
        That(placed.Success && placed.Record.Furniture[1] == new MarriageFurnitureStack(301, 1) &&
            placed.Record.Storage[0].Count == 1 && placed.Record.Revision == stored.Revision + 1 &&
            stored.Furniture.IsEmpty && stored.Storage[0].Count == 2 && placed.Items.IsEmpty,
            "原生双端同编号可向空位置摆放一件家具，数量守恒且不修改原快照");
        var recalled = MarriageCoupleRules.Move(placed.Record, 2, new(0, 1, 301), new(1, 3, 301), catalog);
        That(recalled.Success && recalled.Record.Furniture.IsEmpty && recalled.Record.Storage[3].Count == 1 &&
            recalled.Record.Storage[0].Count == 1, "原生收回家具到空保管槽也回显源编号，不要求目标编号为零");
        var merged = MarriageCoupleRules.Move(placed.Record, 2, new(0, 1, 301), new(1, 0, 301), catalog);
        That(merged.Success && merged.Record.Furniture.IsEmpty && merged.Record.Storage[0].Count == 2,
            "收回家具仍可合并原保管堆叠，不重复生成道具");
        var moved = MarriageCoupleRules.Move(stored, 1, new(1, 0, 301), new(1, 4, 0), catalog);
        That(moved.Success && !moved.Record.Storage.ContainsKey(0) && moved.Record.Storage[4].Count == 2,
            "保管箱内部拖动继续声明空目标为零，整堆搬移");
        var occupied = stored with { Storage = stored.Storage.Add(1, new(301, 3)) };
        var swapped = MarriageCoupleRules.Move(occupied, 1, new(1, 0, 301), new(1, 1, 301), catalog);
        That(swapped.Success && swapped.Record.Storage[0].Count == 3 && swapped.Record.Storage[1].Count == 2,
            "保管箱内部交换按目标实际物品校验，保留两堆完整数量");
        var floor = stored with { Storage = stored.Storage.SetItem(0, new(302, 1)) };
        var invalidPlacement = MarriageCoupleRules.Move(floor, 1, new(1, 0, 302), new(0, 1, 302), catalog);
        That(invalidPlacement.Result == -22 && ReferenceEquals(invalidPlacement.Record, floor),
            "地板不能放在另一种家具位置时返回原生-22，而非伴侣信息错误");
        var full = placed.Record with { Storage = Enumerable.Range(0, MarriageCoupleRules.StorageSlots)
            .ToImmutableDictionary(slot => (ushort)slot, _ => new MarriageFurnitureStack(302, 1)) };
        var noSpace = MarriageCoupleRules.Move(full, 1, new(0, 1, 301), new(1, 0, 301), catalog);
        That(noSpace.Result == -13 && ReferenceEquals(noSpace.Record, full) && noSpace.Items.IsEmpty,
            "满保管箱无法收回或交换到合法位置时返回原生容量错误，家具不丢失");
        var overflow = placed.Record with { Storage = placed.Record.Storage.SetItem(0, new(301, int.MaxValue)) };
        That(MarriageCoupleRules.Move(overflow, 1, new(0, 1, 301), new(1, 0, 301), catalog).Result == -13,
            "同物品堆叠溢出也走容量错误，不伪装成关系丢失");
        That(MarriageCoupleRules.Move(recalled.Record, 1, new(0, 1, 301), new(1, 3, 301), catalog).Result == -1 &&
            MarriageCoupleRules.Move(stored, 99, new(1, 0, 301), new(0, 1, 301), catalog).Result == -2,
            "已收回来源的旧请求和无婚姻资格使用不同返回值");
        foreach (var (result, body) in new[] { (-1, "01FFFFFFFF"), (-2, "01FEFFFFFF"), (-13, "01F3FFFFFF"),
            (-20, "01ECFFFFFF"), (-22, "01EAFFFFFF") })
            Bytes(MarriageCouplePackets.Result(result), Convert.FromHexString(body),
                "1048原生失败保持01加有符号四字节结果，按实际原因编码 " + result);
        foreach (var (record, member, from, to) in new (MarriageRecord, ushort, MarriageCoupleSlot, MarriageCoupleSlot)[]
        {
            (stored, 99, new(1, 0, 301), new(0, 1, 301)),
            (stored with { State = 1 }, 1, new(1, 0, 301), new(0, 1, 301)),
            (stored, 1, new(1, 0, 302), new(0, 1, 302)),
            (stored, 1, new(1, 0, 301), new(0, 1, 302)),
            (stored, 1, new(1, 0, 301), new(0, 1, 0)),
            (stored, 1, new(1, 0, 301), new(0, 2, 301)),
            (stored, 1, new(1, 0, 301), new(1, 4, 301)),
            (occupied, 1, new(1, 0, 301), new(1, 1, 0)),
            (recalled.Record, 1, new(0, 1, 301), new(1, 3, 301))
        })
        {
            var rejected = MarriageCoupleRules.Move(record, member, from, to, catalog);
            That(!rejected.Success && ReferenceEquals(rejected.Record, record) && rejected.Items.IsEmpty,
                "非成员、未婚、错编号、越界位置、错目标语义及收回重放均拒绝且无资产或revision变化");
        }
    }

    static void TravelRules()
    {
        var street = new Location(2, 0, 300, 200);
        var room = new Location(38, 1, 300, 200);
        TownMap Map(string name, bool isRoom = false) => new(name, [new(new(0, 0, 1200, 500), [], [])], [], isRoom, []);
        TownArea Area(byte town, byte area, TownMap map) => new(town, area, 1, [], [], [], null, map, [], []);
        var towns = new TownCatalog(new()
        {
            [(2, 0)] = Area(2, 0, Map("street") with { Links = [new(new(290, 190, 20, 20), 38, 1, [], [])], Npcs = [new(20013, 500, 200)] }),
            [(38, 1)] = Area(38, 1, Map("room", true) with { Links = [new(new(290, 190, 20, 20), 2, 0, [], []), new(new(10, 180, 30, 60), 101, 0, [], [])] }),
            [(101, 0)] = Area(101, 0, Map("home") with { SpecialExits = [new(new(1019, 130, 50, 60), -101, 0, [], [])] }),
            [(39, 8)] = Area(39, 8, Map("hall"))
        }, room, street, new(0, [], []), [], new Dictionary<uint, uint[]>());
        var character = new Character(1, 0, "Travel", 1, room, street) { Level = 30, Marriage = Record() };
        var entry = TownTravel.Plan(character, new(101, 0, 300, 200, 5, 38, 1, 0), towns, new HashSet<int>());
        That(entry.Accepted && entry.Return == street && entry.NpcVisit == new NpcTownVisit(room, 101, 0),
            "婚房正式入口保留普通街区返回点并缓存本次房间来源");
        var inHome = character with { Position = entry.Position! };
        var request = new AreaRequest(38, 1, 300, 200, 5, 101, 0, 0);
        That(TownTravel.Plan(inHome, request, towns, new HashSet<int>(), npcVisit: entry.NpcVisit).Accepted,
            "负101出口按原码固定坐标返回本次来源房间");
        That(!TownTravel.Plan(inHome, request, towns, new HashSet<int>()).Accepted &&
            !TownTravel.Plan(inHome, request with { X = 301 }, towns, new HashSet<int>(), npcVisit: entry.NpcVisit).Accepted &&
            !TownTravel.Plan(inHome, request with { Town = 2, Area = 0 }, towns, new HashSet<int>(), npcVisit: entry.NpcVisit).Accepted,
            "婚房出口拒绝缺失缓存、错误坐标和伪造来源");
        var hall = character with { Position = new(39, 8, 400, 300), WeddingHall = new(Guid.NewGuid(), Guid.NewGuid(), 8, 1) };
        var movie = new AreaRequest(2, 0, 500, 230, 5, 0, 0, 0);
        That(TownTravel.Plan(hall, movie, towns, new HashSet<int>()).Accepted,
            "电影结束来源0/0及NPC0的实际请求按婚礼NPC位置返回");
        That(!TownTravel.Plan(hall with { WeddingHall = null }, movie, towns, new HashSet<int>()).Accepted &&
            !TownTravel.Plan(hall, movie with { Y = 231 }, towns, new HashSet<int>()).Accepted &&
            !TownTravel.Plan(hall, movie with { PathKind = 5 }, towns, new HashSet<int>()).Accepted,
            "电影返回不能用于无礼堂身份或任意坐标与路径传送");
    }

    public static void Run(MarriageCatalog catalog, ItemCatalog? items = null)
    {
        int before = Count;
        if (items is not null)
            That(items.PartnerTeleport(10002915) is { Source.Parameters: [1], PurchaseMaterials: [{ ItemId: 10002861, Count: 5 }] },
                "实际爱相随守护券资源按原生动作解析，商店五枚兑换价格单独保留");
        var record = Record();
        var furniture = catalog.Furniture.Values.First(row => !row.Placements.IsEmpty);
        ushort anchor = checked((ushort)furniture.Placements[0].PositionId);
        var stored = MarriageCoupleRules.Store(record, 1, furniture.ItemId, 2, 9, catalog);
        That(stored.Success && stored.Items.Single() == new MarriageItemDelta(1, furniture.ItemId, -2, 9),
            "家具收纳携带角色与精确原背包槽扣费要求");
        var placed = MarriageCoupleRules.Move(stored.Record, 1, new(1, 0, furniture.ItemId), new(0, anchor, furniture.ItemId), catalog);
        That(placed.Success && placed.Record.Furniture[anchor].Count == 1 && placed.Record.Storage[0].Count == 1,
            "堆叠家具摆放一件并保留仓库余量");
        var retrieved = MarriageCoupleRules.Move(placed.Record, 2, new(0, anchor, furniture.ItemId), new(1, 0, furniture.ItemId), catalog);
        That(retrieved.Success && retrieved.Record.Furniture.IsEmpty && retrieved.Record.Storage[0].Count == 2,
            "配偶收回家具合并同物品而不丢数量");
        That(!MarriageCoupleRules.Move(placed.Record, 1, new(0, anchor, furniture.ItemId), new(1, 40, 0), catalog).Success,
            "共用背包40槽消费者边界拒绝越界");
        var twoStacks = record with { Storage = record.Storage.Add(0, new(furniture.ItemId, 2)).Add(1, new(furniture.ItemId, 3)) };
        var swapped = MarriageCoupleRules.Move(twoStacks, 1, new(1, 0, furniture.ItemId), new(1, 1, furniture.ItemId), catalog);
        That(swapped.Success && swapped.Record.Storage[0].Count == 3 && swapped.Record.Storage[1].Count == 2,
            "共用背包两槽交换保留两个独立堆叠，不误当作家具回收合并");

        var seed = catalog.Seeds.First();
        var definition = catalog.Trees[seed.Value];
        int day = 20261026;
        var planted = MarriageCoupleRules.Plant(record, 1, 5, seed.Key, 100, day, catalog);
        That(planted.Success && planted.Record.Tree!.Expiry == 100 + (long)definition.ExpireDays * 86400 &&
            planted.Items.Single().Slot == 5, "树期限取资源天数且扣种子要求绑定原槽");
        var treeState = planted.Record;
        int water = definition.GrowPoints.OrderBy(row => row.Value).First().Key;
        for (int n = 0; n < definition.DayWaterCount; n++)
        {
            var watered = MarriageCoupleRules.Water(treeState, (ushort)(n % 2 + 1), 6, water, 101, day, catalog);
            That(watered.Success, "双方共用树每日浇水次数");
            treeState = watered.Record;
        }
        That(!MarriageCoupleRules.Water(treeState, 2, 6, water, 101, day, catalog).Success,
            "双方累计达到每日限额后拒绝额外浇水");
        That(!MarriageCoupleRules.Water(treeState, 1, 6, water, 101, day - 1, catalog).Success,
            "倒退日期不能再次获得树每日额度");
        var mature = treeState with { Tree = treeState.Tree! with { Growth = definition.Steps[^1].GrowPoint } };
        var harvested = MarriageCoupleRules.Harvest(mature, 2, 102, catalog, total => total - 1);
        var reward = definition.Rewards[^1];
        That(harvested.Success && harvested.Record.Tree is null &&
            harvested.Items.Single() == new MarriageItemDelta(2, reward.ItemId, reward.Count),
            "成熟值取最后资源阶段，末尾权重区间保留重复物品的独立数量");
        That(!MarriageCoupleRules.Harvest(mature, 2, mature.Tree!.Expiry, catalog, _ => throw new Exception()).Success,
            "到期树拒绝收获，拒绝前不消耗随机数");
        Reject<ArgumentOutOfRangeException>(() => MarriageCoupleRules.Harvest(mature, 2, 102, catalog, total => total),
            "注入抽样值越过真实权重和时拒绝");

        var capsule = catalog.GrowthCapsule;
        var full = record with { Members = record.Members.SetItem(1,
            new() { CapsuleGauge = capsule.MaxGage, CapsulesOwned = 2, CapsuleDay = day, CapsulesTaken = capsule.MaxTakenCount }) };
        var made = MarriageCoupleRules.MakeCapsule(full, 1, day + 1, catalog);
        That(made.Success && made.Record.Members[1] is { CapsuleGauge: 0, CapsulesOwned: 3, CapsulesTaken: 0 },
            "跨日仅重置领取额度，制作消耗完整进度且保留持有数量");
        var pending = made.Record with { Members = made.Record.Members.SetItem(2, new() { CapsulesPending = 2, CapsuleDay = day }) };
        var taken = MarriageCoupleRules.TakeCapsule(pending, 2, day, catalog);
        That(taken.Success && taken.Record.Members[2] is { CapsulesPending: 1, CapsulesTaken: 1 } &&
            taken.Items.Single() == new MarriageItemDelta(2, capsule.CapsuleId, 1), "领取原子减少待领、增加次数并生成真实胶囊奖励");
        Guid eventId = Guid.NewGuid();
        var cleared = MarriageCoupleRules.ObserveQualifiedClear(record, 1, eventId, day, catalog);
        That(cleared.Success && cleared.Record.Members[1].CapsuleGauge == capsule.Gage &&
            !MarriageCoupleRules.ObserveQualifiedClear(cleared.Record, 1, eventId, day, catalog).Success,
            "推荐通关冻结事件按资源累计一次，重复事件不重复增加进度");
        Console.WriteLine($"PASS {Count - before} marriage couple resource assertions");
    }

    static MarriageRecord Record() => new(Guid.NewGuid(), 7, 4, 1, 2, 4)
    {
        Members = ImmutableDictionary<ushort, MarriageMemberState>.Empty.Add(1, new()).Add(2, new())
    };
}
