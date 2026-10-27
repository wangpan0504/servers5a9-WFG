using System.Collections.Concurrent;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record ChronicleInvestmentMetadata(bool Valid, byte Type, string Reason);

internal sealed partial class CharacterInvestmentMetadataCatalog
{
    readonly ConcurrentDictionary<int, Lazy<ChronicleDefinition>> chronicles = new();
    readonly ConcurrentDictionary<int, Lazy<ChronicleDefinition>> installedChronicles = new();
    readonly record struct ChronicleKey(byte Job, byte Grow, byte Slot, byte Number);
    sealed record ChronicleDefinition(byte Type, ImmutableDictionary<ChronicleKey, ImmutableArray<PvfTokenSection>> Entries);
    static readonly string[] ChronicleJobs = ["[swordman]", "[fighter]", "[gunner]", "[mage]", "[priest]",
        "[at gunner]", "[thief]", "[at fighter]", "[at mage]", "[demonic swordman]", "[creator mage]",
        "[at swordman]", "[knight]", "[demonic lancer]", "[at priest]", "[gun blader]"];

    public ChronicleInvestmentMetadata ReadChronicle(int id, byte job, byte grow, byte slot, byte number)
        => ReadChronicle(id, job, grow, slot, number, false);

    // v2.3识别安装器保存的原效果模板；旧版本的动作声明合同与缓存不变。
    public ChronicleInvestmentMetadata ReadInstalledChronicle(int id, byte job, byte grow, byte slot, byte number)
        => ReadChronicle(id, job, grow, slot, number, id is >= 1254 and <= 1256);

    ChronicleInvestmentMetadata ReadChronicle(int id, byte job, byte grow, byte slot, byte number, bool installedEffect)
    {
        if (id <= 0 || !stackable.ContainsKey(id)) return new(false, 0, "unknown_template");
        var cache = installedEffect ? installedChronicles : chronicles;
        var definition = cache.GetOrAdd(id, key => new(() => LoadChronicle(key, installedEffect))).Value;
        if (!definition.Entries.TryGetValue(new(job, grow, slot, number), out var entries))
            return new(false, definition.Type, "missing_key");
        if (entries.Length != 1) return new(false, definition.Type, "ambiguous_key");
        var skill = entries[0];
        if (!skill.IsBlock || !skill.Children.IsEmpty || skill.Values.Length < 8 || (skill.Values.Length - 1) % 7 != 0)
            return new(false, definition.Type, "unsupported_effect");
        // 只确认已安装来源可解释，不按效果数值计分；负数可表示冷却/消耗减少。
        for (int i = 1; i < skill.Values.Length; i += 7)
        {
            var values = skill.Values;
            if (values[i].Type != 6 || !ChronicleJobs.Contains(resolve(values[i].Value), StringComparer.Ordinal) ||
                values[i + 1].Type != 0 || values[i + 1].Value < 0 ||
                values[i + 2].Type != 6 || resolve(values[i + 2].Value) != "[dungeon type]" ||
                values[i + 3].Type != 6 || resolve(values[i + 3].Value) is not ("[level]" or "[static]" or "[cooltime]" or "[mp]") ||
                values[i + 4].Type != 0 || values[i + 4].Value < 0 ||
                values[i + 5].Type != 6 || resolve(values[i + 5].Value) is not ("%" or "+") || values[i + 6].Type != 0)
                return new(false, definition.Type, "unsupported_effect");
        }
        return new(true, definition.Type, "validated_installed_option");
    }

