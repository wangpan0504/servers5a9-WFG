using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;

namespace DfoServer.Game.Dungeons;

internal sealed record EvildomWarCheckpoint(EvildomWarView View, EvildomWarCompletion? Completion = null)
{
    public EvildomWarCompletion? HiddenCompletion { get; init; }
    public void Validate()
    {
        if (View.BattleId == Guid.Empty || View.Revision < 0 || View.Mode > 2 || View.Deadline.ToUnixTimeSeconds() < 0 ||
            View.StartedAt is { } started && (started.ToUnixTimeSeconds() < 0 || started >= View.Deadline) ||
            View.Dungeons.IsDefault || View.Dungeons.Length != 5 || View.Dungeons.Select(row => row.Id).Distinct().Count() != 5 ||
            View.Dungeons.Any(row => row.Id <= 0 || row.Kind > 1 || row.State > 3 || row.Attribute > 3) ||
            View.Dungeons.Count(row => row.Kind == 0) != 4 || View.Dungeons.Count(row => row.Kind == 1) != 1 ||
            View.Dungeons.Count(row => row.State == 1) > 1 ||
            View.Dungeons.Any(row => row.Kind == 0 && row.State == 3) ||
            View.Dungeons.Where(row => row.Kind == 0).Select(row => row.Attribute).Distinct().Count() != 4 ||
            View.Dungeons.Any(row => row.Kind == 1 && row.State != 3) && !View.SealsCleared ||
            View.Completed != (Completion is not null))
            throw new InvalidDataException("魔界检查点结构或完成态无效");
        if (View.Hidden is { } hidden && (hidden.Dungeon == 0 || hidden.Dungeon > int.MaxValue ||
            View.Dungeons.Any(row => row.Id == hidden.Dungeon) || !View.Dungeons.Any(row => row.Id == hidden.StageDungeon && row.Kind == 0)))
            throw new InvalidDataException("魔界隐藏路线不是本场前置图的独立替代副本");
        if (Completion is { } completed && (completed.BattleId != View.BattleId || completed.Mode != View.Mode ||
            completed.ElapsedMilliseconds != EvildomWarCompletion.MeasureElapsed(View.StartedAt, completed.Created) ||
            completed.Created >= View.Deadline || View.Dungeons.Any(row => row.State == 1) || completed.Facts.IsDefaultOrEmpty ||
            completed.Facts.Length > 4 || completed.Facts.Select(fact => fact.Identity.CharacterId).Distinct().Count() != completed.Facts.Length ||
            completed.Facts.Any(fact => fact.DungeonId != View.Dungeons.Single(row => row.Kind == 1).Id ||
                fact.Identity.InstanceId == Guid.Empty || fact.Identity.TaskAttempt <= 0 || fact.ConfirmedDeathEvents.Count == 0)))
            throw new InvalidDataException("魔界完成检查点缺少原王座事实");
        if (HiddenCompletion is { } laboratory && (View.Hidden is not { } route ||
            !View.Dungeons.Any(row => row.Id == route.StageDungeon && row.State == 2) ||
            laboratory.BattleId != View.BattleId || laboratory.Mode != View.Mode || laboratory.Created >= View.Deadline ||
            laboratory.ElapsedMilliseconds != EvildomWarCompletion.MeasureElapsed(View.StartedAt, laboratory.Created) ||
            laboratory.Created.ToUnixTimeSeconds() < 0 || laboratory.Facts.IsDefaultOrEmpty || laboratory.Facts.Length > 4 ||
            laboratory.Facts.Select(fact => fact.Identity.CharacterId).Distinct().Count() != laboratory.Facts.Length ||
            laboratory.Facts.Any(fact => fact.DungeonId != route.Dungeon || fact.Identity.InstanceId == Guid.Empty ||
                fact.Identity.TaskAttempt <= 0 || fact.ConfirmedDeathEvents.Count == 0 ||
                fact.ConfirmedDeathEvents.Distinct().Count() != fact.ConfirmedDeathEvents.Count)))
            throw new InvalidDataException("魔界实验室检查点缺少真实物件终点事实");
    }
}
internal sealed record EvildomWarSeatEntry(EvildomWarView Before, Guid BindingToken, byte Slot, int BusinessDay, bool FirstPayment);
internal sealed record EvildomWarPayment(DungeonRunIdentity FirstRun, byte Slot, int BusinessDay,
    int CountDungeon, DungeonIncountPeriod CountPeriod, WeeklyDungeonEntry? Weekly, ushort RemainingConsumed, ushort UsedAdded);
internal sealed record EvildomWarRecovery(EvildomWarCheckpoint Checkpoint, EvildomWarPayment Payment,
    Guid BindingToken, DateTimeOffset? ReconnectUntil);
internal sealed record EvildomWarRefund(Character Character, int Seats);

// 席位及支付凭据由首次全员入场事务注册，恢复/补偿只能由本角色当前owner消费。
internal interface IEvildomWarStore
{
    EvildomWarRecovery? LoadEvildomWarRecovery(AccountOwners.Lease owner, ushort characterId, DateTimeOffset now);
    bool ClaimEvildomWarSeat(AccountOwners.Lease owner, ushort characterId, EvildomWarRecovery recovery, Guid bindingToken, DateTimeOffset now);
    void DisconnectEvildomWarSeat(AccountOwners.Lease owner, ushort characterId, Guid battleId, Guid bindingToken, DateTimeOffset until);
    void CommitEvildomWarCheckpoint(AccountOwners.Lease owner, ushort characterId, Guid bindingToken,
        EvildomWarView before, EvildomWarCheckpoint after);
    EvildomWarRefund RefundExpiredEvildomWarSeats(AccountOwners.Lease owner, Character expected, DateTimeOffset now);
}
