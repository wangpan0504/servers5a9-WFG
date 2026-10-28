using System.Collections.Immutable;
using DfoServer.Game.Session;

namespace DfoServer.Game.Pvp;

internal enum PvpMatchingLoadPhase { Loading, Ready, Started, Cancelled }
internal sealed record PvpMatchingLoadView(uint Id, ImmutableArray<PvpMatchingTicket> Members,
    PvpMatchingLoadPhase Phase, DateTimeOffset Deadline, ImmutableArray<Guid> Loaded,
    ImmutableArray<PublicCharacterSnapshot> Characters);

// 仅表示匹配预加载，不能替代正式战斗的角色快照、P2P屏障或持久结算。
// 写入由PvpArenaService的gate串行化；对外发布只能使用不可变View。
internal sealed class PvpMatchingLoad(uint id, PvpMatchingTicket first, PvpMatchingTicket second, DateTimeOffset deadline,
    ImmutableArray<PublicCharacterSnapshot> characters)
{
    readonly HashSet<Guid> loaded = [];
    int phase;
    public uint Id { get; } = id;
    public ImmutableArray<PvpMatchingTicket> Members { get; } = [first, second];
    public DateTimeOffset Deadline { get; } = deadline;
    public ImmutableArray<PublicCharacterSnapshot> Characters { get; } = characters;
    public ImmutableArray<PvpArenaMembership> RoomMembers { get; private set; } = [];
    public PvpArenaRoom? Room => RoomMembers.IsEmpty ? null : RoomMembers[0].Room;
    public PvpMatchingLoadPhase Phase => (PvpMatchingLoadPhase)Volatile.Read(ref phase);
    public bool IsCurrent => Phase != PvpMatchingLoadPhase.Cancelled && (Room is null
        ? Members.All(row => row.IsCurrent && ReferenceEquals(row.Load, this) && row.Actor.Session.CanRemainPvpMatching) &&
            Characters.All(row => row.IsUnchanged())
        : RoomMembers.All(row => row.IsCurrent && ReferenceEquals(row.Room.MatchingLoad, this)) &&
            Characters.All(row => row.IsCurrent() && ReferenceEquals(row.Character,
                RoomMembers.Single(member => member.Actor.Character.Id == row.Character.Id).Actor.Session.Selected)));

    internal void EnterRoom(ImmutableArray<PvpArenaMembership> members) => RoomMembers = members;

    internal void BeginBattle() => Volatile.Write(ref phase, (int)PvpMatchingLoadPhase.Started);
    public PvpMatchingLoadView View() => new(Id, Members, Phase, Deadline,
        Members.Where(row => loaded.Contains(row.Token)).Select(row => row.Token).ToImmutableArray(), Characters);

    internal bool Complete(PvpMatchingTicket ticket, DateTimeOffset now)
    {
        if (!IsCurrent || Phase != PvpMatchingLoadPhase.Loading || now >= Deadline || !Members.Contains(ticket) ||
            !loaded.Add(ticket.Token)) return false;
        if (loaded.Count == 2) Volatile.Write(ref phase, (int)PvpMatchingLoadPhase.Ready);
        return true;
    }

    internal bool Cancel()
    {
        if (Phase == PvpMatchingLoadPhase.Cancelled) return false;
        Volatile.Write(ref phase, (int)PvpMatchingLoadPhase.Cancelled);
        return true;
    }
}
