using DfoServer.GameWorld.Adventure;

namespace DfoServer.Game.Adventure;

// 国服95冒险团经验模型：账号累计40级以上角色获得的经验。
// 官方规则：同一账号同一大区内，只有等级达到40级的角色才为冒险团提供经验。
// 角色有效经验位置不超过95级入口线时全额计入；超出部分按资源 [adventure exp rate] 折算。
// 经验胶囊与升级券/状态表/跳跃表不计入；按用户决定，GM升级与改经验同样排除。
//
// 角色里程行保存两个位置：
//   position_experience：角色当前有效里程，40级前的自然经验也照常推进，但不计入账号。
//   baseline_experience：已结算基准。40级前只吸收非自然来源（胶囊/道具/GM改写），
//     使 position−baseline 恰好是待追溯的自然经验；自然升级到40级时整段一次计入，
//     随后基准与里程同步；直升类跨越40级直接改写基准，历史经验作废。
// 排除经验（账号级）记录40级及以上的非自然来源与对账差额，不参与账号总量推进。
internal enum AdventureCreditSource
{
    // 副本击杀、通关结算、任务完成、训练检查点：只有40级及以上的角色计入，并驱动勇者结晶与成长胶囊。
    Earned,
    // 经验胶囊、成长胶囊等经验道具：只推进里程，不计入冒险团。
    ExcludedGrant,
    // 升级券、状态表、跳跃表及GM等级/经验改写：按角色有效位置直接改写里程，不计入冒险团。
    ExcludedState
}

internal sealed record AdventureCreditPlan(AdventureState Before, AdventureState After,
    ulong PositionBefore, ulong PositionAfter, ulong BaselineBefore, ulong BaselineAfter,
    decimal Credit, ulong RawOverflow, decimal Reconciliation)
{
    public DateTimeOffset? OccurredAt { get; init; }
}

internal static class AdventureCredit
{
    // 国服规则：角色达到40级后获得的经验才开始计入冒险团。
    public const int CountingLevel = 40;

    // F(x)：入口线以内100%，超出部分按经验倍率折算。倍率来自资源（float转decimal），全程只用decimal。
    public static decimal Value(ulong position, ulong cap, decimal rate) =>
        position <= cap ? position : cap + (position - cap) * rate;

    // 超出入口线的原始经验，供勇者结晶与成长胶囊计量；与折算后的冒险团经验分开累计。
    public static ulong Overflow(ulong position, ulong cap) => position <= cap ? 0UL : position - cap;

    // 账号状态保存精确总量：各等级边界之和 + 当前级经验 + 小数余量。
    public static decimal ExactTotal(AdventureState state, AdventureCatalog catalog)
    {
        decimal total = state.Experience + state.Remainder;
        for (int level = 2; level <= state.Level; level++) total += catalog.Experience[level];
        return total;
    }

    // 把精确总量映射回等级/当前级经验/余数；等级边界不足一级的部分保留在余数里。
    public static AdventureState FromExactTotal(decimal total, AdventureState template, AdventureCatalog catalog)
    {
        if (total < 0) throw new InvalidDataException("冒险团精确经验总量为负");
        int level = 1;
        decimal remaining = total;
        while (level < catalog.MaximumLevel && remaining >= catalog.Experience[level + 1])
        {
            remaining -= catalog.Experience[level + 1];
            level++;
        }
        ulong experience;
        try { experience = (ulong)decimal.Floor(remaining); }
        catch (OverflowException) { throw new InvalidDataException("冒险团精确经验总量超出范围"); }
        var state = template with { Level = level, Experience = experience, Remainder = remaining - experience };
        state.Validate(catalog);
        return state;
    }

    // storedPosition/storedBaseline 是账本已落库的里程与基准，effectivePosition 是角色当前等级与经验字段的有效位置
    // （取等级入口与经验较大者）。两者不一致且任一侧未到入口线时，说明有绕过本仓储的写入：
    // 差额按排除调整补回账本，再处理本次事件。两侧都在入口线以上属于满级经验条钉住入口线的正常形态。
    public static AdventureCreditPlan Apply(AdventureState before, AdventureCreditSource source, ulong storedPosition,
        ulong storedBaseline, ulong effectivePosition, ulong amount, ulong cap, AdventureCatalog catalog,
        int levelBefore, int levelAfter)
    {
        decimal rate = catalog.ExperienceRate;
        decimal reconciliation = 0;
        ulong position = storedPosition;
        ulong baseline = Math.Min(storedBaseline, storedPosition);
        if (storedPosition != effectivePosition && (storedPosition < cap || effectivePosition < cap))
        {
            reconciliation = Value(effectivePosition, cap, rate) - Value(storedPosition, cap, rate);
            position = effectivePosition;
            baseline = Math.Min(baseline, position);
        }
        ulong baselineBefore = baseline;

        bool countedBefore = levelBefore >= CountingLevel;
        bool countedAfter = levelAfter >= CountingLevel;
        ulong after;
        ulong baselineAfter;
        decimal credit = 0;
        ulong rawOverflow = 0;
        switch (source)
        {
            case AdventureCreditSource.Earned:
                after = checked(position + amount);
                if (!countedAfter)
                {
                    // 40级前：只推进里程，基准不动，自然经验留到升到40时全额追溯。
                    baselineAfter = baseline;
                    break;
                }
                // 自然升级跨过40级时从基准起算，把40级前的自然经验连同本次一起计入；
                // 已在40级以上则只计本次增量。
                ulong earnedFrom = countedBefore ? position : baseline;
                credit = Value(after, cap, rate) - Value(earnedFrom, cap, rate);
                rawOverflow = Overflow(after, cap) - Overflow(earnedFrom, cap);
                baselineAfter = after;
                break;
            case AdventureCreditSource.ExcludedGrant:
                after = checked(position + amount);
                if (!countedAfter)
                {
                    // 40级前（含本笔跨过40级）：非自然来源只抬高基准，不触发历史追溯。
                    baselineAfter = checked(baseline + amount);
                    break;
                }
                credit = Value(after, cap, rate) - Value(position, cap, rate);
                baselineAfter = after;
                break;
            default:
                // ExcludedState：amount 是改写后的绝对有效位置。
                after = amount;
                if (!countedAfter)
                {
                    // 40级以下的等级/经验改写：待追溯的自然经验随差额平移；从40级以上降下来时按新位置重新结算。
                    baselineAfter = countedBefore ? after : ShiftBaseline(baseline, position, after);
                    break;
                }
                // 直升类跨越40级：历史经验作废，基准直接落到改写后的位置；40级以上改写只记录排除额。
                if (countedBefore) credit = Value(after, cap, rate) - Value(position, cap, rate);
                baselineAfter = after;
                break;
        }

        if (baselineAfter > after) baselineAfter = after;
        var adjusted = reconciliation == 0 ? before : before with { ExcludedExperience = before.ExcludedExperience + reconciliation };
        var updated = source == AdventureCreditSource.Earned
            ? AdventureProgression.Advance(adjusted, credit, rawOverflow, catalog)
            : adjusted with { ExcludedExperience = adjusted.ExcludedExperience + credit };
        return new(before, updated, position, after, baselineBefore, baselineAfter, credit, rawOverflow, reconciliation);
    }

    // 40级以下改写位置时保持「待追溯自然经验」不变：基准随差额平移，并收敛到 [0, 新位置]。
    static ulong ShiftBaseline(ulong baseline, ulong position, ulong after)
    {
        if (after >= position) return Math.Min(after, checked(baseline + (after - position)));
        ulong drop = position - after;
        return baseline > drop ? baseline - drop : 0;
    }
}
