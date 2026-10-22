using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Management.ImagePack;
using PvfLib;

namespace DfoServer.Network.Management;

// GM 网页的物品图标与预览：按需读取服务端已加载的 PVF 脚本，不建立第二份物品索引。
// 图标来自客户端 ImagePacks2（移植自 S5A9GmTool 的 ImagePack，MIT）；找不到目录时返回 null，前端显示占位。
internal sealed partial class GmItemExtras(PvfArchive pvf, string? imagePacksPath)
{
    static readonly Regex Backtick = new("`([^`]*)`", RegexOptions.Compiled);
    static readonly Regex Tagged = new(@"\[(?<type>[^\]/\r\n]+)\](?<values>(?:[ \t\r\n]+-?\d+)+)", RegexOptions.Compiled);

    readonly Lazy<ImmutableDictionary<int, (bool Equipment, string Path)>> paths = new(() => BuildPaths(pvf), LazyThreadSafetyMode.ExecutionAndPublication);
    readonly Lazy<ImagePackLibrary?> images = new(() => ImagePackLibrary.TryOpen(Locate(imagePacksPath)), LazyThreadSafetyMode.ExecutionAndPublication);
    readonly Lazy<int> refineMax = new(() =>
    {
        try
        {
            var match = Regex.Match(pvf.ReadText("etc/upgrade_separate.etc"), @"\[separate upgrade max\]\s*(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int max) && max is >= 1 and <= 127)
                return max;
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or KeyNotFoundException) { }
        return 8;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    // 套装定义：part set index -> (名称, 效果脚本)。按需解析 etc/equipmentpartset.etc 一次。
    readonly Lazy<ImmutableDictionary<int, (string Name, string Path)>> sets = new(() =>
    {
        var map = ImmutableDictionary.CreateBuilder<int, (string, string)>();
        try
        {
            foreach (Match match in Regex.Matches(pvf.ReadText("etc/equipmentpartset.etc"), @"\[equipment part set\]\s*(\d+)\s*`([^`]+)`\s*`([^`]*)`"))
                map[int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)] = (match.Groups[3].Value.Trim(), match.Groups[2].Value.Trim());
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or KeyNotFoundException) { }
        return map.ToImmutable();
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    object? SetBonus(PvfDocument document)
    {
        int setIndex = PvfScript.Integers(document.Section("part set index")).FirstOrDefault();
        if (setIndex <= 0 || !sets.Value.TryGetValue(setIndex, out var set)) return null;
        try
        {
            var tiers = GmItemAttributes.SetTiers(pvf.ReadText("equipment/" + set.Path), Stats);
            return new { index = setIndex, name = set.Name, tiers = tiers.Select(tier => new { pieces = tier.Pieces, lines = tier.Lines }) };
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or KeyNotFoundException) { return null; }
    }

    public bool IconsAvailable => images.Value is not null;
    public string? ImagePacksRoot => images.Value?.Root;

    static ImmutableDictionary<int, (bool, string)> BuildPaths(PvfArchive pvf)
    {
        var map = ImmutableDictionary.CreateBuilder<int, (bool, string)>();
        foreach (var (equipment, list) in new[] { (true, "equipment/equipment.lst"), (false, "stackable/stackable.lst") })
            foreach (var (id, path) in PvfScript.List(pvf.ReadText(list))) map[id] = (equipment, path);
        return map.ToImmutable();
    }

    // 显式路径优先；否则依次找数据目录、程序目录及其上几级（含各级的直接子目录和孙目录）里的 ImagePacks2。
    internal static string? Locate(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        string? env = Environment.GetEnvironmentVariable("DFO_IMAGEPACKS2");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        try
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            for (int up = 0; up < 5 && directory is not null; up++, directory = directory.Parent)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "ImagePacks2"))) return Path.Combine(directory.FullName, "ImagePacks2");
                foreach (var child in directory.EnumerateDirectories())
                {
                    string direct = Path.Combine(child.FullName, "ImagePacks2");
                    if (Directory.Exists(direct)) return direct;
                    foreach (var grand in child.EnumerateDirectories())
                    {
                        string deep = Path.Combine(grand.FullName, "ImagePacks2");
                        if (Directory.Exists(deep)) return deep;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    PvfDocument? Script(int id) =>
        paths.Value.TryGetValue(id, out var entry)
            ? entry.Equipment ? EquipmentFile.ReadScript(pvf.ReadText, "equipment/" + entry.Path) : new PvfDocument(pvf.ReadText("stackable/" + entry.Path))
            : null;

    static (string Path, int Frame)? ReadIcon(PvfDocument document, string tag)
    {
        string line = document.Section(tag);
        var match = Backtick.Match(line);
        if (!match.Success || match.Groups[1].Value.Trim().Length == 0) return null;
        string rest = line[(match.Index + match.Length)..];
        int frame = 0;
        var second = Backtick.Match(rest);
        if (second.Success) int.TryParse(second.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out frame);
        else foreach (string token in rest.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(token.Trim('`'), NumberStyles.Integer, CultureInfo.InvariantCulture, out frame)) break;
        return (match.Groups[1].Value.Trim(), frame);
    }

    public byte[]? Icon(int id)
    {
        var library = images.Value;
        if (library is null) return null;
        {
            int key = id;
            try
            {
                if (Script(key) is not { } document || ReadIcon(document, "icon") is not { } icon) return null;
                var mark = ReadIcon(document, "icon mark");
                return library.TryRenderPng(icon.Path, icon.Frame, mark?.Path ?? "", mark?.Frame ?? 0, out var png) ? png : null;
            }
            catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or KeyNotFoundException or FormatException or IOException) { return null; }
        }
    }

    static string Text(PvfDocument document, string tag) =>
        string.Join("\n", Backtick.Matches(document.Section(tag)).Select(match => match.Groups[1].Value.Replace("%%", "%").Trim()).Where(value => value.Length > 0));

    public object? Preview(int id)
    {
        PvfDocument? document;
        try { document = Script(id); }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or KeyNotFoundException or FormatException) { return null; }
        if (document is null) return null;
        bool equipment = paths.Value[id].Equipment;
        string raw = pvf.ReadText((equipment ? "equipment/" : "stackable/") + paths.Value[id].Path);
        var icon = ReadIcon(document, "icon");
        return new
        {
            id,
            name = document.Section("name").Trim('`', ' ', '\r', '\n'),
            kind = equipment ? "equipment" : "stackable",
            tag = Regex.Match(document.Section(equipment ? "equipment type" : "stackable type"), @"\[([^\]]+)\]").Groups[1].Value.ToLowerInvariant(),
            rarity = document.TryGetInt32("rarity", out int rarity) ? rarity : 0,
            minLevel = document.TryGetInt32("minimum level", out int level) ? level : 0,
            usableJob = Regex.Matches(document.Section("usable job"), @"\[([^\]]+)\]").Select(match => match.Groups[1].Value).ToArray(),
            explain = Text(document, "explain"),
            basicExplain = Text(document, "basic explain") is { Length: > 0 } basic ? basic : Text(document, "parameter basic explain"),
            detailExplain = Text(document, "detail explain"),
            flavorText = Text(document, "flavor text"),
            hasIcon = icon is not null,
            iconsAvailable = IconsAvailable,
            stats = Stats(raw),
            // [enchant] 块（徽章/附魔卡）、时装可选属性、技能加成、套装效果：全部来自已加载 PVF 脚本文本。
            enchant = GmItemAttributes.Block(raw, "enchant") is { Length: > 0 } enchantBlock ? Stats(enchantBlock) : [],
            avatarAbility = GmItemAttributes.AvatarAbility(raw),
            skillBonus = GmItemAttributes.SkillBonus(raw),
            setBonus = equipment ? SetBonus(document) : null,
            expiration = new
            {
                usablePeriodDays = document.TryGetInt32("usable period", out int days) ? days : 0,
                // [expiration date] 在 PVF 中是 `yyyy-MM-dd HH:mm:ss` 文本，而不是整数。
                absoluteExpireDate = Regex.Match(raw, @"\[expiration date\]\s*`([^`]+)`") is { Success: true } expiry ? expiry.Groups[1].Value : ""
            },
            edit = EditOptions(document, raw, equipment, refineMax.Value),
            // 以下为游戏内说明框里的其余信息，均来自同一份 PVF 脚本；脚本没写的项为 0 或空。
            grade = document.TryGetInt32("grade", out int grade) ? grade : 0,
            durability = document.TryGetInt32("durability", out int durability) ? durability : 0,
            weight = document.TryGetInt32("weight", out int weight) ? weight : 0,
            stackLimit = document.TryGetInt32("stack limit", out int stackLimit) ? stackLimit : 0,
            price = PvfScript.Integers(document.Section("price")).FirstOrDefault(),
            value = PvfScript.Integers(document.Section("value")).FirstOrDefault(),
            attachType = AttachType(document),
            trade = TradeLabel(AttachType(document)),
            weaponSub = equipment ? DfoServer.Development.DevelopmentCatalog.WeaponSub(Regex.Match(document.Section("equipment type"), @"\[([^\]]+)\]").Groups[1].Value.ToLowerInvariant(), paths.Value[id].Path) : "",
            booster = equipment ? null : BoosterContents(raw)
        };
    }

    // [attach type] 归一成小写裸词（free / trade limit / sealing / account / trade delete …），与交易、仓库判定同一写法。
    static string AttachType(PvfDocument document) =>
        document.Section("attach type").Replace("`", "").Trim().Trim('[', ']').Trim().ToLowerInvariant();

    // 交易限制的中文说明，按服务端交易判定（AuctionService.TransferBlocked、InventoryDiscardPlan：free/trade 可交易，
    // trade limit 按剩余次数，sealing 封装状态可交易；其余一律不可交易）与仓库判定（InventoryCargoService、InventoryDisjointService）归纳。
    internal static string TradeLabel(string attach) => attach switch
    {
        "" => "",
        "free" or "trade" => "可交易",
        "trade limit" => "限次交易",
        "sealing" or "seal" => "封装（解封后不可交易）",
        "trade delete" => "不可交易（不能存个人仓库、不能分解）",
        _ when attach.Contains("account", StringComparison.Ordinal) => "账号绑定（不可交易）",
        _ => "不可交易（" + attach + "）"
    };

    // 礼包内容：用服务端开箱同一个解析（BoosterCatalog.Parse），合并同一物品的数量区间；最多列 30 项。自选礼包只标注，不展开。
    object? BoosterContents(string raw)
    {
        BoosterDefinition definition;
        try { definition = BoosterCatalog.Parse(raw); }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or OverflowException or KeyNotFoundException) { return null; }
        var rewards = definition.Pools.SelectMany(pool => pool.Rewards)
            .Concat(definition.PackageRewards.IsDefaultOrEmpty ? [] : definition.PackageRewards).ToArray();
        if (rewards.Length == 0 && definition.Selection is null) return null;
        var merged = rewards.GroupBy(reward => reward.ItemId).Take(30).Select(group =>
        {
            string name = "";
            try { name = Script(group.Key)?.Section("name").Trim('`', ' ', '\r', '\n') ?? ""; }
            catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or KeyNotFoundException or FormatException) { }
            return new { id = group.Key, name, min = group.Min(reward => reward.Count), max = group.Max(reward => reward.Count) };
        }).ToArray();
        return new
        {
            items = merged,
            more = Math.Max(0, rewards.Select(reward => reward.ItemId).Distinct().Count() - merged.Length),
            selection = definition.Selection is not null,
            random = definition.Pools.Length > 0 && definition.PackageRewards.IsDefaultOrEmpty,
            unsupported = definition.PendingRule
        };
    }

