using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    FiendwarEntryPreparation? fiendwarKeyEntry;
    PartyDungeonNotice[]? fiendwarKeySelections;
    bool fiendwarDeferredTownScene;
    internal Func<FiendwarKeyLeaseMessage, Task>? FiendwarKeyLeaseArrived { get; set; }
    internal TimeProvider FiendwarKeyTimeProvider => timeProvider ?? TimeProvider.System;
    internal long FiendwarKeyNow => checked((long)FiendwarKeyTimeProvider.GetElapsedTime(0).TotalMilliseconds);

    internal static PartyDungeonNotice[] HoldFiendwarKeySelections(PartyDungeonNotice[] notices)
    {
        var held = notices.Select(notice => notice with { SceneTransition = notice.SceneTransition ||
            notice.Recipient.Actor.Session.fiendwarDeferredTownScene }).ToArray();
        foreach (var notice in held) notice.Recipient.Actor.Session.HoldFiendwarKeySelection(held, notice.SceneTransition);
        return held;
    }
    void HoldFiendwarKeySelection(PartyDungeonNotice[] notices, bool deferred) => owner.Execute(() =>
    {
        InvalidateFiendwarKeyPreparation(preserveScene: true);
        fiendwarKeySelections = notices;
        fiendwarDeferredTownScene = deferred;
        return true;
    });
    internal void MarkFiendwarKeySelectionProjected() => owner.Execute(() => { fiendwarDeferredTownScene = false; return true; });
    internal PartyDungeonNotice[] HeldFiendwarKeySelection(uint dungeon) => owner.Execute(() =>
        fiendwarKeySelections is { Length: > 0 } notices && CurrentDungeonSelection?.Raid?.Dungeon == dungeon &&
        notices.All(notice => notice.Selection is not null && notice.Recipient.Actor.Session.SelectionEpoch == notice.Selection.SelectionEpoch)
            ? notices : []);

    internal FiendwarEntryPreparation? CurrentFiendwarKeyPreparation() => owner.Execute(() =>
        fiendwarKeyEntry is { Active: true } pending && FiendwarKeyNow < pending.Deadline ? pending : null);

    // 在本人owner内只失效共享标记和本人准备；不升级到其他成员owner。
    void InvalidateFiendwarKeyPreparation(bool preserveScene = false)
    {
        if (fiendwarKeyEntry is { Completing: false } pending)
        {
            pending.Cancel();
            partyPreparation = null;
            preparedPartyDungeon = null;
        }
        fiendwarKeyEntry = null;
        fiendwarKeySelections = null;
        if (!preserveScene) fiendwarDeferredTownScene = false;
    }

    internal bool CanHoldFiendwarKeys(PartyDungeonPreparation token) => owner.Execute(() =>
        ReferenceEquals(partyPreparation, token) && preparedPartyDungeon is { } prepared &&
        ReferenceEquals(Selected, token.Expected) && SelectionEpoch == prepared.SelectionEpoch && Ready &&
        Phase == SessionPhase.MapSelect && prepared.Dungeon.Entry.RaidEntry?.Kind == RaidKind.Fiendwar &&
        CanInstallRaidDungeon(prepared.Dungeon.Entry));

    internal void HoldFiendwarKeys(FiendwarEntryPreparation pending, PartyDungeonPreparation token) => owner.Execute(() =>
    {
        if (!CanHoldFiendwarKeys(token)) throw new InvalidOperationException("漩涡入场准备身份已变化");
        var prepared = PreparedParty(token);
        prepared.Dungeon.Run.BindFiendwarObjectKeys(pending.Keys);
        fiendwarKeyEntry = pending;
        fiendwarKeySelections = pending.Selections;
        return true;
    });
    internal bool IsCurrentFiendwarKeys(FiendwarEntryPreparation pending, PartyDungeonPreparation token) =>
        ReferenceEquals(fiendwarKeyEntry, pending) && ReferenceEquals(CurrentParty?.Invoke(), pending.Party) &&
        CanHoldFiendwarKeys(token) && pending.Active;

    FiendwarKeyLeaseScene PreparedFiendwarKeyScene(PartyDungeonPreparation token)
    {
        var dungeon = PreparedParty(token).Dungeon;
        var snapshot = dungeon.Run.Snapshot;
        return new(snapshot.Identity.InstanceId, dungeon.Entry.DungeonId, checked((uint)snapshot.CurrentMap.Id), dungeon.MapSeed,
            (uint)(snapshot.CurrentMap.Position.X | snapshot.CurrentMap.Position.Y << 8), 0, snapshot.Identity.Epoch);
    }
    internal FiendwarKeyLeaseMessage[] FiendwarKeyOffers(FiendwarEntryPreparation pending) =>
        AccountOwners.Lease.ExecuteGroup(pending.Party.Members.Select(member => (member.Actor.Owner, member.Actor.Character.Id)).ToArray(), () =>
        {
            if (!pending.Active || FiendwarKeyNow >= pending.Deadline ||
                pending.Prepared.Any(row => !row.Member.Actor.Session.IsCurrentFiendwarKeys(pending, row.Preparation))) return [];
            return pending.Offers = pending.Prepared.Select(row => pending.Keys.Offer(row.Member.Actor.Character.Id,
                row.Member.Actor.Session.PreparedFiendwarKeyScene(row.Preparation), FiendwarKeyNow)).ToArray();
        });

    internal FiendwarKeyLeaseMessage? ReportPreparedFiendwarKeys(FiendwarEntryPreparation pending, FiendwarKeyLeaseMessage request) =>
        AccountOwners.Lease.ExecuteGroup(pending.Party.Members.Select(member => (member.Actor.Owner, member.Actor.Character.Id)).ToArray(), () =>
        {
            var own = pending.Prepared.SingleOrDefault(row => ReferenceEquals(row.Member.Actor.Session, this));
            if (!pending.Active || FiendwarKeyNow >= pending.Deadline || pending.Prepared.Any(row =>
                    !row.Member.Actor.Session.IsCurrentFiendwarKeys(pending, row.Preparation)))
            {
                pending.Cancel();
                return null;
            }
            if (own.Preparation is null || request.Scene != PreparedFiendwarKeyScene(own.Preparation)) return null;
            ushort character = own.Member.Actor.Character.Id;
            if (request.Kind == FiendwarKeyLeaseKind.Cancel)
            {
                if (pending.Keys.CancelBatch(character, request)) pending.Cancel();
                return null;
            }
            if (request.Kind == FiendwarKeyLeaseKind.Ready)
            {
                _ = pending.Keys.Confirm(character, request, FiendwarKeyNow);
                return null;
            }
            return pending.Keys.Grant(character, request, FiendwarKeyNow, key =>
                pending.Prepared.All(row => row.Member.Actor.Character.Id != key &&
                    row.Member.Actor.Session.PreparedFiendwarKeyFree(row.Preparation, key)));
        });

    bool PreparedFiendwarKeyFree(PartyDungeonPreparation token, ushort key)
    {
        var prepared = PreparedParty(token).Dungeon;
        return prepared.Run.CanLeaseFiendwarObjectKey(key) && prepared.Ground.HasFreeDynamicKey(key);
    }

    // 英雄沿普通四人Enter安装同一账本；先在首房预租，进入真实召唤房前再由原全组门禁复核ready。
    internal bool CanBindEvildomWarChallengeKeys(PartyDungeonPreparation token) => owner.Execute(() =>
        ReferenceEquals(partyPreparation, token) && preparedPartyDungeon is { } prepared &&
        prepared.Dungeon.EvildomWarChallengeEntry is { } admission && IsCurrentEvildomWarChallengeAdmission(admission) &&
        prepared.Dungeon.Run.IsEvildomWarChallengeDynamic && prepared.Dungeon.Run.FiendwarObjectKeys is null);

    internal void BindEvildomWarChallengeKeys(PartyDungeonPreparation token, FiendwarObjectKeyLeases keys) => owner.Execute(() =>
    {
        if (!CanBindEvildomWarChallengeKeys(token)) throw new InvalidOperationException("英雄动态对象键准备身份已变化");
        PreparedParty(token).Dungeon.Run.BindFiendwarObjectKeys(keys);
        return true;
    });

    internal FiendwarKeyLeaseScene? CurrentFiendwarKeyScene(FormalDungeonReceipt receipt, uint serial) => owner.Execute(() =>
    {
        if (serial == 0 || !Ready || Selected?.Id != receipt.Character.Id || receipt.Generation != owner.Generation ||
            receipt.SelectionEpoch != SelectionEpoch || formalDungeon is not { } dungeon || dungeon.Run.FiendwarObjectKeys is null ||
            !(dungeon.Entry.RaidEntry?.Kind == RaidKind.Fiendwar || dungeon.Run.IsEvildomWarChallengeDynamic) ||
            dungeon.Run.Scene.Map != receipt.Snapshot.CurrentMap ||
            dungeon.MapSeed != receipt.MapSeed ||
            !(Phase == SessionPhase.Loading && dungeon.Run.Scene.Identity == receipt.Snapshot.Identity ||
                Phase == SessionPhase.Dungeon && dungeon.Run.LastLoadingFact is { } loaded &&
                    loaded.Identity == receipt.Snapshot.Identity && loaded.Map == receipt.Snapshot.CurrentMap))
            return (FiendwarKeyLeaseScene?)null;
        var snapshot = receipt.Snapshot;
        return new FiendwarKeyLeaseScene(snapshot.Identity.InstanceId, receipt.Entry.DungeonId, checked((uint)snapshot.CurrentMap.Id), receipt.MapSeed,
            (uint)(snapshot.CurrentMap.Position.X | snapshot.CurrentMap.Position.Y << 8), serial, snapshot.Identity.Epoch);
    });

    internal FiendwarKeyLeaseMessage? FiendwarSceneKeyOffer(FormalDungeonReceipt receipt, uint serial) => owner.Execute(() =>
        CurrentFiendwarKeyScene(receipt, serial) is { } scene && formalDungeon?.Run.FiendwarObjectKeys is { RecoveryRequired: false } keys &&
        Selected is { } character ? keys.Offer(character.Id, scene, FiendwarKeyNow) : null);

    internal bool CanPublishFiendwarKeyMessage(FiendwarKeyLeaseMessage message) => owner.Execute(() => Ready && Selected is { } character &&
        (message.Kind == FiendwarKeyLeaseKind.Cancel ||
            fiendwarKeyEntry is { Active: true } pending && pending.Keys.MatchesBatch(character.Id, message) ||
            formalDungeon?.Run.FiendwarObjectKeys is { } keys && keys.MatchesBatch(character.Id, message)));

    // 调用方已持队伍/全员命令门禁；instance.Execute在原全组owner边界内复核当前场景。
    internal FiendwarKeyLeaseMessage? ReportLiveFiendwarKeys(PartyDungeonInstance instance, FormalDungeonReceipt receipt,
        uint serial, FiendwarKeyLeaseMessage request) => instance.Execute(members =>
    {
        if (!ReferenceEquals(PartyDungeon, instance) || CurrentFiendwarKeyScene(receipt, serial) is not { } scene || scene != request.Scene ||
            formalDungeon?.Run.FiendwarObjectKeys is not { } ledger || Selected is not { } character ||
            members.Any(member => !ReferenceEquals(member.Actor.Session.formalDungeon?.Run.FiendwarObjectKeys, ledger))) return null;
        if (request.Kind == FiendwarKeyLeaseKind.Cancel)
        {
            if (request.Status != 0 && ledger.MatchesBatch(character.Id, request))
            {
                ledger.RequireRecovery(request.Handshake, scene);
                return request;
            }
            _ = ledger.CancelBatch(character.Id, request);
            return null;
        }
        if (ledger.RecoveryRequired) return null;
        if (request.Kind == FiendwarKeyLeaseKind.Refill)
            return ledger.Refill(character.Id, request, FiendwarKeyNow);
        if (request.Kind == FiendwarKeyLeaseKind.Ready)
        {
            _ = ledger.Confirm(character.Id, request, FiendwarKeyNow);
            return null;
        }
        return ledger.Grant(character.Id, request, FiendwarKeyNow, key => members.All(member =>
            member.Actor.Character.Id != key && member.Actor.Session.formalDungeon is { } dungeon &&
            dungeon.Run.CanLeaseFiendwarObjectKey(key) && dungeon.Ground.HasFreeDynamicKey(key)));
    });

    internal bool CanRecoverFiendwarKeys(Guid run) => formalDungeon is { } dungeon && Ready &&
        dungeon.Run.FiendwarObjectKeys is { RecoveryRequired: true } && dungeon.Run.Scene.Identity.InstanceId == run &&
        (dungeon.Entry.RaidEntry?.Kind == RaidKind.Fiendwar && raidCombat is { Actor.IsCurrent: true, Battle.Kind: RaidKind.Fiendwar } ||
            dungeon.Run.IsEvildomWarChallengeDynamic && ChannelEntry?.Type == 64 && Selected is { } character &&
            (dungeon.OrdinaryReturnPosition ?? character.Position) is { Town: 124, Area: 2 }) &&
        Phase is SessionPhase.Loading or SessionPhase.Dungeon;

    internal FiendwarKeyLeaseMessage? FiendwarKeyRecoveryMessage() => owner.Execute(() =>
        Selected is { } character ? formalDungeon?.Run.FiendwarObjectKeys?.RecoveryMessage(character.Id) : null);
}
