using DfoServer.Game.Characters;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public CharacterDeletionResult DeleteCharacterFromRoster(ushort index, string confirmationName) => owner.Execute(() =>
    {
        if (Selected is not null || Phase != SessionPhase.Unselected)
            return new CharacterDeletionResult(null, SelectionEpoch, "selected");
        var entry = PublishedRosterEntry(index);
        if (entry is null)
            return new CharacterDeletionResult(null, SelectionEpoch, "index-absent");
        if (!string.Equals(entry.Name, confirmationName, StringComparison.Ordinal))
            return new CharacterDeletionResult(null, SelectionEpoch, "confirmation-mismatch");
        // wire是客户端当前名册行索引；真正删除身份由持久槽位与版本决定。
        var target = repository.LoadDeletionTarget(owner, entry.Slot);
        if (target.Id != entry.Id || target.Name != entry.Name)
            throw new InvalidOperationException("Character deletion roster changed");
        if (target.AdventureBusy) return new CharacterDeletionResult(null, SelectionEpoch, "adventure-expedition-busy");
        var deleted = repository.Delete(owner, target);
        SelectionEpoch++;
        return new CharacterDeletionResult(new(index, deleted, CaptureRoster()), SelectionEpoch, "committed");
    });

    public bool CanPublishDeletion(CharacterDeletionReceipt receipt) => owner.Execute(() =>
        MatchesRoster(receipt.Roster) && Selected is null && Phase == SessionPhase.Unselected);

    public CharacterDeletion DeleteCharacter(ushort slot) => owner.Execute(() =>
    {
        if (Selected is not null || Phase != SessionPhase.Unselected)
            throw new InvalidDataException("Delete after selection");
        var target = repository.LoadDeletionTarget(owner, slot);
        var deleted = repository.Delete(owner, target);
        // 名册已变更；锁外开始的旧建角规划不能在删除后继续提交。
        SelectionEpoch++;
        return deleted;
    });
}
