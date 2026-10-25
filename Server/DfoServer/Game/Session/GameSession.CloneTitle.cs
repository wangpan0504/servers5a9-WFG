using DfoServer.Game.Characters;
using DfoServer.Game.Settings;
using DfoServer.Game.Titles;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public (Character? Character, ushort Error, bool Changed) SelectCloneTitle(
        TitleBookService books, ClientSettingsService settings, uint itemId, long utcSeconds)
    {
        var captured = owner.Execute(() => (Character: CanSelectCloneTitle() ? Selected : null, SelectionEpoch, Phase));
        if (captured.Character is not { } character) return (null, 2, false);
        // 模板目录可能首次解析PVF，资格规划放在账号锁外；提交只接受原会话和角色快照。
        var qualification = books.CloneTitleQualification(character, itemId, utcSeconds);
        if (qualification.Error != 0) return (null, qualification.Error, false);
        return owner.Execute<(Character?, ushort, bool)>(() =>
        {
            if (!CanSelectCloneTitle() || SelectionEpoch != captured.SelectionEpoch || Phase != captured.Phase ||
                !ReferenceEquals(Selected, character)) return (null, 2, false);
            bool? changed = settings.SaveCloneTitle(owner, character.Id, itemId, qualification.ExpiresAt);
            if (!changed.HasValue) return (null, 2, false);
            bool visibleChanged = character.CloneTitleItemId != itemId;
            if (visibleChanged) Selected = character with { CloneTitleItemId = itemId };
            return (Selected, 0, changed.Value || visibleChanged);
        });
    }

    bool CanSelectCloneTitle() => Ready && Selected is not null &&
        Phase is SessionPhase.Town or SessionPhase.MapSelect or SessionPhase.Dungeon or SessionPhase.Settlement &&
        pendingRental is null && !HasPendingTownQuestCommit && !HasPendingFormalCard &&
        formalDungeon?.PendingTutorialClear is null && formalDungeon?.PendingInventoryDiscard is null &&
        formalDungeon?.AltarSettlement is not { Stage: < 2 };
}
