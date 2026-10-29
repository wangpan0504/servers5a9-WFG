using System.Collections.Immutable;
using System.Text;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Pvp;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Pvp;

// 持久身份不保存客户端列表位置。删角只清对应位置，队名及队伍身份继续保留。
internal sealed record PvpTotalTeam(Guid Id, long Revision, string Name, ImmutableArray<ushort?> Characters)
{
    public bool Complete => !Characters.IsDefault && Characters.Length == 3 && Characters.All(id => id is not null);
    public bool SameLineup(PvpTotalTeam other) => Id == other.Id && Revision == other.Revision && Name == other.Name &&
        Characters.SequenceEqual(other.Characters);

    public static bool ValidName(string name)
    {
        if (name.Length is < 2 or > 12 || !name.All(char.IsLetterOrDigit)) return false;
        try { return CharacterName.Encoding.GetByteCount(name) <= 24; }
        catch (EncoderFallbackException) { return false; }
    }
}

internal interface IPvpTotalTeamStore
{
    PvpTotalTeam? ReadPvpTotalTeam(AccountOwners.Lease owner);
    bool IsPvpTotalTeamNameTaken(AccountOwners.Lease owner, string name);
    PvpTotalTeam? SavePvpTotalTeam(AccountOwners.Lease owner, PvpTotalTeam? expected, PvpTotalTeam desired,
        ImmutableArray<CharacterRosterEntry> characters);
    PvpTotalTeamRenameCommit? RenamePvpTotalTeam(AccountOwners.Lease owner, PvpTotalTeamRenamePlan plan);
}

internal enum PvpTotalTeamFailure { None, MissingName, InvalidName, InvalidLineup, NameTaken, ArenaActive }
internal sealed record PvpTotalTeamRegistration(PvpTotalTeam? Team, PvpTotalRosterSelection? Selection,
    PvpTotalTeamFailure Failure);
internal sealed record PvpTotalTeamInitialization(PvpTotalTeam Team, PvpTotalRosterSelection Selection);

internal sealed class PvpTotalTeamService(IPvpTotalTeamStore store, PvpArenaCatalog catalog,
    ItemTemplate? renameCard = null, TimeProvider? clock = null)
{
    internal TimeProvider Clock { get; } = clock ?? TimeProvider.System;
    public PvpTotalTeam? Read(AccountOwners.Lease owner) => store.ReadPvpTotalTeam(owner);
    internal PvpTotalTeamRenamePlan? PrepareRename(Character character, PvpTotalTeam team, string name, ushort slot) =>
        PvpTotalTeamRename.Prepare(character, team, name, slot, renameCard, Clock);
    internal PvpTotalTeamRenameCommit? CommitRename(AccountOwners.Lease owner, PvpTotalTeamRenamePlan plan) =>
        store.RenamePvpTotalTeam(owner, plan);

    public PvpTotalTeamInitialization? CaptureInitialization(GameSession session)
    {
        if (session.CapturePvpMatchingAdmission() is not { } admission) return null;
        return admission.Actor.Execute(() =>
        {
            var team = store.ReadPvpTotalTeam(admission.Actor.Owner);
            // 缺员的客户端哨兵仍待核实；这里只回放真实完整阵容，不补空角色或自动换人。
            return team is not null && session.CaptureStoredPvpTotalRoster(team) is { } selection
                ? new PvpTotalTeamInitialization(team, selection) : null;
        });
    }

    public PvpTotalTeam? Register(GameSession session, string name, ImmutableArray<byte> indices, bool blocked = false)
        => RegisterWithReceipt(session, name, indices, blocked).Team;

    // 检查名称不预留名称；最终编队事务仍须再次查重。
    public PvpTotalTeamFailure CheckName(AccountOwners.Lease owner, string name) => owner.Execute(() =>
        string.IsNullOrEmpty(name) ? PvpTotalTeamFailure.MissingName : !PvpTotalTeam.ValidName(name)
            ? PvpTotalTeamFailure.InvalidName : store.IsPvpTotalTeamNameTaken(owner, name)
                ? PvpTotalTeamFailure.NameTaken : PvpTotalTeamFailure.None);

    public PvpTotalTeamRegistration RegisterWithReceipt(GameSession session, string name,
        ImmutableArray<byte> indices, bool blocked = false)
    {
        if (string.IsNullOrEmpty(name)) return Reject(PvpTotalTeamFailure.MissingName);
        if (!PvpTotalTeam.ValidName(name)) return Reject(PvpTotalTeamFailure.InvalidName);
        if (session.CurrentPvpArena is { Room.IsMatching: true } or { Room.Match: not null } || session.CurrentPvpMatching is not null)
            return Reject(PvpTotalTeamFailure.ArenaActive);
        var selection = session.CapturePvpTotalRoster(indices, blocked);
        if (selection is null) return Reject(PvpTotalTeamFailure.InvalidLineup);
        return selection.Actor.Execute(() =>
        {
            if (!session.CanUsePvpTotalRoster(selection)) return Reject(PvpTotalTeamFailure.InvalidLineup);
            var characters = selection.Members.Select(row => row.Character).ToImmutableArray();
            if (characters.Any(row => !Eligible(row)) ||
                characters.Select(row => (row.Job, Grow: row.GrowType & 15)).Distinct().Count() != 3)
                return Reject(PvpTotalTeamFailure.InvalidLineup);
            var before = store.ReadPvpTotalTeam(selection.Actor.Owner);
            // 改名需独立的费用事务；注册阵容不能借此绕过改名道具。
            if (before is not null && before.Name != name) return Reject(PvpTotalTeamFailure.InvalidName);
            var desired = new PvpTotalTeam(before?.Id ?? Guid.NewGuid(), before?.Revision ?? 0, name,
                characters.Select(row => (ushort?)row.Id).ToImmutableArray());
            var team = store.SavePvpTotalTeam(selection.Actor.Owner, before, desired, characters);
            return team is null ? Reject(PvpTotalTeamFailure.NameTaken) : new(team, selection, PvpTotalTeamFailure.None);
        });
    }

    static PvpTotalTeamRegistration Reject(PvpTotalTeamFailure failure) => new(null, null, failure);

    bool Eligible(CharacterRosterEntry character) => character.Level > 0 &&
        catalog.CanEnter(PvpArenaEligibility.Matching, character.Job, (byte)(character.GrowType & 15));
}
