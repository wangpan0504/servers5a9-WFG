using System.Collections.Frozen;
using PvfLib;

namespace DfoServer.GameWorld.Guilds;

internal sealed record GuildNpcLevelDefinition(int Level, byte ClassTier, byte RequiredTier, byte RequiredLevel,
    string FirstCurrency, int FirstAmount, string SecondCurrency, int SecondAmount, int UpgradeDays);
internal sealed record GuildNpcContractDefinition(int NpcLevel, int Option, int Days, int PublicFunds);
internal sealed record GuildNpcDefinition(uint Id, bool Contract, GuildNpcLevelDefinition[] Levels)
{
    public GuildNpcLevelDefinition? At(int level) => Levels.SingleOrDefault(row => row.Level == level);
}

internal sealed class GuildNpcCatalog
{
    // 匹配客户端00527500的个人公会硬币计数；与公共公会结晶是不同资产。
    public const int MemberCoinItem = 490011648;
    public const uint Guardian = 400001060;
    public const uint Spirit = 400001061;
    public const uint Alchemist = 400001065;
    readonly FrozenDictionary<uint, GuildNpcDefinition> definitions;
    readonly GuildNpcContractDefinition[] contracts;
    public IReadOnlyCollection<GuildNpcDefinition> Definitions => definitions.Values;
    public GuildNpcCatalog(PvfArchive pvf) : this(pvf.ReadTokens("etc/guildreform.etc"), pvf.ResolveString) { }
    internal GuildNpcCatalog(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        PvfTokenSection Target(string tag)
        {
            var starts = Enumerable.Range(0, tokens.Count).Where(at => tokens[at].Type == 3 &&
                resolve(tokens[at].Value) == "[" + tag + "]").ToArray();
            var ends = Enumerable.Range(0, tokens.Count).Where(at => tokens[at].Type == 3 &&
                resolve(tokens[at].Value) == "[/" + tag + "]").ToArray();
            if (starts.Length != 1 || ends.Length != 1 || ends[0] <= starts[0])
                throw new InvalidDataException("公会NPC目标段缺失、重复或闭合无效: " + tag);
            // 同文件的其它表复用index标签且有不同闭合风格，不能让训练表的index闭合改变NPC目标段层级。
            var document = new PvfTokenDocument(tokens.Skip(starts[0]).Take(ends[0] - starts[0] + 1).ToArray(), resolve);
            if (!document.Diagnostics.IsEmpty || document.Sections is not [{ } section] || section.Name != tag)
                throw new InvalidDataException("公会NPC目标段声明结构不完整: " + tag);
            return section;
        }
        var root = Target("npc info");
        var result = new Dictionary<uint, GuildNpcDefinition>();
        string Text(PvfToken token) => token.Type is 6 or 7 ? resolve(token.Value) :
            throw new InvalidDataException("公会NPC资源需要字符串");
        int Integer(PvfToken token) => token.Type == 0 ? token.Value :
            throw new InvalidDataException("公会NPC资源需要整数");
        foreach (var npc in root.Children)
        {
            if (npc.Name != "npc index" || npc.Values is not [{ Type: 0, Value: > 0 } id])
                throw new InvalidDataException("公会NPC索引无效");
            var contract = npc.Children.SingleOrDefault(row => row.Name == "contract type");
            var levels = npc.Children.SingleOrDefault(row => row.Name == "level info");
            if (contract?.Values is not [{ } type] || levels is null || levels.Values.Length == 0 || levels.Values.Length % 9 != 0)
                throw new InvalidDataException("公会NPC契约或等级声明无效");
            bool paidContract = Text(type) switch { "free" => false, "contract" => true,
                _ => throw new InvalidDataException("公会NPC契约类型无效") };
            var rows = new List<GuildNpcLevelDefinition>();
            for (int at = 0; at < levels.Values.Length; at += 9)
            {
                var values = levels.Values;
                int level = Integer(values[at]), requiredLevel = Integer(values[at + 3]);
                int firstAmount = Integer(values[at + 5]), secondAmount = Integer(values[at + 7]);
                int days = Integer(values[at + 8]);
                string firstCurrency = Text(values[at + 4]), secondCurrency = Text(values[at + 6]);
                if (level != rows.Count + 1 || requiredLevel is < 0 or > 10 || firstAmount < 0 || secondAmount < 0 ||
                    days < 0 || days > 365 || firstCurrency is not ("public funds" or "guild crystal") ||
                    secondCurrency is not ("public funds" or "guild crystal"))
                    throw new InvalidDataException("公会NPC等级、费用或时间越界");
                rows.Add(new(level, Tier(Text(values[at + 1])), Tier(Text(values[at + 2])), (byte)requiredLevel,
                    firstCurrency, firstAmount, secondCurrency, secondAmount, days));
            }
            if (!result.TryAdd((uint)id.Value, new((uint)id.Value, paidContract, rows.ToArray())))
                throw new InvalidDataException("公会NPC索引重复");
        }
        if (result.Count != 8 || !result.ContainsKey(Guardian)) throw new InvalidDataException("公会NPC名册不完整");
        definitions = result.ToFrozenDictionary();
        var contractRows = Target("npc contract info");
        if (contractRows.Values.Length == 0 || contractRows.Values.Length % 3 != 0)
            throw new InvalidDataException("公会NPC契约费用声明无效");
        var contractList = new List<GuildNpcContractDefinition>();
        for (int at = 0; at < contractRows.Values.Length; at += 3)
        {
            int level = Integer(contractRows.Values[at]), days = Integer(contractRows.Values[at + 1]);
            int price = Integer(contractRows.Values[at + 2]);
            if (level is < 1 or > 5 || days is < 1 or > 90 || price <= 0)
                throw new InvalidDataException("公会NPC契约费用越界");
            contractList.Add(new(level, contractList.Count(row => row.NpcLevel == level), days, price));
        }
        contracts = contractList.ToArray();
    }
    public GuildNpcDefinition? Find(uint id) => definitions.GetValueOrDefault(id);
    public GuildNpcContractDefinition? Contract(int level, uint option) =>
        contracts.SingleOrDefault(row => row.NpcLevel == level && row.Option == option);
    static byte Tier(string value) => value switch
    {
        "low class" => 0, "mid class" => 1, "high class" => 2, "guardian" => 3, "conqueror" => 4,
        _ => throw new InvalidDataException("公会NPC档位无效")
    };
}
