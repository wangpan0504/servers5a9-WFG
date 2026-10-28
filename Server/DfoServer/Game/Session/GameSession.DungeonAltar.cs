using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Session;

internal sealed record DungeonAltarReceipt(long Generation, long SelectionEpoch, Guid InstanceId, DungeonMap Map,
    DungeonAltarState State, int Round, byte Difficulty, long? Due, long Clock)
{
    public DungeonAltarBatch? Batch { get; init; }
    public bool RoundStarted { get; init; }
    public bool DifficultySelected { get; init; }
    public bool RoundAdvanced { get; init; }
    public bool Completed { get; init; }
    public int DisplayRound { get; init; }
    public long? WaitingDeathSerial { get; init; }
    public DungeonAltarResultReceipt? Result { get; init; }
    public DfoServer.Game.Town.TownTravelReceipt? Returned { get; init; }
}

internal sealed partial class GameSession
{
    public bool HasDungeonAltar => owner.Execute(() => formalDungeon?.Run.CurrentAltar is not null);

    public DungeonAltarReceipt? WaitDungeonAltarRevival() => owner.Execute(() =>
    {
        if (!Ready || Selected is null || Phase != SessionPhase.Dungeon ||
            formalDungeon is not { CharacterDead: true, AltarSettlement: null } dungeon ||
            dungeon.Run.CurrentAltar is not { } state) return null;
        long now = AltarClock();
        dungeon.AltarRevivalDue ??= checked(now + 10000);
        return AltarReceipt(state, now) with { Due = dungeon.AltarRevivalDue, WaitingDeathSerial = dungeon.DeathSerial };
    });

    public bool CanFinishDungeonAltarDeath(DungeonAltarReceipt scheduled) => owner.Execute(() =>
        IsCurrentAltar(scheduled) && scheduled.WaitingDeathSerial is { } serial &&
        formalDungeon is { CharacterDead: true, AltarSettlement: null, AltarRevivalDue: { } due } dungeon &&
        dungeon.DeathSerial == serial && AltarClock() >= due);

    public DungeonAltarReceipt? ResumeDungeonAltar() => owner.Execute(() =>
    {
        if (!Ready || Selected is null || Phase != SessionPhase.Dungeon ||
            formalDungeon is not { CharacterDead: false, AltarSettlement: null } dungeon ||
            dungeon.Run.CurrentAltar is not { Started: true } state) return null;
        dungeon.AltarRevivalDue = null;
        return AltarReceipt(state, AltarClock());
    });

    public bool PrepareDungeonAltarUi(FormalDungeonReceipt scene) => owner.Execute(() =>
    {
        if (!CanPublishFormalDungeon(scene) || Phase is not (SessionPhase.Loading or SessionPhase.Dungeon) ||
            formalDungeon?.Run.CurrentAltar is not { } state) return false;
        state.PrepareUi();
        return true;
    });

    public DungeonAltarReceipt? StartDungeonAltar(FormalDungeonReceipt loaded) => owner.Execute(() =>
    {
        if (!CanPublishFormalDungeon(loaded) || Phase != SessionPhase.Dungeon ||
            formalDungeon?.Run.CurrentAltar is not { UiPrepared: true } state || state.Started) return null;
        long now = AltarClock();
        if (!state.Begin(now, state.Plan.Definition.Ultimate ? (byte)1 : (byte)0)) return null;
        return AltarReceipt(state, now) with { RoundStarted = true };
    });

    public DungeonAltarReceipt? TickDungeonAltar(DungeonAltarReceipt scheduled) => owner.Execute(() =>
    {
        if (!IsCurrentAltar(scheduled) || formalDungeon!.Run.HasPendingDeath) return null;
        var state = scheduled.State;
        long now = AltarClock();
        if (formalDungeon.AltarSettlement is { } settlement)
            return TickAltarSettlement(scheduled, settlement, now);
        if (formalDungeon.CharacterDead) return null;
        if (state.WaitingForDifficulty && state.ChoiceDue is { } choiceDue && now >= choiceDue)
        {
            var choices = state.Choices;
            int selected = formalDungeon.Draw(choices.Length);
            if (selected < 0 || selected >= choices.Length) throw new InvalidDataException("极限难度随机索引越界");
            if (!state.Begin(now, choices[selected])) return null;
            return AltarReceipt(state, now) with { RoundStarted = true, DifficultySelected = true };
        }
        return state.TakeDue(now) is { } batch ? AltarReceipt(state, now) with { Batch = batch } :
            state.NextDue is not null || state.ChoiceDue is not null ? AltarReceipt(state, now) : null;
    });

    public DungeonAltarReceipt? SelectDungeonAltarDifficulty(byte difficulty) => owner.Execute(() =>
    {
        if (!Ready || Selected is null || Phase != SessionPhase.Dungeon || formalDungeon is not { } dungeon ||
            dungeon.CharacterDead || dungeon.Run.HasPendingDeath || dungeon.Run.CurrentAltar is not { WaitingForDifficulty: true } state)
            return null;
        long now = AltarClock();
        if (!state.Begin(now, difficulty)) return null;
        return AltarReceipt(state, now) with { RoundStarted = true, DifficultySelected = true };
    });

