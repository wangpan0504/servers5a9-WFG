using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class AvatarEmblemTests
{
    public static void RunCore()
    {
        int checks = Count;
        var items = new ItemCatalog(null!, new Dictionary<int, string>
            { [100] = "skin.equ", [101] = "aura.equ", [102] = "hat.equ" },
            new Dictionary<int, string> { [300] = "red.stk", [301] = "blue.stk", [302] = "platinum.stk", [303] = "mixed.stk" }, 0);
        items.PrefillTemplates(new Dictionary<int, ItemTemplate>
        {
            [100] = Template(100, ItemKind.Avatar), [101] = Template(101, ItemKind.Avatar), [102] = Template(102, ItemKind.Avatar),
            [300] = Template(300, ItemKind.Emblem, 1), [301] = Template(301, ItemKind.Emblem, 2),
            [302] = Template(302, ItemKind.Emblem, 16), [303] = Template(303, ItemKind.Emblem, 3)
        });
        var clock = new ConsumableUseSessionTests.FakeClock();
        clock.Advance(DateTimeOffset.FromUnixTimeSeconds(1000) - clock.GetUtcNow());
        var service = new InventoryAvatarEmblemService(items, clock);
        var detail = new AvatarDetail
        {
            ExpireTime = 1100, ClearAvatarId = 123, ClearAvatarSourceUid = 789, Color1 = 1, Color2 = 2,
            Socket0 = new(239, -1), Socket1 = new(0xFFEF, 999), Socket2 = new(1, -1),
            Socket3 = new(2, -1), Socket4 = new(16, -1)
        };
        InventoryItem Avatar(int id = 100, InventorySlot? slot = null) =>
            new(1, slot ?? new(InventoryListType.Avatar, 0), new(id, ItemKind.Avatar, 1) { Attr = 3 }) { Avatar = detail };
        InventoryItem Material(int id = 300, int count = 5) =>
            new(2, new(InventoryListType.Main, 289), new(id, ItemKind.Emblem, count) { ExpireTime = 1050 });
        Character Role(InventoryItem? target = null, InventoryItem? source = null) =>
            new(1, 0, "Emblem", 0, new(1, 0, 10, 10), null) { Level = 95, Inventory = new([target ?? Avatar(), source ?? Material()]) };
        AvatarEmblemRequest Request(params AvatarEmblemEntry[] entries) => new(new(InventoryListType.Avatar, 0), 100, [.. entries]);
        var pair = Request(new(289, 300, 0), new(289, 300, 1));
        byte[] body = TcpPeer.Build(writer =>
        {
            writer.Write((byte)1); writer.Write((short)0); writer.Write(100); writer.Write((byte)2);
            writer.Write((short)289); writer.Write(300); writer.Write((byte)0);
            writer.Write((short)289); writer.Write(300); writer.Write((byte)1);
        });
        var parsed = AvatarEmblemRequests.Read(body);
        That(parsed.Target == pair.Target && parsed.TargetItemId == pair.TargetItemId && parsed.Emblems.SequenceEqual(pair.Emblems),
            "CMD201保留独立容器字节和两项7字节条目");
        foreach (byte[] invalid in new[] { body[..^1], body.Concat(new byte[] { 0 }).ToArray(),
            body.Select((v, i) => i == 0 ? (byte)0 : v).ToArray(),
            body.Select((v, i) => i == 7 ? (byte)0 : v).ToArray(),
            body.Select((v, i) => i == 14 ? (byte)5 : v).ToArray() })
            Reject<InvalidDataException>(() => AvatarEmblemRequests.Read(invalid), "装扮徽章拒绝畸形、非法容器、零数量或越界孔号");
        That(Convert.ToHexString(AvatarEmblemPackets.Used([new(289, 3)])) == "0101210103000000" &&
            Convert.ToHexString(AvatarEmblemPackets.Failure()) == "000400", "ACK201输出材料槽绝对余量和u16错误码");
        That(Convert.ToHexString(AvatarEmblemPackets.Used([new(290, 0), new(289, 2)])) == "0102220100000000210102000000",
            "多材料槽ACK保持请求首次出现顺序，不把槽位当连续索引");

        var original = Role();
        var used = service.Use(original, pair, out var receipt, out _);
        var changed = used.Plan!.After.Find(pair.Target)!;
        That(used.Accepted && changed.Uid == 1 && changed.Core == original.Inventory.Items[0].Core &&
            changed.Avatar == detail with { Socket0 = new(239, 300), Socket1 = new(0xFFEF, 300) } &&
            used.Plan.After.Find(new(InventoryListType.Main, 289))!.Core.Count == 3 &&
            receipt is { Materials.Length: 1 } && receipt.Materials[0] == new AvatarEmblemRemaining(289, 3),
            "新旧彩孔按同栈两件消费，保留孔型、UID、完整核心、克隆来源和染色");
        That(original.Inventory.Items[0].Avatar == detail && original.Inventory.Items[1].Core.Count == 5 &&
            used.Plan.CharacterPrecondition!.Matches(original, 1049) && !used.Plan.CharacterPrecondition.Matches(original, 1050) &&
            !used.Plan.CharacterPrecondition.Matches(original with { Revision = 1 }, 1000),
            "规划不可变，提交冻结最早期限并复验原revision");
        var separate = Role(source: Material(count: 1));
        separate = separate with { Inventory = separate.Inventory with { Items = separate.Inventory.Items.Add(
            Material(301, 1) with { Uid = 3, Position = new(InventoryListType.Main, 290) }) } };
        var mixed = service.Use(separate, Request(new(290, 301, 1), new(289, 300, 0)), out var mixedReceipt, out _);
        That(mixed.Accepted && mixed.Plan!.After.Items.Length == 1 &&
            mixed.Plan.After.Items[0].Avatar is { Socket0.EmblemId: 300, Socket1.EmblemId: 301 } &&
            mixedReceipt!.Materials.SequenceEqual([new AvatarEmblemRemaining(290, 0), new(289, 0)]),
            "不同栈不同颜色分别消费，回执按首次出现顺序保留两个真实材料槽");
        foreach (int id in new[] { 100, 101, 102 })
        foreach (var slot in new[] { new InventorySlot(InventoryListType.Avatar, 0), new(InventoryListType.Equipment, 8) })
        {
            var result = service.Use(Role(Avatar(id, slot)), pair with { Target = slot, TargetItemId = id }, out _, out _);
            That(result.Accepted, "皮肤、光环与普通装扮在背包及穿戴位置共用镶嵌规则");
        }
        var equipped = Role(Avatar(slot: new(InventoryListType.Equipment, 8)));
        equipped = equipped with { Inventory = equipped.Inventory with
            { AvatarPresets = new(1, [new(0, "", [])], 0) } };
        var presetResult = service.Use(equipped, pair with { Target = new(InventoryListType.Equipment, 8) }, out _, out _);
        That(presetResult.Accepted && presetResult.Plan!.After.AvatarPresets.Find(0)!.Parts.Single().Sockets[0].EmblemId == 300,
            "已穿戴镶嵌在同一计划回写当前橱窗，切换预览不丢失徽章");
        foreach ((ushort type, int[] allowed) in new (ushort, int[])[]
        {
            (1, [300, 303]), (2, [301, 303]), (4, []), (8, []), (16, [302]), (239, [300, 301, 303]), (0xFFEF, [300, 301, 303]), (0, [])
        })
        foreach (int id in new[] { 300, 301, 302, 303 })
        {
            var result = service.Use(Role(Avatar() with { Avatar = detail with { Socket0 = new(type, -1) } }, Material(id)),
                Request(new AvatarEmblemEntry(289, id, 0)), out _, out _);
            That(result.Accepted == allowed.Contains(id), "红蓝双色、白金和彩色孔兼容性以资源mask逐一核对");
        }
        var all = service.Use(Role(), Request(new(289, 300, 0), new(289, 300, 1), new(289, 300, 2)), out _, out _);
        That(all.Accepted && all.Plan!.After.Find(pair.Target)!.Avatar!.Socket2 == new AvatarSocket(1, 300),
            "请求保留第三孔编号而非仅支持皮肤双孔");
        var final = service.Use(Role(source: Material(302, 1)), Request(new AvatarEmblemEntry(289, 302, 4)), out _, out _);
        That(final.Accepted && final.Plan!.After.Find(pair.Target)!.Avatar!.Socket4 == new AvatarSocket(16, 302) &&
            final.Plan.After.Find(new(InventoryListType.Main, 289)) is null, "第五个白金孔和材料耗尽完整处理");
        var five = service.Use(Role(Avatar() with { Avatar = detail with
            { Socket2 = new(239, -1), Socket3 = new(239, -1), Socket4 = new(239, -1) } }),
            Request(new(289, 300, 0), new(289, 300, 1), new(289, 300, 2), new(289, 300, 3), new(289, 300, 4)), out _, out _);
        That(five.Accepted && five.Plan!.After.Find(new(InventoryListType.Main, 289)) is null &&
            five.Plan.After.Find(pair.Target)!.Avatar!.Socket4.EmblemId == 300, "一次请求支持全部五个孔并准确聚合扣除五件");

        foreach (var role in new[]
        {
            Role(source: Material(count: 1)),
            Role(source: Material() with { Core = Material().Core with { EquipmentLockId = 1 } }),
            Role(source: Material() with { Core = Material().Core with { ExpireTime = 1000 } }),
            Role(Avatar() with { Core = Avatar().Core with { EquipmentLockId = 1 } }),
            Role(Avatar() with { Avatar = detail with { ExpireTime = 1000 } }),
            Role(Avatar() with { Avatar = detail with { Socket1 = default } })
        })
        {
            var result = service.Use(role, pair, out var denied, out _);
            That(!result.Accepted && result.Plan is null && denied is null, "材料不足、锁定、到期或关闭孔整批拒绝且无可提交计划");
        }
        foreach (var invalid in new[]
        {
            pair with { TargetItemId = 101 }, pair with { Target = new(InventoryListType.PersonalCargo, 0) },
            Request(new(289, 300, 0), new(289, 300, 0)), Request(new AvatarEmblemEntry(289, 301, 0)), Request(new AvatarEmblemEntry(290, 300, 0)),
            Request(new AvatarEmblemEntry(289, 300, 5)), pair with { Emblems = [] }, pair with { Emblems = default }
        })
            That(!service.Use(original, invalid, out _, out _).Accepted, "模板、归属、重复孔、空槽及非法条目领域层也拒绝");
        clock.Advance(TimeSpan.FromSeconds(50));
        That(!used.Plan.CharacterPrecondition!.Matches(original) && !service.Use(original, pair, out _, out _).Accepted,
            "相同注入时钟到期后规划与提交前置同时拒绝");
        Console.WriteLine($"PASS {Count - checks} 装扮徽章解析、五孔、颜色、资产与期限断言");
    }

    static ItemTemplate Template(int id, ItemKind kind, ushort mask = 0) =>
        new(id, kind, 0, 1, 0, kind == ItemKind.Avatar ? 1 : 999, 0, 0, 0, "[trade]", ushort.MaxValue, 0, "emblem")
        { EmblemTargetSockets = mask };
}
