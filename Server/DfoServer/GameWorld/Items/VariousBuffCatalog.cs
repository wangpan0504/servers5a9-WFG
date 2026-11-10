using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal enum VariousBuffExecutor { ServerDrop, NativeClient, Unverified }
internal enum VariousBuffDropEffect { OrdinaryEpic, InfernoEpic, ParticularEpic, DaybreakEpic, HellLegendEpic, Boss }
internal enum VariousBuffDungeonScope { All, Level, HellLegend, Hell, DungeonIndex, DungeonType, ExceptDungeonType, Unverified }
internal sealed record VariousBuffScopeRule(VariousBuffDungeonScope Kind, ImmutableArray<int> Values);
internal sealed record VariousBuffChoice(ImmutableArray<int> BuffIds, int Weight);
internal sealed record VariousBuffBinding(VariousBuffAssociation Association, ImmutableDictionary<int, VariousBuffDefinition> Definitions)
{
    public bool Ready => Association.Valid && Association.ReachableBuffIds.All(id => Definitions.TryGetValue(id, out var definition) && definition.Supported);
    public ImmutableArray<string> Errors => Association.Errors.AddRange(Association.ReachableBuffIds.SelectMany(id =>
        Definitions.TryGetValue(id, out var definition) ? definition.UnsupportedReasons : ["missing-buff-definition:" + id])).Distinct().ToImmutableArray();
}
internal sealed record VariousBuffAssociation(int ItemId, ImmutableArray<VariousBuffChoice> Choices, bool Random,
    ImmutableArray<string> Errors)
{
    public bool Valid => Errors.IsEmpty;
    public IEnumerable<int> BuffIds => Choices.SelectMany(choice => choice.BuffIds).Distinct();
    public IEnumerable<int> ReachableBuffIds => Choices.Where(choice => choice.Weight > 0).SelectMany(choice => choice.BuffIds).Distinct();
    public ImmutableArray<int> Select(Func<int, int> draw)
    {
        if (!Valid) throw new InvalidDataException("STK Buff关联无效");
        if (!Random) return Choices[0].BuffIds;
        int value = draw(100);
        if (value is < 0 or >= 100) throw new InvalidDataException("Buff抽选超出百分比分母");
        int cumulative = 0;
        foreach (var choice in Choices)
        {
            cumulative += choice.Weight;
            if (value < cumulative) return choice.BuffIds;
        }
        return [];
    }
}

internal sealed record VariousBuffEffect(string Name, ImmutableArray<int> Parameters, VariousBuffExecutor Executor,
    VariousBuffDropEffect? DropEffect = null)
{
    public ImmutableArray<PvfToken> RawParameters { get; init; } = [];
    public ImmutableDictionary<int, string> TextParameters { get; init; } = ImmutableDictionary<int, string>.Empty;
    public bool ParametersSupported { get; init; } = true;
}

internal sealed record VariousBuffDefinition(int BuffId, string SystemGroup, ImmutableArray<VariousBuffEffect> Effects,
    string ApplyType, ImmutableArray<int> ApplyParameters, VariousBuffDungeonScope DungeonScope,
    ImmutableArray<string> UnsupportedReasons)
{
    public int MinimumDungeonLevel { get; init; }
    public int MaximumDungeonLevel { get; init; } = 255;
    public ImmutableArray<VariousBuffScopeRule> ScopeRules { get; init; } = [];
    public ImmutableHashSet<int> ExceptChannels { get; init; } = ImmutableHashSet<int>.Empty;
    public bool Save { get; init; }
    public bool DeleteDaily { get; init; }
    public bool Overlap { get; init; }
    public int OverlapMax { get; init; } = 1;
    public int BanGroup { get; init; }
    public bool BuffCheck { get; init; }
    public int MinimumPartyMembers { get; init; }
    public int MaximumPartyMembers { get; init; } = int.MaxValue;
    public ImmutableArray<int> ExtraParameters { get; init; } = [];
    public ImmutableArray<string> BindingErrors { get; init; } = [];
    public bool BindingReady => BindingErrors.IsEmpty;
    public bool Timed => ApplyType == "expire_time";
    public bool Supported => UnsupportedReasons.IsEmpty;
    public bool OnlyDropEffect(VariousBuffDropEffect kind) => Effects.Count(effect => effect.Executor == VariousBuffExecutor.ServerDrop) == 1 &&
        Effects.Any(effect => effect.DropEffect == kind);
    public int Percent(VariousBuffDropEffect effect) => Effects.Where(row => row.DropEffect == effect)
        .Select(row => row.Parameters[0]).DefaultIfEmpty().Single();
}

