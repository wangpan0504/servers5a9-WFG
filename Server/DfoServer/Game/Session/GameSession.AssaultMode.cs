using System.Diagnostics;
using System.Security.Cryptography;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Session;

// #233 强袭通关后结算所需目录（计数+通关奖励发放）。
internal sealed record AssaultModeRewardServices(AssaultModeCatalog Catalog, ItemCatalog Items, GoldLimitCatalog GoldLimits, EpicPieceCatalog Pieces);

// 强袭通关提交结果：新计数、实际发放的奖励行（未发放时为 null 且 RewardIssue 说明原因）、库存刷新。
internal sealed record AssaultClearResult(AssaultDailyCounts Counts, AssaultModeClearReward? Reward, string? RewardIssue, InventoryResult? Inventory);

internal sealed partial class GameSession
{
    // ACK1547 成功到 C2S16 入场之间的一次性授权，只允许同一角色进入被选中的任务副本。
    sealed record AssaultModeAuthorization(ushort CharacterId, AssaultModeSelection Selection, EpicPieceCatalog Pieces, long IssuedTimestamp)
    {
        public AssaultModeRewardServices? Rewards { get; init; }
    }
    internal static readonly TimeSpan AssaultAuthorizationLifetime = TimeSpan.FromSeconds(60);
    AssaultModeAuthorization? assaultAuthorization;

    public AssaultModeDecision AuthorizeAssaultMode(uint parent, AssaultModeCatalog catalog, EpicPieceCatalog pieces, int day,
        Func<int, int>? draw = null, int? dayKey = null, AssaultModeRewardServices? rewards = null) => owner.Execute(() =>
    {
        assaultAuthorization = null;
        if (!Ready || Selected is not { } character || Phase != SessionPhase.MapSelect || formalDungeon is not null ||
            HasPendingTownQuestCommit || pendingDungeonContinuation is not null)
            return new AssaultModeDecision(null, AssaultModeRejection.StateInvalid, Phase.ToString());
        int key = dayKey ?? AssaultModeEntryPolicy.DayKey(DateTimeOffset.Now);
        var counts = repository.LoadAssaultDaily(owner, character.Id, key);
        var decision = AssaultModeEntryPolicy.Select(catalog, character, parent, day, pieces, DateTimeOffset.UtcNow,
            draw ?? RandomNumberGenerator.GetInt32, counts, key);
        if (decision.Selection is { } selection)
            assaultAuthorization = new(character.Id, selection, pieces, Stopwatch.GetTimestamp()) { Rewards = rewards };
        return decision;
    });

    // NOTI1536 b0/b1：当前角色当日计数；无角色时为0。
    public AssaultDailyCounts AssaultDailyCounts(DateTimeOffset localNow) => owner.Execute(() =>
        Selected is { } character ? repository.LoadAssaultDaily(owner, character.Id, AssaultModeEntryPolicy.DayKey(localNow)) : default);

    AssaultModeAuthorization? CurrentAssaultAuthorization(Character character, uint dungeonId, uint questId) =>
        questId == 0 && assaultAuthorization is { } authorization && authorization.CharacterId == character.Id &&
        authorization.Selection.Mission == dungeonId &&
        Stopwatch.GetElapsedTime(authorization.IssuedTimestamp) <= AssaultAuthorizationLifetime ? authorization : null;

    // 通关结算已提交后调用一次：同一事务内累加当日计数并发放 itemdropinfo_clearreward.etc 中的强袭奖励。
    // 与主结算不在同一事务：两者之间崩溃时丢失本次计数/奖励（不会重复发放）。
    AssaultClearResult CommitAssaultClear(AssaultModeSelection assault, AssaultModeRewardServices services)
    {
        var character = Selected ?? throw new InvalidOperationException("强袭结算缺少角色");
        InventoryPlan? plan = null;
        AssaultModeClearReward? granted = null;
        string? issue = null;
        if (!services.Catalog.ClearRewards.TryGetValue(checked((int)assault.Mission), out var reward)) issue = "no-reward-row";
        else if (reward.Rate < 10000 && RandomNumberGenerator.GetInt32(10000) >= reward.Rate) issue = "rate-miss";
        else
        {
            var grant = InventoryRewardGrantService.TryGrantBatch(character, [new InventoryReward(reward.ItemId, reward.Count)],
                services.Items, services.GoldLimits, services.Pieces, DateTimeOffset.UtcNow, allowOverflowMail: true);
            if (grant.Accepted && grant.Plan is { } accepted) { plan = accepted; granted = reward; }
            else issue = "grant-rejected:" + grant.Error;
        }
        var saved = repository.CommitAssaultClear(owner, character, assault.DayKey, assault.Hidden, plan);
        Selected = saved.Character;
        InventoryResult? inventory = plan is null ? null : new InventoryResult(true, 0, plan)
        {
            Receipt = new(Selected, owner.Generation, SelectionEpoch)
            {
                Phase = Phase,
                DungeonIdentity = formalDungeon?.Run.Snapshot.Identity,
                PreviousSkillSwitchInventory = character.SkillSwitchInventory
            }
        };
        return new(saved.Counts, granted, issue, inventory);
    }
}
