using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Dungeons;

internal sealed record DungeonIncountTicketPlan(Character Expected, InventoryItem Source, InventoryPlan Inventory,
    DungeonIncountRow? Row, DungeonIncountPeriod Period, int WeeklyResetDay, int Amount = 1, bool Reset = false)
{
    public ImmutableArray<AccountTicketQuota> AccountQuotas { get; init; } = [];
    public ImmutableArray<RewardTicketQuota> RewardQuotas { get; init; } = [];
    public ImmutableArray<DungeonIncountRow> ResetUsedTargets { get; init; } = [];
    public bool ResetAntonRewards { get; init; }
    public SingleDungeonGroup? ResetSingleGroup { get; init; }
    public int SingleWeek { get; init; }
}

internal readonly record struct AccountTicketQuota(int Dungeon, int Period, int ResetDay, int Amount);
internal readonly record struct RewardTicketQuota(int Kind, int Target, int Amount);

// 业务资格不足且事务已回滚，与提交结果不明分开处理。
internal sealed class DungeonTicketRejectedException(string reason) : InvalidOperationException(reason);

internal interface IDungeonIncountTicketStore
{
    DungeonIncountCommit CommitDungeonIncountTicket(AccountOwners.Lease owner, DungeonIncountTicketPlan plan, DateTimeOffset now);
}

internal sealed record DungeonIncountTicketResult(ConsumableUseOutcome Outcome, string Reason, ConsumableUseGuard Guard,
    InventoryResult? Inventory = null, ImmutableArray<DungeonEnterCount> Counts = default);