// index只标识资源；效果职责由name注册，生命周期和适用范围由元数据解释。
internal sealed class VariousBuffCatalog
{
    public ImmutableDictionary<int, VariousBuffDefinition> Definitions { get; }
    public ImmutableDictionary<int, VariousBuffDefinition> Items { get; }
    public ImmutableDictionary<string, ImmutableHashSet<int>> Families { get; }
    public ImmutableDictionary<int, VariousBuffAssociation> Associations { get; }
    public VariousBuffBinding? Bind(int item) => Associations.TryGetValue(item, out var association) ? new(association, Definitions) : null;

    internal VariousBuffCatalog(ImmutableDictionary<int, VariousBuffDefinition> definitions,
        ImmutableDictionary<int, VariousBuffDefinition> items, ImmutableDictionary<int, VariousBuffAssociation>? associations = null)
    {
        Definitions = definitions;
        Items = items;
        Associations = associations ?? items.ToImmutableDictionary(pair => pair.Key,
            pair => new VariousBuffAssociation(pair.Key, [new([pair.Value.BuffId], 100)], false, []));
        Families = items.Where(pair => pair.Value.Supported).GroupBy(pair => pair.Value.SystemGroup, StringComparer.Ordinal)
            .ToImmutableDictionary(group => group.Key, group => group.Select(pair => pair.Key).ToImmutableHashSet(), StringComparer.Ordinal);
    }

    public static VariousBuffCatalog Load(PvfArchive archive)
    {
        if (!archive.Contains("etc/variousbufflist.etc") || !archive.Contains("etc/variousbuffstackablelist.etc")) return new([], []);
        var definitions = ReadDefinitions(EpicDropBuffDefinition.ReadIndices(archive.ReadTokens("etc/variousbufflist.etc"),
            archive.ResolveString), archive.ResolveString);
        var rows = EpicDropBuffDefinition.ReadIndices(archive.ReadTokens("etc/variousbuffstackablelist.etc"), archive.ResolveString);
        var associations = ReadAssociations(rows, definitions).ToBuilder();
        var items = ReadMappings(rows, definitions).ToBuilder();
        var declared = rows.SelectMany(row => row.Values).Where(value => value.Type == 0).Select(value => value.Value).ToHashSet();
        // 保留已确认的两项缺映射兼容；这些编号仅修复资源关联，不决定效果类型或参数。
        foreach (var (item, buff, effect, percent) in new[]
        {
            (490000413, 1136, VariousBuffDropEffect.OrdinaryEpic, 100),
            (490011326, 1271, VariousBuffDropEffect.HellLegendEpic, 6520)
        })
            if (!declared.Contains(item) && definitions.TryGetValue(buff, out var definition) && definition.Supported &&
                definition.Timed && definition.Effects is [{ DropEffect: { } kind }] && kind == effect && definition.Percent(effect) == percent)
            {
                items.Add(item, definition);
                associations.Add(item, new(item, [new([buff], 100)], false, []));
            }
        return new(definitions, items.ToImmutable(), associations.ToImmutable());
    }

    internal static VariousBuffEffect ReadEffect(string name, ImmutableArray<int> parameters)
    {
        VariousBuffDropEffect? effect = name switch
        {
            "epic_rarity_item_drop_rate" => VariousBuffDropEffect.OrdinaryEpic,
            "inferno_dungeon_epic_drop_rate_up" => VariousBuffDropEffect.InfernoEpic,
            "particular_dungeon_epic_drop_rate_up" => VariousBuffDropEffect.ParticularEpic,
            "creak_daybreak_dungeon_epic_drop_rate_up" => VariousBuffDropEffect.DaybreakEpic,
            "hell_legend_dungeon_epic_drop_rate_up" => VariousBuffDropEffect.HellLegendEpic,
            "boss_drop_rate" => VariousBuffDropEffect.Boss,
            _ => null
        };
        var executor = effect is not null ? VariousBuffExecutor.ServerDrop : name is "physical_attack" or "physical_defence" or
            "magical_attack" or "magical_defence" or "move_speed" or "village_move_speed"
                ? VariousBuffExecutor.NativeClient : VariousBuffExecutor.Unverified;
        return new(name, parameters, executor, effect)
        {
            ParametersSupported = effect is not null ? parameters is [>= 0] : executor == VariousBuffExecutor.NativeClient && !parameters.IsEmpty
        };
    }

