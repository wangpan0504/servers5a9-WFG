using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Quests;

internal sealed record QuestClearReceipt(Guid AcceptanceId, ushort QuestId, byte QuestSlot,
    Guid EventId, Guid InstanceId, long TaskAttempt, long Epoch, long Revision);

internal sealed record QuestConditionReceipt(Character Character, QuestClearReceipt Clear,
    long Generation, long SelectionEpoch, DungeonRunIdentity? DungeonIdentity);
