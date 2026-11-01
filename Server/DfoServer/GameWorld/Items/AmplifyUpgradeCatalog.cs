using System.Collections.Concurrent;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal enum AmplifyUseKind { Portable, Lucky, Protection }
internal sealed record AmplifyUse(AmplifyUseKind Kind, int MinimumUpgrade = 0, int MaximumUpgrade = 30,
    int MinimumItemLevel = 0, int MaximumItemLevel = 255, int SlotClass = 0, bool SealedOnly = false,
    ImmutableHashSet<int>? Rarities = null, int RetainLevel = 0, bool Village = true, bool SeriaRoom = true, string? PendingRule = null,
    int SuccessBonusWeight = 0)
{
    public bool Reinforce { get; init; }
}
internal sealed record AmplifyUpgradeStep(int FailureWeight, byte PenaltyType, int PenaltyValue, int MaterialId, int MaterialCount);
internal readonly record struct LuckyAmplifyStep(int Level, int DownWeight, int SameWeight, int UpWeight);
internal sealed record UpgradeDestroyBonus(int ItemId, int MinimumGrade, int MinimumUpgrade,
    ImmutableDictionary<int, float> Weights, ImmutableDictionary<int, int> Corrections)
{
    public int Count(ItemTemplate item, byte level) => item.Grade - Corrections.GetValueOrDefault(item.Rarity) >= MinimumGrade &&
        level > MinimumUpgrade && Weights.TryGetValue(item.Rarity, out float weight)
            ? checked((int)((double)(level - MinimumUpgrade) * (level - MinimumUpgrade) * weight)) : 0;
}
internal sealed record AmplifyUpgradeRules(ImmutableArray<AmplifyUpgradeStep> Steps, ImmutableArray<float> Costs,
    ImmutableArray<float> RarityWeights, ImmutableArray<float> TypeWeights, ImmutableDictionary<int, int> DestroyLevels,
    ImmutableDictionary<int, int> MaximumLevels, UpgradeDestroyBonus Bonus)
{
    public int? NoticeLevel { get; init; }
    public int Gold(ItemTemplate item)
    {
        int type = item.EquipmentSlot == 12 ? 0 : item.EquipmentSlot == 25 ? 12 : item.EquipmentSlot - 12;
        if (item.Grade < 0 || item.Grade >= Costs.Length || item.Rarity < 0 || item.Rarity >= RarityWeights.Length ||
            type < 0 || type >= TypeWeights.Length || Costs[item.Grade] <= 0 || TypeWeights[type] <= 0 || RarityWeights[item.Rarity] <= 0)
            throw new InvalidDataException("增幅费用缺少有效的品级、稀有度或部位");
        // 旧端先乘部位再乘稀有度，两个阶段各自四舍五入；增幅没有强化表的等级费用倍率。
        double typed = Math.Floor((double)Costs[item.Grade] * TypeWeights[type] + 0.5);
        return checked((int)Math.Floor(typed * RarityWeights[item.Rarity] + 0.5));
    }

    public byte Penalty(ItemTemplate item, byte level)
    {
        if (level is >= 1 and <= 9) return 1;
        return DestroyLevels.TryGetValue(item.Rarity, out int threshold) && threshold >= 0 && level >= threshold
            ? (byte)3 : Steps[level].PenaltyType;
    }
}

internal sealed class AmplifyUpgradeCatalog
{
    readonly Func<string, PvfTokenDocument> read;
    readonly Func<int, string> resolve;
    readonly IReadOnlyDictionary<int, string> stackable;
    readonly Lazy<AmplifyUpgradeRules> rules;
    readonly Lazy<AmplifyUpgradeRules> reinforceRules;
    readonly Lazy<ImmutableArray<LuckyAmplifyStep>> lucky;
    readonly Lazy<ImmutableArray<LuckyAmplifyStep>> luckyReinforce;
    readonly Lazy<UpgradeDestroyBonus> companionBonus;
    readonly ConcurrentDictionary<int, Lazy<AmplifyUse?>> uses = new();
    public AmplifyUpgradeRules Rules => rules.Value;
    public AmplifyUpgradeRules ReinforceRules => reinforceRules.Value;
    public ImmutableArray<LuckyAmplifyStep> Lucky => lucky.Value;
    public ImmutableArray<LuckyAmplifyStep> LuckyReinforce => luckyReinforce.Value;
    public UpgradeDestroyBonus CompanionBonus => companionBonus.Value;

