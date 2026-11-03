using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.OnlineEvents;

namespace DfoServer.Game.Events;

internal sealed record PcRoomTimePointSnapshot(int Day, uint Seconds, byte DailyClaims, uint QualifiedDays, uint PeriodClaims);
internal sealed record PcRoomTimePointElapsed(int Day, long Milliseconds);
internal sealed record PcRoomTimePointClaim(byte Kind, byte Index, long ActivityRevision);
internal sealed record PcRoomTimePointCommit(PcRoomTimePointSnapshot Snapshot, bool Claimed, bool Replayed, uint MailId);
internal sealed class PcRoomTimePointClaimRejectedException(string message) : InvalidOperationException(message) { }

internal interface IPcRoomTimePointStore
{
    PcRoomTimePointSnapshot Read(AccountOwners.Lease owner, int day, PcRoomTimePointCatalog catalog);
    PcRoomTimePointCommit Update(AccountOwners.Lease owner, Character expected, Guid segmentId,
        IReadOnlyList<PcRoomTimePointElapsed> segmentTotals, DateTimeOffset now, PcRoomTimePointCatalog catalog,
        PcRoomTimePointClaim? claim = null, ImmutableArray<InventoryItem> attachments = default);
}

internal static class PcRoomTimePointDay
{
    public static int At(DateTimeOffset now)
    {
        var date = now.ToOffset(TimeSpan.FromHours(8)).AddHours(-6);
        return date.Year * 10000 + date.Month * 100 + date.Day;
    }

    public static DateTimeOffset NextBoundary(DateTimeOffset now)
    {
        var local = now.ToOffset(TimeSpan.FromHours(8));
        var boundary = new DateTimeOffset(local.Year, local.Month, local.Day, 6, 0, 0, local.Offset);
        return boundary <= now ? boundary.AddDays(1) : boundary;
    }

    internal static void Validate(int day)
    {
        try { _ = new DateOnly(day / 10000, day / 100 % 100, day % 100); }
        catch (ArgumentOutOfRangeException error) { throw new InvalidDataException("累计在线服务日无效", error); }
    }
}
