using DfoServer.Game.Party;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    // 沿用原单人队伍的会话门禁；共享队伍服务在账号提交边界内复核。
    internal bool CanChangeTownParty => Ready && Phase == SessionPhase.Town && Selected is not null && pendingRental is null && pvpMatching is null &&
        !HasPendingTownQuestCommit && !HasActiveDungeonContinuation && !HasPendingQuestReplay && !HasPendingFormalCard &&
        !Strongway2019BlocksTownActions;
    PartyEndpoint? partyEndpoint;
    long partyMembershipVersion;
    internal long PartyMembershipVersion => Volatile.Read(ref partyMembershipVersion);
    internal long AdvancePartyMembership() => Interlocked.Increment(ref partyMembershipVersion);
    public uint PeerAccount { get; private set; }
    public PartyEndpoint? PartyEndpoint => Volatile.Read(ref partyEndpoint);
    public void SetPeerAccount(uint value) => owner.Execute(() => { PeerAccount = value; return true; });
    public void SetPartyEndpoint(PartyEndpoint value) => owner.Execute(() => { Volatile.Write(ref partyEndpoint, value); return true; });
    public Action<PartyNotice>? PartyNoticeArrived { get; set; }
    public Func<bool>? HasMultiplePartyMembers { get; set; }
    public Func<PartySnapshot?>? CurrentParty { get; set; }
}
