using System.Collections.Frozen;
using PvfLib;

namespace DfoServer.GameWorld.Guilds;

internal sealed record GuildNpcBenefitDefinition(uint Id, uint NpcId, int Level, bool Common, string Effect,
    string FirstCurrency, int FirstAmount, string SecondCurrency, int SecondAmount, int DurationSeconds, int[] Parameters);

internal sealed class GuildNpcBenefitCatalog
{
    readonly FrozenDictionary<uint, GuildNpcBenefitDefinition> definitions;
    readonly FrozenDictionary<int, uint> guardianBuffs;
    public IReadOnlyCollection<GuildNpcBenefitDefinition> Definitions => definitions.Values;
    public int TrainingIntervalSeconds { get; }

    public GuildNpcBenefitCatalog(PvfArchive pvf) : this(pvf.ReadTokens("etc/guildreform.etc"), pvf.ResolveString,
        LoadGuardianBuffs(pvf)) { }

    internal GuildNpcBenefitCatalog(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve,
        IReadOnlySet<int> supportedGuardianBuffs)
    {
        PvfToken[] Section(string name, bool closed)
        {
            int[] starts = Enumerable.Range(0, tokens.Count).Where(at => tokens[at].Type == 3 &&
                resolve(tokens[at].Value) == "[" + name + "]").ToArray();
            if (starts.Length != 1) throw new InvalidDataException("公会NPC福利段缺失或重复: " + name);
            int end = starts[0] + 1;
            while (end < tokens.Count && tokens[end].Type != 3) end++;
            if (closed && (end == tokens.Count || resolve(tokens[end].Value) != "[/" + name + "]"))
                throw new InvalidDataException("公会NPC福利段闭合无效: " + name);
            return tokens.Skip(starts[0] + 1).Take(end - starts[0] - 1).ToArray();
        }
        int Number(PvfToken token) => token.Type == 0 ? token.Value :
            throw new InvalidDataException("公会NPC福利需要整数");
        string Text(PvfToken token) => token.Type is 6 or 7 ? resolve(token.Value) :
            throw new InvalidDataException("公会NPC福利需要字符串");
        static bool Currency(string currency, int amount) => amount >= 0 && (currency switch
        {
            "none" => amount == 0,
            "public funds" or "member coins" or "gold" => true,
            _ => false
        });

        var interval = Section("guild trainer buff interval time", false);
        if (interval.Length != 1 || Number(interval[0]) <= 0)
            throw new InvalidDataException("公会训练效果间隔无效");
        TrainingIntervalSeconds = Number(interval[0]);
        var values = Section("npc benefit info", true);
        var rows = new Dictionary<uint, GuildNpcBenefitDefinition>();
        var guardians = new Dictionary<int, uint>();
        for (int at = 0; at < values.Length;)
        {
            if (values.Length - at < 11) throw new InvalidDataException("公会NPC福利行截断");
            int id = Number(values[at]), npc = Number(values[at + 1]), level = Number(values[at + 2]);
            string scope = Text(values[at + 3]), effect = Text(values[at + 4]);
            string first = Text(values[at + 5]), second = Text(values[at + 7]);
            int firstAmount = Number(values[at + 6]), secondAmount = Number(values[at + 8]);
            int minutes = Number(values[at + 9]), count = Number(values[at + 10]);
            if (id <= 0 || npc <= 0 || level is < 1 or > 5 || scope is not ("common" or "individual") ||
                string.IsNullOrWhiteSpace(effect) || !Currency(first, firstAmount) || !Currency(second, secondAmount) ||
                minutes < 0 || minutes > int.MaxValue / 60 || count is < 0 or > 32 || values.Length - at - 11 < count)
                throw new InvalidDataException("公会NPC福利索引、费用、期限或参数数量无效");
            int[] parameters = values.Skip(at + 11).Take(count).Select(Number).ToArray();
            var row = new GuildNpcBenefitDefinition((uint)id, (uint)npc, level, scope == "common", effect,
                first, firstAmount, second, secondAmount, checked(minutes * 60), parameters);
            if (!rows.TryAdd(row.Id, row)) throw new InvalidDataException("公会NPC福利索引重复");
            if (effect == "guild various buff")
            {
                // 福利第二参数是属性声明值，不是持续秒数；永久期限由实际variousbuff资源核实。
                if (!row.Common || row.NpcId != GuildNpcCatalog.Guardian || first != "none" || second != "none" ||
                    minutes != 0 || parameters is not [> 0, > 0] || !supportedGuardianBuffs.Contains(parameters[0]) ||
                    !guardians.TryAdd(level, checked((uint)parameters[0])))
                    throw new InvalidDataException("守护石属性福利与永久公会效果声明不符");
            }
            at += 11 + count;
        }
        if (rows.Count == 0) throw new InvalidDataException("公会NPC福利名册为空");
        definitions = rows.ToFrozenDictionary(); guardianBuffs = guardians.ToFrozenDictionary();
    }

    public GuildNpcBenefitDefinition? Find(uint id) => definitions.GetValueOrDefault(id);
    public uint GuardianBuff(int level) => guardianBuffs.GetValueOrDefault(level);

    static FrozenSet<int> LoadGuardianBuffs(PvfArchive pvf)
    {
        var document = new PvfTokenDocument(pvf.ReadTokens("etc/variousbufflist.etc"), pvf.ResolveString);
        bool Text(PvfTokenSection section, string name, string expected) =>
            section.Children.Where(field => field.Name == name).ToArray() is [{ Values: [{ } value] }] &&
            value.Type is 6 or 7 && pvf.ResolveString(value.Value) == expected;
        return document.Sections.Where(row => row.Name == "index" && row.Values is [{ Type: 0, Value: > 0 }] &&
                Text(row, "system group", "guild reform") &&
                row.Children.Where(field => field.Name == "apply type").ToArray() is [var apply] &&
                Text(apply, "type", "expire_time") &&
                apply.Children.Where(field => field.Name == "param").ToArray() is
                    [{ Values: [{ Type: 0, Value: 0 }, { Type: 0, Value: 0 }] }])
            .GroupBy(row => row.Values[0].Value).Where(group => group.Count() == 1)
            .Select(group => group.Key).ToFrozenSet();
    }
}
