using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Titles;

internal sealed record TitleProgress(TitleCounters Remaining, long CompletedAtRevision = 0);
internal readonly record struct TitleBookRequest(int List, int Slot, int ItemId, int Category, int Index);
internal readonly record struct TitleTriggerRequest(int QuestId, TitleCounters Delta);
internal sealed record TitlePostal(int ItemId, int Count, string Sender, string Message, string Key);
internal sealed record TitleAchievementPlan(Character Before, InventoryPlan Inventory,
    ImmutableDictionary<int, TitleProgress> Progress, ImmutableHashSet<int> Sections,
    ImmutableArray<int> Completed, ImmutableArray<TitlePostal> Postals, ImmutableArray<InventoryItem> PostalItems)
{
    public ImmutableArray<FiendwarTitleCounter> FiendwarFacts { get; init; } = [];
    public ImmutableArray<int> UnlockedEmotions { get; init; } = [];
    public bool Changed => Inventory.Changed || Before.TitleProgress.Count != Progress.Count ||
        Before.TitleProgress.Any(pair => Progress.GetValueOrDefault(pair.Key) != pair.Value) || !Before.TitleSections.SetEquals(Sections);
}
internal sealed record TitleAchievementResult(bool Accepted, ushort Error, int QuestId, TitleCounters Remaining,
    TitleAchievementPlan? Plan = null, InventoryCommitReceipt? Receipt = null)
{
    public static TitleAchievementResult Reject(int questId, ushort error = 2) => new(false, error, questId, default);
}