    // 发放和背包修改只提供这件脚本自己允许的项：强化/增幅看 impossible contents，锻造只给武器，装扮属性来自可选词条。
    static object EditOptions(PvfDocument document, string raw, bool equipmentFile, int maxRefine)
    {
        string tag = Regex.Match(document.Section(equipmentFile ? "equipment type" : "stackable type"), @"\[([^\]]+)\]").Groups[1].Value.ToLowerInvariant();
        bool avatar = tag.Contains("avatar", StringComparison.Ordinal);
        bool gear = equipmentFile && !avatar && GearTags.Contains(tag);
        bool reinforce = false, amplify = false;
        if (gear)
        {
            if (!(document.Has("impossible contents") && !document.Has("/impossible contents")))
            {
                var forbidden = Regex.Matches(document.Block("impossible contents"), "`([^`]*)`")
                    .Select(match => match.Groups[1].Value.Trim('[', ']', ' ').ToLowerInvariant()).ToHashSet();
                reinforce = !forbidden.Contains("upgrade");
                amplify = reinforce && !forbidden.Contains("amplify upgrade");
            }
        }
        int stack = document.TryGetInt32("stack limit", out int limit) && limit > 1 ? limit : 1;
        return new
        {
            gear, avatar, reinforce, amplify, refine = tag == "weapon", maxRefine,
            seal = gear && document.Section("attach type").Contains("[sealing]", StringComparison.OrdinalIgnoreCase),
            stackable = !equipmentFile && stack > 1,
            countMax = !equipmentFile && stack > 1 ? stack : 10,
            abilities = avatar ? GmItemAttributes.AvatarOptions(raw) : [],
            periodDays = document.TryGetInt32("usable period", out int days) ? days : 0
        };
    }

