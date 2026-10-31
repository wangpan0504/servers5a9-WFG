using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Raids;

internal sealed record FiendwarArea(int Id, int WorldMap, int DungeonId, int LimitSeconds,
    int MemberMax, string InitialState, ImmutableArray<int> Neighbours, ImmutableArray<string> Roles);
internal enum FiendwarOp
{
    SymbolEquals, SymbolChanged, SetSymbol, TimerEnded, SetTimer, ResetTimer,
    AreaChanged, AreaEquals, DungeonChanged, DungeonEquals, SetDungeon,
    EnterDungeon, EnterMap, GiveupArea, GiveupDungeon, AliveMonstersLess,
    CreateEnemy, DeleteMonster, MoveEnemy, WarningEnemy, CreateMonsters, CreateMerchant,
    DeleteAllEnemy, CreateSingleEnemy, CreateSingleMonster, Fail, Clear, BossPatternInit, MovieRadar, MovieBatteryOn, MovieBatteryOff,
    MovieCommandCenter, KillAllCharacters
}
internal sealed record FiendwarInstruction(FiendwarOp Op, int A = 0, int B = 0, string? Key = null, int TokenIndex = 0);
internal sealed record FiendwarRule(ImmutableArray<RaidField> Trigger, ImmutableArray<RaidField> Behavior)
{
    public ImmutableArray<FiendwarInstruction> Conditions { get; init; } = [];
    public ImmutableArray<FiendwarInstruction> Actions { get; init; } = [];
}
internal sealed record FiendwarGuide(int From, int To, int Direction);
internal sealed record FiendwarEnemyRate(int Kind, int Weight);
internal sealed record FiendwarMerchantRate(int Kind, int Seconds, int Weight);
internal sealed record FiendwarMerchantPosition(int Area, int X, int Y);

