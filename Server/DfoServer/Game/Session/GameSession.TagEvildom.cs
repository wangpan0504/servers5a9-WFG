using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.TagEvildom;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    long tagEvildomCharacterVersion;

    public TagEvildomProjection? ReadTagEvildom(ITagEvildomConfigurationStore store, DateTimeOffset now) => owner.Execute(() =>
    {
        if (PublishedTagEvildomRoster() is not { } roster || ChannelEntry?.Identity.ServerIndex is not byte server || server == 0)
            return null;
        return new TagEvildomProjection(store.ReadTagEvildomState(owner, TagEvildomPeriod.At(now)), server,
            roster, owner.Generation, SelectionEpoch, Selected!.Id);
    });

    public (TagEvildomProjection? Projection, string Reason) ConfigureTagEvildom(ITagEvildomConfigurationStore store,
        ImmutableArray<TagEvildomMemberRequest>? members, int selectedTeam, DateTimeOffset now,
        TagEvildomCharacters? readiness = null) => owner.Execute(() =>
    {
        if (readiness is not null && !CanApplyTagEvildomReadiness(store, readiness))
            return ((TagEvildomProjection?)null, "character-readiness-changed");
        if (ReadTagEvildom(store, now) is not { } snapshot) return ((TagEvildomProjection?)null, "selection-roster-unavailable");
        var before = snapshot.State.Configuration;
        TagEvildomConfiguration desired;
        var currentRoster = repository.ListRoster(owner);
        if (members is null) desired = before.Reset(snapshot.State.ProtectedMembers);
        else
        {
            if (members.Value.IsDefault || members.Value.Length != 6 || selectedTeam is < 0 or > 2)
                return ((TagEvildomProjection?)null, "invalid-team-shape");
            var ids = new ushort[6];
            for (int index = 0; index < 6; index++)
            {
                var member = members.Value[index];
                if (member.Auxiliary != 0) return ((TagEvildomProjection?)null, "unsupported-member-auxiliary");
                if (member.Server == 0 && member.RosterIndex == -1) continue;
                if (member.Server != snapshot.Server || member.RosterIndex < 0 || member.RosterIndex >= snapshot.Roster.Entries.Length)
                    return ((TagEvildomProjection?)null, "foreign-or-invalid-roster-index");
                var original = snapshot.Roster.Entries[member.RosterIndex];
                var current = currentRoster.SingleOrDefault(row => row.Id == original.Id);
                if (current is null || current.Slot != original.Slot || current.Name != original.Name)
                    return ((TagEvildomProjection?)null, "published-roster-identity-changed");
                ids[index] = original.Id;
            }
            desired = before with { SelectedTeam = selectedTeam, Teams = [new(ids[0], ids[1]), new(ids[2], ids[3]), new(ids[4], ids[5])] };
        }
        string reason;
        try { reason = TagEvildomConfiguration.ValidateChange(before, desired, currentRoster, snapshot.State.ProtectedMembers); }
        catch (InvalidDataException) { return ((TagEvildomProjection?)null, "invalid-team-shape"); }
        if (reason != "ready") return ((TagEvildomProjection?)null, reason);
        var committed = store.CommitTagEvildomConfiguration(owner, before, desired, TagEvildomPeriod.At(now));
        return (snapshot with { State = snapshot.State with { Configuration = committed } }, "committed");
    });

    public bool CanPublishTagEvildom(TagEvildomProjection projection) => owner.Execute(() => Ready && Phase == SessionPhase.Town &&
        projection.Generation == owner.Generation && projection.Epoch == SelectionEpoch && projection.CharacterId == Selected?.Id &&
        ReferenceEquals(projection.Roster, tagEvildomRoster));

    // 设置可能选择别的队首；只读装载选中双人，锁外做资源资格计算，提交前复核同一身份/版本。
    public TagEvildomCharacters? CaptureTagEvildomSettingCharacters(ITagEvildomConfigurationStore store,
        ImmutableArray<TagEvildomMemberRequest> members, int selectedTeam, DateTimeOffset now) => owner.Execute(() =>
    {
        if (selectedTeam is < 0 or > 2 || members.Length != 6 || ReadTagEvildom(store, now) is not { } snapshot) return null;
        var selected = members.Skip(selectedTeam * 2).Take(2).ToArray();
        if (selected.Any(member => member.Server != snapshot.Server || member.RosterIndex < 0 ||
            member.RosterIndex >= snapshot.Roster.Entries.Length || member.Auxiliary != 0)) return null;
        var roles = selected.Select(member => snapshot.Roster.Entries[member.RosterIndex]).Select(row =>
            Selected?.Id == row.Id ? Selected : repository.LoadCharacter(owner, row.Slot)).ToArray();
        if (roles.Any(role => role is null || role.Level < 95 || role.IsPvpPrivate || !role.Learning.Initialized) || roles[0]!.Id == roles[1]!.Id) return null;
        var receipt = new TagEvildomCharacters(snapshot, roles[0]!, roles[1]!, tagEvildomCharacterVersion);
        return CanApplyTagEvildomReadiness(store, receipt) ? receipt : null;
    });

    bool CanApplyTagEvildomReadiness(ITagEvildomConfigurationStore store, TagEvildomCharacters receipt) =>
        CanPublishTagEvildom(receipt.Projection) && receipt.Version == tagEvildomCharacterVersion &&
        (receipt.Primary.Id != Selected?.Id || ReferenceEquals(receipt.Primary, Selected)) &&
        (receipt.Standby.Id != Selected?.Id || ReferenceEquals(receipt.Standby, Selected)) && store.MatchesTagEvildomCharacters(owner, receipt);

    public Character? TagEvildomStandby(TagEvildomProjection projection) => owner.Execute(() =>
    {
        if (!CanPublishTagEvildom(projection)) return null;
        var configuration = projection.State.Configuration;
        var team = configuration.Teams[configuration.SelectedTeam];
        if (team.Empty || team.First != Selected!.Id || Selected.Level < 95 || Selected.IsPvpPrivate || !Selected.Learning.Initialized) return null;
        var original = projection.Roster.Entries.SingleOrDefault(row => row.Id == team.Second);
        if (original is null) return null;
        var character = repository.LoadCharacter(owner, original.Slot);
        if (character.Id != original.Id || character.Name != original.Name || character.Level < 95 ||
            character.IsPvpPrivate || !character.Learning.Initialized) return null;
        return character;
    });

    public TagEvildomCharacters? CaptureTagEvildomCharacters(TagEvildomProjection projection) => owner.Execute(() =>
    {
        if (TagEvildomStandby(projection) is not { } standby) return null;
        // 保留实际捕获值供投影判别主角共享钱包是否变更；发送前仍必须走轻量完整stamp，不能把捕获等同可发送。
        return new TagEvildomCharacters(projection, Selected!, standby, tagEvildomCharacterVersion);
    });

    public bool CanPublishTagEvildomCharacters(ITagEvildomConfigurationStore store, TagEvildomCharacters receipt) => owner.Execute(() =>
        CanPublishTagEvildom(receipt.Projection) && receipt.Version == tagEvildomCharacterVersion &&
        ReferenceEquals(Selected, receipt.Primary) && store.MatchesTagEvildomCharacters(owner, receipt));
}
