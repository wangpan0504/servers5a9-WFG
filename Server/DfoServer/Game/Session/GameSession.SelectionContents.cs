using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;

namespace DfoServer.Game.Session;

internal sealed record SelectionContentsReceipt(CharacterRosterSnapshot Roster, byte? Index, int Day,
    int GameDay, SelectionTowerCounts Towers, SelectionContents? Contents);

internal sealed partial class GameSession
{
    public SelectionContentsReceipt? CaptureSelectionContents(SelectionContentsRules rules, byte? index = null) => owner.Execute(() =>
    {
        if (!CheckState(UserState.CharacterSelect) || publishedRoster is not { } roster || !MatchesRoster(roster)) return null;
        var entry = index is { } row ? PublishedRosterEntry(row) : null;
        if (index is not null && entry is null) return null;
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var state = repository.ReadSelectionContents(owner, entry, now);
        return new SelectionContentsReceipt(roster, index, ShopPurchaseCount.Today(now), QuestCycle.Day(now), rules.Towers(state),
            entry is null ? null : rules.Character(state, entry.Id, now));
    });

    public bool CanPublishSelectionContents(SelectionContentsReceipt receipt) => owner.Execute(() =>
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return CheckState(UserState.CharacterSelect) && ReferenceEquals(publishedRoster, receipt.Roster) && MatchesRoster(receipt.Roster) &&
            receipt.Day == ShopPurchaseCount.Today(now) && receipt.GameDay == QuestCycle.Day(now);
    });
}
