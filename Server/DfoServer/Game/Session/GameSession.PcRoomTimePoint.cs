using System.Collections.Immutable;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed record PcRoomTimePointUse(long Epoch, ushort CharacterId, PcRoomTimePointSnapshot Snapshot,
    bool Enabled, bool Changed, bool Claimed, bool Replayed, uint MailId, long ActivityRevision,
    DateTimeOffset At, DateTimeOffset NextWake);

internal sealed partial class GameSession
{
    PcRoomTimePointService? pcRoomService = pcRoomTimePoint;
    PcRoomTimePointClock? pcRoomClock;
    PcRoomTimePointSnapshot? pcRoomSnapshot;
    bool pcRoomPublishedEnabled, pcRoomFinalCheckpoint;
    Action? pcRoomReplacementCheckpoint;

    void StartPcRoomTimePoint()
    {
        if (pcRoomService is null || pcRoomClock is not null || Selected is null) return;
        pcRoomClock = pcRoomService.StartClock();
        pcRoomFinalCheckpoint = false;
        pcRoomReplacementCheckpoint ??= CheckpointPcRoomTimePoint;
        owner.BeforeReplacement = pcRoomReplacementCheckpoint;
    }

    public PcRoomTimePointUse? RefreshPcRoomTimePoint(PcRoomTimePointService service) =>
        UsePcRoomTimePoint(service, 0, 0xff);

    public PcRoomTimePointUse? UsePcRoomTimePoint(PcRoomTimePointService service, byte type, byte index)
    {
        var captured = owner.Execute(() =>
        {
            if (!CanAdvanceServiceDay()) return null;
            if (pcRoomService is not null && !ReferenceEquals(pcRoomService, service))
                throw new InvalidOperationException("累计在线服务实例已变化");
            pcRoomService = service;
            StartPcRoomTimePoint();
            return new PcRoomCapture(Selected!, SelectionEpoch, pcRoomClock!, pcRoomClock!.Capture());
        });
        if (captured is null) return null;
        var previous = service.Read(owner, captured.Reading.At);
        PcRoomTimePointClaim? claim = Claim(type, index, captured.Reading.ActivityRevision);
        ImmutableArray<InventoryItem> attachments = default;
        bool replay = claim is not null && (claim.Kind == 0
            ? (previous.DailyClaims & (1 << claim.Index)) != 0
            : (previous.PeriodClaims & (1u << claim.Index)) != 0);
        if (claim is not null && !replay)
        {
            if (!captured.Reading.Enabled) claim = null;
            else
            {
                try { attachments = service.Prepare(claim, captured.Reading.At); }
                catch (PcRoomTimePointClaimRejectedException) { claim = null; }
            }
        }
        return owner.Execute(() =>
        {
            if (!ReferenceEquals(Selected, captured.Character) || SelectionEpoch != captured.Epoch ||
                !ReferenceEquals(pcRoomClock, captured.Clock) || !CanAdvanceServiceDay()) return null;
            var reading = captured.Clock.Capture();
            PcRoomTimePointCommit committed;
            try { committed = service.Commit(owner, captured.Character, reading, claim, attachments); }
            catch (PcRoomTimePointClaimRejectedException)
            {
                committed = service.Commit(owner, captured.Character, reading);
            }
            bool changed = pcRoomSnapshot != committed.Snapshot || pcRoomPublishedEnabled != reading.Enabled;
            pcRoomSnapshot = committed.Snapshot;
            pcRoomPublishedEnabled = reading.Enabled;
            return new PcRoomTimePointUse(captured.Epoch, captured.Character.Id, committed.Snapshot,
                reading.Enabled, changed, committed.Claimed, committed.Replayed, committed.MailId,
                reading.ActivityRevision, reading.At, service.NextWake(reading, committed.Snapshot));
        });
    }

    static PcRoomTimePointClaim? Claim(byte type, byte index, long revision)
    {
        if (type == 0 && index == 0xff) return null;
        if (index == 0xff && type is 1 or 2 or 4 or 8)
            return new(0, type switch { 1 => 0, 2 => 1, 4 => 2, _ => 3 }, revision);
        if (type == 0x10 && index <= 4) return new(1, index, revision);
        throw new InvalidDataException("累计在线请求选择无效");
    }

    public bool CanPublishPcRoomTimePoint(PcRoomTimePointUse use)
    {
        if (!owner.IsCurrent) return false;
        try
        {
            return owner.Execute(() =>
            {
                if (!Ready || SelectionEpoch != use.Epoch || Selected?.Id != use.CharacterId || pcRoomClock is null) return false;
                var reading = pcRoomClock.Capture();
                return reading.ActivityRevision == use.ActivityRevision && reading.Enabled == use.Enabled;
            });
        }
        catch (InvalidOperationException error) when (error.Message == "Stale account generation") { return false; }
    }

    // 断线最终检查点由网络层做有界忙锁重试；成功时刻即离线截止，End不再提交其后的尾部。
    public void FlushPcRoomTimePoint()
    {
        if (!owner.IsCurrent) return;
        owner.Execute(() =>
        {
            if (!pcRoomFinalCheckpoint && pcRoomClock is not null && Selected is { } character && pcRoomService is { } service)
                service.Commit(owner, character, pcRoomClock.Capture());
            pcRoomFinalCheckpoint = true;
            return true;
        });
    }

    // 接管时旧lease尚有效，检查点成功才允许替换；失败保留原段水位和当前角色供原请求重试。
    void CheckpointPcRoomTimePoint()
    {
        if (pcRoomClock is null) return;
        if (!pcRoomFinalCheckpoint && owner.IsCurrent && Selected is { } character && pcRoomService is { } service)
            service.Commit(owner, character, pcRoomClock.Capture());
        ReleasePcRoomTimePoint();
    }

    void ReleasePcRoomTimePoint()
    {
        pcRoomClock?.Dispose();
        pcRoomClock = null;
        pcRoomSnapshot = null;
        if (ReferenceEquals(owner.BeforeReplacement, pcRoomReplacementCheckpoint)) owner.BeforeReplacement = null;
    }

    sealed record PcRoomCapture(Game.Characters.Character Character, long Epoch,
        PcRoomTimePointClock Clock, PcRoomTimePointClockReading Reading);
}
