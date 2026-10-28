using System.Text.Json;
using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.ImperialArena;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Inventory.Models;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IImperialArenaRunStore
{
    static void CommitImperialArenaAdmission(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        Character expected, DungeonEntryCharge charge)
    {
        if (charge.ImperialArena is not { } admission) return;
        admission.Validate();
        if (!ReferenceEquals(admission.Primary, expected) || charge.DungeonId != admission.DungeonId ||
            charge.Identity.CharacterId != expected.Id || charge.Items is not null || charge.Fatigue != 0 ||
            charge.Weekly is not null || charge.TagEvildom is not null || charge.Hope is not null || charge.Grief is not null ||
            charge.Special is not null || charge.Incount is not null || charge.Dazzlement is not null || charge.Marriage is not null)
            throw new InvalidDataException("帝国竞技场不能混用其它副本费用或准入");
        var period = admission.Route.Period;
        var state = ReadImperialArenaState(db, tx, owner.Account, period);
        if (state.Configuration != admission.Configuration || state.PaidMode != admission.Mode)
            throw new InvalidOperationException("帝国竞技场本日券或编队已变化");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$first", admission.Primary.Id);
        command.Parameters.AddWithValue("$second", admission.Standby.Id);
        command.Parameters.AddWithValue("$primary", checked(expected.Revision + 1));
        command.Parameters.AddWithValue("$standby", admission.Standby.Revision);
        command.CommandText = """
            SELECT count(*) FROM characters c WHERE account=$account AND level>=90 AND
              ((id=$first AND revision=$primary) OR (id=$second AND revision=$standby))
              AND NOT EXISTS(SELECT 1 FROM account_pvp_private_characters p WHERE p.character_id=c.id)
            """;
        if (command.ExecuteScalar() is not 2L) throw new InvalidOperationException("帝国竞技场双角色身份或版本已变化");
        command.Parameters.AddWithValue("$instance", charge.Identity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$day", period.Day);
        command.Parameters.AddWithValue("$week", period.Week);
        command.Parameters.AddWithValue("$mode", (int)admission.Mode);
        command.Parameters.AddWithValue("$configuration", admission.Configuration.Revision);
        command.Parameters.AddWithValue("$attempt", charge.Identity.TaskAttempt);
        command.Parameters.AddWithValue("$admitted", admission.AdmittedAt);
        command.Parameters.AddWithValue("$minimum", admission.MinimumHardClearMilliseconds);
        command.Parameters.AddWithValue("$team", JsonSerializer.Serialize(new int[] { admission.Primary.Job, admission.Standby.Job,
            admission.Primary.GrowType, admission.Standby.GrowType }));
        command.Parameters.AddWithValue("$route", JsonSerializer.Serialize(admission.Route));
        command.CommandText = """
            INSERT INTO account_imperial_arena_runs(account,instance_id,day,week,mode,first,second,configuration_revision,
              task_attempt,admitted_at,minimum_clear_ms,team_json,route_json)
            VALUES($account,$instance,$day,$week,$mode,$first,$second,$configuration,$attempt,$admitted,$minimum,$team,$route)
            """;
        command.ExecuteNonQuery();
    }

    public ImperialArenaDeathCommit CommitImperialArenaDeath(AccountOwners.Lease owner, ImperialArenaDeathPlan plan) => owner.Execute(() =>
    {
        plan.Validate();
        var admission = plan.Admission;
        string instance = plan.Death.Death.Identity.InstanceId.ToString("N");
        string eventId = plan.Death.Death.EventId.ToString("N");
        string content = JsonSerializer.Serialize(new { plan.Round, plan.Reward, plan.Items, plan.Created, plan.ElapsedMilliseconds });
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitImperialArenaDeath");
        using var tx = write.Transaction;
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$instance", instance);
        command.CommandText = """
            SELECT day,week,mode,first,second,task_attempt,configuration_revision,admitted_at,route_json,cleared_round,events_json,closed
            FROM account_imperial_arena_runs WHERE account=$account AND instance_id=$instance
            """;
        int previous;
        using (var row = command.ExecuteReader())
        {
            if (!row.Read() || row.GetInt32(0) != admission.Route.Period.Day || row.GetInt32(1) != admission.Route.Period.Week ||
                row.GetInt32(2) != (int)admission.Mode || row.GetInt32(3) != admission.Primary.Id || row.GetInt32(4) != admission.Standby.Id ||
                row.GetInt64(5) != plan.Death.Death.Identity.TaskAttempt || row.GetInt64(6) != admission.Configuration.Revision ||
                row.GetInt64(7) != admission.AdmittedAt || row.GetString(8) != JsonSerializer.Serialize(admission.Route))
                throw new InvalidOperationException("帝国竞技场击杀缺少匹配的已提交准入");
            previous = row.GetInt32(9);
            using var events = JsonDocument.Parse(row.GetString(10));
            if (previous >= plan.Round)
            {
                var accepted = events.RootElement[plan.Round - 1];
                if (accepted.GetProperty("Event").GetString() != eventId || accepted.GetProperty("Content").GetString() != content)
                    throw new InvalidDataException("帝国竞技场已提交击杀的冻结内容发生变化");
                uint mailId = accepted.GetProperty("Mail").GetUInt32();
                row.Close(); tx.Commit();
                return new ImperialArenaDeathCommit(plan, mailId, true);
            }
            if (row.GetInt32(11) != 0) throw new InvalidOperationException("帝国困难已结束的挑战不能继续计奖");
        }
        if (previous != plan.Round - 1 || !plan.Run.IsPendingDeath(plan.Death) ||
            plan.Run.TagEvildomLife is not { Defeated: false, Cancelled: false } ||
            ReadDungeonState(db, admission.Primary.Id, tx)?.LastAttempt != plan.Death.Death.Identity.TaskAttempt)
            throw new InvalidOperationException("帝国竞技场击杀必须来自当前实例的下一回合");
        var state = ReadImperialArenaState(db, tx, owner.Account, admission.Route.Period);
        if (state.Configuration != admission.Configuration || state.PaidMode != admission.Mode ||
            admission.Mode == ImperialArenaMode.Hard && state.HardRewardRound != plan.PreviousHardRound ||
            state.RewardedMonsters.Contains(plan.MonsterId) && !plan.Reward.IsEmpty)
            throw new InvalidOperationException("帝国竞技场奖励资格在提交前已变化");
        uint delivered = 0;
        if (!plan.Items.IsEmpty && admission.Mode == ImperialArenaMode.Normal)
        {
            var recipient = FindMailRecipientById(db, tx, admission.Primary.Id);
            if (recipient is null || recipient.Account != owner.Account) throw new InvalidOperationException("帝国竞技场固定收件人已不存在");
            var delivery = new MailboxDelivery("imperial-arena:" + instance + ":" + plan.Round, recipient, "帝国竞技场",
                "本次击败对手的奖励，请领取附件。", 0, plan.Items, plan.Created);
            var (error, mail) = DeliverMailInTransaction(db, tx, delivery);
            if (error != MailboxError.None || mail == 0) throw new InvalidOperationException("帝国竞技场奖励邮件投递失败: " + error);
            delivered = mail;
        }
        command.Parameters.AddWithValue("$day", admission.Route.Period.Day);
        command.Parameters.AddWithValue("$monster", plan.MonsterId);
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$reward", content);
        command.Parameters.AddWithValue("$mail", delivered);
        command.CommandText = """
            INSERT INTO account_imperial_arena_rewards(account,day,monster,instance_id,event_id,reward_json,mail_id)
            VALUES($account,$day,$monster,$instance,$event,$reward,$mail) ON CONFLICT(account,day,monster) DO NOTHING
            """;
        command.ExecuteNonQuery();
        command.Parameters.AddWithValue("$round", plan.Round);
        if (admission.Mode == ImperialArenaMode.Hard)
        {
            command.CommandText = "UPDATE account_imperial_arena_days SET hard_round=max(hard_round,$round) WHERE account=$account AND day=$day";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("帝国竞技场本日困难券消失");
        }
        command.Parameters.AddWithValue("$record", JsonSerializer.Serialize(new { Event = eventId, Content = content, Mail = delivered }));
        command.Parameters.AddWithValue("$elapsed", plan.Round == admission.Route.Opponents(admission.Mode).Length ? plan.ElapsedMilliseconds : (object)DBNull.Value);
        command.Parameters.AddWithValue("$previous", previous);
        command.CommandText = """
            UPDATE account_imperial_arena_runs SET cleared_round=$round,events_json=json_insert(events_json,'$[#]',json($record)),elapsed_ms=$elapsed
            WHERE account=$account AND instance_id=$instance AND cleared_round=$previous
            """;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("帝国竞技场回合提交冲突");
        if (admission.Mode == ImperialArenaMode.Hard && plan.Round == admission.Route.Hard.Length)
        {
            var completed = DateTimeOffset.FromUnixTimeSeconds(plan.Created);
            if (ImperialArenaRankingPolicy.Eligible(admission.Route.Period, admission.MinimumHardClearMilliseconds, plan.ElapsedMilliseconds, completed))
            {
                command.Parameters.AddWithValue("$rankedWeek", admission.Route.Period.Week);
                command.CommandText = "UPDATE account_imperial_arena_runs SET ranked_week=$rankedWeek WHERE account=$account AND instance_id=$instance";
                command.ExecuteNonQuery();
            }
            delivered = CompleteImperialArenaRun(db, tx, owner.Account, instance);
            command.Parameters.AddWithValue("$completedMail", delivered);
            command.CommandText = "UPDATE account_imperial_arena_runs SET events_json=json_set(events_json,'$[#-1].Mail',$completedMail) WHERE account=$account AND instance_id=$instance";
            command.ExecuteNonQuery();
        }
        tx.Commit();
        return new ImperialArenaDeathCommit(plan, delivered, false);
    });

    public ImmutableArray<uint> CompleteImperialArenaRuns(AccountOwners.Lease owner, Guid? instance = null) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var query = db.CreateCommand();
        query.Parameters.AddWithValue("$account", owner.Account);
        query.Parameters.AddWithValue("$instance", instance?.ToString("N") ?? (object)DBNull.Value);
        query.CommandText = "SELECT instance_id FROM account_imperial_arena_runs WHERE account=$account AND mode=2 AND closed=0 AND ($instance IS NULL OR instance_id=$instance)";
        var pending = new List<string>();
        using (var reader = query.ExecuteReader()) while (reader.Read()) pending.Add(reader.GetString(0));
        if (pending.Count == 0) return ImmutableArray<uint>.Empty;
        using var write = db.BeginWrite("CharacterRepository.CompleteImperialArenaRuns");
        using var tx = write.Transaction;
        var mails = ImmutableArray.CreateBuilder<uint>();
        foreach (var run in pending)
        {
            uint mail = CompleteImperialArenaRun(db, tx, owner.Account, run);
            if (mail != 0) mails.Add(mail);
        }
        tx.Commit();
        return mails.ToImmutable();
    });

    // 困难每回合先保存完整附件和本日资格；终局一次投递，断线可由后续owner恢复。
    static uint CompleteImperialArenaRun(SqliteConnection db, SqliteTransaction tx, string account, string instance)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$instance", instance);
        command.CommandText = "SELECT first,admitted_at FROM account_imperial_arena_runs WHERE account=$account AND instance_id=$instance AND mode=2 AND closed=0";
        ushort first;
        long created;
        using (var run = command.ExecuteReader())
        {
            if (!run.Read()) return 0;
            first = checked((ushort)run.GetInt32(0)); created = run.GetInt64(1);
        }
        command.CommandText = "SELECT reward_json FROM account_imperial_arena_rewards WHERE account=$account AND instance_id=$instance AND mail_id=0 ORDER BY monster";
        var items = new List<InventoryItem>();
        using (var rewards = command.ExecuteReader()) while (rewards.Read())
        {
            using var content = JsonDocument.Parse(rewards.GetString(0));
            foreach (var element in content.RootElement.GetProperty("Items").EnumerateArray())
            {
                var item = element.Deserialize<InventoryItem>() ?? throw new InvalidDataException("帝国困难冻结附件为空");
                if (item.Uid != 0 || !item.Core.IsStackable || item.Core.Count <= 0)
                    throw new InvalidDataException("帝国困难冻结附件不是合法堆叠奖励");
                int index = items.FindIndex(previous => (previous with { Core = previous.Core with { Value = 1 } }) ==
                    (item with { Core = item.Core with { Value = 1 } }));
                if (index < 0) items.Add(item);
                else items[index] = items[index] with { Core = item.Core with { Value = checked(items[index].Core.Count + item.Core.Count) } };
            }
        }
        uint delivered = 0;
        if (items.Count > 0)
        {
            if (items.Count > 10) throw new InvalidDataException("帝国困难结算超出邮件附件上限");
            var recipient = FindMailRecipientById(db, tx, first);
            if (recipient is null || recipient.Account != account) throw new InvalidOperationException("帝国困难固定收件人已不存在");
            var delivery = new MailboxDelivery("imperial-arena:" + instance + ":hard-final", recipient, "帝国竞技场",
                "本次困难挑战已结束，请领取累计奖励。", 0, items.ToImmutableArray(), created);
            var (error, mail) = DeliverMailInTransaction(db, tx, delivery);
            if (error != MailboxError.None || mail == 0) throw new InvalidOperationException("帝国困难结算邮件投递失败: " + error);
            delivered = mail;
            command.Parameters.AddWithValue("$mail", mail);
            command.CommandText = "UPDATE account_imperial_arena_rewards SET mail_id=$mail WHERE account=$account AND instance_id=$instance AND mail_id=0";
            command.ExecuteNonQuery();
        }
        command.CommandText = "UPDATE account_imperial_arena_runs SET closed=1 WHERE account=$account AND instance_id=$instance AND mode=2 AND closed=0";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("帝国困难结算状态冲突");
        return delivered;
    }
}
