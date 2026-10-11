using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    long dungeonSelectionId;
    public DungeonSelectionContext? CurrentDungeonSelection { get; private set; }

    public DungeonSelectionReceipt BeginDungeonSelection() => owner.Execute(() =>
    {
        if (!Ready || Selected is null || Phase is not (SessionPhase.Town or SessionPhase.MapSelect))
            throw new InvalidDataException("Invalid dungeon-selection state");
        if (CurrentDungeonSelection is null)
        {
            var back = towns.SelectionReturn(Selected.Position, completed) ?? throw new InvalidDataException("Dungeon gate return position unavailable");
            CurrentDungeonSelection = new(++dungeonSelectionId, SelectionEpoch, Selected.Id, Selected.Position, back);
        }
        ValidateDungeonSelection();
        SetState(SessionPhase.MapSelect);
        return new DungeonSelectionReceipt(Selected, owner.Generation, SelectionEpoch, dungeonSelectionId);
    });

    public DungeonSelectionReceipt? ReturnFromDungeonSelection(bool restorePosition = true) => owner.Execute(() =>
    {
        if (CurrentDungeonSelection is null)
            return null;
        if (!Ready || Phase != SessionPhase.MapSelect)
            throw new InvalidOperationException("Dungeon-selection phase changed");
        ValidateDungeonSelection();
        // 先提交入口外落点，再消费选择身份；提交失败仍保留原MapSelect，不发送回城成功。
        if (restorePosition && Selected!.Position != CurrentDungeonSelection.ReturnPosition)
            Selected = repository.Save(owner, Selected, CurrentDungeonSelection.ReturnPosition, Selected.Return);
        CurrentDungeonSelection = null;
        pendingQuestReplay = null;
        SetState(SessionPhase.Town);
        return new DungeonSelectionReceipt(Selected!, owner.Generation, SelectionEpoch, dungeonSelectionId);
    });

    public bool CanPublishDungeonSelection(DungeonSelectionReceipt receipt, bool returning = false) => owner.Execute(() =>
    {
        if (!Ready || receipt.Generation != owner.Generation || SelectionEpoch != receipt.SelectionEpoch ||
            Selected?.Id != receipt.Character.Id || dungeonSelectionId != receipt.SelectionId ||
            (returning ? Phase != SessionPhase.Town || CurrentDungeonSelection is not null :
                Phase != SessionPhase.MapSelect || CurrentDungeonSelection?.Id != receipt.SelectionId))
            return false;
        // 同阶段的 GM/资产更新也替换快照；不能用旧回城覆盖其投影，关闭后按存档恢复。
        if (!ReferenceEquals(Selected, receipt.Character))
            throw new InvalidOperationException("Dungeon-selection snapshot changed before publication");
        return true;
    });

    void ValidateDungeonSelection()
    {
        var selection = CurrentDungeonSelection!;
        if (selection.SelectionEpoch != SelectionEpoch || Selected?.Id != selection.CharacterId || Selected.Position != selection.OriginPosition)
            throw new InvalidOperationException("Dungeon-selection return position changed");
    }
}
