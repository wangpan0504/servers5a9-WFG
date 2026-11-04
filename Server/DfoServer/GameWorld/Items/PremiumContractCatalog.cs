using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record PremiumContract(int ItemId, int Type, long DurationSeconds);
internal sealed record GrowthContractBenefits(int ExperiencePercent, int QuestItemDropPercent, ImmutableArray<int> IndependentDropPercent);
internal sealed record CubeContractOption(int ItemId, int EquipmentId, int IntervalMilliseconds = 30000, int ConsumeCount = 1);
internal sealed record PremiumContractResourceWarning(int Type, int ConfiguredItems, string Reason);

// premiumlist_new.etc 是契约消耗品到账号服务类型和期限的权威映射。
// 没有正期限的条目属于效果或选择器，不应被当作可激活契约。
internal sealed class PremiumContractCatalog
{
    static readonly Regex TypeBlock = new(@"\[type\]\s*(-?\d+)(?<body>.*?)(?=\[type\]|\z)", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    static readonly Regex ItemBlock = new(@"\[item\]\s*(-?\d+)(?<body>.*?)\[/item\]", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    static readonly Regex Target = new(@"\[target item\]\s*(-?\d+)(?=\s|\[|\z)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex Term = new(@"\[term\]\s*(-?\d+)(?=\s|\[|\z)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex Minute = new(@"\[is term unit minute\]\s*(-?\d+)(?=\s|\[|\z)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex OverSkill = new(@"\[over skill\](?<values>[^\[]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex OverEquipment = new(@"\[over equipinfo\](?<values>[^\[]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public FrozenDictionary<int, PremiumContract> ByItem { get; }
    public FrozenSet<int> RecognizedItems { get; }
    public int ExpertSkillLevels { get; }
    public bool HasExpertSkillRule { get; }
    // 本服按overequip list的一基列表序号装配类型；原码消费者和实机仍需独立证据。
    public FrozenDictionary<int, int> ConquerorEquipmentLevels { get; }
    public FrozenDictionary<int, ImmutableArray<string>> ConquerorEquipmentGroups { get; }
    public bool HasConquerorEquipmentRule { get; }
    public string? ConquerorRuleError { get; }
    public GrowthContractBenefits? Growth { get; }
    public ImmutableArray<int> GrowthEquipment { get; }
    public ImmutableArray<CubeContractOption> Cubes { get; private set; }
    public ImmutableArray<PremiumContractResourceWarning> ResourceWarnings { get; private set; } = [];
    FrozenSet<int> missingStackables = FrozenSet<int>.Empty;
    readonly FrozenDictionary<(ItemKind Kind, byte Slot), int> equipmentAllowances;

    public PremiumContractCatalog(IReadOnlyDictionary<int, PremiumContract> contracts, int? expertSkillLevels = null,
        IReadOnlyDictionary<int, int>? conquerorEquipmentLevels = null,
        IReadOnlyDictionary<int, IReadOnlyList<string>>? conquerorEquipmentGroups = null,
        IEnumerable<int>? recognizedItems = null, GrowthContractBenefits? growth = null,
        IEnumerable<int>? growthEquipment = null, IEnumerable<CubeContractOption>? cubes = null)
    {
        if (expertSkillLevels is < 0 || conquerorEquipmentLevels?.Any(pair => pair.Key < 0 || pair.Value < 0) == true)
            throw new InvalidDataException("契约等级参数不能为负数");
        ByItem = contracts.ToFrozenDictionary();
        RecognizedItems = (recognizedItems ?? contracts.Keys).Concat(contracts.Keys).Where(item => item > 0).ToFrozenSet();
        ExpertSkillLevels = expertSkillLevels ?? 0;
        HasExpertSkillRule = expertSkillLevels.HasValue;
        HasConquerorEquipmentRule = conquerorEquipmentLevels is not null;
        ConquerorEquipmentLevels = (conquerorEquipmentLevels ?? new Dictionary<int, int>()).ToFrozenDictionary();
        if (conquerorEquipmentGroups?.Any(pair => pair.Value is null) == true)
            throw new InvalidDataException("霸王装备名单不能包含空值");
        ConquerorEquipmentGroups = (conquerorEquipmentGroups ?? new Dictionary<int, IReadOnlyList<string>>())
            .ToFrozenDictionary(pair => pair.Key, pair => pair.Value.ToImmutableArray());
        (equipmentAllowances, ConquerorRuleError) = EquipmentRules();
        Growth = growth;
        GrowthEquipment = growthEquipment?.ToImmutableArray() ?? [];
        Cubes = cubes?.ToImmutableArray() ?? [];
        if (growth is not null && (growth.ExperiencePercent < 0 || growth.QuestItemDropPercent < 0 ||
            growth.IndependentDropPercent.Length != 4 || growth.IndependentDropPercent.Any(value => value < 0)) ||
            GrowthEquipment.Any(id => id <= 0) || Cubes.Length is not (0 or 6) ||
            Cubes.Any(row => row.ItemId <= 0 || row.EquipmentId <= 0 || row.IntervalMilliseconds <= 0 || row.ConsumeCount <= 0) ||
            Cubes.Select(row => row.ItemId).Distinct().Count() != Cubes.Length)
            throw new InvalidDataException("成长或晶体契约权益声明无效");
    }
    public PremiumContract? Get(int itemId) => ByItem.GetValueOrDefault(itemId);
    public bool Recognizes(int itemId) => RecognizedItems.Contains(itemId);
    public const int AuctionCouponType = 29;
    public static bool IsConsumableContract(int type) => type is 22 or 27;
    public bool TryConsumableContract(int itemId, out PremiumContract definition)
    {
        definition = Get(itemId)!;
        // 其他 premium 服务另有疲劳、复活币或物品奖励，不能仅记期限就替代完整发放。
        return definition is not null && !missingStackables.Contains(itemId) && definition.ItemId == itemId && definition.DurationSeconds > 0 &&
            (definition.Type == 27 && HasExpertSkillRule || definition.Type == 22 && HasConquerorEquipmentRule && ConquerorRuleError is null);
    }

    public bool TryAcquisitionContract(int itemId, out PremiumContract definition)
    {
        if (TryConsumableContract(itemId, out definition)) return true;
        definition = Get(itemId)!;
        return definition is { DurationSeconds: > 0 } && !missingStackables.Contains(itemId) &&
            (definition.Type == 84 && Growth is not null && !GrowthEquipment.IsEmpty ||
                definition.Type == 97 && Cubes.Length == 6 || definition.Type is 88 or 92 ||
                definition.Type == AuctionCouponType);
    }

    public int EquipmentLevelAllowance(ItemTemplate template) => EquipmentLevelAllowance(template.Kind, template.EquipmentSlot);
    public int EquipmentLevelAllowance(ItemKind kind, byte slot) => equipmentAllowances.GetValueOrDefault((kind, slot));

    public static PremiumContractCatalog Load(PvfArchive archive, IReadOnlyDictionary<int, string>? equipment = null,
        IReadOnlyDictionary<int, string>? stackable = null)
    {
        var catalog = Parse(archive.ReadText("etc/premiumlist_new.etc"), archive.ReadText("etc/premiumserviceeffect.etc"));
        equipment ??= PvfScript.List(archive.ReadText("equipment/equipment.lst"));
        foreach (int id in catalog.GrowthEquipment.Concat(catalog.Cubes.Select(row => row.EquipmentId)).Distinct())
            if (!equipment.TryGetValue(id, out string? path) || !archive.Contains("equipment/" + path))
                throw new InvalidDataException("契约附加装备缺失: " + id);
        catalog.Cubes = catalog.Cubes.Select(option =>
        {
            var script = new PvfDocument(archive.ReadText("equipment/" + equipment[option.EquipmentId]));
            var condition = new PvfDocument(script.Block("if"));
            var action = new PvfDocument(script.Block("then"));
            int[] consume = PvfScript.Integers(action.Section("consume item")).ToArray();
            if (!condition.TryGetInt32("cooltime", out int interval) || interval <= 0 ||
                consume.Length != 2 || consume[0] != option.ItemId || consume[1] <= 0)
                throw new InvalidDataException("晶体契约周期或材料声明无效: " + option.EquipmentId);
            return option with { IntervalMilliseconds = interval, ConsumeCount = consume[1] };
        }).ToImmutableArray();
        stackable ??= PvfScript.List(archive.ReadText("stackable/stackable.lst"));
        catalog.InspectStackables(stackable, archive.Contains);
        return catalog;
    }

    public bool HasStackable(int itemId) => !missingStackables.Contains(itemId);

    // 配置可包含当前未安装的STK，保留编号与期限，仅让缺资源的实际操作不可用。
    internal void InspectStackables(IReadOnlyDictionary<int, string> stackable, Func<string, bool> contains)
    {
        var ids = ByItem.Values.Where(row => row.Type is 22 or 27 or 84 or 97 or 88 or 92 or AuctionCouponType).Select(row => row.ItemId)
            .Concat(Cubes.Select(row => row.ItemId)).Distinct().Order();
        missingStackables = ids.Where(id => !stackable.TryGetValue(id, out string? path) || !contains("stackable/" + path)).ToFrozenSet();
        ResourceWarnings = ByItem.Values.Where(row => row.Type is 22 or 27 or 84 or 97 or 88 or 92 or AuctionCouponType).GroupBy(row => row.Type)
            .Where(group => group.All(row => missingStackables.Contains(row.ItemId))).OrderBy(group => group.Key)
            .Select(group => new PremiumContractResourceWarning(group.Key, group.Count(), "no_contract_stk_available")).ToImmutableArray();
    }

    internal static PremiumContractCatalog Parse(string text, string? effects = null)
    {
        var result = new Dictionary<int, PremiumContract>();
        int expertSkillLevels = 0;
        bool expertFieldFound = false;
        Dictionary<int, int>? conquerorEquipmentLevels = null;
        GrowthContractBenefits? growth = null;
        foreach (Match typeMatch in TypeBlock.Matches(MaskStrings(text)))
        {
            if (!int.TryParse(typeMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int type) || type <= 0)
                continue;
            var bodySpan = typeMatch.Groups["body"];
            string body = text.Substring(bodySpan.Index, bodySpan.Length);
            string numericBody = MaskStrings(body);
            var children = ItemBlock.Matches(numericBody);
            // 子物品有自己的期限和单位；父目标不能读到位于前面的子物品字段。
            char[] parentChars = body.ToCharArray();
            foreach (Match child in children)
                Array.Fill(parentChars, ' ', child.Index, child.Length);
            string parent = new(parentChars);
            string numericParent = MaskStrings(parent);
            if (numericParent.Contains("[item]", StringComparison.Ordinal) || numericParent.Contains("[/item]", StringComparison.Ordinal))
                throw new InvalidDataException("契约子物品段没有完整闭合");
            // 等级权益仅取所属服务的父字段，不能由子物品字段补全或覆盖。
            if (type == 27 && Values(OverSkill, parent) is { } skillValues)
            {
                if (expertFieldFound || skillValues.Length != 1)
                    throw new InvalidDataException("达人等级字段缺失、重复或宽度无效");
                expertSkillLevels = Nonnegative(skillValues[0]);
                expertFieldFound = true;
            }
            if (type == 22 && Values(OverEquipment, parent) is { } equipmentValues)
            {
                if (conquerorEquipmentLevels is not null || equipmentValues.Length == 0)
                    throw new InvalidDataException("霸王装备等级字段缺失或重复");
                int count = Nonnegative(equipmentValues[0]);
                if (equipmentValues.Length != 1L + 2L * count)
                    throw new InvalidDataException("霸王装备等级组数或行宽无效");
                conquerorEquipmentLevels = [];
                for (int index = 0; index < count; index++)
                {
                    int group = Nonnegative(equipmentValues[1 + index * 2]);
                    int levels = Nonnegative(equipmentValues[2 + index * 2]);
                    if (!conquerorEquipmentLevels.TryAdd(group, levels))
                        throw new InvalidDataException("霸王装备等级组编号重复");
                }
            }
            if (type == 84)
            {
                var experience = Values(new(@"\[bonus exp\](?<values>[^\[]*)"), parent);
                var questDrop = Values(new(@"\[quest item drop rate\](?<values>[^\[]*)"), parent);
                var independent = Values(new(@"\[independent drop rate\](?<values>[^\[]*)"), parent);
                if (experience is not null || questDrop is not null || independent is not null)
                {
                    if (growth is not null || experience?.Length != 1 || questDrop?.Length != 1 || independent?.Length != 4)
                        throw new InvalidDataException("成长契约权益缺失、重复或宽度无效");
                    growth = new(Nonnegative(experience[0]), Nonnegative(questDrop[0]), independent.Select(Nonnegative).ToImmutableArray());
                }
            }
            int target = Number(Field(Target, parent));
            int targetTerm = Number(Field(Term, parent));
            if (target > 0 && targetTerm > 0)
                Add(new(target, type, Seconds(targetTerm, IsMinute(parent))));
            foreach (Match item in children)
            {
                int itemId = Number(item);
                string itemBody = item.Groups["body"].Value;
                int term = Number(Field(Term, itemBody));
                if (itemId > 0 && term > 0)
                    Add(new(itemId, type, Seconds(term, IsMinute(itemBody))));
            }
        }
        var equipmentEffects = ServiceEquipment(effects, 84, "add equipment list", 1);
        var cubeEffects = ServiceEquipment(effects, 97, "add selectAble equipment list", 2);
        return new(result, expertFieldFound ? expertSkillLevels : null, conquerorEquipmentLevels, EquipmentGroups(text), Recognized(text),
            growth, equipmentEffects, Enumerable.Range(0, cubeEffects.Length / 2).Select(index => new CubeContractOption(cubeEffects[index * 2], cubeEffects[index * 2 + 1])));

        void Add(PremiumContract contract)
        {
            if (result.TryGetValue(contract.ItemId, out var current) && current != contract)
                throw new InvalidDataException("契约物品存在冲突的类型或期限: " + contract.ItemId);
            result[contract.ItemId] = contract;
        }
        static bool IsMinute(string body)
        {
            body = MaskStrings(body);
            var match = Field(Minute, body);
            if (!match.Success)
            {
                if (body.Contains("[is term unit minute]", StringComparison.Ordinal))
                    throw new InvalidDataException("契约期限单位缺少有效整数标志");
                return false;
            }
            int value = Number(match);
            if (value is not (0 or 1))
                throw new InvalidDataException("契约期限单位标志无效");
            return value == 1;
        }
        static Match Field(Regex expression, string body)
        {
            var fields = expression.Matches(MaskStrings(body));
            if (fields.Count > 1) throw new InvalidDataException("契约字段重复");
            return fields.Count == 0 ? Match.Empty : fields[0];
        }
        static int Number(Match match)
        {
            if (!match.Success) return 0;
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                throw new InvalidDataException("契约字段整数超出范围");
            return value;
        }
        static long Seconds(int term, bool minute) => checked((long)term * (minute ? 60 : 86400));
        static string[]? Values(Regex expression, string parent)
        {
            // 字符串中的标签不是字段；保留原始跨度，数值字段中的字符串仍必须作为非法token拒绝。
            string masked = MaskStrings(parent);
            var fields = expression.Matches(masked);
            if (fields.Count > 1)
                throw new InvalidDataException("契约等级字段重复");
            if (fields.Count == 0)
                return null;
            var values = fields[0].Groups["values"];
            return parent.Substring(values.Index, values.Length).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        }
        static int Nonnegative(string token)
        {
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < 0)
                throw new InvalidDataException("契约等级参数必须是非负整数");
            return value;
        }
    }

    static int[] ServiceEquipment(string? text, int type, string tag, int width)
    {
        if (text is null) return [];
        var rows = new List<int>();
        bool found = false;
        foreach (Match service in Regex.Matches(MaskStrings(text), @"\[premium service\](.*?)\[/premium service\]", RegexOptions.Singleline))
        {
            string block = text.Substring(service.Index, service.Length);
            if (!Regex.IsMatch(MaskStrings(block), @"\[type\]\s*" + type + @"(?=\s|\[|\z)")) continue;
            var matches = Regex.Matches(MaskStrings(block), @"\[" + Regex.Escape(tag) + @"\](?<values>[^\[]*)\[/" + Regex.Escape(tag) + @"\]");
            if (matches.Count == 0) continue;
            if (found || matches.Count != 1) throw new InvalidDataException("契约附加装备声明重复");
            found = true;
            var values = matches[0].Groups["values"];
            foreach (string token in block.Substring(values.Index, values.Length).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id <= 0)
                    throw new InvalidDataException("契约附加装备编号无效");
                rows.Add(id);
            }
        }
        if (rows.Count % width != 0) throw new InvalidDataException("契约附加装备行宽无效");
        return rows.ToArray();
    }

    static IEnumerable<int> Recognized(string text)
    {
        string masked = MaskStrings(text);
        foreach (Match match in Regex.Matches(masked, @"\[(?:target item|item)\]\s*(\d+)(?=\s|\[|\z)"))
        {
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int item))
                throw new InvalidDataException("premium服务物品编号超出整数范围");
            if (item > 0)
                yield return item;
        }
    }

    static Dictionary<int, IReadOnlyList<string>>? EquipmentGroups(string text)
    {
        var firstType = TypeBlock.Match(MaskStrings(text));
        string top = firstType.Success ? text[..firstType.Index] : text;
        string masked = MaskStrings(top);
        var opens = Regex.Matches(masked, @"\[overequip list\]");
        var closes = Regex.Matches(masked, @"\[/overequip list\]");
        if (opens.Count == 0 && closes.Count == 0)
            return null;
        if (opens.Count != 1 || closes.Count != 1 || closes[0].Index < opens[0].Index + opens[0].Length)
            throw new InvalidDataException("霸王顶层装备名单缺失闭合或重复");
        int start = opens[0].Index + opens[0].Length;
        string body = top[start..closes[0].Index];
        string[] tokens = Regex.Matches(body, @"`[^`]*`|\S+").Select(match => match.Value).ToArray();
        var groups = new Dictionary<int, IReadOnlyList<string>>();
        for (int at = 0; at < tokens.Length;)
        {
            if (!int.TryParse(tokens[at++], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count < 0 || count > tokens.Length - at)
                throw new InvalidDataException("霸王装备名单组数或行宽无效");
            var tags = new string[count];
            for (int index = 0; index < count; index++)
            {
                string token = tokens[at++];
                if (token.Length < 5 || !token.StartsWith("`[", StringComparison.Ordinal) || !token.EndsWith("]`", StringComparison.Ordinal))
                    throw new InvalidDataException("霸王装备名单类型必须是完整的PVF标签");
                tags[index] = token[1..^1];
            }
            // 本服资源装配采用一基列表序号；这不是原码消费者已闭合的结论。
            groups.Add(groups.Count + 1, Array.AsReadOnly(tags));
        }
        return groups;
    }

    static string MaskStrings(string text) => Regex.Replace(text, "`[^`]*`", match => new string(' ', match.Length));

    (FrozenDictionary<(ItemKind Kind, byte Slot), int> Rules, string? Error) EquipmentRules()
    {
        var rules = new Dictionary<(ItemKind Kind, byte Slot), int>();
        FrozenDictionary<(ItemKind Kind, byte Slot), int> Empty() => new Dictionary<(ItemKind Kind, byte Slot), int>().ToFrozenDictionary();
        if (!HasConquerorEquipmentRule)
            return (Empty(), "缺少父霸王装备等级字段");
        if (ConquerorEquipmentGroups.Count == 0)
            return (Empty(), "缺少顶层霸王装备类型名单");
        if (!ConquerorEquipmentGroups.Keys.Order().SequenceEqual(Enumerable.Range(1, ConquerorEquipmentGroups.Count)))
            return (Empty(), "霸王装备名单组号不符合一基列表序号");
        if (ConquerorEquipmentLevels.Keys.Any(group => !ConquerorEquipmentGroups.ContainsKey(group)))
            return (Empty(), "霸王装备等级引用未声明分组");
        foreach (var (group, tags) in ConquerorEquipmentGroups.OrderBy(pair => pair.Key))
        {
            if (tags.Length == 0)
                return (Empty(), "霸王装备类型分组为空");
            foreach (string tag in tags)
            {
                if (tag is null || tag.Length < 3 || tag[0] != '[' || tag[^1] != ']')
                    return (Empty(), "霸王装备类型标签无效");
                byte slot;
                try { slot = ItemCatalog.Slot(tag[1..^1]); }
                catch (InvalidDataException) { return (Empty(), "霸王装备类型无法映射: " + tag); }
                ItemKind kind;
                if (slot <= 11) kind = ItemKind.Avatar;
                else if (slot <= 25) kind = ItemKind.Equipment;
                else return (Empty(), "霸王装备类型没有已接入的穿戴效果: " + tag);
                if (!rules.TryAdd((kind, slot), ConquerorEquipmentLevels.GetValueOrDefault(group)))
                    return (Empty(), "霸王装备类型或槽位在分组间重复: " + tag);
            }
        }
        return (rules.ToFrozenDictionary(), null);
    }
}
