using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Game.Social;
using DfoServer.GameWorld.Pvp;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Pvp;

internal sealed record PvpMissionProgress(int Played, int Wins, int TotalPlayed, int TotalWins)
{
    public ImmutableHashSet<int> ComboKeys { get; init; } = [];
    public int ArcadeMask { get; init; }
    public bool Same(PvpMissionProgress other) => Played == other.Played && Wins == other.Wins &&
        TotalPlayed == other.TotalPlayed && TotalWins == other.TotalWins && ArcadeMask == other.ArcadeMask && ComboKeys.SetEquals(other.ComboKeys);
    public PvpMissionProgress Apply(PvpMissionFact? fact) => fact switch
    {
        { Kind: 0, Job: <= 15, Value: >= 0 and < 12, Instance: var instance } when instance != Guid.Empty =>
            this with { ComboKeys = ComboKeys.Add(fact.Job * 12 + fact.Value) },
        { Kind: 1, Job: <= 15, Value: >= 0 and <= 2, Instance: var instance } when instance != Guid.Empty =>
            this with { ArcadeMask = ArcadeMask | (1 << fact.Value) },
        null => this,
        _ => throw new InvalidDataException("决斗训练事实无效")
    };
}
internal sealed record PvpMissionFact(byte Kind, byte Job, int Value, Guid Instance);
internal sealed record PvpMissionState(PvpMissionProgress Progress, ImmutableHashSet<ushort> Claimed)
{
    public bool Same(PvpMissionState other) => Progress.Same(other.Progress) && Claimed.SetEquals(other.Claimed);
}
internal sealed record PvpMissionPlan(Character Expected, PvpMissionState Before, PvpMissionFact? Fact,
    ImmutableArray<ushort> Missions, InventoryResult Inventory)
{
    public ImmutableDictionary<ushort, ImmutableArray<PvpMissionReward>> Rewards { get; init; } = ImmutableDictionary<ushort, ImmutableArray<PvpMissionReward>>.Empty;
}
internal readonly record struct PvpMissionRow(ushort Id, int Current, int Target);
internal sealed record PvpMissionBookSnapshot(SocialActor Actor, ImmutableArray<PvpMissionRow> Rows);

internal interface IPvpMissionProgressStore
{
    PvpMissionProgress ReadPvpMissionProgress(AccountOwners.Lease owner, ushort characterId);
    PvpMissionState ReadPvpMissionState(AccountOwners.Lease owner, ushort characterId);
    Character CommitPvpMissions(AccountOwners.Lease owner, PvpMissionPlan plan);
}

internal sealed class PvpMissionBook(PvpMissionCatalog catalog, IPvpMissionProgressStore store, PvpRatingSettlement ratings,
    ItemCatalog items, GoldLimitCatalog gold, EpicPieceCatalog pieces)
{
    internal PvpMissionPlan Plan(AccountOwners.Lease owner, Character character, PvpMissionFact? fact)
    {
        var before = store.ReadPvpMissionState(owner, character.Id);
        var progress = before.Progress.Apply(fact);
        int grade = ratings.Grade(ratings.Capture(owner, character.Id));
        var claims = Eligible(catalog.Definitions, character.Job, grade, progress, before.Claimed);
        var ids = claims.Select(row => row.Id).ToImmutableArray();
        var rewards = claims.SelectMany(row => row.Rewards).Select(row => new InventoryReward(row.ItemId, row.Count)).ToArray();
        var grant = InventoryRewardGrantService.TryGrantBatch(character, rewards, items, gold, pieces, allowOverflowMail: true);
        if (!grant.Accepted || grant.Plan is null) throw new InvalidDataException("决斗任务奖励无法完整规划");
        return new(character, before, fact, ids, grant with { Plan = new(grant.Plan.Before, grant.Plan.After,
            "pvp_mission_reward", new(character), grant.Plan.OverflowItems) })
            { Rewards = claims.ToImmutableDictionary(row => row.Id, row => row.Rewards) };
    }

    internal Character Commit(AccountOwners.Lease owner, PvpMissionPlan plan) => store.CommitPvpMissions(owner, plan);

    internal static ImmutableArray<PvpMissionDefinition> Eligible(ImmutableArray<PvpMissionDefinition> definitions,
        byte job, int grade, PvpMissionProgress progress, ImmutableHashSet<ushort> claimed)
    {
        var completed = claimed.ToHashSet();
        var result = ImmutableArray.CreateBuilder<PvpMissionDefinition>();
        bool added;
        do
        {
            added = false;
            foreach (var row in definitions)
                if (!completed.Contains(row.Id) && (row.Job is null || row.Job == job) &&
                    grade >= row.MinimumRank && grade <= row.MaximumRank &&
                    (row.Parent == -1 || completed.Contains((ushort)row.Parent)) && Current(row, job, grade, progress) >= row.Target)
                { completed.Add(row.Id); result.Add(row); added = true; }
        } while (added);
        return result.ToImmutable();
    }
    internal PvpMissionBookSnapshot? Capture(GameSession session)
    {
        if (session.ChannelEntry is not { } channel || !ChannelAreaLanding.IsPvpType(channel.Type) ||
            session.CaptureSocialActor() is not { } actor) return null;
        return actor.Owner.Execute(() =>
        {
            if (!actor.IsCurrent()) return null;
            int grade = ratings.Grade(ratings.Capture(actor.Owner, actor.Character.Id));
            var state = store.ReadPvpMissionState(actor.Owner, actor.Character.Id);
            return new PvpMissionBookSnapshot(actor, Rows(catalog.Definitions, actor.Character.Job, grade, state.Progress, state.Claimed));
        });
    }

    internal static ImmutableArray<PvpMissionRow> Rows(ImmutableArray<PvpMissionDefinition> definitions,
        byte job, int grade, PvpMissionProgress progress, ImmutableHashSet<ushort>? claimed = null) => definitions
        .Where(row => row.Job is null || row.Job == job).Where(row => grade >= row.MinimumRank && grade <= row.MaximumRank)
        .Select(row => new PvpMissionRow(row.Id, Math.Max(Current(row, job, grade, progress),
            claimed?.Contains(row.Id) == true ? row.Target : 0), row.Target)).ToImmutableArray();

    internal static int Current(PvpMissionDefinition row, byte job, int grade, PvpMissionProgress progress) => row.Condition switch
        {
            "hereafter join" => row.TotalOnly ? progress.TotalPlayed : progress.Played,
            "hereafter winning count" => row.TotalOnly ? progress.TotalWins : progress.Wins,
            "rank" => grade,
            "combo clear" => progress.ComboKeys.Contains(job * 12 + row.Parameter) ? 1 : 0,
            "arcade mode" => (progress.ArcadeMask & (row.Parameter == -1 ? 7 : 4)) != 0 ? 1 : 0,
            // 斗兽场需要独立的正式对局来源，普通及总力胜局不能冒作斗兽场。
            _ => 0
        };
}
