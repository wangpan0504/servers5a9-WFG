using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.OnlineEvents;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IPcRoomTimePointStore
{
    public PcRoomTimePointSnapshot Read(AccountOwners.Lease owner, int day, PcRoomTimePointCatalog catalog) => owner.Execute(() =>
    {
        ArgumentNullException.ThrowIfNull(catalog);
        PcRoomTimePointDay.Validate(day);
        using var db = database.Open();
        using var tx = db.BeginRead();
        return ReadPcRoomTimePoint(db, tx, owner.Account, day);
    });

    public PcRoomTimePointCommit Update(AccountOwners.Lease owner, Character expected, Guid segmentId,
        IReadOnlyList<PcRoomTimePointElapsed> segmentTotals, DateTimeOffset now, PcRoomTimePointCatalog catalog,
        PcRoomTimePointClaim? claim = null, ImmutableArray<InventoryItem> attachments = default) => owner.Execute(() =>
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(segmentTotals);
        int day = PcRoomTimePointDay.At(now);
        if (segmentId == Guid.Empty || now.ToUnixTimeSeconds() < 0 ||
            segmentTotals.Select(row => row.Day).Distinct().Count() != segmentTotals.Count)
            throw new InvalidDataException("累计在线段或日水位无效");
        foreach (var row in segmentTotals)
        {
            PcRoomTimePointDay.Validate(row.Day);
            if (row.Milliseconds < 0 || row.Day > day)
                throw new InvalidDataException("累计在线日水位无效");
        }
        attachments = attachments.IsDefault ? [] : attachments;
        if (claim is null && !attachments.IsEmpty || claim is not null && (claim.Kind > 1 || claim.Index > 3))
            throw new InvalidDataException("累计在线领奖请求无效");
        using var db = database.Open();
        // 关闭活动、相同水位与重复领取先走只读；需要变更时在 writer 内完整重读。
        for (bool writing = false; ; writing = true)
        {
            using var write = writing ? db.BeginWrite("CharacterRepository.PcRoomTimePoint") : null;
            using var tx = write?.Transaction ?? db.BeginRead();
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "SELECT revision FROM characters WHERE id=$id AND account=$account AND name=$name";
            command.Parameters.AddWithValue("$id", expected.Id);
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$name", expected.Name);
            if (command.ExecuteScalar() is not long revision || revision != expected.Revision)
                throw new InvalidOperationException("累计在线角色快照已失效");
            bool pendingProgress = false;
            foreach (var row in segmentTotals)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$account", owner.Account);
                command.Parameters.AddWithValue("$segment", segmentId.ToString("N"));
                command.Parameters.AddWithValue("$day", row.Day);
                command.CommandText = "SELECT total_ms FROM account_pcroom_timepoint_segments WHERE account=$account AND segment_id=$segment AND day=$day";
                long previous = command.ExecuteScalar() is long value ? value : 0;
                if (row.Milliseconds <= previous) continue;
                if (!writing) { pendingProgress = true; break; }
                long delta = checked(row.Milliseconds - previous);
                command.Parameters.AddWithValue("$total", row.Milliseconds);
                command.CommandText = """
                    INSERT INTO account_pcroom_timepoint_segments(account,segment_id,day,total_ms) VALUES($account,$segment,$day,$total)
                    ON CONFLICT(account,segment_id,day) DO UPDATE SET total_ms=excluded.total_ms;
                    """;
                command.ExecuteNonQuery();
                command.Parameters.AddWithValue("$delta", delta);
                command.Parameters.AddWithValue("$threshold", catalog.Daily[^1].RequiredMilliseconds);
                command.CommandText = """
                    INSERT INTO account_pcroom_timepoint_days(account,day,elapsed_ms,qualified)
                    VALUES($account,$day,$delta,CASE WHEN $delta >= $threshold THEN 1 ELSE 0 END)
                    ON CONFLICT(account,day) DO UPDATE SET
                      elapsed_ms=account_pcroom_timepoint_days.elapsed_ms+excluded.elapsed_ms,
                      qualified=CASE WHEN account_pcroom_timepoint_days.qualified=1 OR
                        account_pcroom_timepoint_days.elapsed_ms+excluded.elapsed_ms >= $threshold THEN 1 ELSE 0 END;
                    """;
                command.ExecuteNonQuery();
            }
            if (pendingProgress) continue;
            var snapshot = ReadPcRoomTimePoint(db, tx, owner.Account, day);
            bool claimed = false, replayed = false;
            uint mailId = 0;
            if (claim is not null)
            {
                int scopeDay = claim.Kind == 0 ? day : 0;
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$account", owner.Account);
                command.Parameters.AddWithValue("$kind", claim.Kind);
                command.Parameters.AddWithValue("$day", scopeDay);
                command.Parameters.AddWithValue("$index", claim.Index);
                command.CommandText = "SELECT mail_id FROM account_pcroom_timepoint_claims WHERE account=$account AND kind=$kind AND day=$day AND reward_index=$index";
                if (command.ExecuteScalar() is long prior)
                {
                    // 邮件领取或删除不删除这条事实；重放不再次校验新资源或重建附件。
                    mailId = checked((uint)prior);
                    replayed = true;
                }
                else
                {
                    int itemId, count;
                    if (claim.Kind == 0)
                    {
                        if (claim.Index >= catalog.Daily.Length) throw new InvalidDataException("累计在线每日档位无效");
                        var reward = catalog.Daily[claim.Index];
                        command.CommandText = "SELECT elapsed_ms FROM account_pcroom_timepoint_days WHERE account=$account AND day=$day";
                        long milliseconds = command.ExecuteScalar() is long elapsed ? elapsed : 0;
                        if (milliseconds < reward.RequiredMilliseconds) throw new PcRoomTimePointClaimRejectedException("累计在线每日时长不足");
                        itemId = reward.ItemId; count = reward.Count;
                    }
                    else
                    {
                        if (claim.Index >= catalog.Period.Length) throw new InvalidDataException("累计在线累计档位无效");
                        var reward = catalog.Period[claim.Index];
                        if (snapshot.QualifiedDays < reward.RequiredDays) throw new PcRoomTimePointClaimRejectedException("累计在线累计天数不足");
                        itemId = reward.ItemId; count = reward.Count;
                    }
                    var schedule = ServerEventRepository.Read(db, tx, ServerEventService.PcRoomTimePointId);
                    if (schedule.Revision != claim.ActivityRevision || !schedule.IsActive(now))
                        throw new PcRoomTimePointClaimRejectedException("累计在线活动已关闭或配置版本已变化");
                    if (attachments.IsEmpty || attachments.Length > 10 ||
                        attachments.Any(item => item.Uid != 0 || item.Core.ItemId != itemId || item.Core.Count <= 0) ||
                        attachments.Sum(item => (long)item.Core.Count) != count)
                        throw new InvalidDataException("累计在线附件与资源奖励不符");
                    if (!writing) continue;
                    string accountKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner.Account)));
                    var delivery = new MailboxDelivery($"online228:{accountKey}:{claim.Kind}:{scopeDay}:{claim.Index}",
                        new(expected.Id, owner.Account, expected.Name, expected.Job, expected.GrowType, expected.Level),
                        "累积在线得好礼", "您领取的活动奖励已通过邮件发放。", 0, attachments, now.ToUnixTimeSeconds());
                    var (error, delivered) = DeliverMailInTransaction(db, tx, delivery);
                    if (error != MailboxError.None) throw new InvalidOperationException("累计在线奖励邮件投递失败：" + error);
                    mailId = delivered;
                    command.Parameters.AddWithValue("$recipient", expected.Id);
                    command.Parameters.AddWithValue("$mail", mailId);
                    command.Parameters.AddWithValue("$item", itemId);
                    command.Parameters.AddWithValue("$count", count);
                    command.Parameters.AddWithValue("$at", now.ToUnixTimeSeconds());
                    command.CommandText = """
                        INSERT INTO account_pcroom_timepoint_claims(account,kind,day,reward_index,recipient_id,mail_id,item_id,item_count,claimed_at)
                        VALUES($account,$kind,$day,$index,$recipient,$mail,$item,$count,$at);
                        """;
                    command.ExecuteNonQuery();
                    claimed = true;
                    snapshot = ReadPcRoomTimePoint(db, tx, owner.Account, day);
                }
            }
            tx.Commit();
            return new PcRoomTimePointCommit(snapshot, claimed, replayed, mailId);
        }
    });

    static PcRoomTimePointSnapshot ReadPcRoomTimePoint(SqliteConnection db, SqliteTransaction tx, string account, int day)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$day", day);
        command.CommandText = "SELECT elapsed_ms FROM account_pcroom_timepoint_days WHERE account=$account AND day=$day";
        long milliseconds = command.ExecuteScalar() is long elapsed ? elapsed : 0;
        command.CommandText = "SELECT count(*) FROM account_pcroom_timepoint_days WHERE account=$account AND qualified=1";
        uint qualifiedDays = checked((uint)(long)command.ExecuteScalar()!);
        command.CommandText = "SELECT kind,reward_index FROM account_pcroom_timepoint_claims WHERE account=$account AND ((kind=0 AND day=$day) OR kind=1)";
        byte daily = 0; uint period = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            int index = reader.GetInt32(1);
            if (reader.GetInt32(0) == 0) daily |= checked((byte)(1 << index));
            else period |= 1u << index;
        }
        return new(day, (uint)Math.Min(milliseconds / 1000, uint.MaxValue), daily, qualifiedDays, period);
    }
}
