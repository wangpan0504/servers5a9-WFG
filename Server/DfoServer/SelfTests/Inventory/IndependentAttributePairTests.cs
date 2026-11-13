using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class IndependentAttributePairTests
{
    const int BoosterId = 490705394;
    const int TicketId = 490705395;
    const int LegacyBooster = 900001;
    const int LegacyTicket = 900002;
    static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    static readonly JsonSerializerOptions JsonOptions = new()
    { Converters = { new JsonStringEnumConverter<AttributePairEntryKind>() } };

    public static void RunResources(ItemResources resources)
    {
        var policy = IndependentAttributePairCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Resources", "independent-attribute-pairs.json"),
            resources.Items, resources.Boosters, resources.LimitCubes, resources.Enchantments, resources.EpicPieces, resources.Contracts,
            id => CurrencyWallet.RewardKind(id) is not null, useBundledDefault: true);
        That(policy.ValidatedFamilies == 1 && policy.Resolve(BoosterId, AttributePairEntryKind.Booster).Policy is { MaximumBeads: 66000 } &&
            policy.Resolve(TicketId, AttributePairEntryKind.LimitCube).Kind == AttributePairResolutionKind.RequiredValidated,
            "实际11基向量、66卡完整token与两个来源共同通过加载，模板堆叠必要上界为66000");
        foreach (var pair in new[] { (First: 0, Second: 0), (First: 4, Second: 4), (First: 10, Second: 0) })
        {
            var random = new DrawRandom(i => i == 0 ? pair.First : pair.Second);
            var debug = new DfoServer.Game.Settings.GameDebugSettings { IgnoreCashShopAbsoluteExpiration = true };
            var booster = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits, resources.EpicPieces,
                random: random, debugSettings: debug, contracts: resources.Contracts, attributePairs: policy);
            var role = Role([new(1, new(InventoryListType.Main, 85), Core(BoosterId))]);
            int expected = ExpectedOrderedBeads[pair.First * 11 + pair.Second];
            var result = booster.Open(role, new(85, 1), out var receipt, Now);
            That(result.Accepted && receipt!.Rewards.SequenceEqual(new[] { new BoosterGrantedItem(expected, 1) }) &&
                random.Bounds.SequenceEqual(new long[] { 11, 11 }), "实际394资源保持真实两抽及既有单珠结果");
            random = new DrawRandom(i => i == 0 ? pair.First : pair.Second);
            var cubes = new InventoryLimitCubeService(resources.Items, resources.LimitCubes, resources.EpicPieces, resources.Contracts,
                resources.Boosters.DirectActivationItems, random.NextInt64, debug, policy);
            role = Role([new(1, new(InventoryListType.Main, 85), Core(TicketId)),
                new(2, new(InventoryListType.Main, 95), Core(LimitCubeTests.TargetId))]);
            result = cubes.Apply(role, new(95, LimitCubeTests.TargetId, 85), out var cube, out _, Now);
            That(result.Accepted && cube!.ResultItemId == expected && random.Bounds.SequenceEqual(new long[] { 11, 11 }) &&
                result.Plan!.After.AccountWallet.Get(CurrencyKind.ClearCube) == 0, "实际395资源保持原账号无色10及独立两抽结果");
        }
        var bundled = IndependentAttributePairCatalog.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "rules.json"),
            resources.Items, resources.Boosters, resources.LimitCubes, resources.Enchantments, resources.EpicPieces, resources.Contracts,
            id => CurrencyWallet.RewardKind(id) is not null, useBundledDefault: true);
        That(bundled.ValidatedFamilies == 1, "实际PVF与单文件内嵌默认规则完整匹配");
        var service = new InventoryLimitCubeService(resources.Items, resources.LimitCubes, resources.EpicPieces, resources.Contracts,
            resources.Boosters.DirectActivationItems, _ => 0, new() { IgnoreCashShopAbsoluteExpiration = true }, bundled);
        var definition = resources.LimitCubes.Get(TicketId)!;
        foreach (var target in definition.Targets)
        {
            var role = RerollRole(target.ItemId);
            var result = service.Apply(role, new(82, target.ItemId, 84), out var receipt, out _, Now);
            That(result.Accepted && definition.Results.Any(row => row.ItemId == receipt!.ResultItemId) &&
                result.Plan!.After.Find(new(InventoryListType.Main, 84))?.Core.Count == 2 &&
                result.Plan.After.AccountWallet.Get(CurrencyKind.ClearCube) == 0,
                "实际券覆盖全部66种目标，含现场319，三张券只扣一张并扣十无色");
        }
        var original = RerollRole(490705319);
        foreach (var (role, targetId, reason) in new[]
        {
            (original with { Inventory = original.Inventory with
                { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.ClearCube, 9) } }, 490705319, "materials"),
            (RerollRole(BoosterId), BoosterId, "target-condition"),
            (ChangeTarget(original, core => core with { EquipmentLockId = 1 }), 490705319, "item-state"),
            (ChangeTarget(original, core => core with { ExpireTime = checked((int)Now.ToUnixTimeSeconds() - 1) }), 490705319, "item-state")
        })
        {
            string before = JsonSerializer.Serialize(role);
            var result = service.Apply(role, new(82, targetId, 84), out var receipt, out var rejection, Now);
            That(!result.Accepted && result.Plan is null && receipt is null && rejection == reason &&
                JsonSerializer.Serialize(role) == before, "实际资源材料不足、不匹配、安全锁和过期仍无消耗拒绝");
        }
        foreach (int petId in new[] { 400990444, 400990445, 400990446, 400990447 })
        {
            var template = resources.Items.Get(petId);
            var pet = InventoryRewardGrantService.CreateItem(template, new(InventoryListType.Pet, 0), Core(petId)) with
                { Uid = 2, Core = Core(petId) with { Value = 2, ExpireTime = 0 } };
            var role = Role([new(1, new(InventoryListType.Main, 82), Core(490705319)), pet]);
            var result = new InventoryEnchantmentService(resources.Items, resources.Enchantments).Plan(role,
                new(new(InventoryListType.Main, 82), pet.Position, 0), Now.ToUnixTimeSeconds());
            That(result.Accepted && result.Plan!.After.Find(pet.Position)?.Core.EnchantCardId == 490705457 &&
                result.Plan.After.Find(new(InventoryListType.Main, 82)) is null,
                "现场319宝珠在四个PVF成年宠物白名单目标上均可右键安装，不依赖重掷策略");
        }
        Character RerollRole(int targetId) => Role([new(1, new(InventoryListType.Main, 84), Core(TicketId) with { Value = 3 }),
            new(2, new(InventoryListType.Main, 82), Core(targetId))]);
        static Character ChangeTarget(Character role, Func<ItemCore, ItemCore> change) => role with
        {
            Inventory = role.Inventory with { Items = role.Inventory.Items.Select(row => row.Position.Index == 82
                ? row with { Core = change(row.Core) } : row).ToImmutableArray() }
        };
        ItemCore Core(int id) => InventoryRewardGrantService.Create(resources.Items.Get(id), utcNow: Now, shopExpiration: true);
        Character Role(ImmutableArray<InventoryItem> rows) => new(1, 0, "PairResource", 0, new(1, 0, 10, 10), null)
        { Level = 95, Inventory = new(rows) { Expansion = 24, AccountWallet = AccountWallet.Empty.Set(CurrencyKind.ClearCube, 10) } };
    }

    public static void RunCore()
    {
        int checks = Count;
        var fixture = new Fixture();
        That(fixture.Policy.ValidatedFamilies == 1, "完整11基向量与66张原始卡可原子装配同一必需族");
        for (int first = 0; first < 11; first++)
            for (int second = 0; second < 11; second++)
            {
                int ordinal = first * 11 + second;
                int expected = ExpectedOrderedBeads[ordinal];
                var random = new DrawRandom(i => i == 0 ? first : second);
                var role = fixture.BoosterCharacter();
                string before = JsonSerializer.Serialize(role);
                var opened = fixture.BoosterService(random).Open(role, new(85, 1), out var bead, Now);
                That(opened.Accepted && bead is not null && bead.Rewards.SequenceEqual(new[] { new BoosterGrantedItem(expected, 1) }) &&
                    random.Bounds.SequenceEqual(new long[] { 11, 11 }) && bead.SelectionTrace!.SequenceEqual(new[] { (byte)ordinal }) &&
                    bead.SelectionSummary is { BeadCount: 1, Bound: 11, DrawCalls: 2, TraceBytes: 1 } &&
                    opened.Plan!.After.Find(new(InventoryListType.Main, 85)) is null &&
                    opened.Plan.After.AccountWallet == role.Inventory.AccountWallet && JsonSerializer.Serialize(role) == before,
                    "394每个有序样本均真实两抽、消耗一盒并只产出独立预期表中的一珠");
                if (ordinal == 0)
                    That(BoosterPackets.Opened(opened, bead).Length == 26 && !JsonSerializer.Serialize(bead).Contains("SelectionTrace"),
                        "394抽样诊断不进入26B单奖励ACK且trace禁止自动JSON输出");
                random = new DrawRandom(i => i == 0 ? first : second);
                role = fixture.CubeCharacter();
                before = JsonSerializer.Serialize(role);
                var rerolled = fixture.CubeService(random.NextInt64).Apply(role, new(95, LimitCubeTests.TargetId, 85),
                    out var reroll, out var reason, Now);
                That(rerolled.Accepted && reason is null && reroll is { ResultCount: 1, TotalWeight: 121 } &&
                    reroll.ResultItemId == expected && reroll.Roll == ordinal &&
                    random.Bounds.SequenceEqual(new long[] { 11, 11 }) && reroll.SelectionTrace!.SequenceEqual(new[] { (byte)ordinal }) &&
                    rerolled.Plan!.After.AccountWallet.Get(CurrencyKind.ClearCube) == 0 &&
                    rerolled.Plan.After.Find(new(InventoryListType.Main, 85))?.Core.Count == 1 &&
                    rerolled.Plan.After.Find(new(InventoryListType.Main, 95)) is null && JsonSerializer.Serialize(role) == before,
                    "395全部121有序输入保持原目标、券1及账号无色10成本，允许重掷原珠");
                if (ordinal == 0)
                    That(LimitCubePackets.Success(reroll!).Length == 12 && !JsonSerializer.Serialize(reroll).Contains("SelectionTrace"),
                        "395保持12B ACK且内部有序轨迹不自动序列化");
            }
        Batch(fixture);
        FailClosed(fixture);
        BundledConfiguration(fixture);
        InvalidFamilies();
        Console.WriteLine($"PASS {Count - checks} 独立属性两抽、完整卡向量、批量与必需策略失败闭合断言");
    }

    static void BundledConfiguration(Fixture fixture)
    {
        string directory = Path.Combine(Path.GetTempPath(), "attribute-pairs-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "rules.json");
        IndependentAttributePairCatalog Load() => IndependentAttributePairCatalog.Load(path, fixture.Items,
            fixture.Boosters, fixture.Cubes, fixture.Enchantments, fixture.Pieces, fixture.Contracts,
            id => CurrencyWallet.RewardKind(id) is not null, useBundledDefault: true);
        try
        {
            That(Load().ValidatedFamilies == 1, "单文件无Resources目录时使用同版本内嵌规则并完成全部资源校验");
            Directory.CreateDirectory(directory);
            That(Load().ValidatedFamilies == 1, "外部目录存在但规则文件缺失时仍可加载内嵌规则");
            File.WriteAllText(path, "{");
            That(Load().ValidatedFamilies == 0, "外部坏配置不能通过内嵌规则回退绕过失败闭合");
            File.WriteAllText(path, Change(fixture.Json, root => root[0]!["Enabled"] = false));
            That(Load().ValidatedFamilies == 0, "外部显式禁用优先于内嵌默认规则");
            File.WriteAllText(path, fixture.Json);
            using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            That(Load().ValidatedFamilies == 0, "外部文件不可读时不能回退内嵌规则");
        }
        finally { if (Directory.Exists(directory)) TestFiles.DeleteDirectory(directory, true); }
    }

    static void Batch(Fixture fixture)
    {
        foreach (int count in new[] { 1, 64, 65, 1000, 1001 })
        {
            var random = new DrawRandom(i => count == 1001 && i == 2001 ? 1 : 0);
            var result = fixture.BoosterService(random).Open(fixture.BoosterCharacter(count), new(85, count), out var receipt, Now);
            var expected = count == 1001 ? new[] { new BoosterGrantedItem(490705315, 1000), new BoosterGrantedItem(490705316, 1) }
                : new[] { new BoosterGrantedItem(490705315, count) };
            That(result.Accepted && receipt!.Rewards.SequenceEqual(expected) && random.Bounds.Count == 2 * count &&
                random.Bounds.All(bound => bound == 11) && receipt.SelectionTrace!.Length == count &&
                receipt.SelectionSummary!.DrawCalls == 2 * count,
                "394跨64/65及资源1000上限仍逐颗两抽，合成超堆1001原可成功分支保留");
        }
        var maximum = new DrawRandom(_ => 0);
        var capacity = fixture.BoosterService(maximum).Open(fixture.BoosterCharacter(66000), new(85, 66000), out var absent, Now);
        That(!capacity.Accepted && capacity.Error == 4 && capacity.Plan is null && absent is null && maximum.Bounds.Count == 132000 &&
            maximum.Bounds.All(bound => bound == 11), "66000为必要条件上界，真实完成132000次抽样后仍保留原容量拒绝");
        foreach (int count in new[] { 66001, int.MaxValue })
        {
            var random = new DrawRandom(_ => 0);
            var role = fixture.BoosterCharacter(count);
            string before = JsonSerializer.Serialize(role);
            var result = fixture.BoosterService(random).Open(role, new(85, count), out var receipt, Now);
            That(!result.Accepted && result.Error == 4 && result.Plan is null && receipt is null && random.Bounds.Count == 0 &&
                JsonSerializer.Serialize(role) == before, "超过66种珠堆叠上限之和的i32请求在RNG前按原容量错误拒绝");
        }
        var invalid = new DrawRandom(_ => 11);
        That(!fixture.BoosterService(invalid).Open(fixture.BoosterCharacter(), new(85, 1), out var bad, Now).Accepted &&
            bad is null && invalid.Bounds.Count == 1, "坏RNG立即拒绝，不映射、补足调用或自动重抽");
        string summary = JsonSerializer.Serialize(new AttributePairSelectionSummary("independent-attribute-pair", new string('x', 64),
            66000, 11, 132000, 66000, new string('F', 64)));
        That(System.Text.Encoding.UTF8.GetByteCount(summary) <= 512, "固定字段最坏选择摘要保持512B预算");
    }

    static void FailClosed(Fixture fixture)
    {
        var invalidConfigurations = new[]
        {
            "{", "[]", Change(fixture.Json, root => root[0]!["Enabled"] = false),
            Change(fixture.Json, root => root[0]!["Sources"]!.AsArray().RemoveAt(1)),
            Change(fixture.Json, root => root[0]!["Sources"]![0]!["Entry"] = "LimitCube"),
            Change(fixture.Json, root => root[0]!["PolicyIdentity"] = new string('x', 65)),
            Change(fixture.Json, root => root[0]!["Pairs"]!.AsArray().RemoveAt(0)),
            Change(fixture.Json, root => root[0]!["Pairs"]![1] = root[0]!["Pairs"]![0]!.DeepClone()),
            Change(fixture.Json, root => root[0]!["Pairs"]![0]!["CardId"] = 1),
            Change(fixture.Json, root => root[0]!["Pairs"]![0]!["Index"] = 2),
            Change(fixture.Json, root => root[0]!["LimitedPetIds"]![0] = 1),
            Change(fixture.Json, root => root[0]!["Bases"]![4]!["Values"]!.AsArray().RemoveAt(1))
        };
        var missing = IndependentAttributePairCatalog.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"),
            fixture.Items, fixture.Boosters, fixture.Cubes, fixture.Enchantments, fixture.Pieces, fixture.Contracts,
            id => CurrencyWallet.RewardKind(id) is not null);
        var cases = invalidConfigurations.Select(fixture.Parse).Prepend(missing)
            .Prepend(IndependentAttributePairCatalog.RequiredOnly).Select(policy => (Policy: policy, UseDefault: false))
            .Prepend((IndependentAttributePairCatalog.RequiredOnly, true));
        foreach (var (policy, useDefault) in cases)
        {
            var random = new DrawRandom(_ => 0);
            var role = fixture.BoosterCharacter();
            string before = JsonSerializer.Serialize(role);
            var result = fixture.BoosterService(random, policy, useDefault).Open(role, new(85, 1), out var receipt, Now);
            That(!result.Accepted && result.Error == 19 && result.RejectReason == "attribute_pair_policy_unavailable" &&
                result.Plan is null && receipt is null && random.Bounds.Count == 0 && JsonSerializer.Serialize(role) == before,
                "必需394策略缺失、坏配置或遗漏注入均零抽样、无消耗规划且不回退旧池");
            role = fixture.CubeCharacter();
            before = JsonSerializer.Serialize(role);
            result = fixture.CubeService(random.NextInt64, policy, useDefault).Apply(role, new(95, LimitCubeTests.TargetId, 85),
                out var cube, out var reason, Now);
            That(!result.Accepted && result.Error == 1 && reason == "selection-policy" && result.Plan is null &&
                cube is null && random.Bounds.Count == 0 && JsonSerializer.Serialize(role) == before,
                "必需395族任一失败均零抽样、无资产规划，不因另一入口有效而部分装配");
        }
        var legacy = new DrawRandom(_ => 3);
        That(fixture.BoosterService(legacy, useDefault: true).Open(fixture.BoosterCharacter(id: LegacyBooster),
            new(85, 1), out var bead, Now).Accepted && bead!.Rewards[0].ItemId == 490705316 &&
            legacy.Bounds.SequenceEqual(new long[] { 4 }), "未注册普通礼盒保留原一次权重抽样");
        legacy = new DrawRandom(_ => 3);
        That(fixture.CubeService(legacy.NextInt64, useDefault: true).Apply(fixture.CubeCharacter(LegacyTicket),
            new(95, LimitCubeTests.TargetId, 85), out var cubeReceipt, out _, Now).Accepted &&
            cubeReceipt!.ResultItemId == 490705316 && legacy.Bounds.SequenceEqual(new long[] { 4 }),
            "未注册普通变更券保留原权重及材料路径");
        That(fixture.Policy.Resolve(BoosterId, AttributePairEntryKind.LimitCube).Kind == AttributePairResolutionKind.RequiredUnavailable,
            "必需身份以正确入口解析，错误入口不能成为普通来源");
    }

    static void InvalidFamilies()
    {
        foreach (string mutation in new[] { "tag", "arity", "value", "extra", "token", "index", "diagnostic", "pets" })
        {
            var fixture = new Fixture();
            int card = Rows.Single(row => row.First == 4 && row.Second == 4).Card;
            var effect = fixture.Effects[card][0];
            if (mutation is "tag" or "arity" or "value" or "extra")
                fixture.Effects[card] = mutation switch
                {
                    "tag" => [effect with { Tag = "[unknown attack]" }],
                    "arity" => [effect with { IntegerValues = [60] }],
                    "value" => [effect with { IntegerValues = [60, 59] }],
                    _ => [effect, effect]
                };
            if (mutation is "token" or "index" or "diagnostic")
                fixture.MutateTokens = (id, tokens) =>
                {
                    if (id != card) return;
                    if (mutation == "token") tokens[4] = new(4, 60);
                    if (mutation == "index") tokens[2] = new(0, 2);
                    if (mutation == "diagnostic") tokens.Add(new(3, 5));
                };
            if (mutation == "pets")
                fixture.Files["stackable/bead-490705315.stk"] = fixture.Files["stackable/bead-490705315.stk"].Replace("400990444", "400990448");
            // Get缓存只影响普通珠身份；宠物限制用独立目录，严格效果每次只读取所需卡。
            var enchantments = mutation == "pets" ? new EnchantmentCatalog(path => fixture.Files[path], fixture.Paths, fixture.Document) : fixture.Enchantments;
            var policy = fixture.Parse(fixture.Json, enchantments);
            That(policy.Resolve(BoosterId, AttributePairEntryKind.Booster).Kind == AttributePairResolutionKind.RequiredUnavailable &&
                policy.Resolve(TicketId, AttributePairEntryKind.LimitCube).Kind == AttributePairResolutionKind.RequiredUnavailable,
                "完整tag、双值向量、token、index、块结构或四宠任一错误使整个族不可用");
        }
    }

    static string Change(string json, Action<JsonArray> mutate)
    {
        var root = JsonNode.Parse(json)!.AsArray();
        mutate(root);
        return root.ToJsonString();
    }

    internal sealed class DrawRandom(Func<int, long> next) : Random
    {
        public List<long> Bounds { get; } = [];
        public override long NextInt64(long maxValue)
        {
            Bounds.Add(maxValue);
            return next(Bounds.Count - 1);
        }
    }

    internal sealed class Fixture
    {
        public ItemCatalog Items { get; }
        public BoosterCatalog Boosters { get; }
        public LimitCubeCatalog Cubes { get; }
        public EnchantmentCatalog Enchantments { get; }
        public EpicPieceCatalog Pieces { get; } = new("[piece list]\n60000 70001\n[/piece list]");
        public PremiumContractCatalog Contracts { get; } = new(new Dictionary<int, PremiumContract>());
        public IndependentAttributePairCatalog Policy { get; }
        public string Json { get; }
        public Dictionary<int, string> Paths { get; } = [];
        public Dictionary<string, string> Files { get; } = [];
        public Dictionary<int, ImmutableArray<EnchantmentEffect>> Effects { get; } = [];
        public Action<int, List<PvfToken>>? MutateTokens { get; set; }

        public Fixture()
        {
            foreach (var row in Rows)
            {
                Paths[row.Bead] = $"bead-{row.Bead}.stk";
                Paths[row.Card] = $"card-{row.Card}.stk";
                Effects[row.Card] = row.Effects;
                Files["stackable/" + Paths[row.Bead]] = $"[stackable type]\n`[enchant waste]` 0\n[monster card id]\n{row.Card}\n[enchant index]\n0\n[bead limited usable item]\n400990444 400990445 400990446 400990447\n";
                Files["stackable/" + Paths[row.Card]] = "[stackable type]\n`[material expert job]` 1\n[string data]\n`card` `[creature]`\n[/string data]\n[enchant table]\n[enchant index]\n0\n" +
                    string.Join('\n', row.Effects.Select(effect => effect.Tag + "\n" + string.Join(' ', effect.IntegerValues))) +
                    "\n[/enchant index]\n[/enchant table]\n";
            }
            Paths[TicketId] = "ticket.stk";
            Paths[LegacyTicket] = "legacy-ticket.stk";
            Files["stackable/ticket.stk"] = CubeText(string.Join('\n', Rows.Select(row => $"{row.Bead} 1 1000")));
            Files["stackable/legacy-ticket.stk"] = CubeText("490705315 1 3\n490705316 1 1");
            Cubes = new(path => Files[path], Paths);
            Enchantments = new(path => Files[path], Paths, Document);
            Items = new(null!, new Dictionary<int, string>(), Paths, 0);
            Items.PrefillTemplates(Rows.Select(row => row.Bead).Concat(new[] { BoosterId, TicketId, LegacyBooster, LegacyTicket })
                .Distinct().Select(id => new KeyValuePair<int, ItemTemplate>(id,
                    new(id, ItemKind.Consumable, 0, 1, 0, 1000, 0, 0, 0, "[free]", ushort.MaxValue, 0, id.ToString()))));
            Boosters = (BoosterCatalog)RuntimeHelpers.GetUninitializedObject(typeof(BoosterCatalog));
            var definitions = new ConcurrentDictionary<int, BoosterDefinition>();
            definitions[BoosterId] = new([new(1, [.. Rows.Select(row => new BoosterReward(row.Bead, 1000, 1))])]);
            definitions[LegacyBooster] = new([new(1, [new(490705315, 3, 1), new(490705316, 1, 1)])]);
            typeof(BoosterCatalog).GetField("definitions", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Boosters, definitions);
            typeof(BoosterCatalog).GetField("<DirectActivationItems>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Boosters, Array.Empty<int>().ToFrozenSet());
            Json = JsonSerializer.Serialize(new[] { new AttributePairConfiguration("golden-pet-bead", "golden-pet-bead.11x11.v1", true,
                [new(BoosterId, AttributePairEntryKind.Booster), new(TicketId, AttributePairEntryKind.LimitCube)], Bases,
                [.. Rows.Select(row => new AttributePairMapping(row.First, row.Second, row.Bead, row.Card, 0))],
                [400990444, 400990445, 400990446, 400990447]) }, JsonOptions);
            Policy = Parse(Json);
        }

        public IndependentAttributePairCatalog Parse(string json) => Parse(json, Enchantments);
        public IndependentAttributePairCatalog Parse(string json, EnchantmentCatalog enchantments) =>
            IndependentAttributePairCatalog.Parse(json, Items, Boosters, Cubes, enchantments, Pieces, Contracts,
                id => CurrencyWallet.RewardKind(id) is not null);

        public PvfTokenDocument Document(string path)
        {
            int card = Paths.Single(row => "stackable/" + row.Value == path).Key;
            var strings = new List<string> { "[enchant table]", "[enchant index]", "[/enchant index]", "[/enchant table]", "unused", "[/enchant table]" };
            var tokens = new List<PvfToken> { new(3, 0), new(3, 1), new(0, 0) };
            foreach (var effect in Effects[card])
            {
                tokens.Add(new(3, strings.Count));
                strings.Add(effect.Tag);
                tokens.AddRange(effect.IntegerValues.Select(value => new PvfToken(0, value)));
            }
            tokens.Add(new(3, 2));
            tokens.Add(new(3, 3));
            MutateTokens?.Invoke(card, tokens);
            return new(tokens, index => strings[index]);
        }

        public InventoryBoosterService BoosterService(Random random, IndependentAttributePairCatalog? policy = null, bool useDefault = false) =>
            new(Items, Boosters, new GoldLimitCatalog("[gold limit from level]\n0 1000000000\n[/gold limit from level]"), Pieces,
                random: random, contracts: Contracts, attributePairs: useDefault ? null : policy ?? Policy);
        public InventoryLimitCubeService CubeService(Func<long, long> draw, IndependentAttributePairCatalog? policy = null, bool useDefault = false) =>
            new(Items, Cubes, Pieces, Contracts, random: draw, attributePairs: useDefault ? null : policy ?? Policy);
        public Character BoosterCharacter(int count = 1, int id = BoosterId) => Role(
            [new(1, new(InventoryListType.Main, 85), new(id, ItemKind.Consumable, count))]);
        public Character CubeCharacter(int id = TicketId) => Role(
            [new(1, new(InventoryListType.Main, 85), new(id, ItemKind.Consumable, 2)),
             new(2, new(InventoryListType.Main, 95), new(LimitCubeTests.TargetId, ItemKind.Consumable, 1))]);
        static Character Role(ImmutableArray<InventoryItem> rows) => new(1, 0, "Pair", 0, new(1, 0, 10, 10), null)
        { Level = 95, Inventory = new(rows) { Expansion = 24, AccountWallet = AccountWallet.Empty.Set(CurrencyKind.ClearCube, 10) } };
        static string CubeText(string results) => "[stackable type]\n`[upgrade limit cube]` 0\n[upgrade limit cube info]\n[a condition item]\n" +
            string.Join('\n', Rows.Select(row => $"{row.Bead} 1")) + "\n[/a condition item]\n[b condition item]\n3037 10\n[result item]\n" +
            results + "\n[/result item]\n[/upgrade limit cube info]\n";
    }

    sealed record SavedRow(int First, int Second, int Bead, int Card, ImmutableArray<EnchantmentEffect> Effects);
    // 下列字面oracle由已冻结的66卡原始文本与独立121表转录，预期不调用生产mapping计算。
    static readonly ImmutableArray<AttributePairBase> Bases =
    [
        new("attribute-0", "[physical attack]", [25]),
        new("attribute-1", "[magical attack]", [25]),
        new("attribute-2", "[physical defense]", [25]),
        new("attribute-3", "[magical defense]", [25]),
        new("attribute-4", "[equipment physical attack]", [30, 30]),
        new("attribute-5", "[equipment magical attack]", [30, 30]),
        new("attribute-6", "[separate attack]", [30, 30]),
        new("attribute-7", "[fire attack]", [8]),
        new("attribute-8", "[water attack]", [8]),
        new("attribute-9", "[dark attack]", [8]),
        new("attribute-10", "[light attack]", [8]),
    ];
    static readonly ImmutableArray<SavedRow> Rows =
    [
        new(0, 0, 490705315, 490703546, [new("[physical attack]", [50])]),
        new(0, 1, 490705316, 490703547, [new("[physical attack]", [25]), new("[magical attack]", [25])]),
        new(0, 2, 490705317, 490703548, [new("[physical attack]", [25]), new("[physical defense]", [25])]),
        new(0, 3, 490705318, 490703549, [new("[physical attack]", [25]), new("[magical defense]", [25])]),
        new(0, 4, 490705319, 490705457, [new("[physical attack]", [25]), new("[equipment physical attack]", [30, 30])]),
        new(0, 5, 490705320, 490705458, [new("[physical attack]", [25]), new("[equipment magical attack]", [30, 30])]),
        new(0, 6, 490705321, 490703552, [new("[physical attack]", [25]), new("[separate attack]", [30, 30])]),
        new(0, 7, 490705322, 490703555, [new("[physical attack]", [25]), new("[fire attack]", [8])]),
        new(0, 8, 490705323, 490703556, [new("[physical attack]", [25]), new("[water attack]", [8])]),
        new(0, 9, 490705324, 490703557, [new("[physical attack]", [25]), new("[dark attack]", [8])]),
        new(0, 10, 490705325, 490703558, [new("[physical attack]", [25]), new("[light attack]", [8])]),
        new(1, 1, 490705326, 490703559, [new("[magical attack]", [50])]),
        new(1, 2, 490705327, 490703560, [new("[magical attack]", [25]), new("[physical defense]", [25])]),
        new(1, 3, 490705328, 490703561, [new("[magical attack]", [25]), new("[magical defense]", [25])]),
        new(1, 4, 490705329, 490705459, [new("[magical attack]", [25]), new("[equipment physical attack]", [30, 30])]),
        new(1, 5, 490705330, 490705460, [new("[magical attack]", [25]), new("[equipment magical attack]", [30, 30])]),
        new(1, 6, 490705331, 490703564, [new("[magical attack]", [25]), new("[separate attack]", [30, 30])]),
        new(1, 7, 490705332, 490703567, [new("[magical attack]", [25]), new("[fire attack]", [8])]),
        new(1, 8, 490705333, 490703568, [new("[magical attack]", [25]), new("[water attack]", [8])]),
        new(1, 9, 490705334, 490703569, [new("[magical attack]", [25]), new("[dark attack]", [8])]),
        new(1, 10, 490705335, 490703570, [new("[magical attack]", [25]), new("[light attack]", [8])]),
        new(2, 2, 490705336, 490703571, [new("[physical defense]", [50])]),
        new(2, 3, 490705337, 490703572, [new("[physical defense]", [25]), new("[magical defense]", [25])]),
        new(2, 4, 490705338, 490705461, [new("[physical defense]", [25]), new("[equipment physical attack]", [30, 30])]),
        new(2, 5, 490705339, 490705462, [new("[physical defense]", [25]), new("[equipment magical attack]", [30, 30])]),
        new(2, 6, 490705340, 490703575, [new("[physical defense]", [25]), new("[separate attack]", [30, 30])]),
        new(2, 7, 490705341, 490703578, [new("[physical defense]", [25]), new("[fire attack]", [8])]),
        new(2, 8, 490705342, 490703579, [new("[physical defense]", [25]), new("[water attack]", [8])]),
        new(2, 9, 490705343, 490703580, [new("[physical defense]", [25]), new("[dark attack]", [8])]),
        new(2, 10, 490705344, 490703581, [new("[physical defense]", [25]), new("[light attack]", [8])]),
        new(3, 3, 490705345, 490703582, [new("[magical defense]", [50])]),
        new(3, 4, 490705346, 490705463, [new("[magical defense]", [25]), new("[equipment physical attack]", [30, 30])]),
        new(3, 5, 490705347, 490705464, [new("[magical defense]", [25]), new("[equipment magical attack]", [30, 30])]),
        new(3, 6, 490705348, 490703585, [new("[magical defense]", [25]), new("[separate attack]", [30, 30])]),
        new(3, 7, 490705349, 490703588, [new("[magical defense]", [25]), new("[fire attack]", [8])]),
        new(3, 8, 490705350, 490703589, [new("[magical defense]", [25]), new("[water attack]", [8])]),
        new(3, 9, 490705351, 490703590, [new("[magical defense]", [25]), new("[dark attack]", [8])]),
        new(3, 10, 490705352, 490703591, [new("[magical defense]", [25]), new("[light attack]", [8])]),
        new(4, 4, 490705353, 490705465, [new("[equipment physical attack]", [60, 60])]),
        new(4, 5, 490705354, 490705466, [new("[equipment physical attack]", [30, 30]), new("[equipment magical attack]", [30, 30])]),
        new(4, 6, 490705355, 490705467, [new("[equipment physical attack]", [30, 30]), new("[separate attack]", [30, 30])]),
        new(4, 7, 490705356, 490705468, [new("[equipment physical attack]", [30, 30]), new("[fire attack]", [8])]),
        new(4, 8, 490705357, 490705469, [new("[equipment physical attack]", [30, 30]), new("[water attack]", [8])]),
        new(4, 9, 490705358, 490705470, [new("[equipment physical attack]", [30, 30]), new("[dark attack]", [8])]),
        new(4, 10, 490705359, 490705471, [new("[equipment physical attack]", [30, 30]), new("[light attack]", [8])]),
        new(5, 5, 490705360, 490705472, [new("[equipment magical attack]", [60, 60])]),
        new(5, 6, 490705361, 490705473, [new("[equipment magical attack]", [30, 30]), new("[separate attack]", [30, 30])]),
        new(5, 7, 490705362, 490705474, [new("[equipment magical attack]", [30, 30]), new("[fire attack]", [8])]),
        new(5, 8, 490705363, 490705475, [new("[equipment magical attack]", [30, 30]), new("[water attack]", [8])]),
        new(5, 9, 490705364, 490705476, [new("[equipment magical attack]", [30, 30]), new("[dark attack]", [8])]),
        new(5, 10, 490705365, 490705477, [new("[equipment magical attack]", [30, 30]), new("[light attack]", [8])]),
        new(6, 6, 490705366, 490703609, [new("[separate attack]", [60, 60])]),
        new(6, 7, 490705367, 490703612, [new("[separate attack]", [30, 30]), new("[fire attack]", [8])]),
        new(6, 8, 490705368, 490703613, [new("[separate attack]", [30, 30]), new("[water attack]", [8])]),
        new(6, 9, 490705369, 490703614, [new("[separate attack]", [30, 30]), new("[dark attack]", [8])]),
        new(6, 10, 490705370, 490703615, [new("[separate attack]", [30, 30]), new("[light attack]", [8])]),
        new(7, 7, 490705371, 490703627, [new("[fire attack]", [16])]),
        new(7, 8, 490705372, 490703628, [new("[fire attack]", [8]), new("[water attack]", [8])]),
        new(7, 9, 490705373, 490703629, [new("[fire attack]", [8]), new("[dark attack]", [8])]),
        new(7, 10, 490705374, 490703630, [new("[fire attack]", [8]), new("[light attack]", [8])]),
        new(8, 8, 490705375, 490703631, [new("[water attack]", [16])]),
        new(8, 9, 490705376, 490703632, [new("[water attack]", [8]), new("[dark attack]", [8])]),
        new(8, 10, 490705377, 490703633, [new("[water attack]", [8]), new("[light attack]", [8])]),
        new(9, 9, 490705378, 490703634, [new("[dark attack]", [16])]),
        new(9, 10, 490705379, 490703635, [new("[dark attack]", [8]), new("[light attack]", [8])]),
        new(10, 10, 490705380, 490703636, [new("[light attack]", [16])]),
    ];
    static readonly int[] ExpectedOrderedBeads =
    [
        490705315, 490705316, 490705317, 490705318, 490705319, 490705320, 490705321, 490705322, 490705323, 490705324, 490705325,
        490705316, 490705326, 490705327, 490705328, 490705329, 490705330, 490705331, 490705332, 490705333, 490705334, 490705335,
        490705317, 490705327, 490705336, 490705337, 490705338, 490705339, 490705340, 490705341, 490705342, 490705343, 490705344,
        490705318, 490705328, 490705337, 490705345, 490705346, 490705347, 490705348, 490705349, 490705350, 490705351, 490705352,
        490705319, 490705329, 490705338, 490705346, 490705353, 490705354, 490705355, 490705356, 490705357, 490705358, 490705359,
        490705320, 490705330, 490705339, 490705347, 490705354, 490705360, 490705361, 490705362, 490705363, 490705364, 490705365,
        490705321, 490705331, 490705340, 490705348, 490705355, 490705361, 490705366, 490705367, 490705368, 490705369, 490705370,
        490705322, 490705332, 490705341, 490705349, 490705356, 490705362, 490705367, 490705371, 490705372, 490705373, 490705374,
        490705323, 490705333, 490705342, 490705350, 490705357, 490705363, 490705368, 490705372, 490705375, 490705376, 490705377,
        490705324, 490705334, 490705343, 490705351, 490705358, 490705364, 490705369, 490705373, 490705376, 490705378, 490705379,
        490705325, 490705335, 490705344, 490705352, 490705359, 490705365, 490705370, 490705374, 490705377, 490705379, 490705380,
    ];
}
