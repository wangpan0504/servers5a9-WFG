using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Session;

internal sealed record QuestHiddenReceipt(Character Character, long Generation, long SelectionEpoch,
    SessionPhase Phase, DungeonRunIdentity? DungeonIdentity, uint QuestId, bool Open, string Reason);

internal sealed partial class GameSession
{
    public QuestHiddenReceipt? ReadHiddenQuest(uint questId, QuestCatalog quests)
    {
        var capture = owner.Execute(() =>
            (Character: Ready && Phase is not (SessionPhase.Unselected or SessionPhase.Loading) ? Selected : null,
                owner.Generation, SelectionEpoch, Phase,
                Identity: Phase is SessionPhase.Dungeon or SessionPhase.Settlement ? formalDungeon?.Run.Snapshot.Identity : null));
        if (capture.Character is not { } expected) return null;
        bool open = questId is > 0 and < 30000 && quests.TryGet((int)questId,out var quest) &&
            QuestHiddenEntry.CanOpen(expected,quest,quests);
        var receipt = new QuestHiddenReceipt(expected,capture.Generation,capture.SelectionEpoch,capture.Phase,
            capture.Identity,questId,open,open ? "合格隐藏候选已确认" : "隐藏资源或当前资格不符");
        return CanPublishQuestHidden(receipt) ? receipt : null;
    }

    public bool CanPublishQuestHidden(QuestHiddenReceipt receipt) => owner.Execute(() => Ready &&
        owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch && Phase == receipt.Phase &&
        ReferenceEquals(Selected,receipt.Character) &&
        (receipt.DungeonIdentity is null || formalDungeon?.Run.Snapshot.Identity == receipt.DungeonIdentity));
}
