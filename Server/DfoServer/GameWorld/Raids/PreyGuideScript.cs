using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Raids;

// 引导与普通普雷共用文件，但地图、门票和阶段规则各自独立；不把普通 PHASE 放入引导目录。
internal static class PreyGuideScript
{
    internal const string Path = "etc/raid/prey/prey.etc";

    internal static RaidScript Parse(IReadOnlyList<PvfToken> tokens, IReadOnlyList<PvfToken> symbolTokens, Func<int, string> resolve)
    {
        List<RaidIssue> issues = [];
        void Issue(string code, int at, string detail) => issues.Add(new(code, Path, at, detail));
        var fields = RaidScriptParser.Fields(tokens, resolve, Issue);
        var symbols = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        var ids = new HashSet<int>();
        for (int i = 0; i < symbolTokens.Count; i += 2)
        {
            if (i + 1 >= symbolTokens.Count || symbolTokens[i] is not { Type: 0, Value: >= 0 } || symbolTokens[i + 1].Type != 3)
            { Issue("invalid-symbol", i, "integer and symbol expected"); continue; }
            string key = RaidScriptParser.Tag(resolve(symbolTokens[i + 1].Value));
            if (!ids.Add(symbolTokens[i].Value) || !symbols.TryAdd(key, symbolTokens[i].Value)) Issue("duplicate-symbol", i, key);
        }
        if (symbols.Count == 0) Issue("symbols-missing", 0, Path);
        bool Ints(RaidField row, int count) => row.Values.Length == count && row.Values.All(value => value.Type == 0);
        RaidField Required(string tag, int count)
        {
            var rows = fields.Where(row => row.Tag == tag).ToArray();
            if (rows is [var row] && Ints(row, count)) return row;
            Issue("invalid-guide-header", rows.FirstOrDefault()?.TokenIndex ?? 0, tag);
            return new(tag, 0, []);
        }
        var waiting = Required("[waiting room]", 2);
        var cost = Required("[single entry cost]", 2);
        var retry = Required("[single retry item]", 2);
        var final = Required("[single limit dungeon index]", 1);
        var delay = Required("[single start delay time]", 1);
        var timeout = Required("[phase total time over]", 1);
        var weekly = Required("[weekly clear count]", 1);
        var daily = Required("[daily account enter limit]", 1);
        ImmutableArray<RaidField> Block(ImmutableArray<RaidField> source, ref int at, string end)
        {
            int start = ++at;
            while (at < source.Length && source[at].Tag != end) at++;
            if (at == source.Length) Issue("guide-block-incomplete", source[start - 1].TokenIndex, end);
            return source[start..at];
        }
        RaidDungeonState? State(RaidField row, int index) => index < row.Values.Length ? row.Values[index].Text switch
        {
            "open" => RaidDungeonState.Open, "close" => RaidDungeonState.Close,
            "clear" => RaidDungeonState.Clear, "hide" => RaidDungeonState.Hide, _ => null
        } : null;
        ImmutableArray<RaidClause> Clauses(ImmutableArray<RaidField> source, bool trigger)
        {
            var result = ImmutableArray.CreateBuilder<RaidClause>();
            for (int i = 0; i < source.Length; i++)
            {
                var field = source[i];
                RaidClause? clause = null;
                if (trigger && field.Tag is "[if]" or "[on change symbol]" || !trigger && field.Tag is "[set]" or "[assign]")
                {
                    if (field.Values.IsEmpty && i + 1 < source.Length)
                    {
                        var target = source[++i];
                        bool assign = field.Tag == "[assign]", change = field.Tag == "[on change symbol]";
                        if (symbols.ContainsKey(target.Tag) && Ints(target, assign || change ? 0 : 1))
                        {
                            string? from = null;
                            if (assign && i + 1 < source.Length && source[i + 1].Values.IsEmpty && symbols.ContainsKey(source[i + 1].Tag))
                                from = source[++i].Tag;
                            if (!assign || from is not null)
                                clause = new(field.Tag, field.TokenIndex, null, target.Tag, null, null,
                                    target.Values.Select(value => value.Number).ToImmutableArray()) { Operator = from };
                        }
                    }
                }
                else if (trigger && field.Tag is "[on clear dungeon]" or "[on change dungeon state]" or "[check dungeon state]" ||
                    !trigger && field.Tag is "[set dungeon state]" or "[spawn]" or "[reserve spawn]")
                {
                    bool state = field.Tag is "[check dungeon state]" or "[set dungeon state]";
                    int count = state || field.Tag == "[reserve spawn]" ? 2 : 1;
                    if (field.Values.Length == count && field.Values[0] is { Type: 0, Number: > 0 } &&
                        (state ? State(field, 1) is not null : Ints(field, count)))
                        clause = new(field.Tag, field.TokenIndex, field.Values[0].Number, null, state ? State(field, 1) : null, null,
                            field.Values.Where(value => value.Type == 0).Select(value => value.Number).ToImmutableArray());
                }
                else if (trigger && field.Tag is "[check timer end]" or "[check raid timer end]" ||
                    !trigger && field.Tag is "[set timer]" or "[reset timer]" or "[raid timer]" or "[reset raid timer]")
                {
                    int count = field.Tag is "[set timer]" or "[raid timer]" ? 3 : 2;
                    if (Ints(field, count) && field.Values.All(value => value.Number >= 0))
                        clause = new(field.Tag, field.TokenIndex, null, null, null,
                            new(field.Values[0].Number, field.Values[1].Number), field.Values.Select(value => value.Number).ToImmutableArray());
                }
                else if (!trigger && field.Tag is "[clear phase]" or "[slenikon]" && field.Values.IsEmpty)
                    clause = new(field.Tag, field.TokenIndex, null, null, null, null, []);
                if (clause is null) Issue("unsupported-guide-clause", field.TokenIndex, field.Tag);
                else result.Add(clause);
            }
            return result.ToImmutable();
        }
        RaidPhase Phase(ImmutableArray<RaidField> source)
        {
            string name = "";
            List<RaidDungeon> dungeons = [];
            List<RaidRule> rules = [];
            List<RaidReward> rewards = [];
            for (int i = 0; i < source.Length; i++)
            {
                var field = source[i];
                switch (field.Tag)
                {
                    case "[phase name]" when field.Values is [{ Type: 6, Text: { } text }]: name = text; break;
                    case "[title img]" when field.Values is [{ Type: 6 }, { Type: 0 }]: break;
                    case "[condition for rank]":
                        foreach (var rank in Block(source, ref i, "[/condition for rank]"))
                            if (rank.Tag != "[dead count]" || !Ints(rank, 3)) Issue("unsupported-guide-rank", rank.TokenIndex, rank.Tag);
                        break;
                    case "[clear reward item]":
                        foreach (var reward in Block(source, ref i, "[/clear reward item]"))
                        {
                            var v = reward.Values;
                            if (reward.Tag != "[state reward]" || v.Length is < 5 or > 6 || v[0].Type != 6 ||
                                v[0].Text is not ("party_card" or "pcroom_card" or "privilege_card" or "squad_item") ||
                                v.Skip(1).Any(value => value.Type != 0) || v[1].Number != -1 || v[2].Number <= 0 || v[3].Number <= 0)
                            { Issue("unsupported-guide-reward", reward.TokenIndex, reward.Tag); continue; }
                            rewards.Add(new(v[0].Text!, v[1].Number, v[2].Number, v[3].Number, v[4].Number)
                                { RawE = v.Length == 6 ? v[5].Number : null });
                        }
                        break;
                    case "[dungeon info]":
                        var block = Block(source, ref i, "[/dungeon info]");
                        RaidField? Unique(string tag)
                        {
                            var rows = block.Where(row => row.Tag == tag).ToArray();
                            return rows is [var row] ? row : null;
                        }
                        var id = Unique("[index]");
                        var init = Unique("[init state]");
                        var max = Unique("[party max]");
                        int hpAt = Array.FindIndex(block.ToArray(), row => row.Tag == "[hp info]");
                        if (id is null || !Ints(id, 1) || init is null || State(init, 0) is not { } state ||
                            max is null || !Ints(max, 1) || max.Values[0].Number != 1 || hpAt < 0 || hpAt + 1 >= block.Length ||
                            !Ints(block[hpAt + 1], 1) || !symbols.ContainsKey(block[hpAt + 1].Tag))
                        { Issue("guide-dungeon-invalid", field.TokenIndex, field.Tag); break; }
                        var hp = block[hpAt + 1];
                        dungeons.Add(new(id.Values[0].Number, state, hp.Tag, hp.Values[0].Number, 1) { Declarations = block });
                        break;
                    case "[trigger]":
                        var trigger = Clauses(Block(source, ref i, "[/trigger]"), true);
                        if (++i >= source.Length || source[i].Tag != "[behavior]")
                        { Issue("guide-behavior-missing", field.TokenIndex, field.Tag); break; }
                        rules.Add(new(trigger, Clauses(Block(source, ref i, "[/behavior]"), false)));
                        break;
                    default: Issue("unsupported-guide-phase-field", field.TokenIndex, field.Tag); break;
                }
            }
            if (name.Length == 0 || dungeons.Count == 0 || rules.Count == 0 || rewards.Count == 0 ||
                dungeons.Select(row => row.Id).Distinct().Count() != dungeons.Count) Issue("guide-phase-incomplete", 0, name);
            foreach (var clause in rules.SelectMany(rule => rule.Trigger.Concat(rule.Behavior)))
                if (clause.DungeonId is { } dungeon && !dungeons.Any(row => row.Id == dungeon)) Issue("guide-dungeon-reference-missing", clause.TokenIndex, dungeon.ToString());
            return new(name, dungeons.ToImmutableArray(), rules.ToImmutableArray(), rewards.ToImmutableArray()) { Declarations = source };
        }
        var phases = ImmutableArray.CreateBuilder<RaidPhase>();
        for (int i = 0; i < fields.Length; i++)
            if (fields[i].Tag == "[single raid phase]") phases.Add(Phase(Block(fields, ref i, "[/single raid phase]")));
        if (phases.Count != 2 || final.Values is not [{ Number: 291100397 }] ||
            phases.Count == 2 && !phases[1].Dungeons.Any(row => row.Id == final.Values[0].Number)) Issue("guide-phases-invalid", 0, Path);
        if (cost.Values.Any(value => value.Number <= 0) || retry.Values.Any(value => value.Number <= 0)) Issue("guide-cost-invalid", 0, Path);
        var headers = new[] { delay with { Tag = "[start delay time]" }, timeout, weekly, daily, final,
            Required("[phase time over]", 2), Required("[single magic tower 1]", 1), Required("[single magic tower 2]", 1),
            Required("[max hp]", 1), Required("[hp decrease]", 2), Required("[single defence fail]", 1),
            Required("[armor level hp boundary]", 2) }.ToImmutableArray();
        if (headers.Any(row => row.Values.Any(value => value.Number <= 0))) Issue("guide-header-range", 0, Path);
        // 普通与引导共用原演出声明；前两列允许0，不能因只选择single业务字段丢掉时钟输入。
        var movie = Required("[movie end time]", 4);
        if (movie.Values.Length == 4 && (movie.Values.Any(value => value.Number is < 0 or > 86400) ||
            movie.Values.All(value => value.Number == 0))) Issue("guide-movie-time-invalid", movie.TokenIndex, movie.Tag);
        headers = headers.Add(movie);
        return new(Path, waiting.Values.Length == 2 ? new(waiting.Values[0].Number, waiting.Values[1].Number) : null,
            cost.Values.Length == 2 ? new(cost.Values[0].Number, cost.Values[1].Number) : null,
            retry.Values.Length == 2 ? new(retry.Values[0].Number, retry.Values[1].Number) : null,
            symbols.ToImmutable(), phases.ToImmutable(), headers, issues.ToImmutableArray());
    }
}
