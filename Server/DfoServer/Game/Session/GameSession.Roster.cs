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

    public void SelectFromRosterIndex(ushort index, GoldLimitCatalog? goldLimits = null, bool resumeTown = false) => owner.Execute(() =>
    {
        if (Selected is not null) throw new InvalidDataException("Invalid selection state");
        var entry = PublishedRosterEntry(index) ?? throw new InvalidDataException("Selection published roster index absent");
        SelectCharacter(entry.Slot, goldLimits, entry, resumeTown);
        return true;
    });

    public CharacterSlotChangeResult ChangeCharacterSlots(uint slotA, uint slotB) => owner.Execute(() =>
    {
        if (Selected is not null || Phase != SessionPhase.Unselected)
            return new CharacterSlotChangeResult(null, SelectionEpoch, "selected");
        if (slotA > ushort.MaxValue || slotB > ushort.MaxValue)
            return new CharacterSlotChangeResult(new(CaptureRoster()), SelectionEpoch, "slot-invalid");
        // 同槽请求不写库，仍回复成功和当前名册，避免客户端停在拖动后的本地行序。
        if (slotA == slotB)
            return new CharacterSlotChangeResult(new(CaptureRoster()), SelectionEpoch, "same-slot");
        // 只按最近已发布名册解释两个持久槽位；名册外的请求不能猜归属，回推当前名册重新同步。
        var roster = publishedRoster;
        if (roster is null || !MatchesRoster(roster) ||
            !roster.Entries.Any(entry => entry.Slot == slotA) || !roster.Entries.Any(entry => entry.Slot == slotB))
            return new CharacterSlotChangeResult(new(CaptureRoster()), SelectionEpoch, "roster-mismatch");
        repository.SwapSlots(owner, checked((ushort)slotA), checked((ushort)slotB));
        SelectionEpoch++;
        return new CharacterSlotChangeResult(new(CaptureRoster()), SelectionEpoch, "committed");
    });

    public bool CanPublishSlotChange(CharacterSlotChangeReceipt receipt) => owner.Execute(() =>
        MatchesRoster(receipt.Roster) && Selected is null && Phase == SessionPhase.Unselected);
}
