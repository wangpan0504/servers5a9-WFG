using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Quests;

internal sealed record QuestGrantedItem(InventoryItem Item, int GrantedCount);
internal sealed record QuestCompletionCommit(Character Character, QuestClearReceipt Clear,
    QuestExperienceReward ExperienceReward, ImmutableArray<QuestGrantedItem> Rewards, bool Duplicate, bool ProjectionCurrent,
    byte PreviousLevel, CharacterLearningState? PreviousLearning = null);

internal sealed record QuestCompletionReceipt(QuestCompletionCommit Commit, long Generation, long SelectionEpoch,
    DungeonRunIdentity? DungeonIdentity);

internal enum QuestFinishOutcome { Committed, Rejected, InsufficientSpace, AlreadyCompleted, Unsupported }
internal sealed record QuestRefusalReceipt(Character Character, long Generation, long SelectionEpoch, DungeonRunIdentity? DungeonIdentity);
internal sealed record QuestFinishResult(QuestFinishOutcome Outcome, QuestCompletionReceipt? Receipt = null,
    QuestRefusalReceipt? RejectionReceipt = null);
