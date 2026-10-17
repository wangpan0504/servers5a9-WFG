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
            }
        };
    }

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
            if (bags.Remove(key, out var bag)) lines.Add(Format(bag));
        lines.AddRange(bags.Values.Select(Format));
        return [.. lines];
    }

    static string Format((string Label, bool Percent, long Low, long High) bag)
    {
        string unit = bag.Percent ? "%" : "";
        string value = bag.Low == bag.High ? Signed(bag.Low) + unit : Signed(bag.Low) + unit + " ~ " + Signed(bag.High) + unit;
        return bag.Label + " " + value;
    }

    static string Signed(long value) => value >= 0 ? "+" + value.ToString(CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);
}