using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class TextureChangeTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    public static void RunCore()
    {
        int checks = Count;

        That(EquipmentTextureChangeRequests.Read([26, 0, 2]) == new EquipmentTextureChangeRequest(26, 2),
            "1546严格解析u16主背包槽与u8目标材质");
        foreach (byte[] malformed in new byte[][] { [], [26, 0], [26, 0, 2, 0], [0xFF, 0xFF, 2], [26, 0, 5] })
            Reject<InvalidDataException>(() => EquipmentTextureChangeRequests.Read(malformed),
                "1546拒绝长度、负槽位与越界材质");

        var fixture = new Fixture();
        That(fixture.Textures.TryGet(101, out var cloth) && cloth.Material == 0 &&
            cloth.Variants.SequenceEqual([101, 102, 103, 104, 105]) &&
            fixture.Textures.TryGet(105, out var plate) && plate.Material == 4 &&
            fixture.Textures.TryGet(305, out var shoesPlate) && shoesPlate.Material == 4 &&
            !fixture.Textures.TryGet(106, out _),
            "材质转换表按输出位置确定材质并提供同族5件，表外物品不命中");
        foreach (var (name, text) in new[]
        {
            ("缺少输出", "[group]\n[input]\n101\n[output]\n101 102 103 104\n[/output]\n[/group]\n"),
            ("输入不在输出", "[group]\n[input]\n106\n[output]\n101 102 103 104 105\n[/output]\n[/group]\n"),
            ("输出顺序错误", "[group]\n[input]\n101\n[output]\n102 101 103 104 105\n[/output]\n[/group]\n"),
            ("物品不在列表", "[group]\n[input]\n101\n[output]\n101 102 103 104 999\n[/output]\n[/group]\n"),
            ("同族冲突", "[group]\n[input]\n101\n[output]\n101 102 103 104 105\n[/output]\n[/group]\n" +
                "[group]\n[input]\n110\n[output]\n110 102 103 104 105\n[/output]\n[/group]\n"),
            ("空表", "")
        })
            Reject<InvalidDataException>(() => new EquipmentTextureChangeCatalog(text, Fixture.Paths),
                "材质转换表拒绝" + name);

        var character = fixture.Character(600_000, 2,
            fixture.Equipment(1, 26, 101),
            fixture.Stack(2, 30, 3146, 60),
            fixture.Stack(3, 31, 3146, 40));
        var before = character.Inventory;
        var original = before.Find(new(InventoryListType.Main, 26))!;
        var result = fixture.Service.Apply(character, new(26, 2), Now);
        var after = result.Plan!.After;
        var converted = after.Find(new(InventoryListType.Main, 26))!;
        That(result.Accepted && result.Plan.Reason == "equipment_texture_change" &&
            converted.Core.ItemId == 103 && converted.Uid == original.Uid && converted.Position == original.Position,
            "布甲转轻甲只替换物品编号并保留UID与槽位");
        That(converted.Core with { ItemId = original.Core.ItemId } == original.Core,
            "强化、增幅、附魔、徽章、词条、锁、耐久与品级Value逐字段不变");
        That(converted.Core is { Value: 999_999_998, Attr: 0x0E, AmplifyValue: 42, EnchantCardId: 5001,
                Emblem1: 300, Random0: { Type: 1, Value1: 2, Value2: 3 }, TradeRestriction: 1 },
            "品级100%与关键保留字段显式抽查");
        That(after.Find(new(InventoryListType.Main, 30)) is null &&
            after.Find(new(InventoryListType.Main, 31))?.Core.Count == 20 &&
            after.Gold == 100_000 && after.AccountWallet.Get(CurrencyKind.LegendarySoul) == 1,
            "两堆材料共扣80、金币扣50万、传说灵魂扣1");
        That(result.Plan.ChangedCurrencies.Contains(CurrencyKind.Gold) &&
            result.Plan.ChangedCurrencies.Contains(CurrencyKind.LegendarySoul) &&
            result.Plan.ChangedSlots.Contains(new InventorySlot(InventoryListType.Main, 26)),
            "计划发布金币、传说灵魂与目标槽变更");
        That(before.Find(new(InventoryListType.Main, 26))!.Core.ItemId == 101 && before.Gold == 600_000 &&
            before.Find(new(InventoryListType.Main, 30))!.Core.Count == 60,
            "规划不修改原快照");

        var toPlate = fixture.Service.Apply(fixture.Character(600_000, 2, fixture.Equipment(1, 26, 101),
            fixture.Stack(2, 30, 3150, 80)), new(26, 4), Now);
        That(toPlate.Accepted && toPlate.Plan!.After.Find(new(InventoryListType.Main, 26))!.Core.ItemId == 105,
            "转板甲替换为板甲编号，板甲专属属性由目标物品PVF数据随重载生效");

        That(Fails(fixture, fixture.Character(600_000, 2, fixture.Equipment(1, 26, 101),
                fixture.Stack(2, 30, 3137, 80)), new(26, 0), 19) &&
            Fails(fixture, fixture.Character(600_000, 2, fixture.Equipment(1, 26, 101),
                fixture.Stack(2, 30, 3137, 80)), new(26, 5), 19),
            "同材质与越界目标材质拒绝");
        That(Fails(fixture, fixture.Character(600_000, 2, fixture.Equipment(1, 26, 106),
                fixture.Stack(2, 30, 3146, 80)), new(26, 2), 19),
            "表外装备拒绝");
        That(Fails(fixture, fixture.Character(600_000, 2,
                fixture.Equipment(1, 26, 101, Fixture.RichCore(101) with { EquipmentLockId = 1 }),
                fixture.Stack(2, 30, 3146, 80)), new(26, 2), 0xD5),
            "装备锁定时按本版锁定码拒绝");
        That(Fails(fixture, fixture.Character(600_000, 2,
                fixture.Equipment(1, 26, 101, Fixture.RichCore(101) with { ExpireTime = 1 }),
                fixture.Stack(2, 30, 3146, 80)), new(26, 2), 19) &&
            Fails(fixture, fixture.Character(600_000, 2,
                fixture.Equipment(1, 26, 101, Fixture.RichCore(101) with { IsRental = true }),
                fixture.Stack(2, 30, 3146, 80)), new(26, 2), 19),
            "过期装备与租赁实例拒绝");
        That(Fails(fixture, fixture.Character(600_000, 2, fixture.Equipment(1, 26, 101),
                fixture.Stack(2, 30, 3146, 79)), new(26, 2), 17) &&
            Fails(fixture, fixture.Character(600_000, 0, fixture.Equipment(1, 26, 101),
                fixture.Stack(2, 30, 3146, 80)), new(26, 2), 17) &&
            Fails(fixture, fixture.Character(499_999, 2, fixture.Equipment(1, 26, 101),
                fixture.Stack(2, 30, 3146, 80)), new(26, 2), 17),
            "材料、传说灵魂与金币不足均整笔拒绝");
        That(Fails(fixture, fixture.Character(600_000, 2), new(26, 2), 19) &&
            Fails(fixture, fixture.Character(600_000, 2, fixture.Stack(9, 26, 3146, 80)), new(26, 2), 19),
            "空槽与非装备槽位拒绝");
        That(Fails(fixture, fixture.Character(600_000, 2, fixture.Equipment(1, 26, 301),
                fixture.Stack(2, 30, 3150, 80)), new(26, 4), 19),
            "目标材质映射到不同部位的物品时拒绝，不生成半成品装备");

        fixture.Items.PrefillTemplates(new Dictionary<int, ItemTemplate>
            { [103] = fixture.Items.Get(103) with { Durability = 18 } });
        foreach (ushort durability in new ushort[] { 0, 10, 44 })
        {
            var input = original with { Core = original.Core with { Durability = durability } };
            var reduced = fixture.Service.Apply(fixture.Character(600_000, 1, input,
                fixture.Stack(2, 30, 3146, 80)), new(26, 2), Now);
            That(reduced.Accepted && reduced.Plan!.After.Find(input.Position) == input with
                { Core = input.Core with { ItemId = 103, Durability = Math.Min(durability, (ushort)18) } },
                "转到较低耐久上限材质时只钳制超限部分，不修理或改写其它打造及UID");
        }
        var clock = new ConsumableUseSessionTests.FakeClock();
        var guarded = new InventoryTextureChangeService(fixture.Items, fixture.Textures,
            new EpicPieceCatalog("[piece list]\n50000 50001\n[/piece list]"), clock);
        int expires = checked((int)clock.GetUtcNow().ToUnixTimeSeconds() + 10);
        foreach (var position in new[] { original.Position, new InventorySlot(InventoryListType.Main, 30), new(InventoryListType.Main, 31) })
        {
            var input = character.Inventory.Find(position)!;
            var limited = character with { Inventory = character.Inventory with
                { Items = character.Inventory.Items.Replace(input, input with { Core = input.Core with { ExpireTime = expires } }) } };
            var pending = guarded.Apply(limited, new(26, 2));
            That(pending.Accepted && pending.Plan!.CharacterPrecondition is { } qualification &&
                qualification.Matches(limited, expires - 1) && !qualification.Matches(limited, expires),
                "转甲的装备、整堆材料及部分扣除材料都在提交前复验最早截止");
        }
        string root = Path.Combine(Path.GetTempPath(), "s5a9-texture-expiry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repo = new CharacterRepository(Path.Combine(root, "game.db"));
            using var owner = new AccountOwners().Acquire("texture-expiry", () => { });
            var saved = repo.Create(owner, new("TexExpiry", character.Position, null)
            {
                Level = character.Level, Inventory = new([
                    original with { Uid = 0, Core = original.Core with { ExpireTime = expires } },
                    fixture.Stack(0, 121, 3146, 80)], Gold: 600_000)
            });
            saved = repo.CommitInventory(owner, saved, new(saved.Inventory, saved.Inventory with
                { AccountWallet = saved.Inventory.AccountWallet.Set(CurrencyKind.LegendarySoul, 2) }, "texture_expiry_fixture"));
            var pending = guarded.Apply(saved, new(26, 2));
            That(pending.Accepted, "真实文件库中期限内的装备可规划转甲");
            string snapshot = JsonSerializer.Serialize(repo.LoadCharacter(owner, saved.Slot));
            clock.Advance(TimeSpan.FromSeconds(10));
            Reject<InventoryQualificationExpiredException>(() => repo.CommitInventory(owner, saved, pending.Plan!),
                "转甲规划后目标恰到期限时文件库拒绝提交");
            That(JsonSerializer.Serialize(repo.LoadCharacter(owner, saved.Slot)) == snapshot,
                "到期拒绝保留装备UID与核心、材料、金币、账号灵魂、revision及账本");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - checks} 材质转换协议、映射表、费用与字段保留断言");
    }

    static bool Fails(Fixture fixture, Character character, EquipmentTextureChangeRequest request, ushort error)
    {
        var result = fixture.Service.Apply(character, request, Now);
        return !result.Accepted && result.Error == error && result.Plan is null;
    }

    sealed class Fixture
    {
        public static readonly Dictionary<int, string> Paths = new()
        {
            [101] = "character/common/pants/cloth/101.equ",
            [102] = "character/common/pants/leather/102.equ",
            [103] = "character/common/pants/larmor/103.equ",
            [104] = "character/common/pants/harmor/104.equ",
            [105] = "character/common/pants/plate/105.equ",
            [106] = "character/common/pants/cloth/106.equ",
            [110] = "character/common/pants/cloth/110.equ",
            [301] = "character/common/pants/cloth/301.equ",
            [302] = "character/common/pants/leather/302.equ",
            [303] = "character/common/pants/larmor/303.equ",
            [304] = "character/common/pants/harmor/304.equ",
            [305] = "character/common/shoes/plate/305.equ"
        };

        const string Table = """
            [group]
            [input]
            101
            [output]
            101 102 103 104 105
            [/output]
            [/group]

            [group]
            [input]
            301
            [output]
            301 302 303 304 305
            [/output]
            [/group]
            """;

        readonly EpicPieceCatalog pieces = new("[piece list]\n50000 50001\n[/piece list]");

        public ItemCatalog Items { get; }
        public EquipmentTextureChangeCatalog Textures { get; }

        public Fixture()
        {
            Items = new(null!, Paths, new Dictionary<int, string>(), 0);
            Textures = new(Table, Paths);
            var templates = new List<ItemTemplate>
            {
                Template(101, 16), Template(102, 16), Template(103, 16), Template(104, 16), Template(105, 16), Template(106, 16),
                Template(301, 16), Template(302, 16), Template(303, 16), Template(304, 16), Template(305, 17),
                Material(3137), Material(3141), Material(3146), Material(3160), Material(3150)
            };
            Items.PrefillTemplates(templates.ToDictionary(template => template.Id));
        }

        public InventoryTextureChangeService Service => new(Items, Textures, pieces);

        public InventoryItem Equipment(int uid, short slot, int itemId, ItemCore? core = null) =>
            new(uid, new(InventoryListType.Main, slot), core ?? RichCore(itemId));

        public InventoryItem Stack(int uid, short slot, int itemId, int count) =>
            new(uid, new(InventoryListType.Main, slot), new(itemId, ItemKind.Material, count));

        public Character Character(uint gold, int souls, params InventoryItem[] items) =>
            new(1, 0, "Texture", 0, new(1, 0, 10, 10), null)
            {
                Job = 0,
                Level = 95,
                Inventory = new(items.ToImmutableArray(), 24, gold)
                {
                    AccountWallet = AccountWallet.Empty.Set(CurrencyKind.LegendarySoul, souls)
                }
            };

        public static ItemCore RichCore(int itemId) => new(itemId, ItemKind.Equipment, 999_999_998)
        {
            Attr = 0x0E,
            Durability = 44,
            EnchantCardId = 5001,
            EnchantUpgrade = 3,
            AmplifyType = 3,
            AmplifyValue = 42,
            EmblemSocketCount = 2,
            Emblem1 = 300,
            Emblem2 = 301,
            Random0 = new RandomOption(1, 2, 3),
            TradeRestriction = 1
        };

        static ItemTemplate Template(int id, byte slot) =>
            new(id, ItemKind.Equipment, slot, 95, 50, 1, 0, 4, 3, "[trade]", ushort.MaxValue, 0, "pants");

        static ItemTemplate Material(int id) =>
            new(id, ItemKind.Material, 0, 0, 0, 999, 0, 3, 3, "[trade]", ushort.MaxValue, 0, "piece");
    }
}
