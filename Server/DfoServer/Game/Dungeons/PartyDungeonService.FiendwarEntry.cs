using DfoServer.Game.Accounts;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed record FiendwarKeyRecoveryResult(PartyDungeonNotice[] Notices, string? FailureReason);

// 仅持原Session准备令牌；正式Run/Ground仍由各Session的原准备字段拥有。
internal sealed class FiendwarEntryPreparation : IDisposable
{
    internal PartySnapshot Party { get; }
    internal GameSession Leader { get; }
    internal PartyDungeonInstance Instance { get; }
    internal uint Dungeon { get; }
    internal byte Difficulty { get; }
    internal PartyDungeonNotice[] Selections { get; }
    internal List<(PartyMember Member, GameSession.PartyDungeonPreparation Preparation)> Prepared { get; }
    internal FiendwarObjectKeyLeases Keys { get; }
    internal long Deadline { get; }
    internal FiendwarKeyLeaseMessage[] Offers { get; set; } = [];
    readonly ITimer timer;
    readonly object stateGate = new();
    int cancelled;
    internal bool Completing { get; private set; }
    internal bool Active => Volatile.Read(ref cancelled) == 0;
    internal FiendwarEntryPreparation(PartySnapshot party, GameSession leader, PartyDungeonInstance instance,
        uint dungeon, byte difficulty, PartyDungeonNotice[] selections,
        List<(PartyMember Member, GameSession.PartyDungeonPreparation Preparation)> prepared)
    {
        Party = party;
        Leader = leader;
        Instance = instance;
        Dungeon = dungeon;
        Difficulty = difficulty;
        Selections = selections;
        Prepared = prepared;
        Keys = new(instance.Id);
        Deadline = checked(leader.FiendwarKeyNow + FiendwarKeyLeaseMessage.TimeoutMilliseconds);
        timer = leader.FiendwarKeyTimeProvider.CreateTimer(static state => ((FiendwarEntryPreparation)state!).Expire(), this,
            TimeSpan.FromMilliseconds(FiendwarKeyLeaseMessage.TimeoutMilliseconds), Timeout.InfiniteTimeSpan);
    }
    internal void Cancel() { lock (stateGate) Interlocked.Exchange(ref cancelled, 1); }
    internal bool BeginCompletion()
    {
        lock (stateGate)
        {
            if (!Active || Completing) return false;
            Completing = true;
            timer.Dispose();
            return true;
        }
    }
    void Expire()
    {
        lock (stateGate)
        {
            if (Completing) return;
            Interlocked.Exchange(ref cancelled, 1);
        }
        // 回调不持任何账号或队伍锁；逐个释放准确令牌，不嵌套升级跨账号锁。
        foreach (var row in Prepared) row.Member.Actor.Session.DiscardPartyPreparation(row.Preparation);
        timer.Dispose();
    }
    public void Dispose()
    {
        Cancel();
        timer.Dispose();
        foreach (var row in Prepared) row.Member.Actor.Session.DiscardPartyPreparation(row.Preparation);
    }
}

internal sealed partial class PartyDungeonService
{
    internal FiendwarEntryPreparation? PrepareFiendwarKeys(GameSession leader, uint dungeon, byte difficulty,
        DungeonCatalog catalog, DungeonRoomCatalog rooms, PartyDungeonNotice[] selections)
    {
        var party = parties.Current(leader);
        if (party is null || selections.Length != party.Members.Length ||
            !party.Members.Any(member => member.Slot == party.Leader && ReferenceEquals(member.Actor.Session, leader)) ||
            selections.Any(notice => notice.Kind != PartyDungeonNoticeKind.RaidSelection || notice.Selection is null) ||
            party.Members.Any(member => member.Actor.Session.Phase != SessionPhase.MapSelect)) return null;
        var instance = new PartyDungeonInstance(Guid.NewGuid(), party, store);
        uint firstKey = party.Members.Max(member => Math.Max(member.Actor.Session.Selected?.DungeonState?.NextObjectKey ?? 65536u,
            (uint)member.Actor.Character.Id + 1));
        var prepared = new List<(PartyMember Member, GameSession.PartyDungeonPreparation Preparation)>();
        FiendwarEntryPreparation? pending = null;
        try
        {
            foreach (var member in party.Members.OrderBy(member => ReferenceEquals(member.Actor.Session, leader) ? 0 : 1))
            {
                var token = member.Actor.Session.PreparePartyDungeon(dungeon, difficulty, catalog, rooms, null, null, null, null,
                    instance.Id, firstKey, prepared.FirstOrDefault().Preparation);
                if (token is null) return null;
                prepared.Add((member, token));
            }
            pending = new(party, leader, instance, dungeon, difficulty, selections, prepared);
            bool installed = AccountOwners.Lease.ExecuteGroup(party.Members.Select(member => (member.Actor.Owner, member.Actor.Character.Id)).ToArray(), () =>
            {
                if (!parties.IsCurrent(party) || selections.Any(notice => !notice.Recipient.Actor.Session.CanPublishDungeonSelection(notice.Selection!)) ||
                    prepared.Any(row => !row.Member.Actor.Session.CanHoldFiendwarKeys(row.Preparation))) return false;
                foreach (var row in prepared) row.Member.Actor.Session.HoldFiendwarKeys(pending, row.Preparation);
                return true;
            });
            if (!installed) { pending.Dispose(); pending = null; }
            return pending;
        }
        finally
        {
            if (pending is null) foreach (var row in prepared) row.Member.Actor.Session.DiscardPartyPreparation(row.Preparation);
        }
    }

    internal bool CanCompleteFiendwarKeys(FiendwarEntryPreparation pending) =>
        AccountOwners.Lease.ExecuteGroup(pending.Party.Members.Select(member => (member.Actor.Owner, member.Actor.Character.Id)).ToArray(), () =>
            pending.Active && pending.Leader.FiendwarKeyNow < pending.Deadline && parties.IsCurrent(pending.Party) &&
            pending.Prepared.All(row => row.Member.Actor.Session.IsCurrentFiendwarKeys(pending, row.Preparation) &&
                pending.Keys.IsReady(row.Member.Actor.Character.Id)));

    internal PartyDungeonNotice[] CompleteFiendwarKeys(FiendwarEntryPreparation pending, DungeonCatalog catalog, DungeonRoomCatalog rooms)
    {
        if (!CanCompleteFiendwarKeys(pending)) { pending.Dispose(); return []; }
        if (!pending.BeginCompletion()) { pending.Dispose(); return []; }
        try
        {
            return Enter(pending.Leader, pending.Dungeon, pending.Difficulty, catalog, rooms, null, null, null, null,
                fiendwarPrepared: pending);
        }
        finally { pending.Dispose(); }
    }

    // 复用原全员退场事务；故障不计通关，不重扣门票，也不施加普通主动撤退惩罚。
    internal FiendwarKeyRecoveryResult RecoverFiendwarKeys(GameSession reporter, PartyDungeonInstance instance)
    {
        string? failure = null;
        var returned = instance.Execute(members =>
        {
            if (!members.Any(member => ReferenceEquals(member.Actor.Session, reporter)) ||
                members.Any(member => !member.Actor.Session.CanRecoverFiendwarKeys(instance.Id))) return [];
            var notices = Exit(store, members, PartyDungeonExitKind.FiendwarKeyRecovery, out failure);
            if (notices.Length == 0) return [];
            instance.CompleteFiendwarKeyRecovery(members);
            return notices;
        });
        return new(returned, failure);
    }
}
