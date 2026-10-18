using System.Buffers.Binary;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class LimitCubeTests
{
    internal const int TicketId = 490705395;
    internal const int TargetId = 490705358;

    public static void RunCore()
    {
        int checks = Count;
        byte[] observed = Convert.FromHexString("5F00CE913F1D5500");
        That(LimitCubeRequests.Read(observed) == new LimitCubeRequest(95, TargetId, 85),
            "CMD338按实收8B解析目标槽、目标模板和变更券槽");
        foreach (byte[] malformed in new byte[][]
        {
            [], new byte[7], new byte[9], Convert.FromHexString("5F00CE913F1D5F00"),
            Convert.FromHexString("5F00000000805500")
        })
            Reject<InvalidDataException>(() => LimitCubeRequests.Read(malformed),
                "CMD338拒绝截断、附加、同槽和超出有符号模板范围的正文");
        var wireReceipt = new LimitCubeReceipt(TicketId, TargetId, TargetId + 1, 1,
            new(InventoryListType.Main, 65), 0, 1);
        Bytes(LimitCubePackets.Success(wireReceipt),
            Convert.FromHexString("01CF913F1D01004100000001"),
            "CMD338成功ACK固定12B并以u32回显已提交结果槽");
        Bytes(LimitCubePackets.Failure(), [0, 1, 0],
            "CMD338失败使用D95公共状态加u16错误信封");

        var fixture = new Fixture();
        var definition = fixture.Cubes.Get(TicketId)!;
        That(definition.Targets is [{ ItemId: TargetId, Count: 1 }] &&
            definition.Materials.Select(row => (row.ItemId, row.Count))
                .SequenceEqual(new[] { (3037, 2), (Fixture.MaterialId, 1) }) &&
            definition.Results.Select(row => (row.ItemId, row.Count, row.Weight))
                .SequenceEqual(new[] { (Fixture.ResultA, 1, 3L), (Fixture.ResultB, 1, 1L) }),
            "variant0目录严格读取A条件、现场无关闭标签的B材料和有序正权重结果");
        That(fixture.Cubes.IsBattleSpirit(Fixture.BattleSpiritId) &&
            !fixture.Cubes.IsBattleSpirit(Fixture.EquipmentTarget),
            "装备自定义能力类型和数量共同识别斗气装备，普通装备不误判");
        var unsupported = new LimitCubeCatalog(_ =>
            "[stackable type]\n`[upgrade limit cube]` 1\n", new Dictionary<int, string> { [1] = "x.stk" });
        That(unsupported.Get(1) is null, "非variant0变更券不进入当前入口");
        var malformedCatalog = new LimitCubeCatalog(_ => Fixture.StackableText.Replace(
            $"{Fixture.ResultB} 1 1", $"{Fixture.ResultB} 1", StringComparison.Ordinal),
            new Dictionary<int, string> { [TicketId] = "bad.stk" });
        Reject<InvalidDataException>(() => malformedCatalog.Get(TicketId),
            "截断结果行不能被数字前缀补成合法奖池");

        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var character = fixture.StackableCharacter();
        var result = fixture.Service(_ => 0).Apply(character,
            new(95, TargetId, 85), out var receipt, out var rejection, now);
        That(result.Accepted && rejection is null && receipt is
            { TicketItemId: TicketId, TargetItemId: TargetId, ResultItemId: Fixture.ResultA,
                ResultCount: 1, Roll: 0, TotalWeight: 4 },
            "首个权重边界选择第一项并返回实际结果信息");
        var after = result.Plan!.After;
        That(character.Inventory.Find(new(InventoryListType.Main, 95)) is not null &&
            after.Find(new(InventoryListType.Main, 95)) is null &&
            after.Find(new(InventoryListType.Main, 85))?.Core.Count == 1 &&
            after.Find(new(InventoryListType.Main, 121)) is null &&
            after.AccountWallet.Get(CurrencyKind.ClearCube) == 3,
            "A目标、1张券、物理B材料和账号晶块只在同一After中原子扣除");
        var output = after.Find(receipt!.ResultSlot);
        That(output is { Uid: 0, Core.ItemId: Fixture.ResultA, Core.Count: 1 } &&
            output.Core.ExpireTime == int.MaxValue &&
            receipt.ResultSlot == new InventorySlot(InventoryListType.Main, 65),
            "结果按真实Main自动放置规则创建临时UID实例、忽略历史绝对日期并回显实际槽");
        That(!fixture.Service(_ => 0, ignoreAbsoluteExpiration: false).Apply(fixture.StackableCharacter(),
            new(95, TargetId, 85), out _, out _, now).Accepted,
            "关闭历史商城绝对日期忽略时不生成已过期结果");
        var lastWeight = fixture.Service(bound => bound - 1).Apply(fixture.StackableCharacter(),
            new(95, TargetId, 85), out var lastReceipt, out _, now);
        That(lastWeight.Accepted && lastReceipt is
            { ResultItemId: Fixture.ResultB, Roll: 3, TotalWeight: 4 },
            "总权重末端选择最后一项，不重抽或改写权重");

        foreach (var denied in new[]
        {
            fixture.StackableCharacter(clearCubes: 1),
            fixture.StackableCharacter(materialCount: 0),
            fixture.StackableCharacter(targetLocked: true),
            fixture.StackableCharacter(ticketLocked: true),
            fixture.StackableCharacter(targetExpiry: (int)now.ToUnixTimeSeconds()),
            fixture.StackableCharacter(ticketExpiry: (int)now.ToUnixTimeSeconds())
        })
        {
            string snapshot = denied.Inventory.ToString()!;
            var rejected = fixture.Service(_ => 0).Apply(denied,
                new(95, TargetId, 85), out var deniedReceipt, out var deniedReason, now);
            That(!rejected.Accepted && rejected.Plan is null && deniedReceipt is null &&
                deniedReason is not null && denied.Inventory.ToString() == snapshot,
                "材料不足、安全锁或准确到期均拒绝且不改变输入快照");
        }
        var crowded = fixture.StackableCharacter(targetCount: 2, fillConsumables: true);
        var full = fixture.Service(_ => 0).Apply(crowded,
            new(95, TargetId, 85), out _, out _, now);
        That(!full.Accepted && crowded.Inventory.Find(new(InventoryListType.Main, 95))!.Core.Count == 2 &&
            crowded.Inventory.Find(new(InventoryListType.Main, 85))!.Core.Count == 2,
            "目标与券扣除后未释放格子的满页失败不留下部分消费");
        var timed = fixture.StackableCharacter(materialExpiry: (int)now.ToUnixTimeSeconds() + 5);
        var timedResult = fixture.Service(_ => 0).Apply(timed,
            new(95, TargetId, 85), out _, out _, now);
        That(timedResult.Accepted &&
            timedResult.Plan!.CharacterPrecondition!.Matches(timed, now.ToUnixTimeSeconds() + 4) &&
            !timedResult.Plan.CharacterPrecondition.Matches(timed, now.ToUnixTimeSeconds() + 5),
            "所有实际消费的有限期限材料在提交前按最早截止秒复验");
        That(!fixture.Service(bound => bound).Apply(fixture.StackableCharacter(),
            new(95, TargetId, 85), out _, out _, now).Accepted,
            "越界随机源明确拒绝，不将异常值映射到候选");

        var equipment = fixture.EquipmentCharacter();
        var converted = fixture.Service(_ => 0).Apply(equipment,
            new(9, Fixture.EquipmentTarget, 86), out var equipmentReceipt, out _, now);
        var convertedItem = converted.Plan!.After.Find(equipmentReceipt!.ResultSlot)!;
        That(converted.Accepted && convertedItem.Position == new InventorySlot(InventoryListType.Main, 9) &&
            convertedItem.Core.ItemId == Fixture.EquipmentResult &&
            convertedItem.Core.EnchantCardId == 3175 && convertedItem.Core.EnchantUpgrade == 2 &&
            convertedItem.Core.SealFlag == 1 && convertedItem.Core.Attr == 0 &&
            convertedItem.Core.GenuineUpgrade == 0 && convertedItem.Core.Random0 == default,
            "装备转换仅继承附魔卡、附魔等级和封装，其他实例属性按结果模板重建");

        Console.WriteLine($"PASS {Count - checks} 变更券目录、权重、原子规划及338号协议断言");
    }

    public static void RunResources(ItemResources resources)
    {
        var definition = resources.LimitCubes.Get(TicketId);
        That(definition is not null &&
            definition.Targets.Any(row => row.ItemId == TargetId && row.Count > 0) &&
            !definition.Results.IsDefaultOrEmpty && definition.Results.All(row => row.Count > 0 && row.Weight > 0),
            "当前暴戾搜捕团宝珠变更券由现场PVF提供目标和正权重结果");
        foreach (var result in definition!.Results)
        {
            var template = resources.Items.Get(result.ItemId);
            That(ItemSlotBoundService.List(template.Kind) == InventoryListType.Main &&
                result.Count <= template.StackLimit &&
                CurrencyWallet.RewardKind(result.ItemId) is null &&
                !resources.EpicPieces.ByPiece.ContainsKey(result.ItemId) &&
                !resources.Contracts.Recognizes(result.ItemId) &&
                !resources.Boosters.DirectActivationItems.Contains(result.ItemId) &&
                template.AcquisitionContract is null && !template.IsPremiumService &&
                (template.Kind != ItemKind.Equipment || !resources.LimitCubes.IsBattleSpirit(template.Id)),
                "现场每个正权重结果都能作为实体Main物品直接交付");
        }
    }

    sealed class Fixture
    {
        internal const int ResultA = 490705359;
        internal const int ResultB = 490705360;
        internal const int MaterialId = 700;
        internal const int EquipmentTicket = 501;
        internal const int EquipmentTarget = 200;
        internal const int EquipmentResult = 201;
        internal const int BattleSpiritId = 202;
        internal const int FillerId = 900;
        internal const string StackableText = """
            [name]
            `[result item] 999 1 999`
            [stackable type]
            `[upgrade limit cube]` 0
            [upgrade limit cube info]
            [a condition item]
            490705358 1
            [/a condition item]
            [b condition item]
            3037 2
            700 1
            [result item]
            490705359 1 3
            490705360 1 1
            [/result item]
            [/upgrade limit cube info]
            """;

        readonly EpicPieceCatalog pieces = new("[piece list]\n60000 70001\n[/piece list]");
        readonly PremiumContractCatalog contracts = new(new Dictionary<int, PremiumContract>());
        public ItemCatalog Items { get; }
        public LimitCubeCatalog Cubes { get; }

        public Fixture()
        {
            var stackable = new Dictionary<int, string>
            {
                [TicketId] = "ticket.stk", [EquipmentTicket] = "equipment-ticket.stk"
            };
            var equipment = new Dictionary<int, string>
            {
                [EquipmentTarget] = "target.equ", [EquipmentResult] = "result.equ",
                [BattleSpiritId] = "battle.equ"
            };
            var files = new Dictionary<string, string>
            {
                ["stackable/ticket.stk"] = StackableText,
                ["stackable/equipment-ticket.stk"] = """
                    [stackable type]
                    `[upgrade limit cube]` 0
                    [upgrade limit cube info]
                    [a condition item]
                    200 1
                    [/a condition item]
                    [b condition item]
                    [/b condition item]
                    [result item]
                    201 1 1
                    [/result item]
                    [/upgrade limit cube info]
                    """,
                ["equipment/target.equ"] = "[equipment type]\n`[title]` 0\n",
                ["equipment/result.equ"] = "[equipment type]\n`[title]` 0\n",
                ["equipment/battle.equ"] = """
                    [equipment type]
                    `[title]` 0
                    [custom ability type]
                    `[warriors dungeon]`
                    [custom ability count]
                    2
                    """
            };
            Cubes = new(path => files[path], stackable, equipment);
            Items = new(null!, equipment, stackable, 0);
            Items.PrefillTemplates(new Dictionary<int, ItemTemplate>
            {
                [TicketId] = Stack(TicketId), [EquipmentTicket] = Stack(EquipmentTicket),
                [TargetId] = Stack(TargetId),
                [ResultA] = Stack(ResultA) with { Expiration = new(1_700_000_000) },
                [ResultB] = Stack(ResultB) with { Expiration = new(1_700_000_000) },
                [MaterialId] = Stack(MaterialId, ItemKind.Material), [FillerId] = Stack(FillerId),
                [EquipmentTarget] = Equipment(EquipmentTarget, 45),
                [EquipmentResult] = Equipment(EquipmentResult, 55) with { AttachType = "[free]" },
                [BattleSpiritId] = Equipment(BattleSpiritId, 55)
            });
        }

        public InventoryLimitCubeService Service(Func<long, long> random, bool ignoreAbsoluteExpiration = true) =>
            new(Items, Cubes, pieces, contracts, new HashSet<int>(), random,
                new GameDebugSettings { IgnoreCashShopAbsoluteExpiration = ignoreAbsoluteExpiration });

        public Character StackableCharacter(int clearCubes = 5, int materialCount = 1,
            int targetCount = 1, bool targetLocked = false, bool ticketLocked = false,
            int targetExpiry = 0, int ticketExpiry = 0, int materialExpiry = 0,
            bool fillConsumables = false)
        {
            var rows = new List<InventoryItem>
            {
                new(1, Bag(85), new(TicketId, ItemKind.Consumable, 2)
                    { SortLockFlag = 1, EquipmentLockId = ticketLocked ? (byte)1 : (byte)0, ExpireTime = ticketExpiry }),
                new(2, Bag(95), new(TargetId, ItemKind.Consumable, targetCount)
                    { EquipmentLockId = targetLocked ? (byte)1 : (byte)0, ExpireTime = targetExpiry })
            };
            if (materialCount > 0)
                rows.Add(new(3, Bag(121), new(MaterialId, ItemKind.Material, materialCount)
                    { ExpireTime = materialExpiry }));
            if (fillConsumables)
                for (short slot = 65; slot <= 120; slot++)
                    if (slot is not (85 or 95))
                        rows.Add(new(1000 + slot, Bag(slot), new(FillerId, ItemKind.Consumable, 1)));
            return Character(rows, AccountWallet.Empty.Set(CurrencyKind.ClearCube, clearCubes));
        }

        public Character EquipmentCharacter() => Character(
        [
            new(10, Bag(86), new(EquipmentTicket, ItemKind.Consumable, 2)),
            new(11, Bag(9), new(EquipmentTarget, ItemKind.Equipment, 1234567)
            {
                Attr = 7, GenuineUpgrade = 4, AmplifyType = 2, AmplifyValue = 21,
                SealFlag = 1, EnchantCardId = 3175, EnchantUpgrade = 2,
                Random0 = new(1, 2, 3), SortLockFlag = 1
            })
        ], AccountWallet.Empty);

        static Character Character(IEnumerable<InventoryItem> items, AccountWallet wallet) =>
            new(1, 0, "LimitCube", 0, new(1, 0, 10, 10), null)
            {
                Level = 95,
                Inventory = new([.. items]) { AccountWallet = wallet }
            };

        static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
        static ItemTemplate Stack(int id, ItemKind kind = ItemKind.Consumable) =>
            new(id, kind, 0, 1, 0, 1000, 0, 0, 0, "[free]", ushort.MaxValue, 0, id.ToString());
        static ItemTemplate Equipment(int id, ushort durability) =>
            new(id, ItemKind.Equipment, 13, 1, durability, 1, 0, 2, 1,
                "[sealing]", ushort.MaxValue, 0, id.ToString());
    }
}
