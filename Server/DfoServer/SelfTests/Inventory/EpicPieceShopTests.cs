using System.Buffers.Binary;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class EpicPieceShopTests
{
    public static void RunCore()
    {
        int before = Count;
        (string Name, int Value)[] fields = [("npcIndex", 1130), ("epic piece provide", 5), ("epic piece received", 10),
            ("gabriel appearance probability", 1), ("list change chance", 2), ("list change cost", 100000),
            ("kind of epic piece", 5), ("same type", 0), ("normal type", 5), ("cost of epic soul", 1)];
        var names = fields.Select(row => "[" + row.Name + "]").ToArray();
        var tokens = fields.SelectMany((row, index) => new[] { new PvfToken(3, index), new PvfToken(0, row.Value) }).ToArray();
        var rules = EpicPieceShopCatalog.Parse(tokens, index => names[index]);
        That(rules == new EpicPieceShopCatalog(1130, 5, 10, 1, 2, 100000, 5, 0, 5, 1), "碎片商店按明确标量字段解析，不与成品史诗交换混用");
        That(EpicPieceShopCatalog.Parse([.. tokens, new(3, 0), new(0, 1131)], index => names[index]).NpcId == 1131,
            "同标签重复按匹配客户端顺序覆盖，不累计或拒绝其它完整字段");
        Reject<InvalidDataException>(() => EpicPieceShopCatalog.Parse(tokens[..^1], index => names[index]), "空费用字段不补默认值");
        foreach (var invalid in new[] { rules with { Choices = 6 }, rules with { NormalType = 4 },
            rules with { Provided = 0 }, rules with { AppearancePercent = 101 }, rules with { RefreshGold = -1 } })
            Reject<InvalidDataException>(invalid.Validate, "非法碎片商店数量、费用及出场比例拒绝");

        var pieces = new EpicPieceCatalog("[piece list]\n" + string.Join(' ', Enumerable.Range(0, 6).Select(index =>
            $"{10000 + index} {20000 + index}")) + "\n[/piece list]\n[equipment recipe info]\n[/equipment recipe info]");
        var templates = new Dictionary<int, ItemTemplate>();
        for (int index = 0; index < 6; index++)
        {
            templates.Add(10000 + index, new(10000 + index, ItemKind.Equipment, index == 0 ? (byte)12 : (byte)(13 + index),
                60, 30, 1, 1, 4, 60, "[free]", ushort.MaxValue, 0, "合成史诗"));
            templates.Add(20000 + index, new(20000 + index, ItemKind.Material, 0, 1, 0, 1000, 0, 4, 1,
                "[trade]", ushort.MaxValue, 0, "合成碎片") { CreationRate = 30 });
        }
        templates[10001] = templates[10001] with { EquipmentSlot = 12 };
        var service = new EpicPieceShopService(rules, new(pieces, id => templates[id]), pieces, new HashSet<uint>());
        int draws = 0;
        int Draw(int bound) { draws++; return 0; }
        That(service.Appears(Draw) && !service.Appears(bound => bound - 1), "本服百分比出场判断覆盖1%两侧，不将配置1误读为必出");
        var character = new Character(1, 0, "PieceShop", 7, new(1, 0, 10, 10), null)
            { Inventory = new([]) { Gold = 200000, AccountWallet = AccountWallet.Empty.Set(CurrencyKind.EpicSoul, 2) } };
        var empty = new EpicPieceShopState(true);
        var selected = service.Plan(character, empty, EpicPieceShopAction.Select, 20000, Draw, DateTimeOffset.UtcNow);
        That(selected.Inventory.Accepted && selected.Inventory.Plan is null && selected.After.SelectedEquipment == 10000 &&
            selected.After.Materials.Length == 5 && selected.After.Materials.Order().SequenceEqual(Enumerable.Range(20001, 5)),
            "请求中的目标碎片映射到装备后冻结五种同等级其它碎片，不额外排除同部位或检查角色穿戴等级");
        int capturedDraws = draws;
        var again = service.Plan(character, selected.After, EpicPieceShopAction.Select, 20000, Draw, DateTimeOffset.UtcNow);
        That(again.Inventory.Accepted && draws == capturedDraws && again.After.Materials.SequenceEqual(selected.After.Materials),
            "重复选择复用原候选，不能借不付费请求重抽");
        var other = service.Plan(character, selected.After, EpicPieceShopAction.Select, 20001, Draw, DateTimeOffset.UtcNow);
        var back = service.Plan(character, other.After, EpicPieceShopAction.Select, 20000, _ => throw new InvalidOperationException("不应重抽"), DateTimeOffset.UtcNow);
        That(back.After.Materials.SequenceEqual(selected.After.Materials) && back.After.Offers.Count == 2,
            "切换装备后返回保留该装备原报价，缓存以有限资源装备编号为边界");
        var book = EpicPieceBook.Empty;
        foreach (var entry in pieces.Entries.Skip(1)) book = book.Set(pieces, entry.Index, 10);
        character = character with { Inventory = character.Inventory with { EpicPieces = book } };
        var normal = service.Plan(character, selected.After, EpicPieceShopAction.Complete, 0, Draw, DateTimeOffset.UtcNow);
        var soul = service.Plan(character, selected.After, EpicPieceShopAction.Complete, 1, Draw, DateTimeOffset.UtcNow);
        That(normal.Inventory.Accepted && normal.After.Completed && normal.Inventory.Plan!.ChangedSlots.IsEmpty &&
            normal.Inventory.Plan.After.EpicPieces.Get(0) == 5 && normal.Inventory.Plan.After.EpicPieces.Counts.Skip(1).All(count => count == 0) &&
            normal.Inventory.Plan.After.AccountWallet.Get(CurrencyKind.EpicSoul) == 2 && normal.Inventory.Plan.After.Gold == 200000,
            "普通交换同笔扣五种各10碎片、入账目标5碎片，不生成UID、改金币或扣史诗灵魂");
        That(soul.Inventory.Accepted && soul.Inventory.Plan!.After.EpicPieces.Get(0) == 5 &&
            soul.Inventory.Plan.After.EpicPieces.Counts.Skip(1).All(count => count == 10) &&
            soul.Inventory.Plan.After.AccountWallet.Get(CurrencyKind.EpicSoul) == 1,
            "另一方式只扣真实账号史诗灵魂钱包，保留五类图鉴成本");
        var capped = character with { Inventory = character.Inventory with { EpicPieces = book.Set(pieces, 0, int.MaxValue) } };
        foreach (int mode in new[] { 0, 1 })
            That(!service.Plan(capped, selected.After, EpicPieceShopAction.Complete, mode, Draw, DateTimeOffset.UtcNow).Inventory.Accepted &&
                capped.Inventory.AccountWallet.Get(CurrencyKind.EpicSoul) == 2 && capped.Inventory.EpicPieces.Get(1) == 10,
                "目标钱包溢出整笔拒绝，不吞已规划的成本");
        var shortBook = character with { Inventory = character.Inventory with { EpicPieces = book.Set(pieces, 1, 9) } };
        That(!service.Plan(shortBook, selected.After, EpicPieceShopAction.Complete, 0, Draw, DateTimeOffset.UtcNow).Inventory.Accepted &&
            !service.Plan(character, normal.After, EpicPieceShopAction.Complete, 1, Draw, DateTimeOffset.UtcNow).Inventory.Accepted &&
            !service.Plan(character, empty, EpicPieceShopAction.Complete, 1, Draw, DateTimeOffset.UtcNow).Inventory.Accepted &&
            !service.Plan(character, empty, EpicPieceShopAction.Select, 10000, Draw, DateTimeOffset.UtcNow).Inventory.Accepted,
            "少一片、已兑换、未选目标及把装备ID当目标碎片均拒绝");
        That(service.Plan(character, empty, EpicPieceShopAction.Select, 10000, Draw, DateTimeOffset.UtcNow)
                .Inventory.RejectReason == "unknown_target_piece" &&
            service.Plan(character, empty, EpicPieceShopAction.Complete, 1, Draw, DateTimeOffset.UtcNow)
                .Inventory.RejectReason == "target_not_selected" &&
            service.Plan(character, soul.After, EpicPieceShopAction.Complete, 1, Draw, DateTimeOffset.UtcNow)
                .Inventory.RejectReason == "shop_completed",
            "原生错误19的诊断区分错误ID域、未建立报价及本次已经兑换");
        var refreshed = service.Plan(character, selected.After, EpicPieceShopAction.Refresh, 0, Draw, DateTimeOffset.UtcNow);
        var second = service.Plan(character with { Inventory = refreshed.Inventory.Plan!.After }, refreshed.After,
            EpicPieceShopAction.Refresh, 0, Draw, DateTimeOffset.UtcNow);
        capturedDraws = draws;
        That(refreshed.Inventory.Accepted && refreshed.After.Refreshes == 1 && refreshed.Inventory.Plan!.After.Gold == 100000 &&
            second.Inventory.Accepted && second.After.Refreshes == 2 && second.Inventory.Plan!.After.Gold == 0 &&
            !service.Plan(character, second.After, EpicPieceShopAction.Refresh, 0, Draw, DateTimeOffset.UtcNow).Inventory.Accepted && draws == capturedDraws,
            "刷新足额扣金币且次数属于本次NPC，超限不继续抽样或扣费");

        That(EpicPieceShopRequests.Select([0xA6, 0x2C, 0x9D, 0]) == 10300582 && EpicPieceShopRequests.Complete([1]) == 1,
            "本版818实际样本为完整i32目标碎片10300582，820仍是方式u8，不把片种当装备或槽位");
        EpicPieceShopRequests.Refresh([]);
        foreach (byte[] body in new[] { new byte[3], new byte[5] })
            Reject<InvalidDataException>(() => EpicPieceShopRequests.Select(body), "818宽度不符拒绝");
        Reject<InvalidDataException>(() => EpicPieceShopRequests.Refresh([0]), "819附加正文拒绝");
        foreach (byte[] body in new[] { Array.Empty<byte>(), new byte[] { 2 }, new byte[2] })
            Reject<InvalidDataException>(() => EpicPieceShopRequests.Complete(body), "820非法方式或宽度拒绝");
        var body748 = EpicPieceShopPackets.Materials(selected.After.Materials, 10);
        Bytes(ServerNoticePackets.Information("材料不足"), Convert.FromHexString("0008000000B2C4C1CFB2BBD7E3"),
            "NOTI502信息提示是type0、i32字节数及CP936正文，不混用UTF8公告或假成功");
        var maximumNotice = ServerNoticePackets.Information(new string('a', 1023));
        That(maximumNotice.Length == 1028 && maximumNotice[0] == 0 &&
            BinaryPrimitives.ReadInt32LittleEndian(maximumNotice.AsSpan(1)) == 1023, "原生信息窗字节数严格小于0x400");
        foreach (string invalid in new[] { "", new string('a', 1024), new string('材', 512) })
            Reject<ArgumentOutOfRangeException>(() => ServerNoticePackets.Information(invalid), "提示按编码后字节数拒绝空正文或越界，不发送原生不读取的正文");
        That(body748.Length == 41 && body748[0] == 5 && Enumerable.Range(0, 5).All(index =>
            BinaryPrimitives.ReadInt32LittleEndian(body748.AsSpan(1 + index * 8)) == selected.After.Materials[index] &&
            BinaryPrimitives.ReadInt32LittleEndian(body748.AsSpan(5 + index * 8)) == 10) &&
            EpicPieceShopPackets.Result(new(true, 0)).SequenceEqual(new byte[] { 1 }) &&
            EpicPieceShopPackets.Result(InventoryResult.Reject(19)).SequenceEqual(new byte[] { 0, 19, 0 }),
            "748用u8行数和i32碎片编号/数量，三个命令成功仅1B、失败跟u16错误码");
        Console.WriteLine($"PASS {Count - before} 碎片商店配置、钱包交换、报价缓存、限次及协议核心断言");
    }
}
