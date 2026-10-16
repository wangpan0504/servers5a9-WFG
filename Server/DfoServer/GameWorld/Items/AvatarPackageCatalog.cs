using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

// [avatar]四字段依次为物品、数量、期限/孔位类型索引、默认属性编号；默认属性是UI预览提示。
internal sealed record AvatarPackageRow(int ItemId, ushort Count, ushort Type, byte DefaultAbility);
internal sealed record AvatarPackageCategory(byte Category, byte Subcategory, ImmutableArray<AvatarPackageRow> Rows,
    string? PendingRule = null);
internal sealed record AvatarPackageDefinition(int SelectionCount, ImmutableArray<int> Dimensions,
    ImmutableArray<AvatarPackageCategory> Categories, string? PendingRule = null)
{
    public bool Recognized { get; init; }
    public bool Available => Recognized && PendingRule is null && !Categories.IsDefaultOrEmpty;
}
internal sealed record AvatarPackageType(int Days, AvatarSocketTypes Sockets, int Gold = 0, int WinPoints = 0, int Cera = 0);
internal sealed record AvatarPackageItemDefinition(ImmutableArray<AvatarPackageType> Types,
    ImmutableHashSet<byte> Abilities, string? PendingRule = null)
{
    public bool RequiresAbilityChoice { get; init; } = true;
}

// 礼盒与装扮定义借用同一份PVF，只解析当前请求涉及的物品，不扫描全部STK。
internal sealed class AvatarPackageCatalog
{
    readonly Func<string, string> read;
    readonly IReadOnlyDictionary<int, string> equipment;
    readonly IReadOnlyDictionary<int, string> stackable;
    readonly Lazy<IReadOnlyDictionary<int, ImmutableHashSet<byte>>> abilityCases;
    readonly ConcurrentDictionary<int, Lazy<AvatarPackageDefinition>> packages = new();
    readonly ConcurrentDictionary<int, Lazy<AvatarPackageItemDefinition>> avatars = new();