    internal static ImmutableDictionary<int, VariousBuffDefinition> ReadDefinitions(ImmutableArray<PvfTokenSection> rows,
        Func<int, string> resolve)
    {
        var result = ImmutableDictionary.CreateBuilder<int, VariousBuffDefinition>();
        var identities = rows.SelectMany(row => row.Values).Where(value => value.Type == 0).GroupBy(value => value.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        foreach (var group in rows.Where(row => row.Values is [{ Type: 0, Value: > 0 }]).GroupBy(row => row.Values[0].Value))
        {
            var row = group.First();
            var binding = ImmutableArray.CreateBuilder<string>();
            var execution = ImmutableArray.CreateBuilder<string>();
            if (group.Count() != 1 || !row.IsBlock || identities[group.Key] != 1) binding.Add("invalid-or-duplicate-definition");
            PvfTokenSection? Field(ImmutableArray<PvfTokenSection> fields, string name)
            {
                var found = fields.Where(field => field.Name == name).ToArray();
                if (found.Length > 1) binding.Add("duplicate-field:" + name);
                return found.Length == 1 ? found[0] : null;
            }
            string Text(PvfTokenSection? field) => field is { Children.Length: 0, Values: [{ Type: 6 } value] } ? resolve(value.Value) : "";
            ImmutableArray<int> Numbers(PvfTokenSection? field) => field is { Children.Length: 0 } && field.Values.All(value => value.Type == 0)
                ? field.Values.Select(value => value.Value).ToImmutableArray() : [];
            int Number(string name, int defaultValue = 0)
            {
                var field = Field(row.Children, name);
                if (field is null) return defaultValue;
                if (Numbers(field) is [var value]) return value;
                binding.Add("invalid-number:" + name); return defaultValue;
            }
            var effects = ImmutableArray.CreateBuilder<VariousBuffEffect>();
            foreach (var effect in row.Children.Where(field => field.Name == "effect"))
            {
                string name = Text(Field(effect.Children, "name"));
                var parameters = effect.Children.Where(field => field.Name == "param").ToArray();
                var raw = parameters.SelectMany(field => field.Values).ToImmutableArray();
                if (name.Length == 0 || effect.Values.Length != 0 || effect.Children.Any(field => field.Name is not ("name" or "param")) ||
                    parameters.Any(field => field.Children.Length != 0)) binding.Add("invalid-effect-structure");
                var values = raw.All(value => value.Type == 0) ? raw.Select(value => value.Value).ToImmutableArray() : [];
                var parsed = ReadEffect(name, values) with { RawParameters = raw, TextParameters = raw.Select((value, index) => (value, index))
                    .Where(pair => pair.value.Type == 6).ToImmutableDictionary(pair => pair.index, pair => resolve(pair.value.Value)) };
                if (parsed.Executor == VariousBuffExecutor.Unverified) execution.Add("unverified-effect:" + name);
                else if (!parsed.ParametersSupported)
                    execution.Add("unverified-effect-parameters:" + name);
                effects.Add(parsed);
            }
            if (effects.Count == 0) binding.Add("no-effects");
            if (effects.GroupBy(effect => effect.Name, StringComparer.Ordinal).Any(items => items.Count() != 1)) binding.Add("duplicate-effect-name");
            var groupField = Field(row.Children, "system group");
            string systemGroup = Text(groupField);
            if (groupField is not null && groupField.Values is not [{ Type: 6 }]) binding.Add("invalid-system-group");
            var apply = Field(row.Children, "apply type");
            // 没有生命周期声明时按缺省计时处理；保留空参数供业务使用STK或30分钟兜底。
            string applyType = apply is null ? "expire_time" : Text(Field(apply.Children, "type"));
            var parameterField = apply is null ? null : Field(apply.Children, "param");
            var applyParameters = Numbers(parameterField);
            bool lifetimeValid = apply is null || apply is { Values.Length: 0 } &&
                (parameterField is null
                    ? applyType == "expire_time" && apply.Children.Length == 1
                    : apply.Children.Length == 2 && applyParameters is [>= 0, 0] &&
                        (applyType == "expire_time" || applyType is "dungeon_one_match" or "effect_apply" && applyParameters[0] > 0));
            if (!lifetimeValid) binding.Add("unverified-lifetime");
            int overlap = Number("overlap"), maximumOverlap = Number("overlap max", 1), banGroup = Number("overlab ban group");
            if (maximumOverlap <= 0 || banGroup is < 0 or > 255) binding.Add("invalid-overlap-metadata");
            var scopeRules = ImmutableArray.CreateBuilder<VariousBuffScopeRule>();
            foreach (var field in row.Children.Where(field => field.Name == "apply dungeon type"))
            {
                var values = field.Values;
                string label = values is [{ Type: 6 } first, ..] ? resolve(first.Value) : "";
                var arguments = values.Skip(1).Where(token => token.Type == 0).Select(token => token.Value).ToImmutableArray();
                bool integers = values.Skip(1).All(token => token.Type == 0);
                VariousBuffDungeonScope kind = label switch
                {
                    "all" => VariousBuffDungeonScope.All, "level" => VariousBuffDungeonScope.Level,
                    "only hell dungeon" => VariousBuffDungeonScope.Hell, "only hell legend seal dungeon" => VariousBuffDungeonScope.HellLegend,
                    "dugeon index" => VariousBuffDungeonScope.DungeonIndex, "dugeon type" => VariousBuffDungeonScope.DungeonType,
                    "dugeon type except" => VariousBuffDungeonScope.ExceptDungeonType, _ => VariousBuffDungeonScope.Unverified
                };
                bool valid = integers && (kind switch
                {
                    VariousBuffDungeonScope.All or VariousBuffDungeonScope.Hell or VariousBuffDungeonScope.HellLegend => arguments.IsEmpty,
                    VariousBuffDungeonScope.Level => arguments is [>= 0, <= 255] && arguments[0] <= arguments[1],
                    VariousBuffDungeonScope.DungeonIndex => arguments is [> 0],
                    VariousBuffDungeonScope.DungeonType or VariousBuffDungeonScope.ExceptDungeonType => arguments.Length > 1 &&
                        arguments[0] == arguments.Length - 1 && arguments.Skip(1).All(value => value >= 0), _ => false
                });
                if (!valid) binding.Add("unverified-dungeon-scope:" + label);
                scopeRules.Add(new(kind, kind is VariousBuffDungeonScope.DungeonType or VariousBuffDungeonScope.ExceptDungeonType ?
                    arguments.Skip(1).ToImmutableArray() : arguments));
            }
            var channels = Numbers(Field(row.Children, "except channel type"));
            if (channels.Any(value => value < 0)) binding.Add("invalid-except-channel");
            var party = Numbers(Field(row.Children, "party member count"));
            if (!party.IsEmpty && (party is not [>= 0, >= 0] || party[0] > party[1])) binding.Add("invalid-party-member-count");
            foreach (var field in row.Children.Where(field => field.Name is not ("system group" or "icon index" or "title str" or
                "tooltip text" or "tooltip duration" or "tooltip text mode" or "effect" or "apply type" or "apply dungeon type" or
                "overlap" or "overlap max" or "overlab ban group" or "is save buff" or "is delete daily" or "buff check" or
                "except channel type" or "party member count" or "param" or "multiple apply dungeon text"))) binding.Add("unverified-field:" + field.Name);
            int save = Number("is save buff"), daily = Number("is delete daily"), check = Number("buff check");
            var scopes = scopeRules.ToImmutable();
            var firstScope = scopes.FirstOrDefault();
            var errors = binding.Distinct().ToImmutableArray();
            result.Add(group.Key, new(group.Key, systemGroup, effects.ToImmutable(), applyType, applyParameters,
                firstScope?.Kind ?? VariousBuffDungeonScope.All, errors.AddRange(execution.Distinct()))
            {
                BindingErrors = errors, ScopeRules = scopes, Save = save != 0, DeleteDaily = daily != 0,
                Overlap = overlap != 0, OverlapMax = maximumOverlap, BanGroup = banGroup, BuffCheck = check != 0,
                ExceptChannels = channels.ToImmutableHashSet(), MinimumPartyMembers = party.IsEmpty ? 0 : party[0],
                MaximumPartyMembers = party.IsEmpty ? int.MaxValue : party[1], ExtraParameters = Numbers(Field(row.Children, "param")),
                MinimumDungeonLevel = firstScope is { Kind: VariousBuffDungeonScope.Level } ? firstScope.Values[0] : 0,
                MaximumDungeonLevel = firstScope is { Kind: VariousBuffDungeonScope.Level } ? firstScope.Values[1] : 255
            });
        }
        return result.ToImmutable();
    }

    internal static ImmutableDictionary<int, VariousBuffDefinition> ReadMappings(ImmutableArray<PvfTokenSection> rows,
        IReadOnlyDictionary<int, VariousBuffDefinition> definitions)
    {
        var result = ImmutableDictionary.CreateBuilder<int, VariousBuffDefinition>();
        var identities = rows.SelectMany(row => row.Values).Where(value => value.Type == 0).GroupBy(value => value.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        foreach (var group in rows.Where(row => row.Values is [{ Type: 0, Value: > 0 }]).GroupBy(row => row.Values[0].Value))
        {
            int? buff = null;
            bool valid = true;
            foreach (var row in group)
            {
                int? value = row.Children is [{ Children.Length: 0 } field] ? field switch
                {
                    { Name: "buff list", Values: [{ Type: 0 } id] } => id.Value,
                    { Name: "probability buff list", Values: [{ Type: 0 } id, { Type: 0, Value: 100 }] } => id.Value,
                    _ => null
                } : null;
                if (value is null || buff is not null && buff != value) { valid = false; break; }
                buff = value;
            }
            if (valid && buff is { } mappedBuff && definitions.TryGetValue(mappedBuff, out var definition) &&
                identities[group.Key] == group.Count())
                result.Add(group.Key, definition);
        }
        return result.ToImmutable();
    }

    internal static ImmutableDictionary<int, VariousBuffAssociation> ReadAssociations(ImmutableArray<PvfTokenSection> rows,
        IReadOnlyDictionary<int, VariousBuffDefinition> definitions)
    {
        var result = ImmutableDictionary.CreateBuilder<int, VariousBuffAssociation>();
        var counts = rows.SelectMany(row => row.Values).Where(value => value.Type == 0).GroupBy(value => value.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        foreach (var group in rows.Where(row => row.Values is [{ Type: 0, Value: > 0 }]).GroupBy(row => row.Values[0].Value))
        {
            var errors = ImmutableArray.CreateBuilder<string>();
            ImmutableArray<VariousBuffChoice> choices = [];
            bool random = false, first = true;
            foreach (var row in group)
            {
                var parsed = ImmutableArray.CreateBuilder<VariousBuffChoice>();
                bool currentRandom = false;
                if (!row.IsBlock || row.Children is not [{ Children.Length: 0 } field] || field.Values.IsEmpty ||
                    field.Values.Any(value => value.Type != 0)) { errors.Add("invalid-association-structure"); continue; }
                var values = field.Values.Select(value => value.Value).ToArray();
                if (field.Name == "buff list" && values.All(id => id > 0)) parsed.Add(new(values.ToImmutableArray(), 100));
                else if (field.Name == "probability buff list" && values.Length % 2 == 0)
                {
                    currentRandom = true;
                    for (int i = 0; i < values.Length; i += 2)
                    {
                        if (values[i] <= 0 || values[i + 1] is < 0 or > 100) errors.Add("invalid-association-choice");
                        parsed.Add(new([values[i]], values[i + 1]));
                    }
                    if (parsed.Sum(choice => choice.Weight) is <= 0 or > 100) errors.Add("invalid-association-total");
                }
                else errors.Add("invalid-association-field");
                var current = parsed.ToImmutable();
                if (!first && (random != currentRandom || choices.Length != current.Length || choices.Zip(current).Any(pair =>
                    pair.First.Weight != pair.Second.Weight || !pair.First.BuffIds.SequenceEqual(pair.Second.BuffIds))))
                    errors.Add("conflicting-association");
                choices = current; random = currentRandom; first = false;
            }
            if (counts[group.Key] != group.Count()) errors.Add("invalid-association-identity");
            foreach (int id in choices.Where(choice => choice.Weight > 0).SelectMany(choice => choice.BuffIds).Distinct())
                if (!definitions.ContainsKey(id)) errors.Add("missing-buff-definition:" + id);
            result.Add(group.Key, new(group.Key, choices, random, errors.Distinct().ToImmutableArray()));
        }
        return result.ToImmutable();
    }
}
