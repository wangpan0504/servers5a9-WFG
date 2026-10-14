using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Quests;

internal enum QuestClearSourceType { MonsterDeath, PassiveObject }
internal sealed record QuestClearReceipt(Guid AcceptanceId, ushort QuestId, byte QuestSlot,
    Guid EventId, Guid InstanceId, long TaskAttempt, long Epoch, long Revision)
{
    public int DungeonId { get; init; }
    public int MapId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public QuestClearSourceType SourceType { get; init; }
}

internal sealed record QuestConditionReceipt(Character Character, QuestClearReceipt Clear,
    long Generation, long SelectionEpoch, DungeonRunIdentity? DungeonIdentity);
