using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Quests;

// 回放只规划已完成任务的显式路线，不修改活动槽、完成记录或奖励资格。
internal sealed record QuestReplayPlan(Character Expected, StoryQuest Quest, uint DungeonId)
{
    public uint QuestId => checked((uint)Quest.Id);

    public static QuestReplayPlan? Prepare(Character character,uint questId,QuestCatalog quests,QuestConnectionCatalog connections)
    {
        if (questId is 0 or >= 30000 || !QuestEligibility.Completed(character).Contains((int)questId) ||
            !quests.TryGet((int)questId,out var quest) || quest.Definition is null ||
            !connections.TryGetReplayDungeon((int)questId,out uint dungeon)) return null;
        return new(character,quest,dungeon);
    }
}
