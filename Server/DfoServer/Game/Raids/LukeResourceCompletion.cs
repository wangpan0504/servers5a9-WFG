using System.Collections.Immutable;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

// 数量、选卡身份、阶段资格次数及601展示均由调用者明示；原STATE REWARD未证列不提供这些默认值。
internal sealed record LukeResourceRewardPhase(byte Kind, byte Card, int BoxCount, int WeeklyEligibilityLimit,
    RaidResultDisplay Result)
{
    public LukeRewardResultSpec? LukeResult { get; init; }
}
internal sealed record LukeResourceCompletionProfile(string Source, string ResourceVersion, RaidWeeklyCycle Cycle,
    ImmutableArray<LukeResourceRewardPhase> Phases);

internal static class LukeResourceCompletion
{
    // 在Runtime完成真实资源装配后解析一次；没有配置时不改变任何原策略或默认准入。
    internal static LukeRaidStartPolicy? Resolve(LukeRaidStartPolicy? policy, RaidDefinition? definition, ItemCatalog items)
    {
        if (policy?.ResourceCompletion is not { } profile) return policy;
        if (policy.Completion is not null) throw new InvalidDataException("卢克手工奖励与资源奖励配置不能同时提供");
        if (definition?.Type != (byte)RaidKind.Luke || definition.Resources?.Script is not { } script)
            throw new InvalidDataException("卢克资源奖励缺少实际资源定义");
        return policy with { Completion = Create(profile, script, id => items.TryGet(id, out var item) ? item : null) };
    }

    internal static LukeRaidCompletionPolicy Create(LukeResourceCompletionProfile profile, RaidScript script,
        Func<int, ItemTemplate?> findItem)
    {
        if (string.IsNullOrWhiteSpace(profile.Source) || string.IsNullOrWhiteSpace(profile.ResourceVersion) ||
            profile.Source.Length > 256 || profile.ResourceVersion.Length > 128 || profile.Cycle is not { Valid: true } ||
            profile.Phases.IsDefault || profile.Phases.Length != 2 || script.Phases.IsDefault || script.Phases.Length != 2 ||
            !script.Issues.IsEmpty || string.IsNullOrWhiteSpace(script.Path))
            throw new InvalidDataException("卢克资源奖励配置、资源版本或阶段声明无效");
        int weekly = Integer(script.Headers, "[weekly clear count]", 1);
        string source = $"{profile.Source};resource={profile.ResourceVersion};path={script.Path}";
        var phases = ImmutableArray.CreateBuilder<LukePhaseCompletionPolicy>(2);
        for (int index = 0; index < 2; index++)
        {
            var configured = profile.Phases[index];
            if (configured is null || configured.Kind is not (0 or 1) || configured.BoxCount is < 1 or > 10000 ||
                configured.WeeklyEligibilityLimit < 1 || configured.Result is null || configured.Result.Phase != index ||
                configured.Result.Result is not (0 or 2))
                throw new InvalidDataException("卢克资源奖励必须明示有界箱数、资格次数、选卡及对应阶段展示");
            var phase = script.Phases[index];
            int minimum = Integer(phase.Declarations, "[minimal dungeon clear count]", 0);
            var cards = phase.Rewards.Where(row => row.Kind == "party_card").ToArray();
            // 仅支持已核对的原列形状，不把100命名为概率，也不把-1或0解释为数量。
            if (cards is not [{ RawA: -1, RawB: 100, RawD: 0, ItemId: > 0 } card] ||
                findItem(card.ItemId) is not { Kind: ItemKind.Consumable } item || item.Id != card.ItemId ||
                item.StackableType.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is not ["[booster]", "0"])
                throw new InvalidDataException("卢克阶段party_card不唯一、列形状变化或原奖励箱不存在");
            var offer = new RaidRewardOffer(configured.Kind, configured.Card,
                [new InventoryReward(card.ItemId, configured.BoxCount)], []) { LukeResult = configured.LukeResult };
            if (offer.LukeResult is { } display && (!display.Matches(offer) ||
                display.EnchantUpgrade != InventoryRewardGrantService.Create(item).EnchantUpgrade))
                throw new InvalidDataException("卢克原箱结果展示与真实发放核心不一致或字段缺失");
            phases.Add(new(minimum, configured.WeeklyEligibilityLimit,
                new(source, [offer], configured.Result)));
        }
        var result = new LukeRaidCompletionPolicy(source, weekly, profile.Cycle, phases.MoveToImmutable(),
            LukeParticipationCutoff.TerminalCommittedFacts);
        if (!AntonRaidBattle.ValidLukeCompletion(result)) throw new InvalidDataException("卢克资源奖励策略无法执行");
        return result;
    }

    static int Integer(ImmutableArray<RaidField> fields, string tag, int minimum)
    {
        if (fields.IsDefault) throw new InvalidDataException("卢克资源奖励声明缺失");
        var values = fields.Where(field => field.Tag == tag).ToArray();
        if (values.Length != 1 || values[0].Values is not [{ Type: 0 } value] || value.Number < minimum)
            throw new InvalidDataException($"卢克资源奖励整数声明无效：{tag}");
        return value.Number;
    }
}
