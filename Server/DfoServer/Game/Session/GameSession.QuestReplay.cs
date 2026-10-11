using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Session;

internal sealed record QuestReplayReceipt(Character Character,long Generation,long SelectionEpoch,uint QuestId,uint DungeonId,SessionPhase Phase);
internal sealed record QuestReplaySelection(bool Pending,DungeonSelectionReceipt? Receipt = null,FormalDungeonReceipt? Replay = null);

internal sealed partial class GameSession
{
    sealed record PendingQuestReplay(PreparedFormalDungeon Prepared,uint QuestId,SessionPhase Phase,bool SelectionOpened = false,FormalDungeonReceipt? Installed = null);
    PendingQuestReplay? pendingQuestReplay;

    public QuestReplayReceipt? PrepareQuestReplay(uint questId,QuestConnectionCatalog connections,DungeonCatalog dungeons,
        DungeonRoomCatalog rooms,DungeonMonsterDropCatalog? drops = null,ItemCatalog? items = null,EpicPieceCatalog? pieces = null,
        Func<DungeonEntryPlan, DungeonPlannedMonster, DungeonDropGenerationPlan>? prepareDrops = null)
    {
        var (expected,epoch,phase,pending) = owner.Execute(() =>
            (Ready && CanEnterFormalDungeon(null) ? Selected : null,SelectionEpoch,Phase,pendingQuestReplay));
        if (expected is null || questCatalog is null) return null;
        PreparedFormalDungeon? prepared;
        if (pending is not null && pending.QuestId == questId && ReferenceEquals(pending.Prepared.Character,expected) &&
            pending.Prepared.SelectionEpoch == epoch && pending.Phase == phase)
            prepared = pending.Prepared;
        else
        {
            var replay = QuestReplayPlan.Prepare(expected,questId,questCatalog,connections);
            if (replay is null || !DungeonEntryPlan.TryCreateReplay(dungeons,replay,out var entry,out _)) return null;
            prepared = PrepareFormalDungeonResources(expected,epoch,entry!,rooms,drops,items,null,pieces,prepareDrops);
        }
        if (prepared is null) return null;
        return owner.Execute(() =>
        {
            if (!Ready || !CanEnterFormalDungeon(null) || SelectionEpoch != epoch || Phase != phase || !ReferenceEquals(Selected,expected))
                return null;
            pendingQuestReplay = new(prepared,questId,phase);
            return new QuestReplayReceipt(expected,owner.Generation,epoch,questId,prepared.Dungeon.Entry.DungeonId,phase);
        });
    }

    public bool CanPublishQuestReplay(QuestReplayReceipt receipt) => owner.Execute(() =>
        Ready && owner.Generation == receipt.Generation && SelectionEpoch == receipt.SelectionEpoch &&
        ReferenceEquals(Selected,receipt.Character) && Phase == receipt.Phase &&
        pendingQuestReplay is { } pending && pending.QuestId == receipt.QuestId && pending.Prepared.Dungeon.Entry.DungeonId == receipt.DungeonId);

    public QuestReplaySelection BeginQuestReplayDungeonSelection(uint dungeonId) => owner.Execute(() =>
    {
        if (pendingQuestReplay is not { } pending) return new QuestReplaySelection(false);
        if (pending.Installed is { } installed)
            return new QuestReplaySelection(true,Replay: dungeonId == installed.Entry.DungeonId && Phase == SessionPhase.Loading &&
                CanPublishFormalDungeon(installed) ? installed : null);
        if (!Ready || !CanEnterFormalDungeon(null) || SelectionEpoch != pending.Prepared.SelectionEpoch ||
            !ReferenceEquals(Selected,pending.Prepared.Character))
        {
            pendingQuestReplay = null;
            return new QuestReplaySelection(true);
        }
        if (pending.Prepared.Dungeon.Entry.DungeonId != dungeonId) return new QuestReplaySelection(true);
        if (CurrentDungeonSelection is null)
        {
            // 回放由任务UI发起；回城锚点保留当前真实位置，不要求另走普通地下城门。
            CurrentDungeonSelection = new(++dungeonSelectionId,SelectionEpoch,Selected!.Id,Selected.Position,Selected.Position);
        }
        ValidateDungeonSelection();
        Phase = SessionPhase.MapSelect;
        pendingQuestReplay = pending with { SelectionOpened = true };
        return new QuestReplaySelection(true,new(Selected!,owner.Generation,SelectionEpoch,dungeonSelectionId));
    });

    public FormalDungeonReceipt? EnterQuestReplay(uint dungeonId,uint questId,byte difficulty) => owner.Execute(() =>
    {
        if (pendingQuestReplay is not { SelectionOpened: true } pending || !Ready || Phase != SessionPhase.MapSelect ||
            !CanEnterFormalDungeon(null) || SelectionEpoch != pending.Prepared.SelectionEpoch ||
            !ReferenceEquals(Selected,pending.Prepared.Character) || pending.QuestId != questId || difficulty != 0 ||
            pending.Prepared.Dungeon.Entry.DungeonId != dungeonId) return null;
        var receipt = InstallFormalDungeon(pending.Prepared);
        if (receipt is not null) pendingQuestReplay = pending with { Installed = receipt };
        return receipt;
    });

    bool HasPendingQuestReplay => pendingQuestReplay is { } pending && pending.Prepared.SelectionEpoch == SelectionEpoch &&
        ReferenceEquals(Selected,pending.Installed?.Character ?? pending.Prepared.Character);
}
