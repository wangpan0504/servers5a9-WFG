using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 本服泰波尔斯地面掉落规则：剧情图(291100321)全怪无掉落，正式三模式(291100320/291100322/5349)普通怪与绿名怪无地面掉落，
// 仅领主掉落碳结晶体(10163135)与深渊派对邀请函(3330)。
internal sealed record TayberrsDropRules(
    int CarbonCrystalRate = 1000,
    int CarbonCrystalMinimumCount = 1,
    int CarbonCrystalMaximumCount = 3,
    int AbyssInvitationRate = 500,
    int AbyssInvitationMinimumCount = 1,
    int AbyssInvitationMaximumCount = 5)
{
    public void Validate()
    {
        if (CarbonCrystalRate is < 0 or > 1000 || AbyssInvitationRate is < 0 or > 1000 ||
            CarbonCrystalMinimumCount < 1 || CarbonCrystalMaximumCount < CarbonCrystalMinimumCount ||
            AbyssInvitationMinimumCount < 1 || AbyssInvitationMaximumCount < AbyssInvitationMinimumCount ||
            (long)CarbonCrystalMaximumCount + AbyssInvitationMaximumCount > 500)
            throw new InvalidDataException("泰波尔斯领主地面掉落配置超出千分比或地面对象预留范围");
    }
}

internal sealed class TayberrsDropPolicy
{
    public const string Rule = "tayberrs-boss-ground-drops/v1";
    public const string StoryRule = "tayberrs-story-no-drops/v1";
    public const int StoryDungeonId = 291100321;
    public const int PartyDungeonId = 291100320;
    public const int SingleDungeonId = 291100322;
    public const int GuideDungeonId = 5349;
    public const int CarbonCrystalId = 10163135;
    public const int AbyssInvitationId = 3330;
    public const int PartyBossMonsterId = 59101;
    public const int SingleBossMonsterId = 59110;

    readonly ImmutableArray<DungeonDropTrial> bossTrials;

    public TayberrsDropPolicy(TayberrsDropRules rules, Func<int, ItemTemplate> resolveItem)
    {
        ArgumentNullException.ThrowIfNull(resolveItem);
        rules.Validate();
        var carbonCrystal = RequireMaterial(resolveItem(CarbonCrystalId), CarbonCrystalId);
        var invitation = RequireMaterial(resolveItem(AbyssInvitationId), AbyssInvitationId);
        var trials = ImmutableArray.CreateBuilder<DungeonDropTrial>(2);
        if (rules.CarbonCrystalRate > 0)
            trials.Add(new(rules.CarbonCrystalRate,
                new([new DungeonWeightedDropItem(carbonCrystal, 1)]),
                rules.CarbonCrystalMinimumCount, rules.CarbonCrystalMaximumCount,
                ProbabilityDenominator: 1000, IndividualObjects: true, Category: DungeonDropCategory.Materials));
        if (rules.AbyssInvitationRate > 0)
            trials.Add(new(rules.AbyssInvitationRate,
                new([new DungeonWeightedDropItem(invitation, 1)]),
                rules.AbyssInvitationMinimumCount, rules.AbyssInvitationMaximumCount,
                ProbabilityDenominator: 1000, IndividualObjects: true, Category: DungeonDropCategory.Materials));
        bossTrials = trials.ToImmutable();
    }

    static ItemTemplate RequireMaterial(ItemTemplate template, int expectedId)
    {
        if (template is null || template.Id != expectedId || template.Kind != ItemKind.Material ||
            template.StackLimit < 1 || !template.Expiration.SupportsCreation)
            throw new InvalidDataException("泰波尔斯领主地面掉落模板必须是可创建的Material: " + expectedId);
        return template;
    }

    public static bool IsStoryDungeon(uint dungeonId) => dungeonId == StoryDungeonId;

    public static bool IsStoryDungeon(DungeonEntryPlan entry) =>
        entry.IsTayberrsStory || entry.DungeonId == StoryDungeonId;

    public static bool IsFormalDungeon(DungeonEntryPlan entry) =>
        entry.IsTayberrs || entry.DungeonId is PartyDungeonId or SingleDungeonId or GuideDungeonId;

    public static bool Applies(DungeonEntryPlan entry) =>
        IsStoryDungeon(entry) || IsFormalDungeon(entry);

    public static bool IsRewardBoss(DungeonEntryPlan entry, DungeonPlannedMonster monster) =>
        !entry.DisableItemDrops && !entry.IsPractice && !entry.IsTutorial && !entry.IsTrainingRoom &&
        !entry.IsClassExperience && IsFormalDungeon(entry) &&
        monster.SourceMap == entry.BossMap && monster.IsBoss && monster.CanReportDeath &&
        !monster.IsFriendly && !monster.IsSpecialChild && !monster.IsScriptedRetirement &&
        !monster.IsScriptedBoss && !monster.IsDummyMapBoss && monster.AiCharacter is null &&
        monster.AltarActor is null && monster.HellWave is null && monster.SpawnType <= 3 &&
        monster.ObjectKey != entry.CharacterId && monster.MonsterId is PartyBossMonsterId or SingleBossMonsterId;

    public Func<DungeonPlannedMonster, DungeonDropGenerationPlan> Prepare(DungeonEntryPlan entry) =>
        monster => Prepare(entry, monster);

    public DungeonDropGenerationPlan Prepare(DungeonEntryPlan entry, DungeonPlannedMonster monster)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(monster);
        if (IsStoryDungeon(entry))
            return new(StoryRule, []);
        if (!IsRewardBoss(entry, monster))
            return new(Rule, []);
        return PrepareBossPreview();
    }

    public DungeonDropGenerationPlan PrepareBossPreview() => new(Rule, bossTrials);
}

internal sealed record PendingSpecialDungeonClearReward(
    PreparedDungeonDeath Death,
    SpecialDungeonClearReward Reward,
    ImmutableArray<SpecialDungeonClearRewardGrant> Grants);

internal sealed record SpecialDungeonClearRewardPlan(
    Character Expected,
    PreparedDungeonDeath Death,
    SpecialDungeonClearReward Reward,
    ImmutableArray<SpecialDungeonClearRewardGrant> Grants,
    InventoryPlan Plan)
{
    public static SpecialDungeonClearRewardPlan? Create(
        Character expected,
        PreparedDungeonDeath death,
        SpecialDungeonClearReward reward,
        ImmutableArray<SpecialDungeonClearRewardGrant> grants,
        InventorySnapshot before,
        ItemCatalog items,
        DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(death);
        ArgumentNullException.ThrowIfNull(reward);
        ArgumentNullException.ThrowIfNull(items);
        if (grants.IsDefaultOrEmpty) return null;
        var batch = grants.Select(row => new InventoryReward(row.ItemId, row.Count)).ToArray();
        var granted = InventoryRewardGrantService.TryGrantDirectItems(expected with { Inventory = before }, batch,
            items, utcNow, allowOverflowMail: true, reason: "special_dungeon_clear_reward",
            overflowMailSender: reward.MailSender, overflowMailText: reward.MailText);
        if (!granted.Accepted || granted.Plan is not { } plan)
            throw new InvalidDataException("特殊地下城通关奖励无法规划入包或溢出邮件");
        return new(expected, death, reward, grants, plan);
    }
}
