using DfoServer.Game.Characters;
using DfoServer.GameWorld.Items;
using System.Collections.Immutable;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    CharacterRosterSnapshot? publishedRoster;

    public CharacterRosterSnapshot PrepareRoster() => owner.Execute(CaptureRoster);

    CharacterRosterSnapshot CaptureRoster() => new(owner.Generation, SelectionEpoch, repository.ListRoster(owner).ToImmutableArray())
        { Capacity = repository.LoadAccountWallet(owner).CharacterSlotCapacity };

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

    public bool PvpPrivateSelectionBlocked(ushort index) => owner.Execute(() =>
        PublishedRosterEntry(index)?.IsPvpPrivate == true && ChannelEntry?.Type != 24);

    public void SelectFromRosterIndex(ushort index, GoldLimitCatalog? goldLimits = null, bool resumeTown = false) => owner.Execute(() =>
    {
        if (Selected is not null) throw new InvalidDataException("Invalid selection state");
        var entry = PublishedRosterEntry(index) ?? throw new InvalidDataException("Selection published roster index absent");
        SelectCharacter(entry.Slot, goldLimits, entry, resumeTown);
        return true;
    });

    public CharacterSlotChangeResult ChangeCharacterSlots(uint indexA, uint indexB) => owner.Execute(() =>
    {
        if (Selected is not null || Phase != SessionPhase.Unselected)
            return new CharacterSlotChangeResult(null, SelectionEpoch, "selected");
        // 同索引请求不写库，仍回复成功和当前名册，避免客户端停在拖动后的本地行序。
        if (indexA == indexB)
            return new CharacterSlotChangeResult(new(CaptureRoster()), SelectionEpoch, "same-slot");
        // 客户端正文是最近名册的行索引，不是持久槽位；有空洞时二者不同，必须按已发布行序映射。
        var roster = publishedRoster;
        if (roster is null || !MatchesRoster(roster) ||
            indexA >= roster.Entries.Length || indexB >= roster.Entries.Length)
            return new CharacterSlotChangeResult(new(CaptureRoster()), SelectionEpoch, "roster-mismatch");
        repository.SwapSlots(owner, roster.Entries[(int)indexA].Slot, roster.Entries[(int)indexB].Slot);
        SelectionEpoch++;
        return new CharacterSlotChangeResult(new(CaptureRoster()), SelectionEpoch, "committed");
    });

    public bool CanPublishSlotChange(CharacterSlotChangeReceipt receipt) => owner.Execute(() =>
        MatchesRoster(receipt.Roster) && Selected is null && Phase == SessionPhase.Unselected);
}
