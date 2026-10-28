using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    internal void SetMarriageDungeonWitness(PartyDungeonInstance instance, QuestMarriagePartyClear? witness)
    {
        if (!ReferenceEquals(PartyDungeon, instance) || formalDungeon is not { } dungeon) return;
        if (witness is not null && (!instance.IsExecuting || witness.SharedInstanceId != instance.Id ||
            dungeon.Run.Snapshot.Identity.InstanceId != instance.Id ||
            Selected is not { } character || character.Id != witness.FirstId && character.Id != witness.SecondId))
            throw new InvalidOperationException("婚约清图名册必须由当前共享副本owner冻结");
        dungeon.Run.MarriagePartyClear = witness;
    }
}
