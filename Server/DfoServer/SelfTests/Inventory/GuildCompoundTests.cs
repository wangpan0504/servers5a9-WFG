using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class GuildCompoundTests
{
    const int Medal0 = 7001, Medal1 = 7011, Medal2 = 7021, Medal3 = 7031, Medal6 = 7061;
    const int Gem0 = 8001, Gem1 = 8011, Gem1B = 8012, Gem2 = 8021, Gem3 = 8031;

    // 与现场 etc/guilddungeoncompound.etc 同形；勋章段结束标记原文就拼作 /guid。
    const string Ladder = """
        [compound info]
        0 4 2000 1 4 1500 2 4 750 3 2 0
        [/compound info]
        [guild flag compound info]
        0 4 2500 1 4 2000 2 4 1000 3 4 500 6 2 0
        [/guid flag compound info]
        """;

    public static void RunCore()
    {
        int checks = Count;
        Parsing();
        Ladders();
        Transactions();
        Packets();
        Console.WriteLine($"PASS {Count - checks} 公会合成协议、阶梯与领域事务断言");
    }

    public static void RunResources(ItemResources resources)
    {
        var medals = resources.GuildCompounds.Ladder(GuildCompoundKind.GuildMedal);
        var gems = resources.GuildCompounds.Ladder(GuildCompoundKind.GuardianGem);
        That(medals.Rows.Select(row => (row.Grade, row.MaxCount, row.RatePerMaterial)).SequenceEqual(
                [(0, 4, 2500), (1, 4, 2000), (2, 4, 1000), (3, 4, 500), (6, 2, 0)]),
            "现场勋章阶梯保留PVF品级、件数上限与万分率");
        That(gems.Rows.Select(row => (row.Grade, row.MaxCount, row.RatePerMaterial)).SequenceEqual(
                [(0, 4, 2000), (1, 4, 1500), (2, 4, 750), (3, 2, 0)]),
            "现场守护珠阶梯保留PVF品级、件数上限与万分率");
        foreach (var kind in new[] { GuildCompoundKind.GuildMedal, GuildCompoundKind.GuardianGem })
        {
            var ladder = resources.GuildCompounds.Ladder(kind);
            var itemKind = kind == GuildCompoundKind.GuildMedal ? ItemKind.GuildMedal : ItemKind.GuardianGem;
            foreach (var row in ladder.Rows.Where(row => row.Compoundable))
            {
                int next = ladder.NextGrade(row.Grade)!.Value;
                var candidates = resources.Items.GuildCompoundCandidates(itemKind, next);
                That(candidates.Count > 0 &&
                    candidates.All(id => resources.Items.Get(id) is { } template && template.Kind == itemKind && template.Rarity == next),
                    $"现场{kind}品级{row.Grade}的下一档候选非空且同族同稀有度");
            }
        }
        That(resources.Items.GuildCompoundCandidates(ItemKind.GuardianGem, 9).Count == 0 &&
            resources.Items.GuildCompoundCandidates(ItemKind.GuildMedal, 9).Count == 0,
            "现场不存在稀有度9的公会合成候选，池按实际资源收敛");
    }

    static void Parsing()
    {
        var parsed = GuildCompoundRequests.Read(Body(2, 0, 3, 97));
        That(parsed.Grade == 0 && parsed.Count == 2 && parsed.Slots.SequenceEqual([3, 97]),
            "37B公会合成请求按件数保留list38槽位与档位");
        That(GuildCompoundRequests.Read(Body(4, 6, 0, 1, 2, 3)).Slots.SequenceEqual([0, 1, 2, 3]),
            "四件材料按发送顺序保留全部引用");
        foreach (uint count in new uint[] { 0x8000_0000, uint.MaxValue })
        {
            var oversized = Body(2, 0, 0, 1);
            BinaryPrimitives.WriteUInt32LittleEndian(oversized.AsSpan(13), count);
            Reject<InvalidDataException>(() => GuildCompoundRequests.Read(oversized),
                "Oversized material counts must use the malformed-request rejection path");
        }
        var looseUnused = Body(2, 0, 0, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(looseUnused.AsSpan(29), 0);
        foreach (var body in new byte[][]
        {
            [], new byte[36], new byte[38], Body(1, 0, 0, 1), Body(5, 0, 0, 1, 2, 3, 4),
            Body(2, 0x8000_0000, 0, 1), Body(2, 0, 0, 98), Body(2, 0, 0, 0xFFFF_FFFF), looseUnused
        })
            Reject<InvalidDataException>(() => GuildCompoundRequests.Read(body),
                "公会合成请求拒绝长度、件数、档位、引用越界与未用位非0xFFFFFFFF");
    }

    static void Ladders()
    {
        var medals = GuildCompoundLadder.Parse(Ladder, "guild flag compound info");
        That(medals.Rows.Select(row => (row.Grade, row.MaxCount, row.RatePerMaterial)).SequenceEqual(
                [(0, 4, 2500), (1, 4, 2000), (2, 4, 1000), (3, 4, 500), (6, 2, 0)]),
            "勋章阶梯保留PVF品级、件数上限与每件万分率");
        That(medals.NextGrade(0) == 1 && medals.NextGrade(1) == 2 && medals.NextGrade(2) == 3 &&
            medals.NextGrade(3) == 6 && medals.NextGrade(6) is null && medals.NextGrade(9) is null,
            "勋章阶梯按相邻行推进下一档，末档与未知档不产出");
        That(medals.Recipe(0)!.SuccessRateFor(2) == 5000 && medals.Recipe(0)!.SuccessRateFor(4) == 10000 &&
            medals.Recipe(6)!.SuccessRateFor(2) == 0 && !medals.Recipe(6)!.Compoundable,
            "成功率=每件万分率×件数并封顶10000，末档不可合成");
        var gems = GuildCompoundLadder.Parse(Ladder, "compound info");
        That(gems.Rows.Select(row => row.Grade).SequenceEqual([0, 1, 2, 3]) &&
            gems.NextGrade(2) == 3 && gems.NextGrade(3) is null,
            "守护珠阶梯到3档结束，末段结束标记拼写不影响解析");
        var capped = GuildCompoundLadder.Parse("[compound info]\n0 4 6000 1 2 0\n[/compound info]", "compound info");
        That(capped.Recipe(0)!.SuccessRateFor(4) == 10000, "万分率乘件数超过10000时封顶");
        var catalog = new GuildDungeonCompoundCatalog(_ => Ladder);
        That(catalog.Ladder(GuildCompoundKind.GuildMedal).Rows.Count == 5 &&
            catalog.Ladder(GuildCompoundKind.GuardianGem).Rows.Count == 4,
            "合成目录按两族取各自阶梯");
        foreach (string invalid in new[]
        {
            "",
            "[compound info]\n0 4\n[/compound info]",
            "[compound info]\n0 4 two 1 2 0\n[/compound info]",
            "[compound info]\n1 4 2000 0 2 0\n[/compound info]",
            "[compound info]\n0 4 2000 0 2 0\n[/compound info]",
            "[compound info]\n0 4 2000 1 2 1\n[/compound info]",
            "[compound info]\n0 4 0 1 2 0\n[/compound info]",
            "[compound info]\n0 5 2000 1 2 0\n[/compound info]",
            "[compound info]\n0 4 10001 1 2 0\n[/compound info]",
            "[compound info]\n-1 4 2000 1 2 0\n[/compound info]"
        })
            Reject<InvalidDataException>(() => GuildCompoundLadder.Parse(invalid, "compound info"),
                "阶梯拒绝空表、行宽、非整数、升降序、概率与件数越界");
    }

    static void Transactions()
    {
        var fixture = new Fixture();
        var pair = fixture.Character(Item(1, ItemKind.GuildMedal, 0, Medal1), Item(2, ItemKind.GuildMedal, 1, Medal1));
        var request = new GuildCompoundRequest(1, [0, 1]);
        var success = fixture.Service(null, Rolls(0, 0)).Apply(pair, GuildCompoundKind.GuildMedal, request,
            out var successReceipt, out var successRejection);
        That(success.Accepted && successRejection is null && successReceipt is
            { Kind: GuildCompoundKind.GuildMedal, InputGrade: 1, InputCount: 2, NextGrade: 2, SuccessRate: 4000,
                Success: true, ProductItemId: Medal2 },
            "勋章合成成功回执含档位、成功率与下一档产物");
        That(success.Plan!.After.Find(new(InventoryListType.GuildMedal, 0))?.Core.ItemId == Medal2 &&
            success.Plan.After.Find(new(InventoryListType.GuildMedal, 1)) is null,
            "两枚材料同事务删除并在腾出的槽位放入产物");

        var same = fixture.Service(null, Rolls(4000, 0)).Apply(pair, GuildCompoundKind.GuildMedal, request,
            out var sameReceipt, out var sameRejection);
        That(same.Accepted && sameRejection is null && sameReceipt is
            { InputGrade: 1, InputCount: 2, NextGrade: 1, SuccessRate: 4000, Success: false, ProductItemId: Medal1 } &&
            same.Plan!.After.Items.Length == 1 &&
            same.Plan.After.Find(new(InventoryListType.GuildMedal, 0))?.Core.ItemId == Medal1,
            "未抽中升档时消耗材料并发放同级随机勋章，不是失败");

        foreach (int count in new[] { 2, 3, 4 })
        {
            var slots = Enumerable.Range(0, count).ToImmutableArray();
            var materials = slots.Select(slot => Item(slot + 1, ItemKind.GuildMedal, (short)slot, Medal1)).ToArray();
            materials[0] = materials[0] with { Core = materials[0].Core with { Attr = 12 } };
            var input = fixture.Character(materials);
            var combined = fixture.Service(null, Rolls(0, 0)).Apply(input,
                GuildCompoundKind.GuildMedal, new(1, slots), out var combinedReceipt, out _);
            That(combined.Accepted && combinedReceipt is { ProductItemId: Medal2 } &&
                combined.Plan!.After.Items.Length == 1,
                $"{count} medals accept reinforced inputs and grant one product");
            var sameGrade = fixture.Service(null, Rolls(9999, 0)).Apply(input, GuildCompoundKind.GuildMedal,
                new(1, slots), out var sameGradeReceipt, out var sameGradeRejection);
            That(sameGrade.Accepted && sameGradeRejection is null && sameGradeReceipt is
                { Success: false, NextGrade: 1, ProductItemId: Medal1 } &&
                sameGrade.Plan!.After.Items.Length == 1,
                $"{count}枚同档勋章未升档时仍产出一枚同级勋章并扣料");
            materials[0] = materials[0] with { Core = materials[0].Core with { GuardianGemKeys = 1 } };
            Rejected("material-socketed", fixture, fixture.Character(materials), GuildCompoundKind.GuildMedal,
                new(1, slots), Rolls());

            var gemInput = fixture.Character(Item(1, ItemKind.GuardianGem, 49, Gem0, 20));
            var gemRequest = new GuildCompoundRequest(0, Enumerable.Repeat(49, count).ToImmutableArray());
            var gemResult = fixture.Service(null, Rolls(0, 0)).Apply(gemInput,
                GuildCompoundKind.GuardianGem, gemRequest, out _, out _);
            That(gemResult.Accepted &&
                gemResult.Plan!.After.Find(new(InventoryListType.GuildMedal, 49))?.Core.Count == 20 - count,
                $"{count} gems from one stack consume exactly the selected quantity");
            var sameGem = fixture.Service(null, Rolls(9999, 0)).Apply(gemInput, GuildCompoundKind.GuardianGem,
                gemRequest, out var sameGemReceipt, out var sameGemRejection);
            That(sameGem.Accepted && sameGemRejection is null && sameGemReceipt is
                { Success: false, NextGrade: 0, ProductItemId: Gem0 } &&
                sameGem.Plan!.After.Find(new(InventoryListType.GuildMedal, 49))?.Core.Count == 21 - count,
                $"{count}枚同档守护珠未升档时仍产出一枚同级守护珠并扣料");
        }

        var four = fixture.Character(
            Item(1, ItemKind.GuildMedal, 0, Medal0), Item(2, ItemKind.GuildMedal, 1, Medal0),
            Item(3, ItemKind.GuildMedal, 2, Medal0), Item(4, ItemKind.GuildMedal, 3, Medal0));
        var capped = fixture.Service(null, Rolls(9999, 0)).Apply(four, GuildCompoundKind.GuildMedal,
            new(0, [0, 1, 2, 3]), out var cappedReceipt, out _);
        That(capped.Accepted && cappedReceipt is { SuccessRate: 10000, Success: true, ProductItemId: Medal1 },
            "四件材料把勋章成功率拉满，9999仍成功并抽到下一档唯一产物");

        var gem = fixture.Character(Item(1, ItemKind.GuardianGem, 49, Gem0, 3));
        var duplicate = fixture.Service(null, Rolls(0, 0)).Apply(gem, GuildCompoundKind.GuardianGem,
            new(0, [49, 49]), out var duplicateReceipt, out _);
        That(duplicate.Accepted && duplicateReceipt is { InputCount: 2, NextGrade: 1, ProductItemId: Gem1 } &&
            duplicate.Plan!.After.Find(new(InventoryListType.GuildMedal, 49))?.Core.Count == 1,
            "守护珠同一槽重复引用按次数各扣一件，产物按池首项发放");
        var ordered = fixture.Service(null, Rolls(0, 1)).Apply(gem, GuildCompoundKind.GuardianGem,
            new(0, [49, 49]), out var orderedReceipt, out _);
        That(ordered.Accepted && orderedReceipt!.ProductItemId == Gem1B,
            "候选按.lst首次出现顺序等概率抽取");

        Rejected("material-page", fixture, fixture.Character(
                Item(1, ItemKind.GuildMedal, 0, Medal0), Item(2, ItemKind.GuardianGem, 49, Gem0, 1)),
            GuildCompoundKind.GuildMedal, new(0, [0, 49]), Rolls(0, 0));
        Rejected("material-duplicate", fixture, pair,
            GuildCompoundKind.GuildMedal, new(1, [0, 0]), Rolls(0, 0));
        Rejected("material-template", fixture, fixture.Character(
                Item(1, ItemKind.GuildMedal, 0, Medal1), Item(2, ItemKind.GuildMedal, 1, Medal0)),
            GuildCompoundKind.GuildMedal, new(0, [0, 1]), Rolls(0, 0));
        Rejected("material-state", fixture, fixture.Character(
                Item(1, ItemKind.GuildMedal, 0, Medal0) with { Core = new(Medal0, ItemKind.GuildMedal, 1) { EquipmentLockId = 5 } },
                Item(2, ItemKind.GuildMedal, 1, Medal0)),
            GuildCompoundKind.GuildMedal, new(0, [0, 1]), Rolls(0, 0));
        Rejected("material-state", fixture, fixture.Character(
                Item(1, ItemKind.GuildMedal, 0, Medal0) with { Core = new(Medal0, ItemKind.GuildMedal, 1) { ExpireTime = 1 } },
                Item(2, ItemKind.GuildMedal, 1, Medal0)),
            GuildCompoundKind.GuildMedal, new(0, [0, 1]), Rolls(0, 0), now: 100);
        Rejected("material-stack", fixture, fixture.Character(
                Item(1, ItemKind.GuildMedal, 0, Medal0) with { Core = new(Medal0, ItemKind.GuildMedal, 2) },
                Item(2, ItemKind.GuildMedal, 1, Medal0)),
            GuildCompoundKind.GuildMedal, new(0, [0, 1]), Rolls(0, 0));
        Rejected("invalid-count", fixture, fixture.Character(
                Item(1, ItemKind.GuardianGem, 49, Gem0, 1), Item(2, ItemKind.GuardianGem, 50, Gem0, 1),
                Item(3, ItemKind.GuardianGem, 51, Gem0, 1)),
            GuildCompoundKind.GuardianGem, new(0, [49, 50, 51]), Rolls(0, 0), catalog: fixture.Narrow);
        var legendary = fixture.Character(
            Item(1, ItemKind.GuildMedal, 0, Medal6), Item(2, ItemKind.GuildMedal, 1, Medal6));
        var rerolled = fixture.Service(null, Rolls(9999, 0)).Apply(legendary,
            GuildCompoundKind.GuildMedal, new(6, [0, 1]), out var legendaryReceipt, out var legendaryRejection);
        That(rerolled.Accepted && legendaryRejection is null &&
            legendaryReceipt is { Success: true, NextGrade: 6, ProductItemId: Medal6 } &&
            rerolled.Plan!.After.Items.Length == 1 &&
            rerolled.Plan.After.Items[0].Core.ItemId == Medal6,
            "Two legendary medals produce one random legendary medal even with a zero upgrade rate");
        var topGem = fixture.Character(Item(1, ItemKind.GuardianGem, 49, Gem3, 3));
        var rerolledGem = fixture.Service(null, Rolls(0, 0)).Apply(topGem, GuildCompoundKind.GuardianGem,
            new(3, [49, 49]), out var rerolledGemReceipt, out var rerolledGemRejection);
        That(rerolledGem.Accepted && rerolledGemRejection is null &&
            rerolledGemReceipt is { Success: true, NextGrade: 3, ProductItemId: Gem3 } &&
            rerolledGem.Plan!.After.Find(new(InventoryListType.GuildMedal, 49))?.Core.Count == 2,
            "两枚末档（神器）守护珠必出同级随机守护珠");
        Rejected("invalid-grade", fixture, fixture.Character(),
            GuildCompoundKind.GuardianGem, new(7, [49, 49]), Rolls(0, 0));
        Rejected("candidate-pool", fixture, gem,
            GuildCompoundKind.GuardianGem, new(0, [49, 49]), Rolls(0, 0), catalog: fixture.Missing);
        Rejected("roll", fixture, pair,
            GuildCompoundKind.GuildMedal, new(1, [0, 1]), Rolls(10000));

        var full = fixture.FullList38();
        var noSpace = fixture.Service(null, Rolls(0, 0)).Apply(full, GuildCompoundKind.GuardianGem,
            new(0, [49, 49]), out var noSpaceReceipt, out var noSpaceReason);
        That(noSpace is { Accepted: false, Error: 95, Plan: null } && noSpaceReceipt is null &&
            noSpaceReason == "guild-compound-capacity" &&
            full.Inventory.Find(new(InventoryListType.GuildMedal, 49))?.Core.Count == 3,
            "扣减后仍无空位时回95，原快照不扣材料");
    }

    static void Rejected(string reason, Fixture fixture, Character character,
        GuildCompoundKind kind, GuildCompoundRequest request, Func<long, long> random,
        GuildDungeonCompoundCatalog? catalog = null, long? now = null)
    {
        var result = fixture.Service(catalog, random).Apply(character, kind, request,
            out var receipt, out var rejection,
            now is null ? null : DateTimeOffset.FromUnixTimeSeconds(now.Value));
        That(result is { Accepted: false, Error: 1, Plan: null } && receipt is null && rejection == reason,
            $"公会合成按{reason}拒绝且不产生扣料计划");
    }

    static void Packets()
    {
        Bytes(GuildCompoundPackets.Success(54321), [1, 0x31, 0xD4, 0, 0],
            "成功回执是1字节标志加u32产物编号");
        Bytes(GuildCompoundPackets.Failure(95), [0, 95, 0], "失败回执是1字节标志加u16错误码");
        Bytes(GuildCompoundPackets.Failure(1), [0, 1, 0], "普通拒绝回错误码1");
    }

    static byte[] Body(int count, uint grade, params uint[] references)
    {
        var body = new byte[GuildCompoundRequests.BodySize];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(13), checked((uint)count));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(17), grade);
        for (int index = 0; index < 4; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(21 + index * 4),
                index < references.Length ? references[index] : 0xFFFF_FFFF);
        return body;
    }

    static Func<long, long> Rolls(params long[] values)
    {
        var queue = new Queue<long>(values);
        return _ => queue.Dequeue();
    }

    static InventoryItem Item(int uid, ItemKind kind, short slot, int itemId, int value = 1) =>
        new(uid, new(InventoryListType.GuildMedal, slot), new(itemId, kind, value));

    sealed class Fixture
    {
        readonly GoldLimitCatalog limits = new("[gold limit from level]\n0 1000000\n[/gold limit from level]");
        readonly EpicPieceCatalog pieces = new("[piece list]\n50000 50001\n[/piece list]");
        public ItemCatalog Items { get; }
        public GuildDungeonCompoundCatalog Compounds { get; }
        public GuildDungeonCompoundCatalog Narrow { get; }
        public GuildDungeonCompoundCatalog Missing { get; }

        public Fixture()
        {
            var equipment = new Dictionary<int, string>
            {
                [Medal0] = "character/common/flag/medal0.equ",
                [Medal1] = "character/common/flag/medal1.equ",
                [Medal2] = "character/common/flag/medal2.equ",
                [Medal3] = "character/common/flag/medal3.equ",
                [Medal6] = "character/common/flag/medal6.equ"
            };
            var stackable = new Dictionary<int, string>
            {
                [Gem0] = "flaggem/gem0.stk",
                [Gem1] = "flaggem/gem1.stk",
                [Gem1B] = "flaggem/gem1b.stk",
                [Gem2] = "flaggem/gem2.stk",
                [Gem3] = "flaggem/gem3.stk"
            };
            Items = new(null!, equipment, stackable, 0);
            Items.PrefillTemplates(new[]
            {
                Template(Medal0, ItemKind.GuildMedal, 0, 50), Template(Medal1, ItemKind.GuildMedal, 1, 50),
                Template(Medal2, ItemKind.GuildMedal, 2, 52), Template(Medal3, ItemKind.GuildMedal, 3, 54),
                Template(Medal6, ItemKind.GuildMedal, 6, 55), Template(Gem0, ItemKind.GuardianGem, 0),
                Template(Gem1, ItemKind.GuardianGem, 1), Template(Gem1B, ItemKind.GuardianGem, 1),
                Template(Gem2, ItemKind.GuardianGem, 2), Template(Gem3, ItemKind.GuardianGem, 3)
            }.ToDictionary(template => template.Id));
            Compounds = new(_ => Ladder);
            Narrow = new(_ => "[compound info]\n0 2 5000 1 2 0\n[/compound info]\n" +
                "[guild flag compound info]\n0 2 5000 1 2 0\n[/guild flag compound info]");
            Missing = new(_ => "[compound info]\n0 4 5000 9 2 0\n[/compound info]\n" +
                "[guild flag compound info]\n0 4 5000 9 2 0\n[/guild flag compound info]");
        }

        public InventoryGuildCompoundService Service(GuildDungeonCompoundCatalog? compounds, Func<long, long> random) =>
            new(Items, compounds ?? Compounds, limits, pieces, random);

        public Character Character(params InventoryItem[] items) =>
            new(1, 0, "Guild", 0, new(1, 0, 10, 10), null) { Job = 0, Level = 95, Inventory = new(items.ToImmutableArray()) };

        public Character FullList38()
        {
            var items = Enumerable.Range(0, 98).Select(slot => slot < 49
                ? Item(slot + 1, ItemKind.GuildMedal, checked((short)slot), Medal0)
                : Item(slot + 1, ItemKind.GuardianGem, checked((short)slot), Gem0, slot == 49 ? 3 : 1)).ToArray();
            return Character(items);
        }

        static ItemTemplate Template(int id, ItemKind kind, int rarity, int grade = 0) =>
            new(id, kind, kind == ItemKind.GuildMedal ? (byte)32 : (byte)0, 1, 0,
                kind == ItemKind.GuildMedal ? 1 : 1000, 0, rarity, grade,
                "[trade]", ushort.MaxValue, 0, "item", kind == ItemKind.GuardianGem ? "[flag gem] 0" : "");
    }
}
