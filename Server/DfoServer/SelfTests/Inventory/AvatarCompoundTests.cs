using System.Buffers.Binary;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class AvatarCompoundTests
{
    public static void RunCore()
    {
        int checks = Count;
        var fixture = new Fixture();
        var request = Request(1000, 1001, 500);
        byte[] requestBody = Body(request);
        That(AvatarCompoundRequests.Read(requestBody) == request && requestBody.Length == AvatarCompoundRequests.BodySize,
            "CMD99按固定22字节解析材料槽、双源回显、目标、能力及两个尾字段");
        foreach (byte[] malformed in new byte[][] { requestBody[..^1], [.. requestBody, 0] })
            Reject<InvalidDataException>(() => AvatarCompoundRequests.Read(malformed), "CMD99拒绝截断和附加正文");

        var config = fixture.Compounds.ForCharacterJob(0);
        That(config.Grade == 1 && config.UpperGrade == 2 && config.MaterialItemId == 500 &&
            config.UpperMaterialItemId == 501 && config.MasterMaterialItemId == 502 &&
            config.Parts.Count == 8 && config.Parts["hat"].RarePool.Select(row => row.ItemId).SequenceEqual([3000, 3001]) &&
            config.Parts["hat"].TargetPool.Single().ItemId == 2000, "token目录只读取根级三档材料并严格拆分稀有与目标候选");
        var badWeight = fixture.Tokens.ToArray();
        int weight = Array.FindIndex(badWeight, token => token is { Type: 0, Value: 10 });
        badWeight[weight] = new(0, 0);
        Reject<InvalidDataException>(() => AvatarCompoundCatalog.Parse(1, "bad-weight",
            new PvfTokenDocument(badWeight, fixture.Resolve), fixture.Resolve), "零权重候选不能进入抽取目录");
        Reject<InvalidDataException>(() => AvatarCompoundCatalog.Parse(1, "duplicate-grade",
            new PvfTokenDocument([.. fixture.Tokens, fixture.Tokens[0], new PvfToken(0, 1)], fixture.Resolve), fixture.Resolve),
            "重复根级必需字段拒绝，不采用首项或末项覆盖");

        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        int draws = 0;
        var service = fixture.Service(bound => { draws++; return bound - 1; });
        var character = fixture.Character(1000, 1001, 500);
        var result = service.Apply(character, request, out var receipt, out var rejection, now);
        That(result.Accepted && rejection is null && receipt is
            { Part: "hat", MaterialItemId: 500, RareRate: 200, RareRoll: 9999, HitRare: false } &&
            receipt.Outputs is [{ ItemId: 2000 }] && draws == 1, "混合品级按基础加高级概率一半计算且未命中只抽一次");
        var after = result.Plan!.After;
        That(character.Inventory.Find(request.Source0) is not null && after.Find(request.Source0) is null &&
            after.Find(request.Source1) is null && after.Find(request.Material)?.Core.Count == 1,
            "纯计划不改原快照，并在同一After中扣双源与一份材料");
        var acceptedReceipt = receipt ?? throw new InvalidOperationException("合成成功测试缺少业务回执");
        var output = after.Find(acceptedReceipt.Outputs[0].Position)!;
        That(output is { Uid: 0, Core.ItemId: 2000, Core.Durability: 3, Avatar: not null } &&
            output.Avatar.Color1 == 0 && output.Avatar.Socket0 == AvatarSocket.Empty(1) &&
            output.Avatar.Socket1 == AvatarSocket.Empty(2), "产出创建独立完整Core和AvatarDetail，不继承源染色或徽章");
        That(result.Plan.CharacterPrecondition!.Matches(character, now.ToUnixTimeSeconds()) &&
            !result.Plan.CharacterPrecondition.Matches(character with { Revision = 1 }, now.ToUnixTimeSeconds()),
            "合成计划绑定角色revision，旧快照不能提交或重抽");

        byte[] ack = AvatarCompoundPackets.Success(request, [output], now.ToUnixTimeSeconds());
        That(ack.Length == AvatarCompoundPackets.SuccessSize && ack[0] == 1 && ack[1] == 3 &&
            ack[2] == 1 && I16(ack, 3) == 10 && I32(ack, 5) == 1 &&
            ack[9] == 1 && I16(ack, 10) == 11 && ack[16] == 0 && I16(ack, 17) == 121,
            "成功ACK固定携带时装双源及Main材料三条扣除记录");
        That(I16(ack, 23) == output.Position.Index && I32(ack, 25) == 2000 && I32(ack, 29) == 0 &&
            U16(ack, 33) == 3 && I32(ack, 35) == 30 && U16(ack, 39) == 1 && U16(ack, 45) == 2 &&
            I32(ack, 69) == 4 && I16(ack, 77) == -1 && U16(ack, 87) == 3 &&
            Convert.ToHexString(AvatarCompoundPackets.Failure()) == "001600",
            "成功ACK两项各54字节并以-1完整占位第二项，失败固定返回安全错误22");

        var normal = fixture.Service(_ => 9999).Apply(fixture.Character(1000, 1000, 500),
            Request(1000, 1000, 500), out var normalReceipt, out _, now);
        var upper = fixture.Service(_ => 9999).Apply(fixture.Character(1001, 1001, 500),
            Request(1001, 1001, 500), out var upperReceipt, out _, now);
        var upperMaterial = fixture.Service(_ => 9999).Apply(fixture.Character(1000, 1001, 501),
            Request(1000, 1001, 501), out var upperMaterialReceipt, out _, now);
        var masterMaterial = fixture.Service(_ => 9999).Apply(fixture.Character(1000, 1001, 502),
            Request(1000, 1001, 502), out var masterMaterialReceipt, out _, now);
        That(normal.Accepted && upper.Accepted && upperMaterial.Accepted && masterMaterial.Accepted &&
            normalReceipt!.RareRate == 100 && upperReceipt!.RareRate == 300 &&
            upperMaterialReceipt!.RareRate == 400 && masterMaterialReceipt!.RareRate == 600,
            "普通/混合/高级组合与三档材料分别应用0、200、400万分比加成");

        var firstRare = fixture.Service(Rolls(0, 0)).Apply(character, request, out var firstRareReceipt, out _, now);
        var secondRare = fixture.Service(Rolls(0, 10)).Apply(character, request, out var secondRareReceipt, out _, now);
        That(firstRare.Accepted && secondRare.Accepted && firstRareReceipt is { HitRare: true, Outputs.Length: 2 } &&
            firstRareReceipt.Outputs[1].ItemId == 3000 && secondRareReceipt!.Outputs[1].ItemId == 3001,
            "命中稀有后按正权重边界抽取第二产出，不重抽或替换所选普通目标");
        var boundaryHit = fixture.Service(Rolls(199, 0)).Apply(character, request, out var boundaryHitReceipt, out _, now);
        var boundaryMiss = fixture.Service(Rolls(200)).Apply(character, request, out var boundaryMissReceipt, out _, now);
        That(boundaryHit.Accepted && boundaryHitReceipt!.HitRare &&
            boundaryMiss.Accepted && !boundaryMissReceipt!.HitRare,
            "万分比命中使用严格小于：混合概率200在199命中、200不命中");

        var full = fixture.Character(1000, 1001, 500, fillAvatar: true);
        var fullResult = fixture.Service(_ => 9999).Apply(full, request, out var fullReceipt, out _, now);
        That(fullResult.Accepted && fullReceipt!.Outputs.Single().Position.Index == 10,
            "时装栏原本满载时先释放输入，再按动态容量复用源槽放置产出");
        var fullRare = fixture.Service(Rolls(0, 0)).Apply(full, request, out var fullRareReceipt, out _, now);
        That(fullRare.Accepted && fullRareReceipt!.Outputs.Select(row => row.Position.Index)
            .SequenceEqual(new short[] { 10, 11 }),
            "满栏命中稀有时同时复用两个源槽，第二产出不绕过动态容量");

        var expiring = fixture.Character(1000, 1001, 500, sourceExpiry: (int)now.ToUnixTimeSeconds() + 5);
        var expiringResult = fixture.Service(_ => 9999).Apply(expiring, request, out _, out _, now);
        That(expiringResult.Accepted && expiringResult.Plan!.CharacterPrecondition!.Matches(expiring, now.ToUnixTimeSeconds() + 4) &&
            !expiringResult.Plan.CharacterPrecondition.Matches(expiring, now.ToUnixTimeSeconds() + 5),
            "源实例期限在提交前复验，等于截止秒即拒绝旧计划");

        foreach (var invalid in new (Character Character, AvatarCompoundRequest Request)[]
        {
            (character, request with { SourceSlot1 = request.SourceSlot0 }),
            (character, request with { SourceItemId0 = 9999 }),
            (fixture.Character(1000, 2100, 500), Request(1000, 2100, 500)),
            (fixture.Character(1000, 1001, 503), Request(1000, 1001, 503)),
            (character, request with { TargetItemId = 2100 }),
            (character, request with { Ability = 4 }),
            (character, request with { Ability = 256 }),
            (character, request with { TargetIsRare = 1 }),
            (fixture.Character(1000, 1001, 500, sourceLocked: true), request),
            (fixture.Character(1000, 1001, 500, materialLocked: true), request),
            (fixture.Character(1000, 1001, 500, sourceExpiry: (int)now.ToUnixTimeSeconds()), request),
            (fixture.Character(1000, 1001, 500, materialExpiry: (int)now.ToUnixTimeSeconds()), request),
            (fixture.Character(1000, 1001, 500, omitSecondDetail: true), request)
        })
        {
            var denied = fixture.Service(_ => 0).Apply(invalid.Character, invalid.Request, out var deniedReceipt,
                out var deniedReason, now);
            That(denied is { Accepted: false, Error: 22, Plan: null } && deniedReceipt is null &&
                deniedReason is not null, "伪造、跨部位、未知材料/目标/能力、锁定、到期或缺详情均原子拒绝");
        }
        Console.WriteLine($"PASS {Count - checks} 装扮合成资源、概率、领域事务及99号协议断言");
    }

    public static void RunResources(ItemResources resources)
    {
        var config = resources.AvatarCompounds.ForCharacterJob(0);
        That(config.Parts.Count == 8 && config.RareRates.Keys.Order().SequenceEqual(AvatarCompoundCatalog.PartNames.Order()) &&
            config.UpperRareRates.Keys.Order().SequenceEqual(AvatarCompoundCatalog.PartNames.Order()),
            "现场compoundavatar职业配置完整提供八部位两组概率");
        foreach (var pool in config.Parts.Values)
        {
            foreach (var candidate in pool.RarePool.Concat(pool.TargetPool))
            {
                var template = resources.Items.Get(candidate.ItemId);
                That(template.Kind == ItemKind.Avatar &&
                    AvatarCompoundCatalog.Part(template.EquipmentSlot) == pool.Part,
                    "现场加权候选均解析为对应部位时装模板");
            }
        }
        foreach (int id in new[] { config.MaterialItemId, config.UpperMaterialItemId, config.MasterMaterialItemId })
            That(resources.Items.Get(id).Kind is ItemKind.Consumable or ItemKind.Material,
                "现场三档合成材料均是可扣除堆叠模板");
    }

    static AvatarCompoundRequest Request(int source0, int source1, int material) =>
        new(121, 10, source0, 11, source1, 2000, 3, 0, (byte)(material - 500));

    static byte[] Body(AvatarCompoundRequest request)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(request.MaterialSlot);
        writer.Write(request.SourceSlot0);
        writer.Write(request.SourceItemId0);
        writer.Write(request.SourceSlot1);
        writer.Write(request.SourceItemId1);
        writer.Write(request.TargetItemId);
        writer.Write(request.Ability);
        writer.Write(request.TargetIsRare);
        writer.Write(request.MaterialType);
        return stream.ToArray();
    }

    static Func<long, long> Rolls(params long[] values)
    {
        var queue = new Queue<long>(values);
        return _ => queue.Dequeue();
    }

    static short I16(byte[] body, int at) => BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(at));
    static ushort U16(byte[] body, int at) => BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at));
    static int I32(byte[] body, int at) => BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(at));

    sealed class Fixture
    {
        readonly Dictionary<int, string> strings = [];
        readonly Dictionary<string, int> indexes = new(StringComparer.Ordinal);
        readonly IReadOnlyDictionary<int, string> equipment;
        readonly IReadOnlyDictionary<int, string> stackable;
        public ItemCatalog Items { get; }
        public AvatarCompoundCatalog Compounds { get; }
        public AvatarPackageCatalog Packages { get; }
        public PvfToken[] Tokens { get; }

        public Fixture()
        {
            equipment = new[] { 1000, 1001, 2000, 2100, 3000, 3001 }.ToDictionary(id => id, id => id + ".equ");
            stackable = new[] { 500, 501, 502, 503 }.ToDictionary(id => id, id => id + ".stk");
            Tokens = ConfigTokens();
            Compounds = new(
                path => path == AvatarCompoundCatalog.RootPath ? "1 `compound/test.etc`" : throw new InvalidDataException(path),
                path => path == "etc/compound/test.etc" ? Tokens : throw new InvalidDataException(path),
                index => strings[index],
                path => path == "etc/compound/test.etc");
            Items = new(null!, equipment, stackable, 0);
            var templates = new Dictionary<int, ItemTemplate>
            {
                [1000] = Avatar(1000, 0, 1, "normal"),
                [1001] = Avatar(1001, 0, 2, "upper"),
                [2000] = Avatar(2000, 0, 2, "target") with { AvatarSockets = new(1, 2, 4, 0, 0) },
                [2100] = Avatar(2100, 1, 2, "hair"),
                [3000] = Avatar(3000, 0, 3, "rare-a") with { AvatarSockets = new(16, 16, 0, 0, 0) },
                [3001] = Avatar(3001, 0, 3, "rare-b") with { AvatarSockets = new(239, 239, 0, 0, 0) },
                [500] = Material(500), [501] = Material(501), [502] = Material(502), [503] = Material(503)
            };
            Items.PrefillTemplates(templates);
            Packages = new(_ => "[avatar select ability]\n3 `[HP MAX]` `+` 100\n[/avatar select ability]",
                equipment, stackable);
        }

        public InventoryAvatarCompoundService Service(Func<long, long> random) =>
            new(Items, Compounds, Packages, random);
        public string Resolve(int index) => strings[index];

        public Character Character(int firstId, int secondId, int materialId, bool fillAvatar = false,
            bool sourceLocked = false, int sourceExpiry = int.MaxValue, bool omitSecondDetail = false,
            bool materialLocked = false, int materialExpiry = 0)
        {
            var first = AvatarItem(11, 10, firstId, sourceExpiry, sourceLocked, false);
            var second = AvatarItem(12, 11, secondId, sourceExpiry, false, omitSecondDetail);
            var rows = new List<InventoryItem> { first, second,
                new(20, new(InventoryListType.Main, 121), new(materialId, ItemKind.Material, 2)
                    { EquipmentLockId = materialLocked ? (byte)1 : (byte)0, ExpireTime = materialExpiry }) };
            if (fillAvatar)
                for (short slot = 0; slot <= 104; slot++)
                {
                    if (slot is 10 or 11) continue;
                    long uid = 1000 + slot;
                    rows.Add(new(uid, new(InventoryListType.Avatar, slot), new(2000, ItemKind.Avatar, checked((int)uid)))
                        { Avatar = new() });
                }
            return new(1, 0, "Compound", 0, new(1, 0, 10, 10), null)
                { Job = 0, Level = 95, Inventory = new([.. rows]) { AvatarExpansion = fillAvatar ? (ushort)0 : (ushort)105 } };
        }

        InventoryItem AvatarItem(long uid, short slot, int itemId, int expiry, bool locked, bool omitDetail)
        {
            var item = new InventoryItem(uid, new(InventoryListType.Avatar, slot),
                new(itemId, ItemKind.Avatar, checked((int)uid))
                    { Durability = 9, EquipmentLockId = locked ? (byte)1 : (byte)0 });
            return omitDetail ? item : item with
            {
                Avatar = new()
                {
                    ExpireTime = expiry,
                    Color1 = 9,
                    Socket0 = new(8, 490000001)
                }
            };
        }

        PvfToken[] ConfigTokens()
        {
            var tokens = new List<PvfToken>();
            Scalar(tokens, "grade", 1);
            Scalar(tokens, "upper grade", 2);
            Rates(tokens, "rare rate", 100);
            Rates(tokens, "upper rare rate", 200);
            Block(tokens, "material", [I(1), I(500), I(1)]);
            Block(tokens, "material_upper", [I(1), I(501), I(1)]);
            Block(tokens, "material_master", [I(1), I(502), I(1)]);
            foreach (string part in AvatarCompoundCatalog.PartNames)
                Block(tokens, part + " avatar", [I(2), I(3000), I(10), I(3001), I(20), I(2000), I(1000)]);
            tokens.Add(Tag("common bind cube"));
            Block(tokens, "material", [I(1), I(999999), I(1)]);
            tokens.Add(Tag("/common bind cube"));
            return tokens.ToArray();
        }

        void Rates(List<PvfToken> tokens, string name, int start)
        {
            var values = new List<PvfToken>();
            for (int index = 0; index < AvatarCompoundCatalog.PartNames.Length; index++)
            {
                values.Add(Str(AvatarCompoundCatalog.PartNames[index]));
                values.Add(I(start + index));
            }
            Block(tokens, name, values);
        }

        void Scalar(List<PvfToken> tokens, string name, int value)
        {
            tokens.Add(Tag(name));
            tokens.Add(I(value));
        }

        void Block(List<PvfToken> tokens, string name, IEnumerable<PvfToken> values)
        {
            tokens.Add(Tag(name));
            tokens.AddRange(values);
            tokens.Add(Tag("/" + name));
        }

        PvfToken Tag(string value) => new(3, String("[" + value + "]"));
        PvfToken Str(string value) => new(6, String(value));
        static PvfToken I(int value) => new(0, value);
        int String(string value)
        {
            if (indexes.TryGetValue(value, out int index)) return index;
            index = indexes.Count + 1;
            indexes.Add(value, index);
            strings.Add(index, value);
            return index;
        }

        static ItemTemplate Avatar(int id, byte slot, int grade, string name) =>
            new(id, ItemKind.Avatar, slot, 1, 0, 1, 0, 0, grade, "[trade]", ushort.MaxValue, 0, name);
        static ItemTemplate Material(int id) =>
            new(id, ItemKind.Material, 0, 1, 0, 999, 0, 0, 0, "[trade]", ushort.MaxValue, 0, "material");
    }
}
