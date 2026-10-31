using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Guilds;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 本服明示规则：严格普通推荐区间、个人八件、每名合格成员每个Boss独立10%。
internal sealed class SpringTreasureDropPolicy
{
    public const string Rule = "spring2020-personal-recommended-boss-10pct/v1";
    readonly SpringTreasureAvatarCatalog avatars;
    readonly ItemTemplate treasure;
    readonly Func<bool>? enabled;
    public int DropItemId => treasure.Id;

    public SpringTreasureDropPolicy(SpringTreasureAvatarCatalog avatars, ItemTemplate treasure, Func<bool>? enabled = null)
    {
        if (treasure.Id != 490705287 || treasure.Kind != ItemKind.Consumable || treasure.StackLimit < 1 || !treasure.Expiration.SupportsCreation)
            throw new InvalidDataException("春节宝藏产物必须是支持完整创建的原287");
        this.avatars = avatars;
        this.treasure = treasure;
        this.enabled = enabled;
    }

    static bool SupportedMode(DungeonEntryPlan entry) => entry.DungeonId != 530 && entry.IsOrdinary && entry.QuestId == 0 &&
        !entry.IsPractice && !entry.IsTrainingRoom && !entry.IsClassExperience && !entry.IsTutorial && !entry.IsQuestReplay &&
        entry.RaidEntry is null && !entry.BeastMode && !entry.IsOrdinaryLuke && !entry.IsOrdinaryPrey &&
        entry.Hope is null && entry.Dimension is null && entry.LostLand is null && entry.GriefLayer is null &&
        !entry.IsAltar && !entry.IsDespair && !entry.IsTournament && !entry.IsBossRush && !entry.IsTagSystem && !entry.IsTagEvildom && !entry.IsImperialArena &&
        !entry.IsAncientInfinite && entry.HellParty is null && entry.HellPartyMode == 0 && !entry.IsInfernoParty &&
        !entry.IsHellLegend && !entry.IsCosmo && entry.Marriage is null;

    // 独立玩法沿现有typed合同排除，不能只凭入口看似普通便进入活动经济。
    static bool SupportedDefinition(DungeonDefinition definition) => definition.Id is not (530 or SleepyHollowRewardCatalog.DungeonId) &&
        !definition.IsRaid && !definition.IsOrdinaryAnton && !definition.IsOrdinaryLuke && !definition.IsOrdinaryPrey &&
        !definition.IsTayberrs && !definition.IsNormalFiendwar && string.IsNullOrEmpty(definition.WarriorsDungeon) &&
        !definition.IsOperationHope && !definition.IsCrackOfDimension && !definition.RequiresCosmoEntryContract && !definition.IsInfernoDungeon &&
        !definition.IsDazzlementTower && !definition.IsDespairTower && !definition.RequiresGriefEntryContract &&
        !definition.Path.StartsWith("dungeon/towers/", StringComparison.OrdinalIgnoreCase) &&
        !definition.RawSections.Any(section => section.Tag is "evildom war dungeon" or "warriors dungeon");

    public static bool IsRecommended(DungeonDefinition definition, DungeonEntryPlan entry, int level) =>
        SupportedMode(entry) && SupportedDefinition(definition) && GuildDungeonMileageEligibility.IsEligible(definition, level);

    public static bool IsPotential(DungeonDefinition definition, DungeonEntryPlan entry) =>
        SupportedMode(entry) && SupportedDefinition(definition) &&
        GuildDungeonMileageEligibility.HasEligibleLevelAtOrAbove(definition, 85);

    public static bool MayDrop(DungeonEntryPlan entry, DungeonPlannedMonster monster) => entry.SpringTreasurePotential &&
        monster.IsBoss && monster.CanReportDeath && !monster.IsFriendly && !monster.IsScriptedBoss &&
        !monster.IsScriptedRetirement && !monster.IsSpecialChild;

    public static int ReservationCount(int originalMaximum, bool potential)
    {
        int count = checked(Math.Max(1, originalMaximum) + (potential ? 1 : 0));
        if (count > 500) throw new InvalidDataException("春节宝藏追加对象超出单怪500预留上限，不能截断原奖励");
        return count;
    }

    public bool HasOutfit(Character character, DateTimeOffset now)
    {
        if (character.Level < 85) return false;
        var uids = new HashSet<long>();
        for (short slot = 0; slot < 8; slot++)
        {
            var item = character.Inventory.Find(new(InventoryListType.Equipment, slot));
            if (item is not { Core.Kind: ItemKind.Avatar, Avatar: not null } || item.Uid <= 0 || !uids.Add(item.Uid) ||
                item.Core.Value != item.Uid || item.IsExpired(now.ToUnixTimeSeconds()) ||
                !avatars.Members.TryGetValue(item.Core.ItemId, out var template) || template.EquipmentSlot != slot ||
                !template.AllowsUse(character.Level, character.Job)) return false;
            try { item.ValidateDetails(allocating: false); }
            catch (InvalidDataException) { return false; }
        }
        return true;
    }

    public IReadOnlyList<DungeonGroundContent> Generate(Character character, DungeonEntryPlan entry, DungeonPlannedMonster monster,
        Func<int, int> draw, DateTimeOffset now, bool shopExpiration, bool ignoreUsablePeriod)
    {
        if (enabled?.Invoke() == false || !entry.SpringTreasureRecommended || !MayDrop(entry, monster) || !HasOutfit(character, now)) return [];
        int roll = draw(1000);
        if ((uint)roll >= 1000) throw new InvalidDataException("春节宝藏随机数超出固定10%分母");
        if (roll >= 100) return [];
        // 仅本活动产物沿已有商城期限设置；普通怪物掉落继续保留原期限语义。
        var core = InventoryRewardGrantService.Create(treasure, 1, now, shopExpiration: shopExpiration, ignoreUsablePeriod: ignoreUsablePeriod);
        return core.ExpireTime > 0 && core.ExpireTime <= now.ToUnixTimeSeconds() ? [] : [new(core)];
    }
}