    ChronicleDefinition LoadChronicle(int id, bool installedEffect)
    {
        var chain = new List<PvfTokenDocument>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string path = Path("stackable", stackable[id]);
        while (true)
        {
            if (chain.Count >= 32 || !visited.Add(path)) throw new InvalidDataException("气息导入循环或超过32层");
            var doc = Document(path);
            chain.Add(doc);
            var import = One(doc, "import script");
            if (import is null) break;
            if (import.IsBlock || import.Values.Length != 1 || import.Values[0].Type != 6)
                throw new InvalidDataException("气息导入须为唯一字符串路径");
            path = Path("stackable", resolve(import.Values[0].Value));
        }
        PvfTokenSection Field(string name)
        {
            var fields = chain.SelectMany(doc => Roots(doc, name)).ToArray();
            if (fields.Length != 1) throw new InvalidDataException("气息字段缺失或继承歧义: " + name);
            return fields[0];
        }
        // EffectItemId固定为1254+Type；原效果表不是消耗品，不要求它重复声明安装动作。
        // 已声明的动作仍须唯一且匹配，不能用新入口吞掉冲突或错误动作。
        if (!installedEffect || chain.Any(doc => Roots(doc, "action type").Length != 0))
        {
            var action = Field("action type");
            if (!action.IsBlock || !action.Children.IsEmpty || action.Values.Length != 1 || action.Values[0].Type != 6 ||
                resolve(action.Values[0].Value) != "[3rd chronicle ability enchant]")
                throw new InvalidDataException("模板不是气息安装动作");
        }
        var kind = Field("stackable type");
        if (kind.IsBlock || kind.Values.Length != 2 || kind.Values[0].Type != 6 || resolve(kind.Values[0].Value) != "[etc]" ||
            kind.Values[1].Type != 0 || kind.Values[1].Value != 1)
            throw new InvalidDataException("气息模板种类不符");
        var type = Field("type");
        if (type.IsBlock || type.Values.Length != 1 || type.Values[0].Type != 0 || type.Values[0].Value is < 0 or > 2)
            throw new InvalidDataException("气息类型不符");
        if (installedEffect && type.Values[0].Value != id - 1254)
            throw new InvalidDataException("原气息效果模板与类型不符");
        var table = Field("3choro enchant");
        if (!table.IsBlock || !table.Values.IsEmpty || table.Children.IsEmpty)
            throw new InvalidDataException("气息表结构不完整");
        var entries = new Dictionary<ChronicleKey, List<PvfTokenSection>>();
        foreach (var group in table.Children)
        {
            if (!group.IsBlock || group.Name is not ("check" or "monitor") || group.Values.Length < 3)
                throw new InvalidDataException("气息键组无法定位");
            byte job = Byte(group.Values[0]), grow = Byte(group.Values[1]);
            int first = group.Name == "check" ? 2 : 3;
            int count = group.Name == "check" ? 1 : group.Values[2].Type == 0 ? group.Values[2].Value : -1;
            if (count is < 1 or > 11 || group.Values.Length != first + count)
                throw new InvalidDataException("气息键组部位数量不符");
            var slots = new HashSet<byte>();
            foreach (var token in group.Values.Skip(first))
            {
                if (token.Type != 6) throw new InvalidDataException("气息部位不是字符串");
                byte slot = ItemCatalog.Slot(resolve(token.Value));
                if (slot is not (12 or >= 14 and <= 23) || !slots.Add(slot))
                    throw new InvalidDataException("气息部位未知或重复");
            }
            foreach (var row in group.Children)
            {
                if (row.Name == "hide skill")
                {
                    if (!row.IsBlock || !row.Children.IsEmpty || row.Values.Any(t => t.Type != 0 || t.Value is < 0 or > 255))
                        throw new InvalidDataException("气息隐藏选择声明不完整");
                    continue;
                }
                if (row.Name == "skill explain")
                {
                    if (row.IsBlock || !row.Children.IsEmpty || row.Values.Length != 1 || row.Values[0].Type != 6)
                        throw new InvalidDataException("气息说明声明不完整");
                    continue;
                }
                if (row.Name != "skill" || row.Values.IsEmpty) throw new InvalidDataException("气息选择键无法定位");
                byte number = Byte(row.Values[0]);
                foreach (byte slot in slots)
                {
                    var key = new ChronicleKey(job, grow, slot, number);
                    if (!entries.TryGetValue(key, out var list)) entries.Add(key, list = []);
                    list.Add(row);
                }
            }
        }
        return new((byte)type.Values[0].Value, entries.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToImmutableArray()));
        static byte Byte(PvfToken token) => token.Type == 0 && token.Value is >= 0 and <= 255
            ? (byte)token.Value : throw new InvalidDataException("气息键超出byte合同");
    }
}
