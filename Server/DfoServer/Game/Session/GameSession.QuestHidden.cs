using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Session;

internal sealed record QuestHiddenReceipt(Character Character, long Generation, long SelectionEpoch,
    SessionPhase Phase, DungeonRunIdentity? DungeonIdentity, uint QuestId, bool Open, string Reason);

internal sealed partial class GameSession
{
    private QuestHiddenReceipt? pendingHiddenOpening;
    private long? hiddenAreaEnteredAt;

    long HiddenQuestTimestamp() => (timeProvider ?? TimeProvider.System).GetTimestamp();

    void TrackHiddenQuestArea(Character? before, Character? after)
    {
        if (before?.Id != after?.Id || before?.Position.Town != after?.Position.Town || before?.Position.Area != after?.Position.Area)
            hiddenAreaEnteredAt = after is not null && Phase == SessionPhase.Town ? HiddenQuestTimestamp() : null;
    }

    bool CanOpenStandbyQuest(Character expected, StoryQuest quest, QuestCatalog quests) =>
        QuestStandbyEntry.Supports(quest) && QuestHiddenEntry.Direct(quest,quests) && QuestStandbyEntry.AtArea(expected) &&
        hiddenAreaEnteredAt is { } arrived &&
        (timeProvider ?? TimeProvider.System).GetElapsedTime(arrived,HiddenQuestTimestamp()) >= TimeSpan.FromMilliseconds(1000) &&
        QuestEligibility.Evaluate(expected,quest,quests).Eligible &&
        !quest.Definition!.HideOnCompletedQuests.Any(QuestEligibility.Completed(expected).Contains) &&
        towns.IsPosition(expected.Position,QuestEligibility.Completed(expected)) &&
        repository is IQuestHiddenHistoryStore history && history.HasQuestDungeonClear(owner,expected,291100397,1);

    // 许可只属于884核验时的不可变角色快照；移动、换图、换角或其它事务改变快照后不得复用。
    internal bool CanAcceptHiddenQuest(Character expected, StoryQuest quest, QuestCatalog quests,
        long selectionEpoch, SessionPhase phase) =>
        !(QuestHiddenEntry.NpcDialogueShape(quest) || QuestStandbyEntry.Supports(quest)) || owner.Execute(() =>
            pendingHiddenOpening is { Open: true } opening && opening.QuestId == quest.Id &&
            ReferenceEquals(opening.Character,expected) && opening.Generation == owner.Generation &&
            opening.SelectionEpoch == selectionEpoch && opening.Phase == phase && phase == SessionPhase.Town &&
            CanPublishQuestHidden(opening) && (QuestStandbyEntry.Supports(quest) ? CanOpenStandbyQuest(expected,quest,quests) :
                QuestHiddenEntry.CanOpenNpcDialogue(expected,quest,quests,towns,(uint)quest.Definition!.Values("npc dialog")[0].Value)));

    public QuestHiddenReceipt? ReadHiddenQuest(uint questId, QuestCatalog quests, uint npcDialog = 0)
    {
        var capture = owner.Execute(() =>
            (Character: Ready && Phase is not (SessionPhase.Unselected or SessionPhase.Loading) ? Selected : null,
                owner.Generation, SelectionEpoch, Phase,
                Identity: Phase is SessionPhase.Dungeon or SessionPhase.Settlement ? formalDungeon?.Run.Snapshot.Identity : null));
        if (capture.Character is not { } expected) return null;
        bool open = questId is > 0 and < 30000 && quests.TryGet((int)questId,out var quest) &&
            (QuestHiddenEntry.CanOpen(expected,quest,quests) || capture.Phase == SessionPhase.Town &&
                (QuestHiddenEntry.CanOpenNpcDialogue(expected,quest,quests,towns,npcDialog) ||
                 owner.Execute(() => ReferenceEquals(Selected,expected) && CanOpenStandbyQuest(expected,quest,quests))));
        var receipt = new QuestHiddenReceipt(expected,capture.Generation,capture.SelectionEpoch,capture.Phase,
            capture.Identity,questId,open,open ? "合格隐藏候选已确认" : "隐藏资源或当前资格不符");
        return owner.Execute(() =>
        {
            if (!CanPublishQuestHidden(receipt)) return null;
            if (quests.TryGet((int)questId,out var candidate) &&
                (QuestHiddenEntry.NpcDialogueShape(candidate) || QuestStandbyEntry.Supports(candidate)))
                pendingHiddenOpening = open ? receipt : null;
            return receipt;
        });
    }

    public bool CanPublishQuestHidden(QuestHiddenReceipt receipt) => owner.Execute(() => Ready &&
        owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch && Phase == receipt.Phase &&
        ReferenceEquals(Selected,receipt.Character) &&
        (receipt.DungeonIdentity is null || formalDungeon?.Run.Snapshot.Identity == receipt.DungeonIdentity));
}
