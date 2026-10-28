using System.Collections.Frozen;
using PvfLib;

namespace DfoServer.GameWorld.Guilds;

internal sealed record GuildDungeonReward(int Ratio, int ItemId, int Amount, int Contribution);
internal sealed record GuildDungeonDefinition(uint Id, int GaugeLimit, int GaugeOnClear, int ConquestRatio,
    uint[] Prerequisites, GuildDungeonReward[] Rewards)
{
    public int Purification(uint clears)
    {
        // 042BE930：原生接收通关次数后乘单次净化值，再截断并夹至0～100。
        ulong gauge = (ulong)clears * (uint)GaugeOnClear;
        return gauge >= (uint)GaugeLimit ? 100 : (int)(gauge * 100 / (uint)GaugeLimit);
    }
}

internal sealed class GuildDungeonCatalog
{
    // 01698BD0固定载入style0；当前正式目录的0号条目指向此文件。
    public const string ResourcePath = "etc/guild/guilddungeon/0_2way6dungeon.etc";
    readonly FrozenDictionary<uint, GuildDungeonDefinition> definitions;
    public IReadOnlyCollection<GuildDungeonDefinition> Definitions => definitions.Values;
    public uint FinalDungeon { get; }
    public int InactiveDays { get; }
    public int InactiveReduction { get; }
    public int RepeatRewardItem { get; }
    public int RepeatRewardAmount { get; }
    public int LevelDifference { get; }
    public int DeleteItem { get; }
    public FrozenDictionary<int, int> PartyBuffs { get; }
    IReadOnlySet<int> partyEffects = new HashSet<int>();
    public uint PartyBuff(int members) => GuildDungeonPartyBuffs.Resolve(PartyBuffs.GetValueOrDefault(members), partyEffects);

    public GuildDungeonCatalog(PvfArchive pvf) : this(pvf.ReadTokens(ResourcePath), pvf.ResolveString)
    { partyEffects = GuildDungeonPartyBuffs.Load(pvf); }

    internal GuildDungeonCatalog(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        if (!document.Diagnostics.IsEmpty) throw new InvalidDataException("公会副本资源结构不完整");
        static PvfTokenSection One(IEnumerable<PvfTokenSection> rows, string name)
        {
            var fields = rows.Where(row => row.Name == name).ToArray();
            return fields.Length == 1 ? fields[0] : throw new InvalidDataException("公会副本资源字段缺失或重复: " + name);
        }
        static int[] Numbers(PvfTokenSection section, int count = -1)
        {
            if ((count >= 0 && section.Values.Length != count) || section.Values.Any(token => token.Type != 0))
                throw new InvalidDataException("公会副本资源字段宽度或类型无效: " + section.Name);
            return section.Values.Select(token => token.Value).ToArray();
        }
        static int Number(IEnumerable<PvfTokenSection> rows, string name) => Numbers(One(rows, name), 1)[0];
        var root = document.Sections;
        int final = Number(One(root, "weekly reward").Children, "final dungeon");
        var rows = new List<GuildDungeonDefinition>();
        foreach (var section in root.Where(row => row.Name == "dungeon"))
        {
            int id = Number(section.Children, "dungeon index");
            int limit = Number(section.Children, "gauge limit"), gain = Number(section.Children, "gauge on clear");
            int ratio = Number(section.Children, "conquest gauge ratio");
            var required = section.Children.Where(row => row.Name == "required dungeon conquest").ToArray();
            if (required.Length > 1) throw new InvalidDataException("公会副本前置段重复");
            int[] prerequisites = required.Length == 0 ? [] : Numbers(required[0]);
            var rewards = new List<GuildDungeonReward>();
            foreach (var reward in One(section.Children, "reward").Children)
            {
                if (reward.Name != "gauge ratio") throw new InvalidDataException("公会副本奖励类型无效");
                int threshold = Numbers(reward, 1)[0];
                var item = Numbers(One(reward.Children, "item"), 2);
                int contribution = Number(reward.Children, "contribute");
                if (threshold is < 0 or > 100 || item[0] <= 0 || item[1] <= 0 || contribution < 0)
                    throw new InvalidDataException("公会副本奖励范围无效");
                rewards.Add(new(threshold, item[0], item[1], contribution));
            }
            if (id <= 0 || limit <= 0 || gain <= 0 || ratio is < 1 or > 100 ||
                prerequisites.Any(value => value <= 0 || value == id) || prerequisites.Distinct().Count() != prerequisites.Length ||
                rewards.Count == 0 || rewards.Select(row => row.Ratio).Distinct().Count() != rewards.Count)
                throw new InvalidDataException("公会副本编号、净化或奖励声明无效");
            rows.Add(new((uint)id, limit, gain, ratio, prerequisites.Select(value => (uint)value).ToArray(),
                rewards.OrderBy(row => row.Ratio).ToArray()));
        }
        if (rows.Count is < 1 or > 64 || rows.Select(row => row.Id).Distinct().Count() != rows.Count ||
            final <= 0 || rows.All(row => row.Id != (uint)final))
            throw new InvalidDataException("公会副本目录或最终副本声明无效");
        definitions = rows.ToFrozenDictionary(row => row.Id);
        var visiting = new HashSet<uint>();
        var complete = new HashSet<uint>();
        void Visit(uint id)
        {
            if (complete.Contains(id)) return;
            if (!definitions.TryGetValue(id, out var row) || !visiting.Add(id))
                throw new InvalidDataException("公会副本前置缺失或成环");
            foreach (uint prerequisite in row.Prerequisites) Visit(prerequisite);
            visiting.Remove(id); complete.Add(id);
        }
        foreach (var row in rows) Visit(row.Id);
        FinalDungeon = (uint)final;
        var reset = Numbers(One(root, "dungeon reset info"), 2);
        var repeat = Numbers(One(root, "repeat clear bonus reward"), 2);
        InactiveDays = reset[0]; InactiveReduction = reset[1];
        RepeatRewardItem = repeat[0]; RepeatRewardAmount = repeat[1];
        LevelDifference = Number(root, "level difference"); DeleteItem = Number(root, "delete item");
        int[] buffs = Numbers(One(root, "party buff list"));
        if (InactiveDays is < 1 or > 365 || InactiveReduction is < 1 or > 100 || RepeatRewardItem <= 0 ||
            RepeatRewardAmount <= 0 || LevelDifference < 0 || DeleteItem <= 0 || buffs.Length == 0 || buffs.Length % 2 != 0)
            throw new InvalidDataException("公会副本周期、重复奖励或组队增益声明无效");
        var partyBuffs = new Dictionary<int, int>();
        for (int at = 0; at < buffs.Length; at += 2)
            if (buffs[at] is < 2 or > 4 || buffs[at + 1] <= 0 || !partyBuffs.TryAdd(buffs[at], buffs[at + 1]))
                throw new InvalidDataException("公会副本组队增益人数或索引无效");
        PartyBuffs = partyBuffs.ToFrozenDictionary();
    }

    public GuildDungeonDefinition? Find(uint id) => definitions.GetValueOrDefault(id);

    public bool IsUnlocked(uint id, IReadOnlyDictionary<uint, uint> clears) =>
        definitions.TryGetValue(id, out var row) && row.Prerequisites.All(prerequisite =>
            definitions[prerequisite].Purification(clears.GetValueOrDefault(prerequisite)) >= definitions[prerequisite].ConquestRatio);
}
