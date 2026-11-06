using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static partial class PromotionAvatarCompoundConsumerTests
{
    static void RunBoundaries(ItemResources resources)
    {
        var fixture = new CompoundBoundaryFixture(resources);
        That(fixture.Validate() is null, "材料仅属第二职业时完整消费者通过");
        That(fixture.Validate(fixture.Source + 1) is null && fixture.Validate(fixture.Source + 2) is null,
            "第二职业高级与大师档材料均连接同一真实消费者");

        fixture = new(resources) { BadGradeJob = 1 };
        That(fixture.Validate() is null, "非关联职业坏grade不阻断当前材料");
        That(fixture.Validate(fixture.PlainEquipment) is null && fixture.TokenReads == 0,
            "无材料图不解析无关职业材料、grade或候选");
        fixture = new(resources) { BadGradeJob = 1, SharedMaterial = true };
        That(fixture.Validate() is not null, "共用材料仍完整校验另一个职业，不只任一成功职业");
        fixture = new(resources) { ShiftedSharedMaterial = true };
        That(fixture.Validate() is not null, "畸形根材料段编号移位仍保留可能关联，不能借另一好职业放行");
        fixture = new(resources) { NestedSharedMaterial = true };
        That(fixture.Validate() is { } nested &&
            nested.Contains("etc/compound/a.etc", StringComparison.Ordinal) &&
            nested.Contains("[material]", StringComparison.Ordinal) &&
            nested.Contains("根级值块", StringComparison.Ordinal),
            "根材料段内嵌套的共享编号仍关联坏职业，并由完整材料解析拒绝");

        fixture = new(resources);
        fixture.Templates[fixture.TargetSecondPart] = fixture.Templates[fixture.TargetSecondPart] with { Jobs = 1 };
        That(fixture.Validate() is not null, "第二职业第二部位目标的职业不符拒绝");
        fixture = new(resources);
        fixture.Templates[fixture.RareSecondPartSecond] = fixture.Templates[fixture.RareSecondPartSecond] with { EquipmentSlot = 0 };
        That(fixture.Validate() is not null, "第二部位第二稀有候选错误拒绝，不只首项");

        fixture = new(resources) { TargetExtraAbility = true };
        That(fixture.Validate() is not null, "交集非空仍拒绝目标另一个合法能力在稀有分支失败");
        fixture = new(resources) { RareRate = 0, UpperRate = 0, TargetExtraAbility = true };
        That(fixture.Validate() is null, "稀有不可达时不要求目标能力与稀有池相交");
        That(fixture.Validate(fixture.Source + 2) is not null, "大师材料加成使零基础概率稀有可达");
        fixture = new(resources) { RareRate = 0, UpperRate = 100, TargetExtraAbility = true };
        That(fixture.Validate() is not null, "高级源概率使普通材料的稀有仍可达");

        fixture = new(resources);
        fixture.Templates[fixture.Source] = fixture.Templates[fixture.Source] with { UseForbidden = true };
        That(fixture.Validate() is not null, "禁用材料保持拒绝");
        fixture = new(resources);
        fixture.Templates[fixture.Source] = fixture.Templates[fixture.Source] with { IsPremiumService = true };
        That(fixture.Validate() is not null, "特殊owner不能被合成材料登记覆盖");
        fixture = new(resources);
        That(fixture.Validate(fixture.Unknown) is { } unknown && unknown.Contains("material_consumer_not_connected", StringComparison.Ordinal),
            "普通etc材料没有真实登记仍拒绝");
        fixture = new(resources) { BadGradeJob = 1 };
        That(fixture.Validate(fixture.Unknown) is { } unrelated && unrelated.Contains("material_consumer_not_connected", StringComparison.Ordinal),
            "未知材料仍以原未知用途失败，不被无关坏grade替换");
    }

    sealed class CompoundBoundaryFixture
    {
        readonly ItemResources resources;
        readonly Dictionary<int, string> strings = [];
        readonly Dictionary<string, int> indexes = new(StringComparer.Ordinal);
        public int Source => 880007001;
        public int Unknown => 880007099;
        public int PlainEquipment => 880007100;
        public int TargetSecondPart => Candidate(2, 1, 2);
        public int RareSecondPartSecond => Candidate(2, 1, 1);
        public Dictionary<int, ItemTemplate> Templates { get; } = [];
        public int BadGradeJob { get; init; }
        public bool SharedMaterial { get; init; }
        public bool ShiftedSharedMaterial { get; init; }
        public bool NestedSharedMaterial { get; init; }
        public bool TargetExtraAbility { get; init; }
        public int RareRate { get; init; } = 100;
        public int UpperRate { get; init; } = 100;
        public int TokenReads { get; private set; }

        public CompoundBoundaryFixture(ItemResources resources)
        {
            this.resources = resources;
            Templates[PlainEquipment] = new(PlainEquipment, ItemKind.Equipment, 12, 1, 0, 1, 0, 0, 1,
                "[free]", ushort.MaxValue, 0, "compound boundary equipment");
            foreach (int id in new[] { Source, Source + 1, Source + 2, Source + 10, Source + 11, Source + 12, Unknown })
                Templates[id] = new(id, ItemKind.Material, 0, 1, 0, 1000, 0, 0, 0,
                    "[free]", ushort.MaxValue, 0, "compound boundary material", "[etc] 1");
            for (int job = 1; job <= 2; job++)
            for (int part = 0; part < AvatarCompoundCatalog.PartNames.Length; part++)
            for (int candidate = 0; candidate < 3; candidate++)
            {
                int id = Candidate(job, part, candidate);
                byte slot = part switch { 3 => 6, 4 => 3, 5 => 4, 6 => 7, 7 => 5, _ => checked((byte)part) };
                Templates[id] = new(id, ItemKind.Avatar, slot, 1, 0, 1, 0, 0, candidate == 2 ? 1 : 3,
                    "[free]", checked((ushort)(1 << (job - 1))), 0, "compound boundary avatar");
            }
        }

        public string? Validate(int? rootItem = null)
        {
            TokenReads = 0;
            var equipment = Templates.Values.Where(template => template.Kind == ItemKind.Avatar)
                .ToDictionary(template => template.Id, template => template.Id + ".equ");
            var items = new ItemCatalog(null!, equipment, new Dictionary<int, string>(), 0);
            items.PrefillTemplates(Templates);
            var avatars = new AvatarPackageCatalog(path =>
            {
                int id = int.Parse(Path.GetFileNameWithoutExtension(path), System.Globalization.CultureInfo.InvariantCulture);
                bool target = (id - 880010000) % 10 == 2;
                return "[avatar select ability]\n3 `[HP MAX]` `+` 100\n" +
                    (TargetExtraAbility && target ? "4 `[MP MAX]` `+` 100\n" : "") + "[/avatar select ability]";
            }, equipment, new Dictionary<int, string>());
            var compounds = new AvatarCompoundCatalog(_ => "1 `compound/a.etc` 2 `compound/b.etc`", path =>
            {
                TokenReads++;
                return Tokens(path.EndsWith("a.etc", StringComparison.Ordinal) ? 1 : 2);
            }, index => strings[index], path => path is "etc/compound/a.etc" or "etc/compound/b.etc");
            int id = rootItem ?? Source;
            var shop = CashShopCatalog.Parse($"[item]\n1 {id} 1 0 0 100 0 0 0\n");
            var rules = new PromotionRules(new("compound-boundary", "period", "v1", "default", "package"),
                10, 3, 31, id, 1, 5000, [new(1, id, 1)], [new(2, id, 1)]);
            return new PromotionConsumerValidation(items, resources.Boosters, avatars, shop,
                resources.EpicPieces, resources.Contracts, resources.RandomOptions, resources.AttributePairs,
                resources.Creatures, resources.CollectBoxes, resources.LimitCubes, resources.Enchantments,
                resources.Enhancements, resources.RecoveryConsumables, resources.Amplifications, resources.AmplifyUpgrades,
                avatarCompounds: compounds).Validate(rules);
        }

        PvfToken[] Tokens(int job)
        {
            var result = new List<PvfToken>();
            result.Add(Tag("grade")); result.Add(I(job == BadGradeJob ? -1 : 1));
            result.Add(Tag("upper grade")); result.Add(I(2));
            Block(result, "rare rate", AvatarCompoundCatalog.PartNames.SelectMany(part => new[] { Str(part), I(RareRate) }));
            Block(result, "upper rare rate", AvatarCompoundCatalog.PartNames.SelectMany(part => new[] { Str(part), I(UpperRate) }));
            int source = job == 2 || SharedMaterial ? Source : Source + 10;
            if (job == 1 && NestedSharedMaterial)
            {
                result.Add(Tag("material")); result.Add(I(1));
                Block(result, "broken", [I(Source)]);
                result.Add(Tag("/material"));
            }
            else
                Block(result, "material", job == 1 && ShiftedSharedMaterial
                    ? [I(1), I(source), I(Source), I(1)] : [I(1), I(source), I(1)]);
            Block(result, "material_upper", [I(1), I(source + 1), I(1)]);
            Block(result, "material_master", [I(1), I(source + 2), I(1)]);
            for (int part = 0; part < AvatarCompoundCatalog.PartNames.Length; part++)
                Block(result, AvatarCompoundCatalog.PartNames[part] + " avatar",
                    [I(2), I(Candidate(job, part, 0)), I(1), I(Candidate(job, part, 1)), I(1), I(Candidate(job, part, 2)), I(1)]);
            // 嵌套的同名material不能进入根级材料反查。
            result.Add(Tag("common bind cube"));
            Block(result, "material", [I(1), I(Unknown), I(1)]);
            result.Add(Tag("/common bind cube"));
            return result.ToArray();
        }

        void Block(List<PvfToken> output, string name, IEnumerable<PvfToken> values)
        {
            output.Add(Tag(name)); output.AddRange(values); output.Add(Tag("/" + name));
        }
        PvfToken Tag(string value) => new(3, Intern("[" + value + "]"));
        PvfToken Str(string value) => new(6, Intern(value));
        static PvfToken I(int value) => new(0, value);
        int Intern(string value)
        {
            if (indexes.TryGetValue(value, out int index)) return index;
            index = indexes.Count + 1;
            indexes.Add(value, index); strings.Add(index, value);
            return index;
        }
        static int Candidate(int job, int part, int index) => 880010000 + job * 1000 + part * 10 + index;
    }
}
