using PvfLib.Scripting;

namespace PvfLib;

public sealed record StackableTypeDeclaration(string Label, IReadOnlyList<int> Parameters);

/// <summary>堆叠物品的原始声明，不包含持有数量、商店准入或消耗行为。</summary>
public sealed record StackableFile(int TemplateId, string ScriptPath,
    StackableTypeDeclaration? TypeDeclaration, int? DeclaredStackLimit, int? DeclaredPrice,
    int? DeclaredSellPrice, int? DeclaredMinimumLevel, string? DeclaredItemGroupName,
    IReadOnlyList<string> UnsupportedFields)
{
    public StackableActionDeclaration? ActionDeclaration { get; init; }
    public bool HasActionType { get; init; }
    public int? DeclaredMaximumLevel { get; init; }
    public bool HasMaximumLevel { get; init; }
    public bool HasMinimumLevel { get; init; }
    public int? DeclaredUseActionPacket { get; init; }
    public bool HasUseActionPacket { get; init; }
    public IReadOnlyList<string>? DeclaredUsableJobs { get; init; }
    public bool HasUsableJobs { get; init; }
    public IReadOnlyList<string>? DeclaredActionUsablePlaces { get; init; }
    public bool HasActionUsablePlace { get; init; }
    public IReadOnlyList<string>? DeclaredActionUsableConditions { get; init; }
    public IReadOnlyList<string>? DeclaredUsableWorlds { get; init; }
    public IReadOnlyList<string>? DeclaredActionUsableServerTypes { get; init; }
    // 商店兑换价格，不是使用道具时的附加成本。
    public IReadOnlyList<int>? DeclaredNeededMaterials { get; init; }
    public IReadOnlyList<int>? DeclaredDontChangeGrowTypes { get; init; }
    public IReadOnlyList<int>? DeclaredUsableFirstGrowTypes { get; init; }
    public bool ClassChangeGrowTypeCheckOff { get; init; }
    public IReadOnlyList<int>? DeclaredActionLimitIndexes { get; init; }
    public IReadOnlyList<int>? DeclaredActionResultLimitIndexes { get; init; }
    public int? DeclaredCreatureMinimumLevel { get; init; }
    public bool HasCreatureMinimumLevel { get; init; }
    public int? DeclaredLevelUpRange { get; init; }
    public int? DeclaredCoolTime { get; init; }
    public bool HasCoolTime { get; init; }
    public bool HasBoosterInfo { get; init; }
    public IReadOnlyList<int>? DeclaredBoosterEtc { get; init; }
    public int? DeclaredStackInAccountInventory { get; init; }
    public bool HasStackInAccountInventory { get; init; }
    public bool HasCommonInventory { get; init; }

    public static StackableFile Parse(int templateId, string path, PvfToken[] tokens, Func<int, string> resolve)
    {
        var fields = new PvfTokenFields(tokens, resolve,
            ["stackable type", "stack limit", "price", "sell price", "minimum level", "item group name",
                "action type", "maximum level", "use action packet", "usable job", "action usable place",
                "action usable server type", "need material", "dont change grow type", "usable first growtype",
                "class change grow type check off", "stackable action limit indexes",
                "stackable action result limit indexes", "creature minimum level", "levelup range", "cool time",
                "stack in account inventory", "stack in account common item inventory"]);
        StackableTypeDeclaration? type = null;
        PvfTokenDocument? repeatedDocument = null;
        PvfTokenSection[] RepeatedSections(string name)
        {
            int count = tokens.Count(token => token.Type == 3 && resolve(token.Value) == "[" + name + "]");
            if (count < 2) return [];
            try
            {
                repeatedDocument ??= new(tokens, resolve);
                var sections = repeatedDocument.Sections.Where(section => section.Name == name).ToArray();
                // 只兼容根级完整声明，不把嵌套或损坏的声明抹成最后一个有效值。
                return repeatedDocument.Diagnostics.IsEmpty && sections.Length == count &&
                    sections.All(section => section.Children.IsEmpty) ? sections : [];
            }
            catch (InvalidDataException) { return []; }
        }
        if (fields.TryGet("stackable type", out var values))
        {
            type = ReadType(values, resolve);
            if (type is null)
                fields.Unsupported("stackable type");
        }
        else
        {
            var declarations = RepeatedSections("stackable type").Select(section => ReadType(section.Values.AsSpan(), resolve)).ToArray();
            // 与客户端及 ItemCatalog 一致，堆叠类型由最后一次完整声明决定。
            if (declarations.Length > 1 && declarations.All(declaration => declaration is not null)) type = declarations[^1];
        }
        int? limit = fields.Scalar("stack limit", true), price = fields.Scalar("price", true),
            sellPrice = fields.Scalar("sell price", true), minimum = fields.Scalar("minimum level", true);
        string? group = null;
        if (fields.TryGet("item group name", out values))
        {
            if (values.Length == 1 && values[0].Type == 6)
                group = resolve(values[0].Value);
            else
                fields.Unsupported("item group name");
        }
        StackableActionDeclaration? action = null;
        if (fields.TryGet("action type", out values))
        {
            action = StackableActionDeclaration.ReadValues(values, resolve);
            if (action is null || action.Arguments.Any(value => value.Type is not (0 or 1 or 2 or 6)))
                fields.Unsupported("action type");
        }
        IReadOnlyList<string>? jobs = null;
        if (fields.TryGet("usable job", out values))
        {
            if (values.ToArray().All(token => token.Type == 6))
                jobs = Array.AsReadOnly(values.ToArray().Select(token => resolve(token.Value)).ToArray());
            else fields.Unsupported("usable job");
        }
        bool hasMaximum = tokens.Any(token => token.Type == 3 && resolve(token.Value) == "[maximum level]");
        var places = ReadActionUsablePlaces(tokens, resolve);
        bool hasUseAction = tokens.Any(token => token.Type == 3 && resolve(token.Value) == "[use action packet]");
        int? maximum = fields.Scalar("maximum level"), useAction = fields.Scalar("use action packet");
        IReadOnlyList<int>? Integers(string name)
        {
            if (!tokens.Any(token => token.Type == 3 && resolve(token.Value) == "[" + name + "]")) return [];
            if (!fields.TryGet(name, out var data) || data.ToArray().Any(token => token.Type != 0)) return null;
            return Array.AsReadOnly(data.ToArray().Select(token => token.Value).ToArray());
        }
        IReadOnlyList<string>? servers = null;
        if (tokens.Any(token => token.Type == 3 && resolve(token.Value) == "[action usable server type]") &&
            fields.TryGet("action usable server type", out values) && values.ToArray().All(token => token.Type == 6))
            servers = Array.AsReadOnly(values.ToArray().Select(token => resolve(token.Value)).ToArray());
        bool checkOff = tokens.Any(token => token.Type == 3 && resolve(token.Value) == "[class change grow type check off]");
        if (checkOff && (!fields.TryGet("class change grow type check off", out values) || values.Length != 0))
            fields.Unsupported("class change grow type check off");
        int? levelUpRange=fields.Scalar("levelup range"), coolTime=fields.Scalar("cool time");
        bool hasBooster = tokens.Any(token => token.Type == 3 && resolve(token.Value) == "[booster info]");
        IReadOnlyList<int>? boosterEtc = null;
        if (hasBooster)
        {
            var document = new PvfTokenDocument(tokens, resolve);
            var sections = document.Sections.Where(section => section.Name == "booster info").ToArray();
            if (document.Diagnostics.IsEmpty && sections is [{ Values.Length: 0, Children: [{ Name: "etc", Children.Length: 0 } child] }] &&
                child.Values.All(token => token.Type == 0))
                boosterEtc = Array.AsReadOnly(child.Values.Select(token => token.Value).ToArray());
        }
        int? accountInventory = fields.Scalar("stack in account inventory");
        if (accountInventory is not (null or 0 or 1)) fields.Unsupported("stack in account inventory");
        var unsupported = fields.UnsupportedFields.Where(name => !ItemUseFields.IsIgnoredServerType(name) && !(name == "stackable type" && type is not null ||
            name == "action usable place" && places is not null)).ToArray();
        return new(templateId, path, type, limit, price, sellPrice, minimum, group, Array.AsReadOnly(unsupported))
        {
            // stack in account common item inventory 是无参数的存在标记，不能按账号材料的0/1标量读取。
            HasCommonInventory = tokens.Any(token => token.Type == 3 && resolve(token.Value) == "[stack in account common item inventory]"),
            DeclaredStackInAccountInventory = accountInventory,
            HasStackInAccountInventory = tokens.Any(token => token.Type == 3 && resolve(token.Value) == "[stack in account inventory]"),
            ActionDeclaration = action, HasActionType = tokens.Any(token => token.Type == 3 && resolve(token.Value) == "[action type]"),
            DeclaredMaximumLevel = maximum, HasMaximumLevel = hasMaximum,
            HasMinimumLevel = tokens.Any(token => token.Type == 3 && resolve(token.Value) == "[minimum level]"),
            DeclaredUseActionPacket = useAction, HasUseActionPacket = hasUseAction, DeclaredUsableJobs = jobs,
            HasUsableJobs = tokens.Any(token => token.Type == 3 && resolve(token.Value) == "[usable job]"),
            DeclaredActionUsablePlaces = places,
            DeclaredActionUsableConditions = ItemUseFields.ActionConditions(tokens, resolve),
            DeclaredUsableWorlds = ItemUseFields.UsableWorlds(tokens, resolve),
            HasActionUsablePlace = tokens.Any(token => token.Type == 3 && resolve(token.Value) == "[action usable place]"),
            DeclaredActionUsableServerTypes = servers,
            DeclaredNeededMaterials = Integers("need material"),
            DeclaredDontChangeGrowTypes = Integers("dont change grow type"),
            DeclaredUsableFirstGrowTypes = Integers("usable first growtype"),
            ClassChangeGrowTypeCheckOff = checkOff,
            DeclaredActionLimitIndexes = Integers("stackable action limit indexes"),
            DeclaredActionResultLimitIndexes = Integers("stackable action result limit indexes"),
            DeclaredCreatureMinimumLevel = fields.Scalar("creature minimum level"),
            HasCreatureMinimumLevel = tokens.Any(token => token.Type == 3 && resolve(token.Value) == "[creature minimum level]"),
            DeclaredLevelUpRange = levelUpRange,
            DeclaredCoolTime = coolTime,
            HasCoolTime = tokens.Any(token => token.Type == 3 && resolve(token.Value) == "[cool time]"),
            HasBoosterInfo = hasBooster, DeclaredBoosterEtc = boosterEtc
        };
    }

    /// <summary>按资源顺序读取各次地点声明中的连续字符串；没有声明与空声明分别保留。</summary>
    public static IReadOnlyList<string>? ReadActionUsablePlaces(PvfToken[] tokens, Func<int, string> resolve)
    {
        List<string>? places = null;
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Type != 3 || resolve(tokens[i].Value) != "[action usable place]") continue;
            places ??= [];
            // df_game_r 的 ScanStr 遇到非字符串即结束本次读取；重复声明继续追加。
            while (i + 1 < tokens.Length && tokens[i + 1].Type == 6)
                places.Add(resolve(tokens[++i].Value));
        }
        return places is null ? null : Array.AsReadOnly(places.ToArray());
    }

    static StackableTypeDeclaration? ReadType(ReadOnlySpan<PvfToken> values, Func<int, string> resolve)
    {
        // 已支持形状为字符串后接1、2或4个整数，参数不推断成背包类型枚举。
        if (values.Length is not (2 or 3 or 5) || values[0].Type != 6) return null;
        var parameters = new int[values.Length - 1];
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i].Type != 0) return null;
            parameters[i - 1] = values[i].Value;
        }
        return new(resolve(values[0].Value), Array.AsReadOnly(parameters));
    }
}
