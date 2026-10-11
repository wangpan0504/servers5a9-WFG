using DfoServer.Game.Characters;
using DfoServer.GameWorld.Items;
using System.Collections.Immutable;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    CharacterRosterSnapshot? publishedRoster;

    public CharacterRosterSnapshot PrepareRoster() => owner.Execute(CaptureRoster);

    CharacterRosterSnapshot CaptureRoster() => new(owner.Generation, SelectionEpoch, repository.ListRoster(owner).ToImmutableArray());

    bool MatchesRoster(CharacterRosterSnapshot roster) => roster.Generation == owner.Generation && roster.SelectionEpoch == SelectionEpoch;

    bool CanSendRoster(CharacterRosterSnapshot roster) => MatchesRoster(roster) && Selected is null && Phase == SessionPhase.Unselected;

    public bool CanPublishRoster(CharacterRosterSnapshot roster) => owner.Execute(() => CanSendRoster(roster));

    public void MarkRosterPublished(CharacterRosterSnapshot roster) => owner.Execute(() =>
    {
        // 发送失败不调用；发送guard跳过或其后状态已变时也不能安装旧映射。
        if (CanSendRoster(roster)) publishedRoster = roster;
        return true;
    });

    CharacterRosterEntry? PublishedRosterEntry(ushort index) => publishedRoster is { } roster && MatchesRoster(roster) &&
        index < roster.Entries.Length ? roster.Entries[index] : null;

    public void SelectFromRosterIndex(ushort index, GoldLimitCatalog? goldLimits = null) => owner.Execute(() =>
    {
        if (Selected is not null) throw new InvalidDataException("Invalid selection state");
        var entry = PublishedRosterEntry(index) ?? throw new InvalidDataException("Selection published roster index absent");
        SelectCharacter(entry.Slot, goldLimits, entry);
        return true;
    });
}
