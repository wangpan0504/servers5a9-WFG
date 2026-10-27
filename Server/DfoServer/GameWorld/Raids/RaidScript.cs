using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Raids;

internal enum RaidScriptKind { Anton, Luke }

// 只保存资源声明；状态值来自 042A77B0，不是 NOTI587 的团状态。
internal enum RaidDungeonState : byte { Open = 0, Close = 1, Hide = 2, Clear = 3 }
internal sealed record RaidValue(byte Type, int Number, string? Text);
internal sealed record RaidField(string Tag, int TokenIndex, ImmutableArray<RaidValue> Values);
internal sealed record RaidIssue(string Code, string Path, int TokenIndex, string Detail);
internal sealed record RaidCost(int ItemId, int Count);
internal sealed record RaidWaitingRoom(int Town, int Area);
internal sealed record RaidDungeon(int Id, RaidDungeonState InitialState, string HpSymbol, int HpRaw, int PartyMax)
{
    public ImmutableArray<RaidField> Declarations { get; init; } = [];
}
internal sealed record RaidTimer(int Kind, int DungeonId);
internal sealed record RaidClause(string Tag, int TokenIndex, int? DungeonId, string? Symbol,
    RaidDungeonState? State, RaidTimer? Timer, ImmutableArray<int> RawIntegers)
{
    public string? Operator { get; init; }
    public int? MapId { get; init; }
}
internal sealed record RaidRule(ImmutableArray<RaidClause> Trigger, ImmutableArray<RaidClause> Behavior);
// 未闭合的列保持原始顺序，不能解释成数量或概率。
internal sealed record RaidReward(string Kind, int RawA, int RawB, int ItemId, int RawD);
internal sealed record RaidPhase(string Name, ImmutableArray<RaidDungeon> Dungeons,
    ImmutableArray<RaidRule> Rules, ImmutableArray<RaidReward> Rewards)
{
    public ImmutableArray<RaidField> Declarations { get; init; } = [];
}
internal sealed record RaidScript(string Path, RaidWaitingRoom? WaitingRoom, RaidCost? EntryCost,
    RaidCost? RetryItem, ImmutableDictionary<string, int> Symbols, ImmutableArray<RaidPhase> Phases,
    ImmutableArray<RaidField> Headers, ImmutableArray<RaidIssue> Issues)
{
    public ImmutableArray<LukeCardMapRecord> LukeCardMapRecords { get; init; } = [];
}

// 按团本类型隔离已核对的资源语法；只保存声明，不执行触发器、计时、状态推进或奖励。
internal static class RaidScriptParser
{
    internal static string Tag(string value) => value.Trim().ToLowerInvariant();

