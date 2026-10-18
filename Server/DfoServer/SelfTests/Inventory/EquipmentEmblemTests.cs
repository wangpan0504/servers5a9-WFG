using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class EquipmentEmblemTests
{
    public static void RunCore()
    {
        int checks = Count;
        var fixture = new Fixture();

        byte[] useBody = Build(writer =>
        {
            writer.Write((short)9);
            writer.Write(100);
            writer.Write((byte)2);
            writer.Write((short)289);
            writer.Write(300);
            writer.Write((byte)0);
            writer.Write((short)290);
            writer.Write(301);
            writer.Write((byte)1);
        });
        var parsedUse = EquipmentEmblemRequests.Use(useBody);
        That(parsedUse is { TargetSlot: 9, TargetItemId: 100, Emblems.Length: 2 } &&
            parsedUse.Emblems[0] == new EquipmentEmblemEntry(289, 300, 0) &&
            parsedUse.Emblems[1] == new EquipmentEmblemEntry(290, 301, 1),
            "C915严格解析目标和两组7字节徽章条目");
        foreach (byte[] invalid in new[]
        {
            useBody[..^1],
            useBody.Concat(new byte[] { 0 }).ToArray(),
            useBody[..6].Concat(new byte[] { 0 }).ToArray(),
            useBody[..20].Concat(new byte[] { 2 }).ToArray()
        })
            Reject<InvalidDataException>(() => EquipmentEmblemRequests.Use(invalid),
                "C915拒绝截断、附加、零数量及非法孔位");

        byte[] openBody = Build(writer =>
        {
            writer.Write((short)9);
            writer.Write(100);
            writer.Write((short)121);
        });
        That(EquipmentEmblemRequests.Open(openBody) == new EquipmentSocketRequest(9, 100, 121),
            "C916固定8字节解析目标与开孔材料槽");
        Reject<InvalidDataException>(() => EquipmentEmblemRequests.Open(openBody[..^1]),
            "C916拒绝非8字节正文");
        That(Convert.ToHexString(EquipmentEmblemPackets.Used(
                [new(289, 2), new(290, 0)])) == "0102210102000000220100000000" &&
            Convert.ToHexString(EquipmentEmblemPackets.SocketOpened(9, 121)) == "0109007900" &&
            Convert.ToHexString(EquipmentEmblemPackets.Failure()) == "000400",
            "S5A9成功ACK使用绝对剩余数量，失败按u16错误码输出3字节");

        ushort parsedMask = ItemCatalog.ReadEmblemTargetSockets(new PvfDocument(
            "[avatar emblem target type]\n\t`[A socket]`\t`[B socket]`\t`[C socket]`\t`[D socket]`\t`[S socket]`"));
        ushort mixedMask = ItemCatalog.ReadEmblemTargetSockets(new PvfDocument(
            "[avatar emblem target type]\n\t`[M socket]`"));
        That(parsedMask == 31 && mixedMask == 239,
            "徽章资源A/B/C/D/S及混色M声明映射为匹配孔位掩码");

        int now = 1_800_000_000;
        var closed = fixture.Character(
            fixture.Equipment(1, new(InventoryListType.Main, 9), 100, expiry: now + 10),
            fixture.Stack(2, new(InventoryListType.Main, 121), 500, ItemKind.Material, 2, expiry: now + 20));
        var opened = fixture.Service.OpenSockets(closed, new(9, 100, 121),
            out var openReceipt, out var openRejection, now);
        var openedTarget = opened.Plan!.After.Find(new(InventoryListType.Main, 9))!;
        That(opened.Accepted && openRejection is null &&
            openReceipt == new EquipmentSocketReceipt(new(InventoryListType.Main, 9), 121) &&
            openedTarget.Core is { EmblemSocketCount: 2, Emblem1: -1, Emblem2: -1, Attr: 7, Durability: 44 } &&
            opened.Plan.After.Find(new(InventoryListType.Main, 121))?.Core.Count == 1 &&
            opened.Plan.CharacterPrecondition!.Matches(closed, now + 9) &&
            !opened.Plan.CharacterPrecondition.Matches(closed, now + 10),
            "普通装备开两孔、扣一件材料、保留完整核心并冻结最早期限");
        That(closed.Inventory.Find(new(InventoryListType.Main, 9))!.Core.EmblemSocketCount == 0 &&
            closed.Inventory.Find(new(InventoryListType.Main, 121))!.Core.Count == 2,
            "开孔仅生成不可变计划，不修改原角色快照");

        var openCharacter = fixture.Character(
            fixture.Equipment(1, new(InventoryListType.Main, 9), 100, sockets: 2, emblem1: -1, emblem2: -1),
            fixture.Stack(2, new(InventoryListType.Main, 289), 300, ItemKind.Emblem, 2));
        var used = fixture.Service.UseEmblems(openCharacter, new(9, 100,
                ImmutableArray.Create(new EquipmentEmblemEntry(289, 300, 0),
                    new EquipmentEmblemEntry(289, 300, 1))),
            out var useReceipt, out var useRejection, now);
        var usedTarget = used.Plan!.After.Find(new(InventoryListType.Main, 9))!;
        That(used.Accepted && useRejection is null &&
            usedTarget.Core is { EmblemSocketCount: 2, Emblem1: 300, Emblem2: 300, Attr: 7, Durability: 44 } &&
            used.Plan.After.Find(new(InventoryListType.Main, 289)) is null &&
            useReceipt is { Materials.Length: 1 } && useReceipt.Materials[0] == new EquipmentEmblemRemaining(289, 0),
            "同一徽章栈可一次镶嵌两孔，聚合扣除与ACK槽且覆盖目标核心");

        var support = fixture.Character(
            fixture.Equipment(1, new(InventoryListType.Equipment, 22), 101, sockets: 1, emblem1: -1),
            fixture.Stack(2, new(InventoryListType.Main, 289), 302, ItemKind.Emblem, 1));
        var single = fixture.Service.UseEmblems(support, new(22, 101,
                ImmutableArray.Create(new EquipmentEmblemEntry(289, 302, 1))),
            out _, out _, now);
        That(single.Accepted && single.Plan!.After.Find(new(InventoryListType.Equipment, 22))!.Core is
            { EmblemSocketCount: 1, Emblem1: 302, Emblem2: 0 },
            "辅助装备单孔兼容客户端的孔位0/1输入并统一写逻辑第一孔");

        var redEmblem = fixture.Character(
            fixture.Equipment(1, new(InventoryListType.Main, 9), 100, sockets: 2, emblem1: -1, emblem2: -1),
            fixture.Stack(2, new(InventoryListType.Main, 289), 301, ItemKind.Emblem, 1));
        var insufficient = fixture.Character(
            fixture.Equipment(1, new(InventoryListType.Main, 9), 100, sockets: 2, emblem1: -1, emblem2: -1),
            fixture.Stack(2, new(InventoryListType.Main, 289), 300, ItemKind.Emblem, 1));
        var expired = fixture.Character(
            fixture.Equipment(1, new(InventoryListType.Main, 9), 100, sockets: 2, emblem1: -1, emblem2: -1),
            fixture.Stack(2, new(InventoryListType.Main, 289), 300, ItemKind.Emblem, 2, expiry: now));
        foreach (var denied in new[]
        {
            fixture.Service.UseEmblems(redEmblem, new(9, 100,
                ImmutableArray.Create(new EquipmentEmblemEntry(289, 301, 0))), out _, out _, now),
            fixture.Service.UseEmblems(insufficient, new(9, 100,
                ImmutableArray.Create(new EquipmentEmblemEntry(289, 300, 0),
                    new EquipmentEmblemEntry(289, 300, 1))), out _, out _, now),
            fixture.Service.UseEmblems(expired, new(9, 100,
                ImmutableArray.Create(new EquipmentEmblemEntry(289, 300, 0))), out _, out _, now),
            fixture.Service.UseEmblems(support, new(22, 101,
                ImmutableArray.Create(new EquipmentEmblemEntry(289, 302, 0),
                    new EquipmentEmblemEntry(289, 302, 1))), out _, out _, now),
            fixture.Service.OpenSockets(closed with
            {
                Inventory = new([fixture.Equipment(1, new(InventoryListType.Main, 9), 100, locked: true),
                    fixture.Stack(2, new(InventoryListType.Main, 121), 500, ItemKind.Material, 2)])
            }, new(9, 100, 121), out _, out _, now),
            fixture.Service.OpenSockets(openCharacter, new(9, 100, 289), out _, out _, now)
        })
            That(!denied.Accepted && denied.Plan is null && denied.Error == InventoryEquipmentEmblemService.Rejection,
                "颜色不兼容、数量不足、到期、单孔重复、安全锁及已开孔目标均原子拒绝");

        Console.WriteLine($"PASS {Count - checks} 普通装备开孔、徽章资源、领域事务及915/916协议断言");
    }

    static byte[] Build(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        write(writer);
        return stream.ToArray();
    }

    sealed class Fixture
    {
        public ItemCatalog Items { get; }
        public InventoryEquipmentEmblemService Service { get; }

        public Fixture()
        {
            Items = new(null!,
                new Dictionary<int, string> { [100] = "coat.equ", [101] = "support.equ" },
                new Dictionary<int, string>
                {
                    [300] = "green.stk", [301] = "red.stk", [302] = "platinum.stk", [500] = "material.stk"
                }, 0);
            Items.PrefillTemplates(new Dictionary<int, ItemTemplate>
            {
                [100] = Template(100, ItemKind.Equipment, 14),
                [101] = Template(101, ItemKind.Equipment, 22),
                [300] = Template(300, ItemKind.Emblem, mask: 4),
                [301] = Template(301, ItemKind.Emblem, mask: 1),
                [302] = Template(302, ItemKind.Emblem, mask: 16),
                [500] = Template(500, ItemKind.Material)
            });
            Service = new(Items);
        }

        public Character Character(params InventoryItem[] items) =>
            new(1, 0, "Emblem", 0, new(1, 0, 10, 10), null)
            {
                Level = 95,
                Inventory = new(items.ToImmutableArray())
            };

        public InventoryItem Equipment(long uid, InventorySlot position, int itemId, byte sockets = 0,
            int emblem1 = 0, int emblem2 = 0, int expiry = 0, bool locked = false) =>
            new(uid, position, new(itemId, ItemKind.Equipment, 123456)
            {
                Attr = 7,
                Durability = 44,
                EmblemSocketCount = sockets,
                Emblem1 = emblem1,
                Emblem2 = emblem2,
                ExpireTime = expiry,
                EquipmentLockId = locked ? (byte)1 : (byte)0
            });

        public InventoryItem Stack(long uid, InventorySlot position, int itemId, ItemKind kind, int count,
            int expiry = 0) =>
            new(uid, position, new(itemId, kind, count) { ExpireTime = expiry });

        static ItemTemplate Template(int id, ItemKind kind, byte slot = 0, ushort mask = 0) =>
            new(id, kind, slot, 1, 0, kind == ItemKind.Equipment ? 1 : 999, 0, 0, 0,
                "[trade]", ushort.MaxValue, 0, id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                EmblemTargetSockets = mask
            };
    }
}
