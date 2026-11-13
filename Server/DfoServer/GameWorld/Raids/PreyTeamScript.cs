using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Raids;

// 普通攻坚使用外层PHASE；显示动作中的同名块及single分支不参与服务端阶段。
internal static class PreyTeamScript
{
    internal const string Path = "etc/raid/prey/prey.etc";

    internal static RaidScript Parse(IReadOnlyList<PvfToken> tokens, IReadOnlyList<PvfToken> symbolTokens, Func<int, string> resolve)
    {
        List<RaidIssue> issues = [];
        void Issue(string code, int at, string detail) => issues.Add(new(code, Path, at, detail));
        // 非业务显示字段允许保留其它token类型；真正消费的字段仍逐项核对类型。
        var fields = RaidScriptParser.Fields(tokens, resolve, (code, at, detail) =>
        {
            if (code != "unsupported-token") Issue(code, at, detail);
        });
        var symbols = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        var ids = new HashSet<int>();
        for (int i = 0; i < symbolTokens.Count; i += 2)
        {
            if (i + 1 >= symbolTokens.Count || symbolTokens[i] is not { Type: 0, Value: >= 0 } || symbolTokens[i + 1].Type != 3)
            {
                Issue("prey-symbol-invalid", i, "integer and symbol expected");
                continue;
            }
            string key = RaidScriptParser.Tag(resolve(symbolTokens[i + 1].Value));
            if (!ids.Add(symbolTokens[i].Value) || !symbols.TryAdd(key, symbolTokens[i].Value))
                Issue("prey-symbol-duplicate", i, key);
        }
        if (symbols.Count == 0) Issue("prey-symbols-missing", 0, Path);

        static bool Ints(RaidField field, int count) => field.Values.Length == count && field.Values.All(value => value.Type == 0);
        ImmutableArray<RaidField> Block(ImmutableArray<RaidField> source, ref int at)
        {
            var opening = source[at];
            string end = "[/" + opening.Tag[1..];
            int start = ++at, depth = 1;
            while (at < source.Length)
            {
                if (source[at].Tag == opening.Tag) depth++;
                if (source[at].Tag == end && --depth == 0) return source[start..at];
                at++;
            }
            Issue("prey-block-incomplete", opening.TokenIndex, end);
            return source[start..];
        }

        RaidDungeonState? State(RaidField field, int index) => index < field.Values.Length ? field.Values[index].Text switch
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
                else if (trigger && field.Tag is "[on clear dungeon]" or "[on change dungeon state]" or "[check dungeon state]" or
                    "[on enter dungeon]" or "[on giveup dungeon]" or "[on kick out dungeon]" ||
                    !trigger && field.Tag is "[set dungeon state]" or "[spawn]" or "[reserve spawn]" or "[kick out dungeon]")
                {
                    bool state = field.Tag is "[check dungeon state]" or "[set dungeon state]";
                    int count = state || field.Tag == "[reserve spawn]" ? 2 : 1;
                    if (field.Values.Length == count && field.Values[0] is { Type: 0, Number: > 0 } &&
                        (state ? State(field, 1) is not null : Ints(field, count)))
                        clause = new(field.Tag, field.TokenIndex, field.Values[0].Number, null, state ? State(field, 1) : null, null,
                            field.Values.Where(value => value.Type == 0).Select(value => value.Number).ToImmutableArray());
                }
                else if (trigger && field.Tag == "[on enter map]" && Ints(field, 1) && field.Values[0].Number > 0)
                    clause = new(field.Tag, field.TokenIndex, null, null, null, null, [field.Values[0].Number])
                        { MapId = field.Values[0].Number };
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
                if (clause is null) Issue("prey-clause-unsupported", field.TokenIndex, field.Tag);
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
                    case "[minimal dungeon clear count]":
                        if (!Ints(field, 1) || field.Values[0].Number < 0) Issue("prey-clear-count-invalid", field.TokenIndex, field.Tag);
                        break;
                    case "[clear reward item]":
                        foreach (var reward in Block(source, ref i))
                        {
                            var values = reward.Values;
                            if (reward.Tag != "[state reward]" || values.Length is < 5 or > 6 || values[0] is not { Type: 6, Text: { } kind } ||
                                values.Skip(1).Any(value => value.Type != 0) || values[3].Number <= 0)
                            {
                                Issue("prey-reward-invalid", reward.TokenIndex, reward.Tag);
                                continue;
                            }
                            rewards.Add(new(kind, values[1].Number, values[2].Number, values[3].Number, values[4].Number)
                                { RawE = values.Length == 6 ? values[5].Number : null });
                        }
                        break;
                    case "[dungeon info]":
                        var block = Block(source, ref i);
                        RaidField? Unique(string tag)
                        {
                            var rows = block.Where(row => row.Tag == tag).ToArray();
                            return rows is [var row] ? row : null;
                        }
                        var id = Unique("[index]");
                        var init = Unique("[init state]");
                        var max = Unique("[party max]");
                        var retry = block.Where(row => row.Tag == "[member retry time]").ToArray();
                        int hpAt = Array.FindIndex(block.ToArray(), row => row.Tag == "[hp info]");
                        if (id is null || !Ints(id, 1) || id.Values[0].Number <= 0 || init is null || State(init, 0) is not { } state ||
                            max is null || !Ints(max, 1) || max.Values[0].Number <= 0 || hpAt < 0 || hpAt + 1 >= block.Length ||
                            !Ints(block[hpAt + 1], 1) || !symbols.ContainsKey(block[hpAt + 1].Tag) ||
                            retry.Length != 0 && (retry.Length != 1 || !Ints(retry[0], 1) || retry[0].Values[0].Number < 0))
                        {
                            Issue("prey-dungeon-invalid", field.TokenIndex, field.Tag);
                            break;
                        }
                        var hp = block[hpAt + 1];
                        dungeons.Add(new(id.Values[0].Number, state, hp.Tag, hp.Values[0].Number, max.Values[0].Number) { Declarations = block });
                        break;
                    case "[trigger]":
                        var trigger = Clauses(Block(source, ref i), true);
                        if (++i >= source.Length || source[i].Tag != "[behavior]")
                        {
                            Issue("prey-behavior-missing", field.TokenIndex, field.Tag);
                            break;
                        }
                        rules.Add(new(trigger, Clauses(Block(source, ref i), false)));
                        break;
                    case "[condition for rank]":
                        Block(source, ref i);
                        break;
                    default:
                        // 未消费的显示扩展仍按完整块跳过，不能误认其内部同名业务标签。
                        if (!field.Tag.StartsWith("[/", StringComparison.Ordinal) && field.Tag.Length > 2 &&
                            source.Skip(i + 1).Any(row => row.Tag == "[/" + field.Tag[1..])) Block(source, ref i);
                        break;
                }
            }
            if (dungeons.Count == 0 || rules.Count == 0 || dungeons.Select(row => row.Id).Distinct().Count() != dungeons.Count)
                Issue("prey-phase-incomplete", source.FirstOrDefault()?.TokenIndex ?? 0, name);
            foreach (var clause in rules.SelectMany(rule => rule.Trigger.Concat(rule.Behavior)))
                if (clause.DungeonId is { } dungeon && !dungeons.Any(row => row.Id == dungeon))
                    Issue("prey-dungeon-reference-missing", clause.TokenIndex, dungeon.ToString());
            return new(name, dungeons.ToImmutableArray(), rules.ToImmutableArray(), rewards.ToImmutableArray()) { Declarations = source };
        }

        List<RaidField> headers = [];
        var phases = ImmutableArray.CreateBuilder<RaidPhase>();
        var closing = fields.Where(field => field.Tag.StartsWith("[/", StringComparison.Ordinal)).Select(field => field.Tag).ToHashSet();
        var scope = new Stack<string>();
        for (int i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            if (scope.Count == 0 && field.Tag is "[phase]" or "[single raid phase]")
            {
                var block = Block(fields, ref i);
                if (field.Tag == "[phase]") phases.Add(Phase(block));
                continue;
            }
            headers.Add(field);
            if (scope.Count != 0 && scope.Peek() == field.Tag) scope.Pop();
            else if (field.Tag.Length > 2 && !field.Tag.StartsWith("[/", StringComparison.Ordinal) && closing.Contains("[/" + field.Tag[1..]))
                scope.Push("[/" + field.Tag[1..]);
        }
        RaidField Required(string tag, int count)
        {
            var rows = headers.Where(row => row.Tag == tag).ToArray();
            if (rows is [var row] && Ints(row, count)) return row;
            Issue("prey-header-invalid", rows.FirstOrDefault()?.TokenIndex ?? 0, tag);
            return new(tag, 0, []);
        }
        var waiting = Required("[waiting room]", 2);
        var cost = Required("[entry cost]", 2);
        var retry = Required("[retry item]", 2);
        var count = Required("[phase max]", 1);
        var maximum = Required("[raid member max]", 1);
        var minimum = Required("[raid start minimum member]", 1);
        if (count.Values.Length == 1 && (count.Values[0].Number <= 0 || count.Values[0].Number != phases.Count))
            Issue("prey-phase-count-invalid", count.TokenIndex, count.Tag);
        if (maximum.Values is [{ Number: var maxMembers }] && minimum.Values is [{ Number: var minMembers }] &&
            (minMembers <= 0 || maxMembers < minMembers)) Issue("prey-member-limits-invalid", maximum.TokenIndex, maximum.Tag);
        if (cost.Values.Any(value => value.Number <= 0) || retry.Values.Any(value => value.Number <= 0))
            Issue("prey-cost-invalid", cost.TokenIndex, cost.Tag);
        if (waiting.Values.Any(value => value.Number < 0)) Issue("prey-waiting-room-invalid", waiting.TokenIndex, waiting.Tag);
        return new(Path, waiting.Values.Length == 2 ? new(waiting.Values[0].Number, waiting.Values[1].Number) : null,
            cost.Values.Length == 2 ? new(cost.Values[0].Number, cost.Values[1].Number) : null,
            retry.Values.Length == 2 ? new(retry.Values[0].Number, retry.Values[1].Number) : null,
            symbols.ToImmutable(), phases.ToImmutable(), headers.ToImmutableArray(), issues.ToImmutableArray());
    }
}