    public DungeonAltarReceipt? ObserveDungeonAltarDeath(NativeDungeonDeathObservation observation) => owner.Execute(() =>
    {
        // 带参与者的CMD39沿原祭坛账本确认已发布对象，不接入普通经验/掉落事务。
        if (!observation.ChecksumMatches || observation.MonsterKey > ushort.MaxValue)
            return null;
        return ObserveAltarDeaths([checked((ushort)observation.MonsterKey)], observation.BodySha256, acknowledgeDuplicate: true);
    });

    public DungeonAltarReceipt? ObserveDungeonAltarDeaths(ImmutableArray<ushort> keys, string fingerprint) =>
        owner.Execute(() => ObserveAltarDeaths(keys, fingerprint));

    public DungeonAltarReceipt? ObserveDungeonAltarScriptedDeath(NativeDungeonScriptedDeathObservation observation) => owner.Execute(() =>
    {
        // 零参与者也可以是真实祭坛击杀，不转入普通经验/掉落通道。
        if (!observation.ChecksumMatches || observation.MonsterKey > ushort.MaxValue)
            return null;
        return ObserveAltarDeaths([checked((ushort)observation.MonsterKey)], observation.BodySha256, acknowledgeDuplicate: true);
    });

    DungeonAltarReceipt? ObserveAltarDeaths(ImmutableArray<ushort> keys, string fingerprint, bool acknowledgeDuplicate = false)
    {
        if (!Ready || Selected is null || Phase is not (SessionPhase.Dungeon or SessionPhase.Settlement) ||
            formalDungeon is not { } dungeon || dungeon.Run.HasPendingDeath || dungeon.Run.CurrentAltar is not { Started: true } state ||
            keys.IsDefaultOrEmpty || keys.Length > byte.MaxValue || keys.Distinct().Count() != keys.Length ||
            keys.Any(key => !state.WasPublished(key))) return null;
        bool changed = false;
        bool duplicate = false;
        foreach (ushort key in keys)
        {
            var issued = dungeon.Authority.ValidateAndIssueAltar(dungeon.Run, dungeon.Run.Snapshot.Identity, new(key, fingerprint));
            if (issued.Death is null) continue;
            var preparation = dungeon.Run.PrepareConfirmedDeath(issued.Death);
            if (preparation.Outcome == DungeonRunOutcome.Duplicate) { duplicate = true; continue; }
            if (preparation.Outcome != DungeonRunOutcome.Accepted || preparation.Pending is not { } pending) continue;
            // 旧端kill_blood_monster只移除已生成对象；祭坛奖励归专用结算，不伪造普通怪物经验/掉落。
            if (dungeon.Run.ConfirmCommittedDeath(pending).Outcome != DungeonRunOutcome.Accepted)
                throw new InvalidOperationException("祭坛死亡不能确认原实例候选");
            changed = true;
        }
        if (!changed)
            return acknowledgeDuplicate && duplicate ? AltarReceipt(state, AltarClock()) with { Completed = state.Completed } : null;
        long now = AltarClock();
        bool advanced = dungeon.Run.AdvanceAltar(now);
        SetState(dungeon.Run.Phase == SinglePlayerDungeonPhase.Settlement ? SessionPhase.Settlement : SessionPhase.Dungeon);
        if (dungeon.Run.ClearFact is not null)
            dungeon.ClearTimeMilliseconds = checked((uint)now);
        return AltarReceipt(state, now) with { RoundAdvanced = advanced, Completed = state.Completed,
            RoundStarted = advanced && !state.Completed && !state.Plan.Definition.Ultimate };
    }

    public bool CanPublishDungeonAltar(DungeonAltarReceipt receipt) => owner.Execute(() =>
        IsCurrentAltar(receipt) && receipt.State.Round == receipt.Round && receipt.State.Difficulty == receipt.Difficulty &&
        (receipt.Result is null || formalDungeon!.AltarSettlement is { } settlement &&
            ReferenceEquals(settlement.Pending, receipt.Result.Pending) && settlement.Stage == receipt.Result.Stage &&
            ReferenceEquals(Selected, receipt.Result.Commit.Character)));

    bool IsCurrentAltar(DungeonAltarReceipt receipt) => Ready && Selected is not null &&
        Phase is SessionPhase.Dungeon or SessionPhase.Settlement && owner.Generation == receipt.Generation &&
        SelectionEpoch == receipt.SelectionEpoch && formalDungeon is { } dungeon &&
        dungeon.Run.Snapshot.Identity.InstanceId == receipt.InstanceId && dungeon.Run.Snapshot.CurrentMap == receipt.Map &&
        ReferenceEquals(dungeon.Run.CurrentAltar, receipt.State);

    long AltarClock() => (long)(timeProvider ?? TimeProvider.System).GetElapsedTime(formalDungeon!.AltarStartedTimestamp).TotalMilliseconds;

    DungeonAltarReceipt AltarReceipt(DungeonAltarState state, long now) => new(owner.Generation, SelectionEpoch,
        formalDungeon!.Run.Snapshot.Identity.InstanceId, formalDungeon.Run.Snapshot.CurrentMap, state,
        state.Round, state.Difficulty, state.NextDue ?? state.ChoiceDue, now)
        { DisplayRound = formalDungeon.Run.CompletedAltarRounds + state.Round };
}