    public AvatarPackageCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> equipment,
        IReadOnlyDictionary<int, string> stackable, IReadOnlyDictionary<int, ImmutableHashSet<byte>>? abilityCases = null)
        : this(archive.ReadText, equipment, stackable, abilityCases) { }

    internal AvatarPackageCatalog(Func<string, string> read, IReadOnlyDictionary<int, string> equipment,
        IReadOnlyDictionary<int, string> stackable, IReadOnlyDictionary<int, ImmutableHashSet<byte>>? abilityCases = null)
    {
        this.read = read;
        this.equipment = equipment;
        this.stackable = stackable;
        this.abilityCases = new(() => abilityCases ?? ParseAbilityCases(read("skill/abilitydatas.dat")));
    }

    public AvatarPackageDefinition Get(int itemId)
    {
        var pending = packages.GetOrAdd(itemId, id => new(() =>
            stackable.TryGetValue(id, out var path) ? Parse(read("stackable/" + path)) : new(0, [], [], "非装扮礼盒")));
        try { return pending.Value; }
        catch
        {
            packages.TryRemove(new KeyValuePair<int, Lazy<AvatarPackageDefinition>>(itemId, pending));
            throw;
        }
    }

    public AvatarPackageItemDefinition GetAvatar(int itemId)
    {
        var pending = avatars.GetOrAdd(itemId, id => new(() =>
        {
            if (!equipment.TryGetValue(id, out var path)) return new([], [], "装扮资源缺失");
            string text = read("equipment/" + path);
            return ParseAvatar(text, Fields(text, "import script").Count == 0 && Fields(text, "avatar select ability").Count == 0 &&
                Fields(text, "ability case index").Count != 0
                ? abilityCases.Value : null);
        }));
        try { return pending.Value; }
        catch
        {
            avatars.TryRemove(new KeyValuePair<int, Lazy<AvatarPackageItemDefinition>>(itemId, pending));
            throw;
        }
    }

    internal static AvatarPackageDefinition Parse(string text)
    {
        bool recognized = false;
        AvatarPackageDefinition Reject(string reason) => new(0, [], [], reason) { Recognized = recognized };
        if (!Regex.IsMatch(Scalar(text, "stackable type"), @"^\s*`\[booster selection\]`\s+0\s*$"))
            return Reject("其他礼包入口");
        string masked = MaskStrings(text);
        var blocks = Regex.Matches(masked, @"\[booster select category\](.*?)\[/booster select category\]", RegexOptions.Singleline);
        // 身份先于可用性判定；上层未闭合资格不能让已声明装扮族回落为普通自选包。
        recognized = blocks.Any(block =>
        {
            string body = block.Groups[1].Value;
            var header = Regex.Match(body, @"^\s*(\d+)\s+(\d+)\s*");
            return header.Success && byte.TryParse(header.Groups[1].Value, out _) && byte.TryParse(header.Groups[2].Value, out _) &&
                Regex.IsMatch(body[header.Length..], @"\[(?:avatar|special avatar)\]");
        });
        foreach (string tag in new[] { "booster info", "package data", "cooltime maintenance", "effect maintenance",
            "total usable count", "total weekly usable count", "total accumulated usable count", "use count limit all server item",
            "usable datetime", "usable first growtype", "random box", "random box removal items", "raid booster item",
            "convert boost item", "check usable itemlevel", "set disjoint instance type", "set epic exchange instance type",
            "random option unseal", "custom ability grade", "daybreak drop item", "battle royal", "action usable server type",
            "usable game server type", "usable server type", "booster select custom ability info" })
            if (Fields(text, tag).Count != 0) return Reject(tag);
        if (!int.TryParse(Scalar(text, "booster selection num").Trim(), out int selection) || selection < 0)
            return Reject("装扮选择数量无效");
        var dimensions = Tokens(Scalar(text, "booster category num"));
        if (dimensions.Length is not (2 or 3) || !int.TryParse(dimensions[0], out int rank) || rank != dimensions.Length - 1)
            return Reject("装扮分类维数无效");
        var sizes = ImmutableArray.CreateBuilder<int>();
        foreach (string token in dimensions.Skip(1))
        {
            if (!int.TryParse(token, out int size) || size is < 0 or > 256) return Reject("装扮分类范围无效");
            // 零维度表示不展示分类选择，仍使用唯一的坐标0；不能放宽为任意分类。
            sizes.Add(Math.Max(1, size));
        }
        if (blocks.Count == 0 || blocks.Count != Fields(text, "booster select category").Count)
            return Reject("装扮分类缺失或未闭合");
        var categories = ImmutableArray.CreateBuilder<AvatarPackageCategory>();
        var keys = new HashSet<(byte, byte)>();
        foreach (Match block in blocks)
        {
            string body = block.Groups[1].Value;
            var header = Regex.Match(body, @"^\s*(\d+)\s+(\d+)\s*");
            if (!header.Success || !byte.TryParse(header.Groups[1].Value, out byte category) ||
                !byte.TryParse(header.Groups[2].Value, out byte subcategory) || !keys.Add((category, subcategory)) ||
                category >= sizes[0] || (rank == 1 ? subcategory != 0 : subcategory >= sizes[1]))
                return Reject("装扮分类索引无效或重复");
            categories.Add(ParseCategory(category, subcategory, body[header.Length..]));
        }
        return new(selection, sizes.ToImmutable(), categories.ToImmutable()) { Recognized = recognized };
    }

    static AvatarPackageCategory ParseCategory(byte category, byte subcategory, string body)
    {
        AvatarPackageCategory Reject(string reason) => new(category, subcategory, [], reason);
        var rows = ImmutableArray.CreateBuilder<AvatarPackageRow>();
        int at = 0;
        while (at < body.Length)
        {
            while (at < body.Length && char.IsWhiteSpace(body[at])) at++;
            if (at == body.Length) break;
            var field = Regex.Match(body[at..], @"^\[([^/\]\r\n]+)\]([^\[]*)");
            if (!field.Success) return Reject("未识别的装扮分类字段");
            at += field.Length;
            string tag = field.Groups[1].Value;
            var values = Tokens(field.Groups[2].Value);
            if (tag is "default select" or "recommend")
            {
                if (values.Length == 0 || !int.TryParse(values[0], out int count) || count < 0 || count != values.Length - 1 ||
                    values.Skip(1).Any(value => !int.TryParse(value, out _))) return Reject("装扮选择提示无效");
                continue; // UI提示不代替客户端实际选择，也不产生额外奖励。
            }
            if (tag != "avatar") return Reject(tag);
            const string end = "[/avatar]";
            if (!body.AsSpan(at).StartsWith(end, StringComparison.Ordinal) || values.Length == 0 || values.Length % 4 != 0)
                return Reject("装扮奖励行宽或闭合标记无效");
            at += end.Length;
            for (int i = 0; i < values.Length; i += 4)
            {
                if (!int.TryParse(values[i], out int id) || id <= 0 || !ushort.TryParse(values[i + 1], out ushort count) || count == 0 ||
                    !ushort.TryParse(values[i + 2], out ushort type) || !byte.TryParse(values[i + 3], out byte ability))
                    return Reject("装扮奖励参数超出字段范围");
                rows.Add(new(id, count, type, ability));
            }
        }
        return rows.Count == 0 ? Reject("装扮分类为空") : new(category, subcategory, rows.ToImmutable());
    }

    internal static AvatarPackageItemDefinition ParseAvatar(string text,
        IReadOnlyDictionary<int, ImmutableHashSet<byte>>? abilityCases = null)
    {
        AvatarPackageItemDefinition Reject(string reason) => new([], [], reason);
        if (Fields(text, "import script").Count != 0) return Reject("装扮import继承视图尚未闭合");
        ImmutableArray<AvatarTypeSelectionRow> rawTypes;
        try { rawTypes = AvatarTypeSelectionFile.Parse(new PvfDocument(text)); }
        catch (InvalidDataException error) { return Reject(error.Message); }
        if (rawTypes.IsEmpty && Fields(text, "avatar type select").Count != 0) return Reject("装扮期限类型无效");
        // Client 04135EE0先插入index0的7天无孔类型，再追加PVF显式行；礼盒直接使用该索引。
        var types = ImmutableArray.CreateBuilder<AvatarPackageType>();
        if (!rawTypes.IsEmpty) types.Add(new(7, default));
        foreach (var raw in rawTypes)
        {
            var sockets = new ushort[5];
            for (int i = 0; i < raw.Sockets.Length; i++)
            {
                sockets[i] = raw.Sockets[i] switch
                {
                    "[A socket]" => 1, "[B socket]" => 2, "[C socket]" => 4,
                    "[D socket]" => 8, "[S socket]" => 16, "[M socket]" => 239, _ => 0
                };
                if (sockets[i] == 0) return Reject("未知装扮孔位类型");
            }
            types.Add(new(raw.Days, new(sockets[0], sockets[1], sockets[2], sockets[3], sockets[4]), raw.Gold, raw.WinPoints, raw.Cera));
        }
        string direct = Block(text, "avatar select ability");
        ImmutableHashSet<byte> abilities;
        if (Fields(text, "avatar select ability").Count != 0)
        {
            abilities = ParseAbilities(direct);
            if (abilities.IsEmpty) return Reject("装扮属性选项无效");
        }
        else if (Fields(text, "ability case index").Count != 0)
        {
            if (!int.TryParse(Scalar(text, "ability case index").Trim(), out int index) ||
                abilityCases is null || !abilityCases.TryGetValue(index, out abilities!) || abilities.IsEmpty)
                return Reject("装扮共用属性资源缺失");
        }
        else abilities = [0];
        return new(types.ToImmutable(), abilities)
        {
            RequiresAbilityChoice = Fields(text, "avatar select ability").Count != 0 || Fields(text, "ability case index").Count != 0
        };
    }

    internal static ImmutableHashSet<byte> ParseAbilities(string text)
    {
        var tokens = Tokens(text);
        var options = ImmutableHashSet.CreateBuilder<byte>();
        for (int at = 0; at < tokens.Length;)
        {
            if (tokens.Length - at < 4 || !byte.TryParse(tokens[at++], out byte option) || !options.Add(option)) return [];
            string kind = tokens[at++];
            if (!Regex.IsMatch(kind, @"^`\[[^\]]+\]`$")) return [];
            if (kind == "`[SKILL_LEVEL]`")
            {
                if (tokens.Length - at < 3 || !Regex.IsMatch(tokens[at++], @"^`\[[^\]]+\]`$") ||
                    !int.TryParse(tokens[at++], out int skill) || skill < 0 || !int.TryParse(tokens[at++], out int level) || level < 0) return [];
            }
            else if (tokens[at++] is not ("`+`" or "`*`") ||
                !decimal.TryParse(tokens[at++], NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return [];
        }
        return options.ToImmutable();
    }

    internal static IReadOnlyDictionary<int, ImmutableHashSet<byte>> ParseAbilityCases(string text)
    {
        string body = Block(text, "ability datas");
        if (body.Length == 0) throw new InvalidDataException("装扮共用属性表缺失或重复");
        var blocks = Regex.Matches(MaskStrings(body), @"\[ability case\](.*?)\[/ability case\]", RegexOptions.Singleline);
        if (blocks.Count == 0 || blocks.Count != Fields(body, "ability case").Count)
            throw new InvalidDataException("装扮共用属性case未完整闭合");
        var cases = ImmutableDictionary.CreateBuilder<int, ImmutableHashSet<byte>>();
        int at = 0;
        foreach (Match block in blocks)
        {
            if (!string.IsNullOrWhiteSpace(body[at..block.Index])) throw new InvalidDataException("装扮共用属性表含未知字段");
            at = block.Index + block.Length;
            var value = block.Groups[1];
            string raw = body.Substring(value.Index, value.Length);
            var header = Regex.Match(raw, @"^\s*(\d+)\s+");
            if (!header.Success || !int.TryParse(header.Groups[1].Value, out int id) || cases.ContainsKey(id))
                throw new InvalidDataException("装扮共用属性case索引无效或重复");
            // 非法case保留为空定义，实际选择该case时整件拒绝；不删除其中的未知选项后冒充完整成功。
            cases.Add(id, ParseAbilities(raw[header.Length..]));
        }
        if (!string.IsNullOrWhiteSpace(body[at..])) throw new InvalidDataException("装扮共用属性表含未知尾段");
        return cases.ToImmutable();
    }

    static string[] Tokens(string text) => Regex.Matches(text, "`[^`]*`|\\S+").Select(match => match.Value).ToArray();
    static string MaskStrings(string text) => Regex.Replace(text, "`[^`]*`", match => new string(' ', match.Length));
    static MatchCollection Fields(string text, string tag) => Regex.Matches(MaskStrings(text),
        @"\[" + Regex.Escape(tag) + @"\]([^\[]*)", RegexOptions.CultureInvariant);
    static string Scalar(string text, string tag)
    {
        var fields = Fields(text, tag);
        if (fields.Count != 1) return "";
        var value = fields[0].Groups[1];
        return text.Substring(value.Index, value.Length);
    }
    static string Block(string text, string tag)
    {
        var blocks = Regex.Matches(MaskStrings(text), @"\[" + Regex.Escape(tag) + @"\](.*?)\[/" + Regex.Escape(tag) + @"\]", RegexOptions.Singleline);
        if (blocks.Count != 1 || Fields(text, tag).Count != 1) return "";
        var value = blocks[0].Groups[1];
        return text.Substring(value.Index, value.Length);
    }
}