    public AmplifyUpgradeCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> stackable)
        : this(path => new(archive.ReadTokens(path), archive.ResolveString), archive.ResolveString, stackable) { }
    internal AmplifyUpgradeCatalog(Func<string, PvfTokenDocument> read, Func<int, string> resolve, IReadOnlyDictionary<int, string> stackable)
    {
        this.read = read; this.resolve = resolve; this.stackable = stackable;
        rules = new(() => Parse(read("etc/amplifyupgrade.etc"), resolve));
        reinforceRules = new(() => Parse(read("etc/upgrade.etc"), resolve, reinforce: true));
        lucky = new(() => ParseLucky(read("etc/chn_lucky_enchant_deed.etc"), resolve));
        luckyReinforce = new(() => ParseLucky(read("etc/chn_lucky_enchant_deed.etc"), resolve, reinforce: true));
        companionBonus = new(() => ParseBonus(read("etc/upgrade.etc"), resolve));
    }

    public AmplifyUse? Get(int id)
    {
        if (!stackable.ContainsKey(id)) return null;
        var pending = uses.GetOrAdd(id, key => new(() => ParseUse(read("stackable/" + stackable[key]), resolve)));
        try { return pending.Value; }
        catch { uses.TryRemove(new KeyValuePair<int, Lazy<AmplifyUse?>>(id, pending)); throw; }
    }

    internal static AmplifyUpgradeRules Parse(PvfTokenDocument document, Func<int, string> resolve, bool reinforce = false)
    {
        var tables = document.Sections.Where(row => row.Name == "table").SelectMany(row => row.Children)
            .Where(row => row.Name == "table type" && row.Values.Length != 0 && row.Values[0].Type == 6 &&
                resolve(row.Values[0].Value) == (reinforce ? "normal" : "amplify")).ToArray();
        Require(tables.Length == 1);
        var rows = AmplificationFiles.UpgradeRows(tables[0]);
        Require(rows.Length >= 31 && rows.All(row => row.FailureWeight is >= 0 and <= 100000 && row.PenaltyType is >= 1 and <= 3 &&
            row.PenaltyValue is >= 0 and <= 255 && row.MaterialId > 0 && row.MaterialCount > 0));
        var costs = AmplificationFiles.Numbers(One(document.Sections, "cost"));
        var rarity = AmplificationFiles.Numbers(One(document.Sections, "cost weights by rarity"));
        var types = AmplificationFiles.Numbers(One(document.Sections, "type"));
        Require(costs.Length > 1 && rarity.Length == 7 && types.Length == 13 && costs.Concat(rarity).Concat(types).All(value => float.IsFinite(value) && value >= 0));
        return new(rows.Select(row => new AmplifyUpgradeStep(row.FailureWeight, (byte)row.PenaltyType, row.PenaltyValue,
            row.MaterialId, row.MaterialCount)).ToImmutableArray(), costs, rarity, types,
            Named(document.Sections, "destroy level by rarity", resolve).ToImmutableDictionary(row => row.Key, row => checked((int)row.Value)),
            Named(document.Sections, "max upgrade level by rarity", resolve).ToImmutableDictionary(row => row.Key, row => checked((int)row.Value)),
            ParseBonus(document, resolve))
        {
            NoticeLevel = document.Sections.LastOrDefault(row => row.Name == "notice") is { } notice
                ? AmplificationFiles.Scalar(notice) : null
        };
    }

    internal static ImmutableArray<LuckyAmplifyStep> ParseLucky(PvfTokenDocument document, Func<int, string> resolve, bool reinforce = false)
    {
        var sections = document.Sections.Where(row => row.Name == "lucky enchant deed" &&
            AmplificationFiles.Scalar(row with { Children = [] }) == (reinforce ? 0 : 1)).ToArray();
        Require(sections.Length == 1);
        var table = One(sections[0].Children, "enchant type");
        Require(table.Values.Length > 0 && table.Values[0].Type == 6 && resolve(table.Values[0].Value) == (reinforce ? "strengthening" : "amplify"));
        var rows = AmplificationFiles.LuckyRows(table);
        Require(rows.Length == 15 && rows.Select((row, at) => row.Level == at && row.DownWeight >= 0 && row.SameWeight >= 0 && row.UpWeight >= 0 &&
            (long)row.DownWeight + row.SameWeight + row.UpWeight == 100).All(value => value));
        return rows.Select(row => new LuckyAmplifyStep(row.Level, row.DownWeight, row.SameWeight, row.UpWeight)).ToImmutableArray();
    }

    internal static UpgradeDestroyBonus ParseBonus(PvfTokenDocument document, Func<int, string> resolve)
    {
        var fields = One(document.Sections, "disjoint").Children;
        int id = AmplificationFiles.Scalar(One(fields, "disjoint bonus item")), grade = AmplificationFiles.Scalar(One(fields, "equip level const")),
            level = AmplificationFiles.Scalar(One(fields, "upgrade const for bonus item count"));
        var weights = Named(fields, "upgrade failed bonus weight by rarity", resolve);
        var corrections = Named(fields, "correction grade by rarity", resolve);
        Require(id > 0 && grade >= 0 && level is >= 0 and <= 255 && weights.Values.All(value => value >= 0) &&
            corrections.Values.All(value => value >= 0 && value <= 255 && value == MathF.Truncate(value)));
        return new(id, grade, level, weights, corrections.ToImmutableDictionary(row => row.Key, row => (int)row.Value));
    }

    internal static AmplifyUse? ParseUse(PvfTokenDocument document, Func<int, string> resolve)
    {
        var raw = StackableActionDeclaration.Read(document, resolve);
        if (raw is null) return null;
        var parsed = StackableActionCatalog.Parse(raw);
        var values = parsed.Arguments?.Parameters;
        string name = raw.Label;
        AmplifyUse? result = name switch
        {
            "[portable upgrade]" or "[new portable upgrade]" => new(AmplifyUseKind.Portable) { Reinforce = true },
            "[protect equipment]" or "[new protect equipment]" => new(AmplifyUseKind.Protection) { Reinforce = true },
            "[portable amplify]" => new(AmplifyUseKind.Portable),
            "[new portable amplify]" => new(AmplifyUseKind.Portable),
            "[lucky enchant deed]" when values is [1] => new(AmplifyUseKind.Lucky, MaximumUpgrade: 14),
            "[lucky enchant deed]" when values is [0] => new(AmplifyUseKind.Lucky, MaximumUpgrade: 14) { Reinforce = true },
            "[amplify protect equipment]" => new(AmplifyUseKind.Protection),
            "[new amplify protect equipment]" => new(AmplifyUseKind.Protection),
            _ => null
        };
        if (result is null) return null;
        Require(raw.HasIntegerArguments && parsed.Status == StackableActionParseStatus.Parsed && values is not null);
        if (result.Kind == AmplifyUseKind.Portable)
        {
            bool boosted = name is "[new portable amplify]" or "[new portable upgrade]";
            Require(values!.Count == (boosted ? 3 : 2) && values[0] >= 0 && values[1] >= values[0] &&
                (!boosted || values[2] >= 0));
            result = result with { MinimumUpgrade = values[0], MaximumUpgrade = values[1],
                SuccessBonusWeight = boosted ? values[2] : 0 };
        }
        else if (result.Kind == AmplifyUseKind.Protection)
        {
            Require(name is "[amplify protect equipment]" or "[protect equipment]" ? values!.Count == 0 : values is [>= 0 and <= 30]);
            result = result with { RetainLevel = values!.Count == 1 ? values[0] : 0 };
        }
        if (!AllowsGeneralServer(document, resolve)) return result with { PendingRule = "action usable server type" };
        foreach (string unsupported in new[] { "total usable count", "total weekly usable count", "total accumulated usable count",
            "usable datetime", "usable first growtype", "usable server type", "usable game server type", "use count limit all server item" })
            if (document.Sections.Any(row => row.Name == unsupported)) return result with { PendingRule = unsupported };
        var limits = document.Sections.Where(row => row.Name == "check usable itemlevel").ToArray();
        Require(limits.Length <= 1);
        if (limits.Length == 1)
        {
            var limit = AmplificationFiles.Levels(limits[0]);
            Require(limit.Length == 1 && limit[0].Level >= 0 && limit[0].Weight >= limit[0].Level);
            result = result with { MinimumItemLevel = limit[0].Level, MaximumItemLevel = limit[0].Weight };
        }
        var random = document.Sections.Where(row => row.Name == "enchant random").ToArray();
        Require(random.Length <= 1);
        if (random.Length == 1)
        {
            Require(random[0].Values.IsEmpty && random[0].Children.All(row => row.Name is "er_grade" or "er_slot" or "er_seal"));
            var rarities = One(random[0].Children, "er_grade").Values;
            int slot = AmplificationFiles.Scalar(One(random[0].Children, "er_slot")), seal = AmplificationFiles.Scalar(One(random[0].Children, "er_seal"));
            Require(rarities.All(token => token.Type == 0 && token.Value is >= 0 and <= 6) && slot is >= 0 and <= 3 && seal is >= 0 and <= 2);
            result = result with { SlotClass = slot, SealedOnly = seal == 1, Rarities = rarities.Select(token => token.Value).ToImmutableHashSet() };
        }
        var places = document.Sections.Where(row => row.Name == "action usable place").ToArray();
        Require(places.Length <= 1);
        if (places.Length == 1)
        {
            Require(places[0].Children.IsEmpty && places[0].Values.All(token => token.Type == 6));
            var labels = places[0].Values.Select(token => resolve(token.Value)).ToHashSet();
            Require(labels.All(label => label is "[village]" or "[seria room]" or "[dont use assult]"));
            result = result with { Village = labels.Contains("[village]"), SeriaRoom = labels.Contains("[seria room]") };
        }
        return result;
    }

    static bool AllowsGeneralServer(PvfTokenDocument document, Func<int, string> resolve)
    {
        const string field = "action usable server type";
        bool Nested(IEnumerable<PvfTokenSection> sections) => sections.Any(section =>
            section.Children.Any(child => child.Name == field) || Nested(section.Children));
        var declarations = document.Sections.Where(section => section.Name == field).ToArray();
        if (Nested(document.Sections)) return false;
        if (declarations.Length == 0) return true;
        // 本服按普通服资格消费；活动专服、未知或残缺声明仍保持不可用。
        if (!document.Diagnostics.IsEmpty || declarations is not
            [{ IsBlock:true, Children.Length:0, Values.Length:>0 } section] || section.Values.Any(token => token.Type != 6)) return false;
        var servers = section.Values.Select(token => resolve(token.Value)).ToArray();
        return servers.Contains("[general server]") && servers.Length == servers.Distinct().Count() &&
            servers.All(server => server is "[general server]" or "[event server]");
    }

    static ImmutableDictionary<int, float> Named(IEnumerable<PvfTokenSection> fields, string name, Func<int, string> resolve)
    {
        string[] rarities = ["common", "uncommon", "rare", "unique", "epic", "chronicle", "legendary"];
        var rows = AmplificationFiles.NamedNumbers(One(fields, name), resolve);
        Require(rows.All(row => rarities.Contains(row.Label) && float.IsFinite(row.Value)));
        return rows.ToImmutableDictionary(row => Array.IndexOf(rarities, row.Label), row => row.Value);
    }
    static PvfTokenSection One(IEnumerable<PvfTokenSection> fields, string name)
    {
        var rows = fields.Where(row => row.Name == name).ToArray();
        return rows.Length == 1 ? rows[0] : throw new InvalidDataException("增幅字段缺失或重复: " + name);
    }
    static void Require(bool condition)
    {
        if (!condition) throw new InvalidDataException("增幅资源字段、范围或重复声明无效");
    }
}