internal sealed class DungeonIncountTicketService(ItemCatalog items, DungeonIncountCatalog incounts, TownCatalog towns,
    TimeProvider? clock = null, ClearRewardLimitCatalog? rewardLimits = null)
{
    public const uint Action = 214;
    public const uint AddDaybreakAction = 213;
    public const uint ResetDaybreakAction = 221;
    public static bool Handles(uint action) => action is Action or AddDaybreakAction or ResetDaybreakAction or 59 or 104 or 155 or 160 or 222;
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public DungeonIncountTicketPlan Prepare(Character expected, InventorySlot slot, uint? requestedAction = null)
    {
        var source = expected.Inventory.Find(slot) ?? throw new InvalidDataException("追加券源不存在");
        var definition = items.ActionConsumable(source.Core.ItemId) ?? throw new InvalidDataException("追加券动作目录不存在");
        uint action = definition.Label switch
        {
            "[add dungeon incount item limit count]" => Action,
            "[add crackofdaybreak dungeon enter count]" => AddDaybreakAction,
            "[reset crackofdaybreak dungeon enter count]" => ResetDaybreakAction,
            "[add enter limit dungeonincount]" => 104,
            "[add account enter limit dungeonincount]" => 155,
            "[init dimension dungeon count]" => 59,
            "[dungeon reward reset]" => 160,
            "[reset single dungeon]" => 222,
            _ => 0
        };
        if (requestedAction is { } requested && requested != action)
            throw new InvalidDataException("请求动作与真实追加券不符");
        int amount = 1;
        bool reset = false;
        DungeonIncountRow? row = null;
        var now = Clock.GetUtcNow();
        ImmutableArray<AccountTicketQuota> accounts = [];
        ImmutableArray<RewardTicketQuota> rewards = [];
        ImmutableArray<DungeonIncountRow> resetUsed = [];
        bool anton = false;
        SingleDungeonGroup? single = null;
        if (definition.Label == "[add crackofdaybreak dungeon enter count]" && definition.Parameters.Length == 1 &&
            definition.Parameters[0] is > 0 and <= 10000)
        {
            amount = definition.Parameters[0];
            row = incounts.Find(530);
        }
        else if (definition.Label == "[reset crackofdaybreak dungeon enter count]" && definition.Parameters.Length == 0)
        {
            reset = true;
            row = incounts.Find(530);
        }
        else if (definition.Label == "[add dungeon incount item limit count]" &&
            (definition.Parameters is [> 0, 0, 0] or [8523, -1, -1]))
        {
            row = incounts.Find(definition.Parameters[0]);
        }
        else if (definition.Label == "[add dungeon incount item limit count]" &&
            definition.Parameters is [291100320, 10305620, 10306736])
        {
            row = incounts.Find(definition.Parameters[0]);
            var reward = rewardLimits?.FindDungeon(definition.Parameters[0])
                ?? throw new InvalidDataException("泰波尔斯追加券缺少奖励次数目录");
            if (definition.Parameters.Skip(1).Any(id => rewardLimits!.FindItem(id) is null))
                throw new InvalidDataException("泰波尔斯追加券奖励容器不在次数目录");
            rewards = [new(0, reward.GroupId, 1), .. definition.Parameters.Skip(1).Select(id => new RewardTicketQuota(1, id, 1))];
        }
        else if (action == 160 && definition.Parameters is [28, 0]) anton = true;
        else if (action == 222 && definition.Parameters is [>= 1 and <= 4])
        {
            if (source.Core.ItemId == 10309602)
                throw new InvalidDataException("普雷挑战次数追加券不可当成绑定初始化券");
            single = incounts.SingleGroups.SingleOrDefault(group => group.Id == definition.Parameters[0])
                ?? throw new InvalidDataException("绑定重置券缺少对应单人副本组");
        }
        else if (action is 59 or 104 or 155)
        {
            var values = definition.Parameters;
            int count = values[0];
            bool explicitAmount = action == 155 && values.Length == count + 2;
            int start = explicitAmount ? 2 : 1;
            amount = explicitAmount ? values[1] : 1;
            if (count <= 0 || count > 100 || values.Length != count + start || amount is <= 0 or > 10000 ||
                values.Skip(start).Any(id => id <= 0) || values.Skip(start).Distinct().Count() != count)
                throw new InvalidDataException("追加券副本名册或追加次数无效");
            if (action == 59)
                resetUsed = [.. values.Skip(start).Select(id => incounts.Find(id)
                    ?? throw new InvalidDataException("重置券指定副本未登记当前入场次数: " + id)).DistinctBy(target => target.CountKey)];
            else if (action == 104)
            {
                // 当前角色券仅声明单个目标；不丢弃多目标参数。
                if (count != 1) throw new InvalidDataException("角色追加券目标数量不符");
                row = incounts.Find(values[start]);
            }
            else
                accounts = [.. values.Skip(start).Select(id =>
                {
                    var target = incounts.FindAccount(id) ?? throw new InvalidDataException("账号追加券目标无次数表");
                    return new AccountTicketQuota(id, DungeonIncountPolicy.AccountPeriod(incounts, target, now),
                        incounts.ResetInfo[target.ResetType], amount);
                })];
        }
        else throw new InvalidDataException("追加券动作、参数或次数表不符");
        if (row is not { Limit: > 0 } && accounts.IsEmpty && resetUsed.IsEmpty && !anton && single is null)
            throw new InvalidDataException("追加券动作、参数或次数表不符");
        if (incounts.AvailableDungeons is { } available &&
            (row is not null && row.Dungeons.Any(id => !available.Contains(id)) ||
                accounts.Any(target => !available.Contains(target.Dungeon)) ||
                resetUsed.Any(target => target.Dungeons.Any(id => !available.Contains(id)))))
            throw new InvalidDataException("券指定副本已从当前资源目录移除");
        string place = towns.IsRoom(expected.Position, QuestEligibility.Completed(expected)) ? "[seria room]" : "[village]";
        var result = InventoryActionConsumableService.Plan(expected, slot, definition, place, Clock, now);
        if (!result.Accepted) throw new InvalidDataException("追加券源或使用资格不符");
        int resetDay = incounts.WeeklyResetDay(row?.CountKey ?? 0);
        var period = DungeonIncountPolicy.Period(now, resetDay);
        var inventory = result.Plan!;
        // 写入闸门可能跨过06点，周期资格必须在原库存事务内再次复验。
        inventory = inventory.WithAfter(inventory.After, inventory.CharacterPrecondition!.WithQualification(expected,
            DailyBoosterUsage.NextReset(now).ToUnixTimeSeconds(), () => DungeonIncountPolicy.Period(Clock.GetUtcNow(), resetDay) == period));
        return new(expected, source, inventory, row, period, resetDay, amount, reset)
        { AccountQuotas = accounts, RewardQuotas = rewards, ResetUsedTargets = resetUsed, ResetAntonRewards = anton,
            ResetSingleGroup = single, SingleWeek = single is null ? 0 : DungeonIncountPolicy.Period(now, single.ResetDay).Week };
    }
}
