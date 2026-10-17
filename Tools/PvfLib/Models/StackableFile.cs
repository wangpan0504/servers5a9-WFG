using PvfLib.Scripting;

namespace PvfLib;

public sealed record StackableTypeDeclaration(string Label, IReadOnlyList<int> Parameters);

/// <summary>堆叠物品的原始声明，不包含持有数量、商店准入或消耗行为。</summary>
public sealed record StackableFile(int TemplateId, string ScriptPath,
    StackableTypeDeclaration? TypeDeclaration, int? DeclaredStackLimit, int? DeclaredPrice,
    int? DeclaredSellPrice, int? DeclaredMinimumLevel, string? DeclaredItemGroupName,
    IReadOnlyList<string> UnsupportedFields)
{
    public StackableTypeDeclaration? ActionDeclaration { get; init; }
    public int? DeclaredMaximumLevel { get; init; }
    public bool HasMaximumLevel { get; init; }
    public bool HasMinimumLevel { get; init; }
    public int? DeclaredUseActionPacket { get; init; }
    public bool HasUseActionPacket { get; init; }
    public IReadOnlyList<string>? DeclaredUsableJobs { get; init; }
    public bool HasUsableJobs { get; init; }
    public IReadOnlyList<string>? DeclaredActionUsablePlaces { get; init; }
    public bool HasActionUsablePlace { get; init; }
    public IReadOnlyList<string>? DeclaredActionUsableServerTypes { get; init; }
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

    public static StackableFile Parse(int templateId, string path, PvfToken[] tokens, Func<int, string> resolve)
    {
        var fields = new PvfTokenFields(tokens, resolve,
            ["stackable type", "stack limit", "price", "sell price", "minimum level", "item group name",
                "action type", "maximum level", "use action packet", "usable job", "action usable place",
                "action usable server type", "need material", "dont change grow type", "usable first growtype",
                "class change grow type check off", "stackable action limit indexes",
                "stackable action result limit indexes", "creature minimum level", "levelup range", "cool time"]);
        StackableTypeDeclaration? type = null;
        if (fields.TryGet("stackable type", out var values))
        {
            // 已支持形状为字符串后接1、2或4个整数，参数不推断成背包类型枚举。
            if (values.Length is 2 or 3 or 5 && values[0].Type == 6)
            {
                var parameters = new List<int>(values.Length - 1);
                foreach (var token in values[1..])
                {
                    if (token.Type != 0)
                        break;
                    parameters.Add(token.Value);
                }
                if (parameters.Count == values.Length - 1)
                    type = new(resolve(values[0].Value), parameters.AsReadOnly());
            }
            if (type is null)
                fields.Unsupported("stackable type");
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
        StackableTypeDeclaration? action = null;
        if (fields.TryGet("action type", out values))
        {
            if (values.Length > 0 && values[0].Type == 6 && values[1..].ToArray().All(token => token.Type == 0))
                action = new(resolve(values[0].Value), Array.AsReadOnly(values[1..].ToArray().Select(token => token.Value).ToArray()));
            else fields.Unsupported("action type");
        }
        IReadOnlyList<string>? jobs = null;
        if (fields.TryGet("usable job", out values))
        {
            if (values.ToArray().All(token => token.Type == 6))
                jobs = Array.AsReadOnly(values.ToArray().Select(token => resolve(token.Value)).ToArray());
            else fields.Unsupported("usable job");
        }
        bool hasMaximum = tokens.Any(token => token.Type == 3 && resolve(token.Value) == "[maximum level]");
        IReadOnlyList<string>? places = null;
        if (fields.TryGet("action usable place", out values))
        {
            if (values.ToArray().All(token => token.Type == 6))
                places = Array.AsReadOnly(values.ToArray().Select(token => resolve(token.Value)).ToArray());
            else fields.Unsupported("action usable place");
        }
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
        return new(templateId, path, type, limit, price, sellPrice, minimum, group, fields.UnsupportedFields)
        {
            ActionDeclaration = action, DeclaredMaximumLevel = maximum, HasMaximumLevel = hasMaximum,
            HasMinimumLevel = tokens.Any(token => token.Type == 3 && resolve(token.Value) == "[minimum level]"),
            DeclaredUseActionPacket = useAction, HasUseActionPacket = hasUseAction, DeclaredUsableJobs = jobs,
            HasUsableJobs = tokens.Any(token => token.Type == 3 && resolve(token.Value) == "[usable job]"),
            DeclaredActionUsablePlaces = places,
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
            HasBoosterInfo = tokens.Any(token => token.Type == 3 && resolve(token.Value) == "[booster info]")
        };
    }
}
