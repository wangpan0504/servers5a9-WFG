using PvfLib.Scripting;

namespace PvfLib;

public sealed record StackableTypeDeclaration(string Label, IReadOnlyList<int> Parameters);

/// <summary>堆叠物品的原始声明，不包含持有数量、商店准入或消耗行为。</summary>
public sealed record StackableFile(int TemplateId, string ScriptPath,
    StackableTypeDeclaration? TypeDeclaration, int? DeclaredStackLimit, int? DeclaredPrice,
    int? DeclaredSellPrice, int? DeclaredMinimumLevel, string? DeclaredItemGroupName,
    IReadOnlyList<string> UnsupportedFields)
{
    public static StackableFile Parse(int templateId, string path, PvfToken[] tokens, Func<int, string> resolve)
    {
        var fields = new PvfTokenFields(tokens, resolve,
            ["stackable type", "stack limit", "price", "sell price", "minimum level", "item group name"]);
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
        return new(templateId, path, type, limit, price, sellPrice, minimum, group, fields.UnsupportedFields);
    }
}
