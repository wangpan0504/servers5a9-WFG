using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.TagEvildom;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using DfoServer.Game.Mailbox;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : ITagEvildomClearStore
{
    public TagEvildomClearCommit CommitTagEvildomClear(AccountOwners.Lease owner, TagEvildomClearPlan plan) => owner.Execute(() =>
    {
        plan.Validate();
        var fact = plan.Fact;
        var admission = fact.Admission;
        string instance = fact.Identity.InstanceId.ToString("N");
        string content = JsonSerializer.Serialize(new { plan.Reward, plan.Items });
        using var db = database.Open();
        // 零变化历史回放只读，不申请全库writer；首次写入仍在writer事务内复验同一准入/回执。
        using (var read = db.BeginRead())
        {
            var restored = ReadTagEvildomClearReceipt(db, read, owner.Account, plan, content);
            read.Commit();
            if (restored is { } mail) return new TagEvildomClearCommit(plan, mail, true);
        }
        using var write = db.BeginWrite("CharacterRepository.CommitTagEvildomClear");
        using var tx = write.Transaction;
        if (ReadTagEvildomClearReceipt(db, tx, owner.Account, plan, content) is { } completed)
        {
            tx.Commit();
            return new TagEvildomClearCommit(plan, completed, true);
        }
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$instance", instance);
        if (!plan.Run.IsPendingDeath(plan.Death) || plan.Run.TagEvildomLife is not { Defeated: false, Cancelled: false } ||
            ReadDungeonState(db, admission.Primary.Id, tx)?.LastAttempt != fact.Identity.TaskAttempt)
            throw new InvalidOperationException("再战首次结算必须来自当前尝试的真实终点准备");
        var configuration = ReadTagEvildomConfiguration(db, tx, owner.Account);
        if (configuration.Teams[admission.TeamIndex] != admission.Team)
            throw new InvalidOperationException("再战固定队伍已被实质更换");
        // 只固定本队身份；其它队的配置revision刷新不阻断已经合法入场的Run。
        command.Parameters.AddWithValue("$first", admission.Primary.Id);
        command.Parameters.AddWithValue("$second", admission.Standby.Id);
        command.CommandText = """
            SELECT count(*) FROM characters c WHERE c.account=$account AND c.id IN($first,$second) AND c.level>=95
              AND NOT EXISTS(SELECT 1 FROM account_pvp_private_characters p WHERE p.character_id=c.id)
            """;
        if (command.ExecuteScalar() is not 2L) throw new InvalidOperationException("再战队首或备用的账号归属/资格已变化");
        var period = plan.Period;
        var state = ReadTagEvildomState(db, tx, owner.Account, period);
        if (state.UsedThisWeek >= 3 || state.ProtectedMembers.Contains(admission.Primary.Id) || state.ProtectedMembers.Contains(admission.Standby.Id))
            throw new InvalidOperationException("再战真实通关次数或当日保护已变化");
        command.CommandText = "UPDATE account_tag_evildom_challenges SET state=2 WHERE account=$account AND instance_id=$instance AND state=0";
        command.ExecuteNonQuery();
        var recipient = FindMailRecipientById(db, tx, admission.Primary.Id);
        if (recipient is null || recipient.Account != owner.Account) throw new InvalidOperationException("再战固定队首收件人不存在");
        command.Parameters.AddWithValue("$day", period.Day);
        command.Parameters.AddWithValue("$week", period.Week);
        command.CommandText = """
            INSERT INTO account_tag_evildom_clearances(account,receipt,day,week,first,second)
            VALUES($account,$instance,$day,$week,$first,$second)
            """;
        command.ExecuteNonQuery();
        var delivery = new MailboxDelivery("tag-evildom:" + instance, recipient, "再战魔界",
            "本队通关奖励，请领取附件。", 0, plan.Items, plan.Created);
        var (error, mailId) = DeliverMailInTransaction(db, tx, delivery);
        if (error != MailboxError.None || mailId == 0) throw new InvalidOperationException("再战通关邮件投递失败: " + error);
        command.Parameters.AddWithValue("$event", fact.EventId.ToString("N"));
        command.Parameters.AddWithValue("$reward", content);
        command.Parameters.AddWithValue("$cleared", plan.Created);
        command.Parameters.AddWithValue("$elapsed", plan.ElapsedMilliseconds);
        command.Parameters.AddWithValue("$mail", mailId);
        command.CommandText = """
            UPDATE account_tag_evildom_runs SET clearance_receipt=$instance,clear_event=$event,reward_json=$reward,
              cleared_at=$cleared,elapsed_ms=$elapsed,mail_id=$mail
            WHERE account=$account AND instance_id=$instance AND clearance_receipt IS NULL
            """;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("再战通关回执并发冲突");
        ApplyBattlePassTagEvildomCompletion(db, tx, owner.Account, plan);
        tx.Commit();
        return new TagEvildomClearCommit(plan, mailId, false);
    });

    static uint? ReadTagEvildomClearReceipt(SqliteConnection db, SqliteTransaction tx, string account,
        TagEvildomClearPlan plan, string content)
    {
        var fact = plan.Fact;
        var admission = fact.Admission;
        string instance = fact.Identity.InstanceId.ToString("N");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$instance", instance);
        command.CommandText = """
            SELECT first,second,task_attempt,configuration_revision,team_index,dungeon_id,resource_key,admitted_at,
              clearance_receipt,clear_event,reward_json,cleared_at,elapsed_ms,mail_id
            FROM account_tag_evildom_runs WHERE account=$account AND instance_id=$instance
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) != admission.Primary.Id || reader.GetInt32(1) != admission.Standby.Id ||
            reader.GetInt64(2) != fact.Identity.TaskAttempt || reader.GetInt64(3) != admission.Configuration.Revision ||
            reader.GetInt32(4) != admission.TeamIndex || reader.GetInt32(5) != admission.DungeonId ||
            reader.GetString(6) != admission.ResourceKey || reader.GetInt64(7) != admission.AdmittedAt)
            throw new InvalidOperationException("再战通关没有匹配的已提交固定准入");
        if (reader.IsDBNull(8)) return null;
        // 自己的clearance、后续换角/删信不能令重试重新发奖或再次抽样。
        if (reader.GetString(8) != instance || reader.GetString(9) != fact.EventId.ToString("N") ||
            reader.GetString(10) != content || reader.GetInt64(11) != plan.Created || reader.GetInt64(12) != plan.ElapsedMilliseconds)
            throw new InvalidDataException("再战已提交回执的终点、队首或冻结奖励发生变化");
        return checked((uint)reader.GetInt64(13));
    }

    static void CommitTagEvildomAdmission(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        Character expected, DungeonEntryCharge charge, long reservedRevision)
    {
        if (charge.TagEvildom is not { } admission) return;
        admission.Validate();
        if (!ReferenceEquals(admission.Primary, expected) || charge.DungeonId != admission.DungeonId ||
            charge.Identity.CharacterId != expected.Id || charge.Fatigue != 0 || charge.Items is not null ||
            charge.Grief is not null || charge.Hope is not null || charge.Special is not null || charge.Incount is not null || charge.Weekly is not null)
            throw new InvalidDataException("再战准入不能混用其它副本身份、费用或次数");
        RequireTagEvildomConfiguration(ReadTagEvildomConfiguration(db, tx, owner.Account), admission.Configuration);
        // 主角使用本事务付款并预留后的实际版本；备用仍匹配原始只读快照。
        RequireTagEvildomMember(db, tx, owner.Account, admission.Primary, reservedRevision);
        RequireTagEvildomMember(db, tx, owner.Account, admission.Standby, admission.Standby.Revision);
        var state = ReadTagEvildomState(db, tx, owner.Account,
            TagEvildomPeriod.At(DateTimeOffset.FromUnixTimeSeconds(admission.AdmittedAt)));
        if (state.UsedThisWeek >= 3 || state.ProtectedMembers.Contains(admission.Primary.Id) || state.ProtectedMembers.Contains(admission.Standby.Id))
            throw new InvalidOperationException("再战已通关额度或角色当天资格不足");
        CommitTagEvildomChallenge(db, tx, owner, admission, charge.Identity.InstanceId);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$account", owner.Account);
        // 每账号仅保留当前未完成准入；跨日未完成次数另由挑战记录保留，历史已通关receipt永不裁剪。
        command.CommandText = "DELETE FROM account_tag_evildom_runs WHERE account=$account AND clearance_receipt IS NULL";
        command.ExecuteNonQuery();
        command.Parameters.AddWithValue("$instance", charge.Identity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$first", admission.Primary.Id);
        command.Parameters.AddWithValue("$second", admission.Standby.Id);
        command.Parameters.AddWithValue("$attempt", charge.Identity.TaskAttempt);
        command.Parameters.AddWithValue("$configuration", admission.Configuration.Revision);
        command.Parameters.AddWithValue("$team", admission.TeamIndex);
        command.Parameters.AddWithValue("$dungeon", admission.DungeonId);
        command.Parameters.AddWithValue("$resource", admission.ResourceKey);
        command.Parameters.AddWithValue("$at", admission.AdmittedAt);
        command.CommandText = """
            INSERT INTO account_tag_evildom_runs(account,instance_id,first,second,task_attempt,configuration_revision,team_index,dungeon_id,resource_key,admitted_at)
            VALUES($account,$instance,$first,$second,$attempt,$configuration,$team,$dungeon,$resource,$at)
            """;
        command.ExecuteNonQuery();
    }

    static void RequireTagEvildomMember(SqliteConnection db, SqliteTransaction tx, string account, Character expected, long revision)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT count(*) FROM characters c WHERE c.account=$account AND c.id=$id AND c.revision=$revision
              AND c.name=$name AND c.level=$level AND c.level>=95 AND c.job=$job AND c.grow_type=$grow
              AND NOT EXISTS(SELECT 1 FROM account_pvp_private_characters p WHERE p.character_id=c.id)
            """;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$name", expected.Name);
        command.Parameters.AddWithValue("$level", expected.Level);
        command.Parameters.AddWithValue("$job", expected.Job);
        command.Parameters.AddWithValue("$grow", expected.GrowType);
        if (command.ExecuteScalar() is not 1L) throw new InvalidOperationException("再战参与者账号、资料或资格已变化");
    }
}
