using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class PromotionRuneConsumerTests
{
    const int Source = 880002001, EquipmentLeaf = 880002002;
    const int RuneA = 880002101, RuneB = 880002102, RuneC = 880002103, Stone = 880002201;
    static readonly int[] RealBoxes =
    [
        10310783, 10310784, 10310786, 10310946, 10311365, 490014161,
        10310785, 10310787, 10311013, 10311014,
        10313591, 10313592, 10313593, 10313594, 10313595, 10313596, 10313597, 10313598
    ];

    public static void RunResources(ItemResources resources)
    {
        var shipped = JsonSerializer.Deserialize<PromotionConfiguration[]>(ServerRuntime.ReadShippedPromotionConfig())
            ?? throw new InvalidDataException("shipped promotion missing");
        var original = shipped[0];
        int sequence = original.Rules.Tiers[0].Sequence;
        foreach (int id in RealBoxes)
        {
            That(resources.Boosters.Get(id) is { Available: true, Lottery: true, RuneBox: not null },
                "真实符文/护石箱有正式族档定义 " + id);
            // 只复制内存配置，保持正式商品、其余档位及溢出根；不改运营JSON。
            var rules = original.Rules with
            {
                Tiers = original.Rules.Tiers.Select(tier => tier.Sequence == sequence ? tier with { ItemId = id } : tier).ToImmutableArray()
            };
            var catalog = ServerRuntime.LoadCashShopPromotions(resources,
                JsonSerializer.Serialize(new[] { original with { Enabled = true, Rules = rules } }));
            var entry = catalog.Activities.Single();
            That(entry.Availability == PromotionAvailability.Active,
                "正式loader连接真实符文/护石箱全部品级和typed消费者 " + id + ": " + entry.Error);
        }
        RunIsolated(resources);
    }

    static void RunIsolated(ItemResources resources)
    {
        // typed定义沿真实模板复制，只改隔离ID；不手编技能、阵营、转职或孔位资格。
        var rune0 = resources.Items.Rune(resources.Items.RuneCandidates(0)[0])!;
        var rune1 = resources.Items.Rune(resources.Items.RuneCandidates(1)[0])!;
        var stone = resources.Items.Talisman(resources.Items.TalismanCandidates(2)[0])!;
        Fixture New() => new(resources, rune0, rune1, stone);

        var valid = New();
        That(valid.Validate() is null, "隔离真实API枚举两个正权重品级与全部符文叶，不抽样");
        var talisman = New();
        talisman.Definitions[Source] = new([], null) { Lottery = true, RuneBox = (1, 0) };
        talisman.SetTable(Table((1, 0), new(10000, [((byte)2, 1)])));
        That(talisman.Validate() is null, "隔离护石箱连接同一typed护石owner");

        var missing = New();
        missing.SetTable(new(new Dictionary<(int, int), RuneRandomChooseRow>().ToFrozenDictionary()));
        Reject(missing, "rune_box_table", "缺family/tier表项");
        var empty = New();
        empty.SetRunePool(new Dictionary<byte, ImmutableArray<int>> { [0] = [RuneA], [1] = [] });
        Reject(empty, "rune_box_empty", "正权重第二品级空池");
        var zero = New();
        zero.SetTable(Table((0, 0), new(10000, [((byte)0, 1), ((byte)6, 0)])));
        That(zero.Validate() is null, "零权重不存在品级不产生消费者边，也不要求该品级非空");

        var missingRune = New();
        missingRune.Runes.TryRemove(RuneC, out _);
        // RuneC位于第二个正权重品级的第二项，不能仅验证首品级/首候选。
        Reject(missingRune, "rune_definition", "非首品级非首候选缺typed定义", RuneC);
        var wrongRune = New();
        var originalRune = wrongRune.Runes[RuneC].Value;
        wrongRune.Runes[RuneC] = new(() => originalRune with { Item = originalRune.Item with { Id = RuneA } });
        Reject(wrongRune, "rune_definition", "typed Rune.Item与实际候选模板不符", RuneC);

        var missingStone = New();
        missingStone.Definitions[Source] = new([], null) { Lottery = true, RuneBox = (1, 0) };
        missingStone.SetTable(Table((1, 0), new(10000, [((byte)2, 1)])));
        missingStone.Talismans.TryRemove(Stone, out _);
        Reject(missingStone, "talisman_definition", "护石缺typed定义", Stone);
        var placeholder = New();
        placeholder.Definitions[Source] = new([], null) { Lottery = true, RuneBox = (1, 0) };
        placeholder.SetTable(Table((1, 0), new(10000, [((byte)2, 1)])));
        var originalStone = placeholder.Talismans[Stone].Value;
        placeholder.Talismans[Stone] = new(() => originalStone with { SkillIndex = null });
        Reject(placeholder, "talisman_definition", "护石占位物没有最终穿戴消费者", Stone);

        var ordinary = New();
        ordinary.Definitions[Source] = new([new(1, [new(RuneA, 1, 1)])], Lottery: true);
        Reject(ordinary, "lottery_reward_not_main", "普通Lottery不借RuneBox专用容器46路径放行", RuneA);
    }

    static void Reject(Fixture fixture, string reason, string label, int? itemId = null)
    {
        string? error = fixture.Validate();
        That(error is not null && error.Contains(reason, StringComparison.Ordinal) &&
            (itemId is null || error.Contains(itemId.Value.ToString(), StringComparison.Ordinal)),
            label + "保留精确消费者拒绝: " + error);
    }

    static RuneRandomChooseTable Table((int Family, int Tier) key, RuneRandomChooseRow row) =>
        new(new Dictionary<(int, int), RuneRandomChooseRow> { [key] = row }.ToFrozenDictionary());

    sealed class Fixture
    {
        readonly ItemResources resources;
        public ItemCatalog Items { get; }
        public ConcurrentDictionary<int, BoosterDefinition> Definitions { get; }
        public ConcurrentDictionary<int, Lazy<RuneDefinition>> Runes { get; }
        public ConcurrentDictionary<int, Lazy<TalismanDefinition>> Talismans { get; }
        readonly BoosterCatalog boosters;
        readonly AvatarPackageCatalog avatars;
        readonly CashShopCatalog shop;
        readonly PromotionRules rules;

        public Fixture(ItemResources resources, RuneDefinition rune0, RuneDefinition rune1, TalismanDefinition stone)
        {
            this.resources = resources;
            var archive = Field<PvfArchive>(resources.Items, "pvf");
            Items = new(archive, new Dictionary<int, string>(), new Dictionary<int, string>(), 100);
            var a = rune0 with { Item = rune0.Item with { Id = RuneA } };
            var b = rune1 with { Item = rune1.Item with { Id = RuneB } };
            var c = rune1 with { Item = rune1.Item with { Id = RuneC } };
            var t = stone with { Item = stone.Item with { Id = Stone } };
            var source = new ItemTemplate(Source, ItemKind.Consumable, 0, 1, 0, 1000,
                0, 0, 0, "[free]", ushort.MaxValue, 0, "符文箱隔离源", "[legacy] 0");
            var equipment = new ItemTemplate(EquipmentLeaf, ItemKind.Equipment, 12, 1, 0, 1,
                0, 0, 0, "[free]", ushort.MaxValue, 0, "合法装备控制叶");
            Items.PrefillTemplates(new[] { source, equipment, a.Item, b.Item, c.Item, t.Item }
                .Select(item => KeyValuePair.Create(item.Id, item)));
            Items.PrefillRunes([a, b, c]);
            Items.PrefillTalismans([t]);
            Runes = Field<ConcurrentDictionary<int, Lazy<RuneDefinition>>>(Items, "runes");
            Talismans = Field<ConcurrentDictionary<int, Lazy<TalismanDefinition>>>(Items, "talismans");
            SetTable(Table((0, 0), new(5000, [((byte)0, 1), ((byte)1, 1)])));
            SetRunePool(new Dictionary<byte, ImmutableArray<int>> { [0] = [RuneA], [1] = [RuneB, RuneC] });
            Set(Items, "talismanPool", new Lazy<FrozenDictionary<int, ImmutableArray<int>>>(() =>
                new Dictionary<int, ImmutableArray<int>> { [2] = [Stone] }.ToFrozenDictionary()));
            boosters = new(archive, new Dictionary<int, string>());
            Definitions = Field<ConcurrentDictionary<int, BoosterDefinition>>(boosters, "definitions");
            Definitions[Source] = new([], null) { Lottery = true, RuneBox = (0, 0) };
            avatars = new(_ => throw new InvalidDataException("隔离符文用例不应读取装扮资源"),
                new Dictionary<int, string>(), new Dictionary<int, string>());
            shop = CashShopCatalog.Parse($"[item]\n1 {Source} 1 0 0 100 0 0 0\n");
            rules = new(new("rune-fixture", "period", "v1", "default", "package"),
                10, 3, 31, EquipmentLeaf, 1, 5000, [new(1, Source, 1)], [new(2, EquipmentLeaf, 1)]);
        }

        public void SetTable(RuneRandomChooseTable table) => Set(Items, "runeRandomChoices", new Lazy<RuneRandomChooseTable>(() => table));
        public void SetRunePool(Dictionary<byte, ImmutableArray<int>> pool) =>
            Set(Items, "runePool", new Lazy<FrozenDictionary<byte, ImmutableArray<int>>>(() => pool.ToFrozenDictionary()));

        public string? Validate() => new PromotionConsumerValidation(Items, boosters, avatars, shop,
            resources.EpicPieces, resources.Contracts, resources.RandomOptions, resources.AttributePairs,
            resources.Creatures, resources.CollectBoxes, resources.LimitCubes, resources.Enchantments,
            resources.Enhancements, resources.RecoveryConsumables, resources.Amplifications, resources.AmplifyUpgrades).Validate(rules);
    }

    // 仅隔离实例反射注入；正式ItemResources及其lazy/candidate缓存绝不修改。
    static T Field<T>(object owner, string name) => (T)owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    static void Set<T>(object owner, string name, T value) => owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
}
