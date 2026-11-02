using DfoServer.GameWorld.Dungeons;
using DfoServer.Game.Characters;

namespace DfoServer.Game.Dungeons;

// 奖励候选携带明确规则与资源身份；合法死亡及当前owner仍由提交路径核验。
internal sealed record DungeonExperienceReward(string RuleId, string ResourceSource, int MonsterId, int MonsterLevel, uint Amount)
{
    // 本只怪的基础经验；冻结后 Amount 是含加成的到账总额，展示明细仍要按基础口径。
    public uint BaseAmount { get; init; }
    public uint GrowthContractExperience { get; init; }
    public uint PotionExperience { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ExperienceBonuses? Bonuses { get; init; }
}

// 存储返回实际提交后的角色；Duplicate表示账本精确重放，不是新的击杀或新奖励。
// GainedExperience是角色经验到账，ProficiencyGained是同一笔经验记入熟练度的额度；两者之和是本次有效经验。
internal sealed record DungeonGrowthCommit(Character Character, bool Duplicate, uint GainedExperience = 0, uint ProficiencyGained = 0)
{
    public ExperienceBonuses? CommittedBonuses { get; init; }
    public DungeonDirectPickupCommit DirectPickup { get; init; } = DungeonDirectPickupCommit.Empty;
    public System.Collections.Immutable.ImmutableArray<DfoServer.Game.Inventory.Services.EquipmentGrowthChange> EquipmentChanges { get; init; } = [];
}

internal static class DungeonKillExperiencePolicy
{
    internal const string AltarQuestDeathRule = "altar-quest-death-no-kill-reward-v1";

    // 祭坛击杀只推进任务及其材料；普通经验与普通物品归bloodclearreward专用结算。
    public static DungeonExperienceReward PlanAltarQuestDeath(SinglePlayerDungeonRun run, PreparedDungeonDeath prepared)
    {
        if (!run.Entry.IsAltar || !run.IsPendingDeath(prepared))
            throw new InvalidOperationException("祭坛任务死亡只能规划当前祭坛候选");
        return new(AltarQuestDeathRule, DungeonAltarRewardCatalog.ResourcePath, prepared.Monster.MonsterId,
            prepared.Monster.CandidateLevel, 0);
    }

    public static DungeonExperienceReward Plan(SinglePlayerDungeonRun run, PreparedDungeonDeath prepared,
        DungeonKillExperienceCatalog catalog, byte characterLevel)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(catalog);
        if (!run.IsPendingDeath(prepared))
            throw new InvalidOperationException("击杀经验只能规划当前待提交死亡");
        var monster = prepared.Monster;
        uint amount = run.Entry.DisableMonsterAndClearExperience ? 0 : Calculate(catalog.GetExperience(monster.CandidateLevel), run.Entry.ExperienceWeight,
            catalog.GetDifficultyRate(run.Entry.Difficulty),
            // 参考S4A21以APC自身等级和对应品级计算；怪物named目录不套到同号APC。
            catalog.GetMonsterRate(monster.RewardType, monster.AiCharacter is null && run.Entry.NamedMonsters.Contains(monster.MonsterId)),
            characterLevel, monster.CandidateLevel);
        return new(DungeonKillExperienceCatalog.RuleId, DungeonKillExperienceCatalog.ResourcePath, monster.MonsterId,
            monster.CandidateLevel, amount);
    }

    internal static uint Calculate(uint basic, float weight, float difficulty, float monsterRate, int characterLevel, int monsterLevel)
    {
        if (characterLevel <= 0 || monsterLevel <= 0 || !float.IsFinite(weight) || weight < 0 ||
            !float.IsFinite(difficulty) || difficulty < 0 || !float.IsFinite(monsterRate) || monsterRate < 0)
            throw new InvalidDataException("击杀经验计算前提无效");
        // external的float32三阶段：权重后截整数、难度/品级后截整数、最后应用等级差。
        int weighted = TruncateInt(basic * weight);
        int beforePenalty = TruncateInt(weighted * difficulty * monsterRate);
        float penalty = ((long)monsterLevel - characterLevel) switch
        {
            <= -7 => .05f, -6 => .20f, -5 => .50f, -4 => .75f,
            <= 0 => 1f, <= 3 => 1.12f, <= 5 => 1f,
            6 => .75f, 7 => .70f, 8 => .60f, 9 => .50f, _ => .05f
        };
        float gain = beforePenalty * penalty;
        if (!float.IsFinite(gain) || gain < 0 || (double)gain > uint.MaxValue)
            throw new InvalidDataException("击杀经验超出u32范围");
        return (uint)gain;
    }

    private static int TruncateInt(float value) => float.IsFinite(value) && value >= 0 && (double)value <= int.MaxValue
        ? (int)value : throw new InvalidDataException("击杀经验中间值超出i32范围");
}
