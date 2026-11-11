using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;

namespace DfoServer.Game.Quests;

internal sealed record QuestGrantedItem(InventoryItem Item, int GrantedCount);
internal sealed record QuestCompletionCommit(Character Character, QuestClearReceipt? Clear,
    QuestExperienceReward ExperienceReward, ImmutableArray<QuestGrantedItem> Rewards, bool Duplicate, bool ProjectionCurrent,
    byte PreviousLevel, CharacterLearningState? PreviousLearning = null)
{
    public DfoServer.Game.Adventure.AdventureProgressionCommit? Adventure { get; init; }
    public ushort QuestId { get; init; } = Clear?.QuestId ?? 0;
    public Guid AcceptanceId { get; init; } = Clear?.AcceptanceId ?? Guid.Empty;
    public byte ConditionType { get; init; } = 3;
    public uint GoldAmount { get; init; }
    public uint MailedGoldAmount { get; init; }
    public uint DirectGoldAmount => GoldAmount - MailedGoldAmount;
    public uint? GoldMailId { get; init; }
    public uint? ResourceMailId { get; init; }
    public uint GainedExperience { get; init; }
    public QuestStagePlan? StagePlan { get; init; }
    public uint OrdinaryExperience { get; init; }
    public uint ContractExperience { get; init; }
    public uint StageExperience => StagePlan?.BonusExperience ?? 0;
    public uint TotalRequestedExperience { get; init; }
    public ushort TpAmount { get; init; }
    public bool HellChallenge { get; init; }
    public ImmutableArray<QuestConsumedItem> Consumed { get; init; } = [];
    public ImmutableArray<QuestCurrencyReward> Currencies { get; init; } = [];
    public ImmutableArray<InventorySlot> ChangedSlots { get; init; } = [];
    public ImmutableArray<InventorySlot> ReplacedRewardSlots { get; init; } = [];
    public IReadOnlyList<ActiveMainQuest> PreviousQuests { get; init; } = [];
    public byte PreviousGrowType { get; init; }
    public byte? ProfessionGrowType { get; init; }
    public byte? ExpertJobType { get; init; }
    public byte PreviousEquipmentSlotFlags { get; init; }
}

internal sealed record QuestCompletionReceipt(QuestCompletionCommit Commit, long Generation, long SelectionEpoch,
    DungeonRunIdentity? DungeonIdentity)
{
    public SessionPhase Phase { get; init; } = DungeonIdentity is null ? SessionPhase.Town : SessionPhase.Settlement;
    public DateTimeOffset? BattlePassMissionAt { get; init; }
    // 父33回调默认不发574/356，以免打断城镇对话。范围零奖励子任务离开活动表后必须靠这个标记补发。
    public bool ArchivedTownRange { get; init; }
}

internal enum QuestFinishOutcome { Committed, Rejected, InsufficientSpace, AlreadyCompleted, Unsupported }
internal sealed record QuestRefusalReceipt(Character Character, long Generation, long SelectionEpoch, DungeonRunIdentity? DungeonIdentity)
{
    public SessionPhase Phase { get; init; } = DungeonIdentity is null ? SessionPhase.Town : SessionPhase.Settlement;
}
internal sealed record QuestFinishResult(QuestFinishOutcome Outcome, QuestCompletionReceipt? Receipt = null,
    QuestRefusalReceipt? RejectionReceipt = null)
{
    public QuestCompletionFailure Failure { get; init; }
    // 仅供服务端日志；不进入发给客户端的包。
    public string? Reason { get; init; }
    // 父任务最终被拒时，已经提交的范围子任务回执。发布必须用其中最新一份，不能只回父任务拒绝。
    public ImmutableArray<QuestCompletionReceipt> ArchivedChildren { get; init; } = [];
    // 范围子任务已经归档，但这份结果带不走补归档前的活动表。调用方关闭连接，不发过期活动槽。
    public bool CloseForUnpublishedRangeArchive { get; init; }
}
