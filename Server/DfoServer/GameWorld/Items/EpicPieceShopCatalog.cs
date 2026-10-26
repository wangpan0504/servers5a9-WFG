using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record EpicPieceShopCatalog(int NpcId, int Provided, int Received, int AppearancePercent,
    int RefreshLimit, int RefreshGold, int Choices, int SameType, int NormalType, int SoulCost)
{
    public const string Path = "etc/epicpieceshop.etc";
    public static EpicPieceShopCatalog Load(PvfArchive archive) => Parse(archive.ReadTokens(Path), archive.ResolveString);

    internal static EpicPieceShopCatalog Parse(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        if (!document.Diagnostics.IsEmpty) throw new InvalidDataException("碎片交换配置包含未配对标签");
        var fields = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in document.Sections)
        {
            if (section.Values is not [{ Type: 0 } value] || !section.Children.IsEmpty)
                throw new InvalidDataException("碎片交换配置必须是完整整数: " + section.Name);
            // 04259620顺序读取并覆盖同一字段；不把重复的合法声明累加。
            fields[section.Name] = value.Value;
        }
        int Value(string name) => fields.TryGetValue(name, out int value) ? value :
            throw new InvalidDataException("碎片交换缺少字段: " + name);
        var result = new EpicPieceShopCatalog(Value("npcindex"), Value("epic piece provide"), Value("epic piece received"),
            Value("gabriel appearance probability"), Value("list change chance"), Value("list change cost"),
            Value("kind of epic piece"), Value("same type"), Value("normal type"), Value("cost of epic soul"));
        result.Validate();
        return result;
    }

    public void Validate()
    {
        if (NpcId <= 0 || Provided <= 0 || Received <= 0 || AppearancePercent is < 0 or > 100 ||
            RefreshLimit is < 0 or > 255 || RefreshGold < 0 || Choices is < 1 or > 5 ||
            SameType < 0 || NormalType < 0 || (long)SameType + NormalType != Choices || SoulCost < 0)
            throw new InvalidDataException("碎片交换配置超出本版客户端数量或费用范围");
    }
}
