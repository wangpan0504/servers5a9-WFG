using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Session;

internal sealed record BeastRunReceipt(long Generation, long SelectionEpoch, BeastRunProgress Progress);

internal sealed partial class GameSession
{
    internal FormalDungeonDeathResult? ResumePendingBeastDeath() => owner.Execute(() =>
        PartyDungeon is null && Ready ? formalDungeon?.PendingBeastDeath?.Invoke() : null);

    internal BeastRunReceipt? CaptureBeastRun() => owner.Execute(() =>
        Ready && Selected is not null && Phase is SessionPhase.Loading or SessionPhase.Dungeon or SessionPhase.Settlement &&
        formalDungeon?.BeastProgress is { } progress ? new BeastRunReceipt(owner.Generation, SelectionEpoch, progress) : null);

    internal bool CanPublishBeastRun(BeastRunReceipt receipt) => owner.Execute(() => Ready && Selected is not null &&
        receipt.Generation == owner.Generation && receipt.SelectionEpoch == SelectionEpoch &&
        ReferenceEquals(formalDungeon?.BeastProgress, receipt.Progress) &&
        Phase is SessionPhase.Loading or SessionPhase.Dungeon or SessionPhase.Settlement);

    internal bool ConfirmBeastPartyStage(BeastStageScore score) => owner.Execute(() =>
        CanMutatePartyDungeon && formalDungeon?.BeastProgress?.Confirm(score) == true);

    internal bool CanExpireBeastRun() => owner.Execute(() => CanMutatePartyDungeon && Ready && Selected is not null &&
        Phase is SessionPhase.Loading or SessionPhase.Dungeon && formalDungeon is { BeastProgress: not null } dungeon &&
        !dungeon.Run.HasPendingDeath && !dungeon.Run.HasPendingSettlement && !dungeon.Run.HasPendingPassiveClear &&
        !dungeon.Ground.HasPendingOperation && !dungeon.HasPendingFatigue && !HasPendingQuestScene);

    internal bool ExpireBeastRun(BeastRunReceipt receipt) => owner.Execute(() =>
    {
        if (!CanPublishBeastRun(receipt) || !CanExpireBeastRun()) return false;
        if (receipt.Progress.View().Terminal != BeastTerminal.TimedOut && !receipt.Progress.TryExpire()) return false;
        if (!formalDungeon!.Run.ExpireBeast()) throw new InvalidOperationException("魔兽超时不能结束已复验的原Run");
        SetState(SessionPhase.Settlement);
        return true;
    });
}