    public static RaidScript Parse(string path, IReadOnlyList<PvfToken> tokens,
        IReadOnlyList<PvfToken> symbolTokens, Func<int, string> resolve, RaidScriptKind kind = RaidScriptKind.Anton)
    {
        bool luke = kind == RaidScriptKind.Luke;
        path = PvfArchive.NormalizePath(path);
        var issues = new List<RaidIssue>();
        void Issue(string code, int token, string detail) => issues.Add(new(code, path, token, detail));
        var fields = Fields(tokens, resolve, Issue);
        var symbols = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        var symbolIds = new HashSet<int>();
        for (int i = 0; i < symbolTokens.Count; i += 2)
        {
            if (i + 1 >= symbolTokens.Count || symbolTokens[i] is not { Type: 0, Value: >= 0 } || symbolTokens[i + 1].Type != 3)
            {
                issues.Add(new("invalid-symbol", path + ".symbol", i, "expected integer and symbol tag"));
                continue;
            }
            string name = Tag(resolve(symbolTokens[i + 1].Value));
            if (name.Length < 3 || !name.StartsWith('[') || !name.EndsWith(']') || !symbolIds.Add(symbolTokens[i].Value) ||
                !symbols.TryAdd(name, symbolTokens[i].Value))
                issues.Add(new("duplicate-or-invalid-symbol", path + ".symbol", i, name));
        }
        if (symbols.Count == 0) Issue("symbol-definitions-missing", 0, path + ".symbol");

        List<RaidField> headers = [];
        List<RaidPhase> phases = [];
        for (int i = 0; i < fields.Length; i++)
        {
            if (fields[i].Tag == "[phase]")
            {
                Empty(fields[i]);
                phases.Add(Phase(Block(fields, ref i, "[/phase]")));
            }
            else headers.Add(fields[i]);
        }

        int? Scalar(string tag, int count = 1)
        {
            var rows = headers.Where(f => f.Tag == tag).ToArray();
            if (rows.Length != 1 || !Integers(rows[0], count))
            {
                Issue("invalid-header", rows.FirstOrDefault()?.TokenIndex ?? 0, tag);
                return null;
            }
            return rows[0].Values[0].Number;
        }
        int? phaseMax = Scalar("[phase max]");
        int? memberMax = Scalar("[raid member max]"), minimum = Scalar("[raid start minimum member]");
        if (memberMax is not > 0 || minimum is not > 0 || minimum > memberMax) Issue("member-limits-invalid", 0, "raid member limits");
        if (phaseMax is not > 0 || phases.Count != phaseMax) Issue("phase-count-mismatch", 0, "[phase max]");
        var waiting = headers.Where(f => f.Tag == "[waiting room]").ToArray();
        RaidWaitingRoom? room = waiting is [var w] && Integers(w, 2) && w.Values.All(v => v.Number >= 0)
            ? new(w.Values[0].Number, w.Values[1].Number) : null;
        if (room is null) Issue("waiting-room-invalid", waiting.FirstOrDefault()?.TokenIndex ?? 0, "[waiting room]");
        RaidCost? Cost(string tag)
        {
            var rows = headers.Where(f => f.Tag == tag).ToArray();
            if (rows is [var row] && Integers(row, 2) && row.Values.All(v => v.Number > 0))
                return new(row.Values[0].Number, row.Values[1].Number);
            Issue("cost-declaration-invalid", rows.FirstOrDefault()?.TokenIndex ?? 0, tag);
            return null;
        }
        var entry = Cost("[entry cost]");
        var retry = Cost("[retry item]");
        // 其余头字段只核对形状并保留原值；不赋予时间单位、随机或计费语义。
        var headerShapes = new Dictionary<string, int>
        {
            ["[phase max]"] = 1, ["[raid member max]"] = 1, ["[raid start minimum member]"] = 1,
            ["[waiting room]"] = 2, ["[waiting room npc]"] = 2, ["[daily account enter limit]"] = 1,
            ["[disconn member wait time]"] = 1, ["[start delay time]"] = 1, ["[phase time over]"] = phases.Count,
            ["[phase break time]"] = 1, ["[movie end time]"] = 4, ["[entry cost]"] = 2, ["[/entry cost]"] = 0,
            ["[retry]"] = 1, ["[retry item]"] = 2, ["[weekly clear count]"] = 1, ["[shield charge rate]"] = 2,
            ["[hatchery info]"] = 6, ["[/hatchery info]"] = 0, ["[except cheat dungeon]"] = 1, ["[/except cheat dungeon]"] = 0
        };
        if (luke)
        {
            headerShapes.Remove("[shield charge rate]");
            headerShapes.Remove("[hatchery info]"); headerShapes.Remove("[/hatchery info]");
            headerShapes.Remove("[except cheat dungeon]"); headerShapes.Remove("[/except cheat dungeon]");
            foreach (var (tag, count) in new (string, int)[]
            {
                ("[dungeon random max buff count]", 3), ("[card set map info]", 15), ("[/card set map info]", 0),
                ("[1phase obj destroy]", 9), ("[/1phase obj destroy]", 0), ("[sanctum timer count]", 1),
                ("[default object count]", 0), ("[birth sanctum]", 2), ("[extinction sanctum]", 2), ("[spread sanctum]", 2),
                ("[/default object count]", 0), ("[force change difficulty dungeon list]", 0), ("[/force change difficulty dungeon list]", 0)
            }) headerShapes[tag] = count;
        }
        foreach (var group in headers.GroupBy(f => f.Tag))
        {
            if (group.Count() != 1) Issue("duplicate-header", group.First().TokenIndex, group.Key);
            foreach (var field in group)
                if (!headerShapes.TryGetValue(field.Tag, out int shape) || !Integers(field, shape))
                    Issue("unsupported-header", field.TokenIndex, field.Tag);
        }
        foreach (string start in luke ? new[] { "[entry cost]", "[card set map info]", "[1phase obj destroy]", "[force change difficulty dungeon list]" } :
            new[] { "[entry cost]", "[hatchery info]", "[except cheat dungeon]" })
        {
            int at = headers.FindIndex(f => f.Tag == start);
            if (at < 0 || at + 1 >= headers.Count || headers[at + 1].Tag != "[/" + start[1..])
                Issue("header-block-incomplete", at < 0 ? 0 : headers[at].TokenIndex, start);
        }
        if (luke)
        {
            string[] objectTags = ["[default object count]", "[birth sanctum]", "[extinction sanctum]", "[spread sanctum]", "[/default object count]"];
            int start = headers.FindIndex(f => f.Tag == objectTags[0]);
            if (start < 0 || !headers.Skip(start).Take(objectTags.Length).Select(f => f.Tag).SequenceEqual(objectTags))
                Issue("header-block-incomplete", start < 0 ? 0 : headers[start].TokenIndex, objectTags[0]);
            var firstPhase = phases.FirstOrDefault()?.Dungeons.Select(d => d.Id).ToHashSet() ?? [];
            foreach (var field in headers.Where(f => objectTags[1..4].Contains(f.Tag)))
                if (field.Values.Length == 2 && !firstPhase.Contains(field.Values[1].Number))
                    Issue("header-dungeon-reference-missing", field.TokenIndex, field.Tag);
        }
        var allDungeons = phases.SelectMany(p => p.Dungeons).Select(d => d.Id).ToHashSet();
        foreach (var field in headers.Where(f => f.Tag is "[hatchery info]" or "[except cheat dungeon]"))
            foreach (var value in field.Values.Skip(field.Tag == "[hatchery info]" ? 2 : 0))
                if (value.Type == 0 && !allDungeons.Contains(value.Number))
                    Issue("header-dungeon-reference-missing", field.TokenIndex, value.Number.ToString());
        if (luke)
        {
            // 卢克timer属于实例键空间，可引用其它阶段登记的地下城；RESET缺键是幂等取消。
            var timerClauses = phases.SelectMany(p => p.Rules).SelectMany(r => r.Trigger.Concat(r.Behavior))
                .Where(c => c.Timer is not null).ToArray();
            var declaredTimers = timerClauses.Where(c => c.Tag == "[set timer]").Select(c => c.Timer).ToHashSet();
            foreach (var clause in timerClauses)
            {
                if (!allDungeons.Contains(clause.Timer!.DungeonId))
                    Issue("raid-timer-dungeon-reference-missing", clause.TokenIndex, clause.Timer.DungeonId.ToString());
                if (clause.Tag == "[check timer end]" && !declaredTimers.Contains(clause.Timer))
                    Issue("raid-timer-reference-missing", clause.TokenIndex, $"{clause.Timer.Kind}:{clause.Timer.DungeonId}");
            }
        }
        ImmutableArray<LukeCardMapRecord> cardMaps = [];
        if (luke && headers.Where(field => field.Tag == "[card set map info]").ToArray() is [var cardMap])
        {
            try
            {
                cardMaps = LukeCardMapTable.Parse(tokens.Skip(cardMap.TokenIndex + 1).Take(cardMap.Values.Length).ToArray());
            }
            catch (InvalidDataException error)
            {
                Issue("invalid-card-map-records", cardMap.TokenIndex, error.Message);
            }
        }
        return new(path, room, entry, retry, symbols.ToImmutable(), phases.ToImmutableArray(),
            headers.ToImmutableArray(), issues.ToImmutableArray()) { LukeCardMapRecords = cardMaps };

        bool Integers(RaidField field, int count) => field.Values.Length == count && field.Values.All(v => v.Type == 0);
        bool Text(RaidField field, int count = 1) => field.Values.Length == count && field.Values.All(v => v.Type == 6);
        void Empty(RaidField field)
        {
            if (!Integers(field, 0)) Issue("invalid-block-marker", field.TokenIndex, field.Tag);
        }
        ImmutableArray<RaidField> Block(ImmutableArray<RaidField> source, ref int at, string end)
        {
            int start = ++at;
            while (at < source.Length && source[at].Tag != end) at++;
            if (at == source.Length) Issue("block-incomplete", source[start - 1].TokenIndex, end);
            else Empty(source[at]);
            return source[start..at];
        }
        RaidDungeonState? State(RaidField field, int at)
        {
            if (at < field.Values.Length && field.Values[at] is { Type: 6, Text: { } value })
            {
                var state = value.Trim().ToLowerInvariant() switch
                {
                    "open" => RaidDungeonState.Open, "close" => RaidDungeonState.Close,
                    "hide" => RaidDungeonState.Hide, "clear" => RaidDungeonState.Clear, _ => (RaidDungeonState?)null
                };
                if (state is not null) return state;
            }
            Issue("invalid-dungeon-state", field.TokenIndex, field.Tag);
            return null;
        }
        void Symbol(string symbol, int token)
        {
            if (!symbols.ContainsKey(symbol)) Issue("symbol-reference-missing", token, symbol);
        }
        ImmutableArray<RaidClause> Clauses(ImmutableArray<RaidField> source, bool trigger, int markerToken)
        {
            List<RaidClause> result = [];
            if (source.IsEmpty) Issue("empty-rule-block", markerToken, trigger ? "[trigger]" : "[behavior]");
            for (int i = 0; i < source.Length; i++)
            {
                var f = source[i];
                string? symbol = null, operation = null;
                int? map = null;
                int? dungeon = null;
                RaidDungeonState? state = null;
                RaidTimer? timer = null;
                ImmutableArray<int> raw = [];
                bool valid;
                if (trigger && f.Tag is "[if]" or "[on change symbol]" || !trigger && f.Tag == "[set]")
                {
                    valid = Integers(f, 0) && i + 1 < source.Length;
                    if (valid)
                    {
                        var target = source[++i];
                        symbol = target.Tag;
                        Symbol(symbol, target.TokenIndex);
                        valid = Integers(target, f.Tag == "[on change symbol]" ? 0 : 1);
                        raw = target.Values.Where(v => v.Type == 0).Select(v => v.Number).ToImmutableArray();
                        if (luke && !trigger && f.Tag == "[set]" && target.Values.IsEmpty && i + 1 < source.Length && source[i + 1].Tag == "[+]")
                        {
                            var addition = source[++i];
                            operation = addition.Tag;
                            valid = Integers(addition, 1);
                            raw = addition.Values.Where(v => v.Type == 0).Select(v => v.Number).ToImmutableArray();
                        }
                    }
                }
                else if (trigger && f.Tag is "[check dungeon state]" or "[on change dungeon state]" ||
                    !trigger && f.Tag is "[set dungeon state]" or "[reserve dungeon state]")
                {
                    int count = f.Tag == "[on change dungeon state]" ? 1 : f.Tag == "[reserve dungeon state]" ? 3 : 2;
                    valid = f.Values.Length == count && f.Values[0] is { Type: 0, Number: >= 0 };
                    if (valid)
                    {
                        dungeon = f.Values[0].Number;
                        if (count > 1) state = State(f, 1);
                        valid &= count == 1 || state is not null;
                        if (count == 3) valid &= f.Values[2] is { Type: 0, Number: >= 0 };
                        raw = f.Values.Where(v => v.Type == 0).Select(v => v.Number).ToImmutableArray();
                    }
                }
                else if (trigger && f.Tag == "[check timer end]" || !trigger && f.Tag is "[set timer]" or "[reset timer]")
                {
                    valid = Integers(f, f.Tag == "[set timer]" ? 3 : 2) && f.Values.All(v => v.Number >= 0);
                    if (valid)
                    {
                        timer = new(f.Values[0].Number, f.Values[1].Number);
                        dungeon = timer.DungeonId;
                        raw = f.Values.Select(v => v.Number).ToImmutableArray();
                    }
                }
                else if (luke && (trigger && f.Tag is "[on giveup dungeon]" or "[on enter dungeon]" ||
                    !trigger && f.Tag == "[reset sanctum object]"))
                {
                    valid = Integers(f, 1) && f.Values[0].Number >= 0;
                    if (valid) { dungeon = f.Values[0].Number; raw = [dungeon.Value]; }
                }
                else if (luke && trigger && f.Tag is "[on enter map]" or "[phase end]")
                {
                    valid = Integers(f, 1) && f.Values[0].Number >= 0;
                    if (valid)
                    {
                        raw = [f.Values[0].Number];
                        if (f.Tag == "[on enter map]") map = raw[0];
                    }
                }
                else valid = !trigger && (f.Tag == "[clear phase]" || !luke && f.Tag == "[open random hatchery]" ||
                    luke && f.Tag == "[fail phase]") && Integers(f, 0);
                if (!valid) Issue("unsupported-clause", f.TokenIndex, f.Tag);
                result.Add(new(f.Tag, f.TokenIndex, dungeon, symbol, state, timer, raw) { Operator = operation, MapId = map });
            }
            return result.ToImmutableArray();
        }
        RaidPhase Phase(ImmutableArray<RaidField> source)
        {
            string name = "";
            List<RaidDungeon> dungeons = [];
            List<RaidRule> rules = [];
            List<RaidReward> rewards = [];
            var singles = new HashSet<string>();
            for (int i = 0; i < source.Length; i++)
            {
                var field = source[i];
                switch (field.Tag)
                {
                    case "[phase name]":
                        if (Text(field) && singles.Add(field.Tag)) name = field.Values[0].Text!;
                        else Issue("phase-name-invalid", field.TokenIndex, field.Tag);
                        break;
                    case "[title img]":
                        if (!singles.Add(field.Tag) || field.Values is not [{ Type: 6 }, { Type: 0 }])
                            Issue("phase-title-invalid", field.TokenIndex, field.Tag);
                        break;
                    case "[phase coin limit]" when luke:
                    case "[minimal dungeon clear count]":
                        if (!singles.Add(field.Tag) || !Integers(field, 1) || field.Values[0].Number < 0)
                            Issue("phase-clear-count-invalid", field.TokenIndex, field.Tag);
                        break;
                    case "[condition for rank]":
                        Empty(field);
                        if (!singles.Add(field.Tag)) Issue("duplicate-phase-field", field.TokenIndex, field.Tag);
                        foreach (var rank in Block(source, ref i, "[/condition for rank]"))
                            if (rank.Tag != "[dead count]" || !Integers(rank, 3)) Issue("unsupported-rank", rank.TokenIndex, rank.Tag);
                        break;
                    case "[clear reward item]":
                        Empty(field);
                        if (!singles.Add(field.Tag)) Issue("duplicate-phase-field", field.TokenIndex, field.Tag);
                        foreach (var reward in Block(source, ref i, "[/clear reward item]"))
                        {
                            if (reward.Tag != "[state reward]" || reward.Values is not
                                [{ Type: 6, Text: "party_card" or "pcroom_card" or "pcroom_card_a" or "squad_item" or "privilege_card" },
                                 { Type: 0, Number: >= -1 and <= 4 }, { Type: 0 }, { Type: 0, Number: > 0 }, { Type: 0, Number: >= 0 and <= 2 }])
                            { Issue("unsupported-reward", reward.TokenIndex, reward.Tag); continue; }
                            if (!luke && reward.Values[0].Text == "pcroom_card_a")
                            { Issue("unsupported-reward", reward.TokenIndex, reward.Tag); continue; }
                            var v = reward.Values;
                            rewards.Add(new(v[0].Text!, v[1].Number, v[2].Number, v[3].Number, v[4].Number));
                        }
                        break;
                    case "[dungeon info]":
                        Empty(field);
                        var block = Block(source, ref i, "[/dungeon info]");
                        var basic = luke && block.Length >= 6 ? block[..5] : block;
                        if (basic is [var id, var init, var hp, var hpSymbol, var max] &&
                            id.Tag == "[index]" && Integers(id, 1) && id.Values[0].Number >= 0 &&
                            init.Tag == "[init state]" && Text(init) && State(init, 0) is { } initial &&
                            hp.Tag == "[hp info]" && Integers(hp, 0) && Integers(hpSymbol, 1) && hpSymbol.Values[0].Number >= 0 &&
                            max.Tag == "[party max]" && Integers(max, 1) && max.Values[0].Number > 0)
                        {
                            Symbol(hpSymbol.Tag, hpSymbol.TokenIndex);
                            if (luke)
                            {
                                bool lockout = block.Length >= 6 && block[5].Tag == "[lockout type]" && Integers(block[5], 2);
                                bool objects = block.Length == 8 && block[6].Tag == "[destroy object info]" && Integers(block[6], 0) && Integers(block[7], 0);
                                if (!lockout || block.Length != 6 && !objects) Issue("dungeon-declaration-invalid", field.TokenIndex, field.Tag);
                                if (objects) Symbol(block[7].Tag, block[7].TokenIndex);
                            }
                            dungeons.Add(new(id.Values[0].Number, initial, hpSymbol.Tag, hpSymbol.Values[0].Number, max.Values[0].Number)
                                { Declarations = block });
                        }
                        else Issue("dungeon-declaration-invalid", field.TokenIndex, field.Tag);
                        break;
                    case "[trigger]":
                        Empty(field);
                        var trigger = Clauses(Block(source, ref i, "[/trigger]"), true, field.TokenIndex);
                        if (i + 1 >= source.Length || source[i + 1].Tag != "[behavior]")
                        { Issue("behavior-missing", field.TokenIndex, field.Tag); break; }
                        Empty(source[++i]);
                        int behaviorToken = source[i].TokenIndex;
                        rules.Add(new(trigger, Clauses(Block(source, ref i, "[/behavior]"), false, behaviorToken)));
                        break;
                    default:
                        Issue("unsupported-phase-field", field.TokenIndex, field.Tag);
                        break;
                }
            }
            if (name.Length == 0 || dungeons.Count == 0 || rules.Count == 0 || rewards.Count == 0)
                Issue("phase-definition-incomplete", source.FirstOrDefault()?.TokenIndex ?? 0, name);
            var ids = dungeons.Select(d => d.Id).ToHashSet();
            if (ids.Count != dungeons.Count) Issue("duplicate-phase-dungeon", source[0].TokenIndex, name);
            var clauses = rules.SelectMany(r => r.Trigger.Concat(r.Behavior)).ToArray();
            var timers = clauses.Where(c => c.Tag == "[set timer]").Select(c => c.Timer).ToHashSet();
            foreach (var clause in clauses)
            {
                if ((!luke || clause.Timer is null) && clause.DungeonId is { } id && !ids.Contains(id)) Issue("phase-dungeon-reference-missing", clause.TokenIndex, id.ToString());
                if (!luke && clause.Timer is { } t && !timers.Contains(t)) Issue("phase-timer-reference-missing", clause.TokenIndex, $"{t.Kind}:{t.DungeonId}");
            }
            return new(name, dungeons.ToImmutableArray(), rules.ToImmutableArray(), rewards.ToImmutableArray()) { Declarations = source };
        }
    }

    static ImmutableArray<RaidField> Fields(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve,
        Action<string, int, string> issue)
    {
        var fields = ImmutableArray.CreateBuilder<RaidField>();
        for (int i = 0; i < tokens.Count;)
        {
            int start = i;
            if (tokens[i].Type != 3) { issue("orphan-token", i++, "expected tag"); continue; }
            string tag = Tag(resolve(tokens[i++].Value));
            if (!tag.StartsWith('[') || !tag.EndsWith(']')) issue("invalid-tag", start, tag);
            var values = ImmutableArray.CreateBuilder<RaidValue>();
            while (i < tokens.Count && tokens[i].Type != 3)
            {
                var token = tokens[i++];
                if (token.Type is not (0 or 6)) issue("unsupported-token", i - 1, tag);
                values.Add(new(token.Type, token.Value, token.Type == 6 ? resolve(token.Value) : null));
            }
            fields.Add(new(tag, start, values.ToImmutable()));
        }
        return fields.ToImmutable();
    }
}