// 漩涡的区域图与字符串timer不同于安徒恩/卢克；官方单人支线不进入完整战场。
internal sealed record FiendwarScript(RaidScript Script, ImmutableDictionary<int, FiendwarArea> Areas,
    ImmutableArray<FiendwarRule> Rules, ImmutableArray<FiendwarGuide> Guides,
    ImmutableArray<FiendwarEnemyRate> EnemyRates, ImmutableArray<FiendwarMerchantRate> MerchantRates,
    ImmutableArray<FiendwarMerchantPosition> MerchantPositions, int MemberMax, int MinimumMembers,
    int TotalSeconds, int DisconnectSeconds, int DailyAccountLimit, int WeeklyClearLimit,
    int MinimumClears, int RadarBonus, int BatteryDebuff)
{
    public bool Guide { get; init; }

    public static FiendwarScript Parse(string path, IReadOnlyList<PvfToken> tokens,
        IReadOnlyList<PvfToken> symbolTokens, Func<int, string> resolve) => ParseMode(path, tokens, symbolTokens, resolve, false);

    internal static FiendwarScript ParseGuide(string path, IReadOnlyList<PvfToken> tokens,
        IReadOnlyList<PvfToken> symbolTokens, Func<int, string> resolve) => ParseMode(path, tokens, symbolTokens, resolve, true);

    static FiendwarScript ParseMode(string path, IReadOnlyList<PvfToken> tokens,
        IReadOnlyList<PvfToken> symbolTokens, Func<int, string> resolve, bool guide)
    {
        path = PvfArchive.NormalizePath(path);
        List<RaidIssue> issues = [];
        void Issue(string code, int at, string detail) => issues.Add(new(code, path, at, detail));
        var fields = RaidScriptParser.Fields(tokens, resolve, Issue);
        var symbols = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        HashSet<int> symbolIds = [];
        for (int i = 0; i < symbolTokens.Count; i += 2)
        {
            if (i + 1 >= symbolTokens.Count || symbolTokens[i] is not { Type: 0, Value: >= 0 } || symbolTokens[i + 1].Type != 3)
            { Issue("invalid-symbol", i, "expected integer and tag"); continue; }
            string tag = RaidScriptParser.Tag(resolve(symbolTokens[i + 1].Value));
            if (!symbolIds.Add(symbolTokens[i].Value) || !symbols.TryAdd(tag, symbolTokens[i].Value))
                Issue("duplicate-symbol", i, tag);
        }
        if (symbols.Count == 0) Issue("symbol-definitions-missing", 0, path + ".symbol");
        var headers = ImmutableArray.CreateBuilder<RaidField>();
        ImmutableArray<RaidField> phase = [];
        bool foundPhase = false;
        for (int i = 0; i < fields.Length; i++)
        {
            if (fields[i].Tag is "[phase]" or "[single raid phase]")
            {
                bool selected = (fields[i].Tag == "[single raid phase]") == guide;
                string end = fields[i].Tag == "[phase]" ? "[/phase]" : "[/single raid phase]";
                var block = Block(fields, ref i, end);
                if (selected)
                {
                    if (foundPhase) Issue("duplicate-phase", fields[i].TokenIndex, fields[i].Tag);
                    foundPhase = true;
                    phase = block;
                }
            }
            else headers.Add(fields[i]);
        }
        int Scalar(string tag, int minimum = 0)
        {
            var rows = headers.Where(f => f.Tag == tag).ToArray();
            if (rows is [var row] && Ints(row, 1) && row.Values[0].Number >= minimum) return row.Values[0].Number;
            Issue("invalid-header", rows.FirstOrDefault()?.TokenIndex ?? 0, tag);
            return 0;
        }
        RaidCost? Cost(string tag)
        {
            var rows = headers.Where(f => f.Tag == tag).ToArray();
            if (rows is [var row] && Ints(row, 2) && row.Values.All(v => v.Number > 0))
                return new(row.Values[0].Number, row.Values[1].Number);
            Issue("cost-declaration-invalid", rows.FirstOrDefault()?.TokenIndex ?? 0, tag);
            return null;
        }
        int maximum = Scalar("[fiendwar member max]", 1), minimum = Scalar("[fiendwar start minimum member]", 1);
        if (guide) maximum = minimum = 1;
        if (maximum > 8 || minimum > maximum) Issue("member-limits-invalid", 0, "fiendwar");
        if (Scalar("[phase max]", 1) != 1 || !foundPhase) Issue("phase-count-mismatch", 0, "fiendwar");
        var waiting = headers.Where(f => f.Tag == (guide ? "[single waiting room]" : "[waiting room]")).ToArray();
        RaidWaitingRoom? room = waiting is [var w] && Ints(w, 2) && w.Values.All(v => v.Number >= 0)
            ? new(w.Values[0].Number, w.Values[1].Number) : null;
        if (room is null) Issue("waiting-room-invalid", 0, "[waiting room]");
        var areas = ImmutableDictionary.CreateBuilder<int, FiendwarArea>();
        var dungeons = new Dictionary<int, RaidDungeon>();
        var rules = ImmutableArray.CreateBuilder<FiendwarRule>();
        var rewards = ImmutableArray.CreateBuilder<RaidReward>();
        int minimumClears = 0;
        string name = "";
        for (int i = 0; i < phase.Length; i++)
        {
            var f = phase[i];
            switch (f.Tag)
            {
                case "[phase name]":
                    if (f.Values is [{ Type: 6, Text: { } text }]) name = text;
                    else Issue("phase-name-invalid", f.TokenIndex, f.Tag);
                    break;
                case "[area info]":
                    var source = Block(phase, ref i, "[/area info]");
                    int Value(string tag, int fallback = -1)
                    {
                        var rows = source.Where(row => row.Tag == tag).ToArray();
                        if (rows is [var row] && Ints(row, 1)) return row.Values[0].Number;
                        Issue("invalid-area-field", f.TokenIndex, tag);
                        return fallback;
                    }
                    int id = Value("[area index]"), dungeon = Value("[dungeon index]"), world = Value("[worldmap index]");
                    int members = Value("[member max]");
                    var limit = source.Where(row => row.Tag == "[dungeon limit time]").ToArray();
                    int seconds = limit is [var l] && Ints(l, 1) ? l.Values[0].Number : 0;
                    if (dungeon > 0 && seconds <= 0 || members is < 1 or > 8 || id < 0)
                        Issue("invalid-area-limits", f.TokenIndex, id.ToString());
                    var states = source.Where(row => row.Tag == $"[{id} area state]").ToArray();
                    string state = states is [var s] && s.Values is [{ Type: 6, Text: { } st }] ? st : "";
                    if (state is not ("empty" or "enemy" or "enemy_monster" or "monster"))
                        Issue("invalid-area-state", f.TokenIndex, state);
                    var neighbours = source.Where(row => row.Tag == "[move available map]").ToArray();
                    ImmutableArray<int> links = neighbours is [var n] && n.Values.All(v => v.Type == 0 && v.Number >= 0)
                        ? n.Values.Select(v => v.Number).ToImmutableArray() : [];
                    if (links.IsEmpty) Issue("area-neighbours-missing", f.TokenIndex, id.ToString());
                    var roles = source.Where(row => row.Tag.EndsWith(" area]", StringComparison.Ordinal))
                        .Select(row => row.Tag).ToImmutableArray();
                    if (!areas.TryAdd(id, new(id, world, dungeon, seconds, members, state, links, roles)))
                        Issue("duplicate-area", f.TokenIndex, id.ToString());
                    break;
                case "[dungeon info]":
                    var ds = Block(phase, ref i, "[/dungeon info]");
                    var index = ds.Where(row => row.Tag == "[index]").ToArray();
                    var initial = ds.Where(row => row.Tag == "[init state]").ToArray();
                    var capacity = ds.Where(row => row.Tag == "[party max]").ToArray();
                    if (index is [var di] && Ints(di, 1) && di.Values[0].Number > 0 &&
                        initial is [var si] && si.Values is [{ Type: 6, Text: "hide" }] &&
                        capacity is [var ci] && Ints(ci, 1) && ci.Values[0].Number > 0)
                    {
                        int dungeonId = di.Values[0].Number;
                        if (!dungeons.TryAdd(dungeonId, new(dungeonId, RaidDungeonState.Hide, "", 0, ci.Values[0].Number) { Declarations = ds }))
                            Issue("duplicate-phase-dungeon", f.TokenIndex, dungeonId.ToString());
                    }
                    else Issue("invalid-dungeon-info", f.TokenIndex, f.Tag);
                    break;
                case "[trigger]":
                    var trigger = Block(phase, ref i, "[/trigger]");
                    if (i + 1 >= phase.Length || phase[i + 1].Tag != "[behavior]")
                    { Issue("behavior-missing", f.TokenIndex, f.Tag); break; }
                    i++;
                    var behavior = Block(phase, ref i, "[/behavior]");
                    if (trigger.IsEmpty || behavior.IsEmpty) Issue("empty-rule-block", f.TokenIndex, f.Tag);
                    var conditions = CompileClauses(trigger, true);
                    var actions = CompileClauses(behavior, false);
                    rules.Add(new(trigger, behavior) { Conditions = conditions, Actions = actions });
                    break;
                case "[clear reward item]":
                    foreach (var row in Block(phase, ref i, "[/clear reward item]"))
                    {
                        if (row.Tag == "[state reward]" && row.Values is [{ Type: 6, Text: { } kind },
                            { Type: 0 } a, { Type: 0, Number: > 0 } b, { Type: 0, Number: > 0 } item, { Type: 0 } d])
                            rewards.Add(new(kind, a.Number, b.Number, item.Number, d.Number));
                        else Issue("invalid-reward", row.TokenIndex, row.Tag);
                    }
                    break;
                case "[minimal dungeon clear count]":
                    if (Ints(f, 1) && f.Values[0].Number > 0) minimumClears = f.Values[0].Number;
                    else Issue("invalid-minimum-clears", f.TokenIndex, f.Tag);
                    break;
                case "[condition for rank]":
                    _ = Block(phase, ref i, "[/condition for rank]");
                    break;
                case "[title img]": break;
                default: Issue("unsupported-phase-field", f.TokenIndex, f.Tag); break;
            }
        }
        foreach (var area in areas.Values)
        {
            foreach (int link in area.Neighbours)
                if (!areas.ContainsKey(link)) Issue("area-reference-missing", 0, $"{area.Id}:{link}");
            if (area.DungeonId > 0)
                dungeons.TryAdd(area.DungeonId, new(area.DungeonId, RaidDungeonState.Open, "", 0, 1));
        }
        if (name.Length == 0 || areas.Count == 0 || rules.Count == 0 || rewards.Count == 0 || minimumClears <= 0)
            Issue("phase-definition-incomplete", 0, "fiendwar");
        var guides = Rows("[guide info]", 3).Select(v => new FiendwarGuide(v[0], v[1], v[2]))
            .Where(row => areas.ContainsKey(row.From) && areas.ContainsKey(row.To)).ToImmutableArray();
        var enemies = Rows("[enemy create rate]", 2).Select(v => new FiendwarEnemyRate(v[0], v[1])).ToImmutableArray();
        var merchants = Rows("[merchant create rate]", 3).Select(v => new FiendwarMerchantRate(v[0], v[1], v[2])).ToImmutableArray();
        var positions = Rows("[merchant create pos]", 3).Select(v => new FiendwarMerchantPosition(v[0], v[1], v[2]))
            .Where(row => !guide || areas.ContainsKey(row.Area)).ToImmutableArray();
        foreach (var row in positions)
            if (!areas.ContainsKey(row.Area)) Issue("merchant-area-missing", 0, row.Area.ToString());
        var entry = Cost(guide ? "[single entry cost]" : "[entry cost]"); var retry = Cost(guide ? "[single retry item]" : "[retry item]");
        int total = Scalar(guide ? "[single raid time over]" : "[phase time over]", 1), disconnect = Scalar("[disconn member wait time]", 1),
            daily = Scalar("[daily account enter limit]", 1), weekly = Scalar("[weekly clear count]", 1),
            radar = Scalar("[radar skill attack bonus buff rate]"), battery = Scalar("[battery defence debuff rate]");
        var script = new RaidScript(path, room, entry, retry, symbols.ToImmutable(),
            [new(name, dungeons.Values.OrderBy(d => d.Id).ToImmutableArray(), [], rewards.ToImmutable()) { Declarations = phase }],
            headers.ToImmutable(), issues.ToImmutableArray());
        return new(script, areas.ToImmutable(), rules.ToImmutable(), guides, enemies, merchants, positions,
            maximum, minimum, total, disconnect, daily, weekly, minimumClears, radar, battery) { Guide = guide };

        bool Ints(RaidField field, int count) => field.Values.Length == count && field.Values.All(v => v.Type == 0);
        ImmutableArray<RaidField> Block(ImmutableArray<RaidField> source, ref int at, string end)
        {
            int start = ++at;
            while (at < source.Length && source[at].Tag != end) at++;
            if (at == source.Length) Issue("block-incomplete", source[start - 1].TokenIndex, end);
            return source[start..at];
        }
        IEnumerable<int[]> Rows(string tag, int width)
        {
            var rows = headers.Where(row => row.Tag == tag).ToArray();
            if (rows is not [var row] || row.Values.IsEmpty || row.Values.Length % width != 0 || row.Values.Any(v => v.Type != 0 || v.Number < 0))
            { Issue("invalid-header-table", rows.FirstOrDefault()?.TokenIndex ?? 0, tag); yield break; }
            for (int i = 0; i < row.Values.Length; i += width)
                yield return row.Values.Skip(i).Take(width).Select(v => v.Number).ToArray();
        }
        ImmutableArray<FiendwarInstruction> CompileClauses(ImmutableArray<RaidField> source, bool trigger)
        {
            var program = ImmutableArray.CreateBuilder<FiendwarInstruction>();
            for (int i = 0; i < source.Length; i++)
            {
                var f = source[i];
                bool valid;
                FiendwarInstruction? instruction = null;
                if (f.Tag is "[if]" or "[on change symbol]" or "[set]")
                {
                    valid = f.Values.IsEmpty && i + 1 < source.Length &&
                        (trigger ? f.Tag != "[set]" : f.Tag == "[set]");
                    if (valid)
                    {
                        var target = source[++i];
                        valid = symbols.ContainsKey(target.Tag) && Ints(target, f.Tag == "[on change symbol]" ? 0 : 1);
                        if (valid) instruction = new(f.Tag == "[set]" ? FiendwarOp.SetSymbol : f.Tag == "[if]" ?
                            FiendwarOp.SymbolEquals : FiendwarOp.SymbolChanged, target.Values.IsEmpty ? 0 : target.Values[0].Number, Key: target.Tag);
                        if (trigger && f.Tag == "[if]" && target.Tag == "[alive monster count]")
                        {
                            valid = target.Values.IsEmpty && i + 1 < source.Length && source[++i] is { Tag: "[<]" } limit && Ints(limit, 1);
                            if (valid) instruction = new(FiendwarOp.AliveMonstersLess, source[i].Values[0].Number);
                        }
                    }
                }
                else if (f.Tag is "[set timer]" or "[reset timer]" or "[check timer end]")
                {
                    valid = f.Values.Length == (f.Tag == "[set timer]" ? 3 : 2) &&
                        f.Values[0] is { Type: 6, Text.Length: > 0 } && f.Values.Skip(1).All(v => v.Type == 0 && v.Number >= 0) &&
                        (trigger ? f.Tag == "[check timer end]" : f.Tag != "[check timer end]");
                    if (valid) instruction = new(f.Tag == "[set timer]" ? FiendwarOp.SetTimer : f.Tag == "[reset timer]" ?
                        FiendwarOp.ResetTimer : FiendwarOp.TimerEnded, f.Values[1].Number, f.Values.Length == 3 ? f.Values[2].Number : 0, f.Values[0].Text);
                }
                else if (f.Tag is "[check area state]" or "[check dungeon state]" or "[set dungeon state]")
                {
                    valid = f.Values is [{ Type: 0, Number: >= 0 }, { Type: 6, Text.Length: > 0 }];
                    if (valid) instruction = new(f.Tag == "[check area state]" ? FiendwarOp.AreaEquals :
                        f.Tag == "[check dungeon state]" ? FiendwarOp.DungeonEquals : FiendwarOp.SetDungeon, f.Values[0].Number, Key: f.Values[1].Text);
                }
                else if (f.Tag == "[alive monster count]")
                    valid = trigger && f.Values.IsEmpty && i + 1 < source.Length && source[++i] is { Tag: "[<]" } comparison && Ints(comparison, 1);
                else if (f.Tag is "[move enemy]" or "[single create enemy]" or "[single create monster]") valid = !trigger && Ints(f, 2);
                else if (trigger && f.Tag is "[on change area state]" or "[on change dungeon state]" or "[on enter dungeon]" or
                    "[on enter map]" or "[on giveup area]" or "[on giveup dungeon]") valid = Ints(f, 1);
                else if (!trigger && f.Tag is "[create enemy]" or "[delete monster]" or "[warning enemy]") valid = Ints(f, 1);
                else valid = !trigger && f.Tag is "[create monsters]" or "[create merchant]" or "[delete all enemy]" or
                    "[fail fiendwar]" or "[clear phase]" or "[boss pattern init]" or "[movie radar on]" or
                    "[movie battery on]" or "[movie battery off]" or "[movie command center on]" or "[kill all character]" && f.Values.IsEmpty;
                if (!valid) Issue("unsupported-clause", f.TokenIndex, f.Tag);
                else
                {
                    if (instruction is null)
                    {
                        FiendwarOp? op = f.Tag switch
                        {
                            "[on change area state]" => FiendwarOp.AreaChanged, "[on change dungeon state]" => FiendwarOp.DungeonChanged,
                            "[on enter dungeon]" => FiendwarOp.EnterDungeon, "[on enter map]" => FiendwarOp.EnterMap,
                            "[on giveup area]" => FiendwarOp.GiveupArea, "[on giveup dungeon]" => FiendwarOp.GiveupDungeon,
                            "[move enemy]" => FiendwarOp.MoveEnemy, "[create enemy]" => FiendwarOp.CreateEnemy,
                            "[single create enemy]" => FiendwarOp.CreateSingleEnemy, "[single create monster]" => FiendwarOp.CreateSingleMonster,
                            "[delete monster]" => FiendwarOp.DeleteMonster, "[warning enemy]" => FiendwarOp.WarningEnemy,
                            "[create monsters]" => FiendwarOp.CreateMonsters, "[create merchant]" => FiendwarOp.CreateMerchant,
                            "[delete all enemy]" => FiendwarOp.DeleteAllEnemy, "[fail fiendwar]" => FiendwarOp.Fail,
                            "[clear phase]" => FiendwarOp.Clear, "[boss pattern init]" => FiendwarOp.BossPatternInit,
                            "[movie radar on]" => FiendwarOp.MovieRadar, "[movie battery on]" => FiendwarOp.MovieBatteryOn,
                            "[movie battery off]" => FiendwarOp.MovieBatteryOff, "[movie command center on]" => FiendwarOp.MovieCommandCenter,
                            "[kill all character]" => FiendwarOp.KillAllCharacters, _ => null
                        };
                        if (op is { } operation) instruction = new(operation, f.Values.Length > 0 ? f.Values[0].Number : 0,
                            f.Values.Length > 1 ? f.Values[1].Number : 0);
                    }
                    if (instruction is null) Issue("instruction-missing", f.TokenIndex, f.Tag);
                    else program.Add(instruction with { TokenIndex = f.TokenIndex });
                }
            }
            return program.ToImmutable();
        }
    }
}
