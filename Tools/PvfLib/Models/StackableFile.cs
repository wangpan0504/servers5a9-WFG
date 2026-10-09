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
    public int? DeclaredUseActionPacket { get; init; }
    public bool HasUseActionPacket { get; init; }
    public IReadOnlyList<string>? DeclaredUsableJobs { get; init; }
    public bool HasUsableJobs { get; init; }
    public IReadOnlyList<string>? DeclaredActionUsablePlaces { get; init; }
    public bool HasActionUsablePlace { get; init; }

    public static StackableFile Parse(int templateId, string path, PvfToken[] tokens, Func<int, string> resolve)
    {
        var fields = new PvfTokenFields(tokens, resolve,
            ["stackable type", "stack limit", "price", "sell price", "minimum level", "item group name",
                "action type", "maximum level", "use action packet", "usable job", "action usable place"]);
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
        return new(templateId, path, type, limit, price, sellPrice, minimum, group, fields.UnsupportedFields)
        {
            ActionDeclaration = action, DeclaredMaximumLevel = maximum, HasMaximumLevel = hasMaximum,
            DeclaredUseActionPacket = useAction, HasUseActionPacket = hasUseAction, DeclaredUsableJobs = jobs,
            HasUsableJobs = tokens.Any(token => token.Type == 3 && resolve(token.Value) == "[usable job]"),
            DeclaredActionUsablePlaces = places,
            HasActionUsablePlace = tokens.Any(token => token.Type == 3 && resolve(token.Value) == "[action usable place]")
        };
    }
}
