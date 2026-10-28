using System.Collections.Frozen;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Pvp;

internal enum PvpArenaEligibility { Ordinary, Matching, Free, Tournament }

// 四份准入表独立保存；地图序号按原声明排列，不能把脚本ID当作请求里的序号。
internal sealed class PvpArenaCatalog
{
    readonly FrozenDictionary<PvpArenaEligibility, FrozenSet<(byte Job, byte Grow)>> eligibility;
    public ImmutableArray<int> MapOrder { get; }
    public TimeSpan BattleDuration { get; }
    public TimeSpan TournamentBattleDuration { get; }
    public TimeSpan RelayBattleDuration { get; }
    public TimeSpan TournamentRelayBattleDuration { get; }

    // 频道列选择采用资料兼容规则，与原服同版完整时钟调用链的还原结论分开。
    public TimeSpan BattleDurationFor(int channelType, bool relay = false) => channelType is 8 or 13 or 20 or 24 or 31 or 41
        ? relay ? TournamentRelayBattleDuration : TournamentBattleDuration
        : relay ? RelayBattleDuration : BattleDuration;

    public PvpArenaCatalog(PvfArchive pvf) : this(
        new(pvf.ReadTokens("etc/pvpparameter.etc"), pvf.ResolveString),
        new(pvf.ReadTokens("etc/pvpmapbasicdata.etc"), pvf.ResolveString),
        new(pvf.ReadTokens("etc/pvp_ref.etc"), pvf.ResolveString)) { }

    internal PvpArenaCatalog(PvfTokenDocument parameters, PvfTokenDocument maps, PvfTokenDocument rules)
    {
        if (!parameters.Diagnostics.IsEmpty || !maps.Diagnostics.IsEmpty || !rules.Diagnostics.IsEmpty)
            throw Invalid("声明未闭合或重复闭合");
        var tables = new Dictionary<PvpArenaEligibility, FrozenSet<(byte, byte)>>();
        foreach (var (kind, name) in new[] {
            (PvpArenaEligibility.Ordinary, "pvp enterable character"),
            (PvpArenaEligibility.Matching, "pvp matching possible character"),
            (PvpArenaEligibility.Free, "free pvp enterable character"),
            (PvpArenaEligibility.Tournament, "tournament enterable character") })
        {
            int[] values = Numbers(Single(parameters, name));
            if (values.Length == 0 || values.Length % 2 != 0) throw Invalid(name);
            var rows = new HashSet<(byte, byte)>();
            for (int at = 0; at < values.Length; at += 2)
            {
                if (values[at] is < 0 or > byte.MaxValue || values[at + 1] is < 0 or > byte.MaxValue ||
                    !rows.Add(((byte)values[at], (byte)values[at + 1]))) throw Invalid(name);
            }
            tables.Add(kind, rows.ToFrozenSet());
        }
        eligibility = tables.ToFrozenDictionary();
        int[] order = Numbers(Single(maps, "pvp map order"));
        if (order.Length is 0 or > 256 || order.Any(id => id <= 0) ||
            order.Distinct().Count() != order.Length) throw Invalid("地图顺序");
        var definitions = new HashSet<int>();
        foreach (var section in maps.Sections.Where(row => row.Name == "pvp map basic data"))
        {
            int[] values = Numbers(section);
            // 后六列的玩法含义未核实，仅核原表宽度，不拿标志位猜支持模式。
            if (values.Length != 7 || values[0] <= 0 || !definitions.Add(values[0])) throw Invalid("地图基础行");
        }
        if (order.Any(id => !definitions.Contains(id))) throw Invalid("地图引用缺失");
        MapOrder = order.ToImmutableArray();
        BattleDuration = Duration("pvp normal battle time");
        TournamentBattleDuration = Duration("pvp normal battle time for tournament");
        RelayBattleDuration = Duration("pvp relay battle time");
        TournamentRelayBattleDuration = Duration("pvp relay battle time for tournament");

        TimeSpan Duration(string key)
        {
            int[] values = Numbers(Single(rules, key));
            if (values.Length != 1 || values[0] is <= 0 or > 3_600_000) throw Invalid(key);
            return TimeSpan.FromMilliseconds(values[0]);
        }
    }

    public bool CanEnter(PvpArenaEligibility kind, byte job, byte grow) =>
        eligibility.TryGetValue(kind, out var rows) && rows.Contains((job, grow));
    public bool IsMapIndex(ushort index) => index < MapOrder.Length;

    static PvfTokenSection Single(PvfTokenDocument document, string name)
    {
        var rows = document.Sections.Where(row => row.Name == name).ToArray();
        return rows.Length == 1 ? rows[0] : throw Invalid(name);
    }

    static int[] Numbers(PvfTokenSection section)
    {
        if (!section.Children.IsEmpty || section.Values.Any(token => token.Type != 0)) throw Invalid(section.Name);
        return section.Values.Select(token => token.Value).ToArray();
    }
    static InvalidDataException Invalid(string field) => new("决斗场资源无效：" + field);
}
