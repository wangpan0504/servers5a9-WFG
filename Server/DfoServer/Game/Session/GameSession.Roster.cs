using DfoServer.Game.Characters;
using DfoServer.GameWorld.Items;
using System.Collections.Immutable;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    CharacterRosterSnapshot? publishedRoster;
    CharacterRosterSnapshot? selectedRoster;

    // TAG 的 index 仍指向入城前实际发送的 NOTI2 行序；不能以城镇重查或过滤后的名册重新编号。
    public CharacterRosterSnapshot? PublishedTagEvildomRoster() => owner.Execute(() =>
        Ready && Phase == SessionPhase.Town && Selected is not null && selectedRoster is { } roster && MatchesRoster(roster)
            ? roster : null);

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

    public bool BeastSelectionBlocked(ushort index) => owner.Execute(() => BeastChannel.IsEntry(ChannelEntry) &&
        PublishedRosterEntry(index) is { BeastEligible: false });

    public bool PvpPrivateSelectionBlocked(ushort index) => owner.Execute(() =>
        PublishedRosterEntry(index)?.IsPvpPrivate == true && ChannelEntry?.Type is not (8 or 24));

    public void SelectFromRosterIndex(ushort index, GoldLimitCatalog? goldLimits = null, bool resumeTown = false)
    {
        owner.Execute(() =>
        {
            if (Selected is not null) throw new InvalidDataException("Invalid selection state");
            var entry = PublishedRosterEntry(index) ?? throw new InvalidDataException("Selection published roster index absent");
            var roster = publishedRoster!;
            SelectCharacter(entry.Slot, goldLimits, entry, resumeTown);
            // 只有选角成功才把原发布映射绑定到新生命周期；普通选角映射仍保持自己的旧 epoch。
            selectedRoster = roster with { SelectionEpoch = SelectionEpoch };
            return true;
        });
        PrepareSelectedQuestItemFacts();
    }

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
