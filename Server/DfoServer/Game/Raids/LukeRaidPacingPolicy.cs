using System.Collections.Immutable;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

// 全部人数共用的本服节奏约定；不根据人数、分队数或掉线情况切换脚本。
internal sealed record LukeRaidPacingPolicy(string Source, int StorageRecoverySeconds, int StorageAttackSeconds,
    int ControlRecoveryScale)
{
    internal static LukeRaidPacingPolicy Shared95 { get; } = new("95jp/luke/shared-95-v1", 300, 600, 5);
    internal bool Valid => !string.IsNullOrWhiteSpace(Source) && StorageRecoverySeconds is >= 300 and <= 3600 &&
        StorageAttackSeconds is >= 300 and <= 3600 && ControlRecoveryScale is >= 1 and <= 10;

    internal RaidScript Adapt(RaidScript original)
    {
        if (!Valid || !original.Issues.IsEmpty || original.Phases.Length != 2 ||
            original.Phases[0].Rules.Length != 13 || original.Phases[1].Rules.Length != 27 ||
            !new[] { 5061, 5062, 5063, 5066 }.All(id => original.Phases[1].Dungeons.Count(row => row.Id == id) == 1) ||
            !new[] { ("[kill all character]", 102), ("[kick out character]", 103),
                ("[energy control effect]", 138), ("[energy save effect]", 139) }
                .All(row => original.Symbols.GetValueOrDefault(row.Item1) == row.Item2))
            throw new InvalidDataException("卢克统一节奏策略与当前95级两阶段资源不匹配");

        int token = original.Phases.SelectMany(phase => phase.Rules).SelectMany(rule => rule.Trigger.Concat(rule.Behavior))
            .Max(clause => clause.TokenIndex) + 1;
        bool Trigger(RaidRule rule, string tag, int? dungeon = null, string? symbol = null) =>
            rule.Trigger.Any(clause => clause.Tag == tag && clause.DungeonId == dungeon && clause.Symbol == symbol);

        var birth = original.Phases[0].Dungeons.SingleOrDefault(row => row.Id == 5054);
        if (birth is not { HpRaw: 3 } || !original.Symbols.ContainsKey(birth.HpSymbol))
            throw new InvalidDataException("卢克诞生圣所缺少三次通关HP声明");
        int sanctumGates = 0;
        var firstPhaseRules = original.Phases[0].Rules.Select(rule =>
        {
            if (!Trigger(rule, "[on enter dungeon]", 5054)) return rule;
            if (rule.Behavior.Count(action => action is
                { Tag: "[set dungeon state]", DungeonId: 5055 or 5056, State: RaidDungeonState.Open }) != 2)
                throw new InvalidDataException("卢克一阶段圣所开门规则形状变化");
            sanctumGates++;
            // 首个已提交Run使HP从3降至2才开后两图；进入、放弃或后续清图不重复触发。
            var trigger = rule.Trigger.Select(clause => clause is { Tag: "[on enter dungeon]", DungeonId: 5054 }
                ? new RaidClause("[on change symbol]", token++, null, birth.HpSymbol, null, null, [])
                : clause).ToImmutableArray().Add(
                    new RaidClause("[if]", token++, null, birth.HpSymbol, null, null, [2]));
            return rule with { Trigger = trigger };
        }).ToImmutableArray();
        if (sanctumGates != 1)
            throw new InvalidDataException("卢克一阶段缺少唯一诞生圣所开门规则");

        int temples = 0, storageEntries = 0, lightClears = 0, giveups = 0, fullEnergy = 0, storageTimers = 0, controlTimers = 0;
        var rules = ImmutableArray.CreateBuilder<RaidRule>();
        foreach (var rule in original.Phases[1].Rules)
        {
            // 离开王座去压能量时，保留当前能源进度和另一分队的能源Run，不重置或踢走队友。
            if (Trigger(rule, "[on giveup dungeon]", 5061) || Trigger(rule, "[on giveup dungeon]", 5062))
            { giveups++; continue; }
            var actions = rule.Behavior.Select(action =>
            {
                if (action.Tag != "[set timer]" || action.RawIntegers.Length != 3) return action;
                if (action.Timer is { DungeonId: 5066, Kind: 1 or 2 } timer)
                {
                    storageTimers++;
                    return action with { RawIntegers = action.RawIntegers.SetItem(2,
                        timer.Kind == 1 ? StorageAttackSeconds : StorageRecoverySeconds) };
                }
                if (action.Timer == new RaidTimer(2, 5063))
                {
                    controlTimers++;
                    return action with { RawIntegers = action.RawIntegers.SetItem(2, checked(action.RawIntegers[2] * ControlRecoveryScale)) };
                }
                return action;
            }).ToImmutableArray();

            if (Trigger(rule, "[on change symbol]", symbol: "[temple clear]") &&
                rule.Trigger.Any(clause => clause is { Tag: "[if]", Symbol: "[temple clear]", RawIntegers: [2] }))
            {
                temples++;
                // 双圣殿只开放光王座；储藏室沿实际入王座事件开放，不能在候战时提前压制。
            }
            if (Trigger(rule, "[on change dungeon state]", 5061) &&
                rule.Trigger.Any(clause => clause is { Tag: "[check dungeon state]", DungeonId: 5061, State: RaidDungeonState.Clear }))
            {
                lightClears++;
                if (actions.Count(clause => clause is { Tag: "[set dungeon state]", DungeonId: 5066, State: RaidDungeonState.Hide }) != 1 ||
                    actions.Count(clause => clause is { Tag: "[set]", Symbol: "[energy save effect]", RawIntegers: [0] }) != 1)
                    throw new InvalidDataException("卢克统一节奏储藏室重开规则形状变化");
                // 光清后能源原样延续，既不免费清零，也不提前结束已经获得的压制窗口。
                // 不投递原KILL，防止误伤仍在能源中的另一分队。
                actions = actions.Where(action => action is not { Tag: "[set dungeon state]", DungeonId: 5066 } &&
                    action is not { Tag: "[set]", Symbol: "[energy save effect]" or "[kill all character]" }).ToImmutableArray();
            }
            if (rule.Trigger.Any(clause => clause.Tag == "[check timer end]" && clause.Timer == new RaidTimer(1, 5066)))
            {
                fullEnergy++;
                // 满能源由原Battle标记当时王座Run，安全退场不伪造死亡或扣币；effect=2仍保留。
                if (actions.Count(clause => clause is { Tag: "[set dungeon state]", DungeonId: 5066, State: RaidDungeonState.Hide }) != 1)
                    throw new InvalidDataException("卢克统一节奏满能量规则形状变化");
                actions = actions.Where(action => action is not { Tag: "[set]", Symbol: "[kill all character]" })
                    .Select(action => action is { Tag: "[set dungeon state]", DungeonId: 5066 }
                    ? action with { State = RaidDungeonState.Open } : action).ToImmutableArray();
            }
            var trigger = rule.Trigger;
            if (trigger.Any(clause => clause is { Tag: "[on enter map]", MapId: 55845 }))
            {
                storageEntries++;
                trigger = trigger.Select(clause => clause is { Tag: "[on enter map]", MapId: 55845 }
                    ? new RaidClause("[on enter dungeon]", token++, 5061, null, null, null, [5061]) : clause).ToImmutableArray();
            }
            rules.Add(rule with { Trigger = trigger, Behavior = actions });
        }
        if ((temples, storageEntries, lightClears, giveups, fullEnergy, storageTimers, controlTimers) != (1, 1, 1, 2, 1, 2, 3))
            throw new InvalidDataException("卢克统一节奏能源规则数量或时钟声明变化");
        // 原资源与token保留只读；本服差量不改地图、Boss、奖励或实例/SQL所有权。
        return original with { Phases = original.Phases
            .SetItem(0, original.Phases[0] with { Rules = firstPhaseRules })
            .SetItem(1, original.Phases[1] with { Rules = rules.ToImmutable() }) };
    }
}