    static readonly HashSet<string> GearTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "weapon", "coat", "shoulder", "pants", "shoes", "waist", "amulet", "wrist", "ring", "earring", "support", "magic stone", "support weapon"
    };

    // 沿用朋友工具的做法：扫描脚本里 “[属性] 数值…” 片段，按映射表合并并按固定顺序输出；未收录的属性不显示。
    static string[] Stats(string text)
    {
        var bags = new Dictionary<string, (string Label, bool Percent, long Low, long High)>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Tagged.Matches(text))
        {
            string tag = match.Groups["type"].Value.Replace('_', ' ').Trim();
            if (!GmItemStatSpecs.Table.TryGetValue(tag, out var spec)) continue;
            var values = Regex.Matches(match.Groups["values"].Value, "-?\\d+").Select(value => long.Parse(value.Value, CultureInfo.InvariantCulture)).ToArray();
            if (values.Length == 0) continue;
            long low = values.Length >= 2 ? Math.Min(values[0], values[1]) : values[0];
            long high = values.Length >= 2 ? Math.Max(values[0], values[1]) : values[0];
            bags[spec.Key] = bags.TryGetValue(spec.Key, out var old) ? (old.Label, old.Percent, old.Low + low, old.High + high) : (spec.Label, spec.Percent, low, high);
        }
        var lines = new List<string>();
        foreach (string key in GmItemStatSpecs.Order)
            if (bags.Remove(key, out var bag)) lines.Add(OwnProperty.Contains(key) ? Plain(bag) : Format(bag));
        lines.AddRange(bags.Select(pair => OwnProperty.Contains(pair.Key) ? Plain(pair.Value) : Format(pair.Value)));
        return [.. lines];
    }

    // 耐久、重量、堆叠、冷却是物品自身参数，不是加成。加成才用加号。
    static readonly HashSet<string> OwnProperty = new(StringComparer.OrdinalIgnoreCase) { "durability", "weight", "stack limit", "cool time" };

    static string Plain((string Label, bool Percent, long Low, long High) bag)
    {
        string unit = bag.Percent ? "%" : "";
        string value = bag.Low == bag.High ? bag.Low.ToString(CultureInfo.InvariantCulture) + unit : bag.Low + unit + " ~ " + bag.High + unit;
        return bag.Label + " " + value;
    }

    static string Format((string Label, bool Percent, long Low, long High) bag)
    {
        string unit = bag.Percent ? "%" : "";
        string value = bag.Low == bag.High ? Signed(bag.Low) + unit : Signed(bag.Low) + unit + " ~ " + Signed(bag.High) + unit;
        return bag.Label + " " + value;
    }

    static string Signed(long value) => value >= 0 ? "+" + value.ToString(CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);
}