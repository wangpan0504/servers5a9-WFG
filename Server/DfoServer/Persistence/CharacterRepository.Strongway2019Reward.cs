using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Strongway2019;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IStrongway2019RewardStore
{
    public Strongway2019RewardState ReadStrongway2019RewardState(AccountOwners.Lease owner,
        Strongway2019RewardPeriod period, Strongway2019RewardBucket bucket) => owner.Execute(() =>
    {
        if (period.Day <= 0 || period.Week <= 0 || bucket is not (Strongway2019RewardBucket.Challenge or Strongway2019RewardBucket.Matching))
            throw new InvalidDataException("首期奖励周期或额度类型无效");
        using var db = database.Open();
        using var tx = db.BeginRead();
        var state = ReadStrongway2019RewardState(db, tx, owner.Account, period, bucket);
        tx.Commit();
        return state;
    });

    public Strongway2019RewardCommit CommitStrongway2019Reward(AccountOwners.Lease owner,
        Strongway2019RewardPlan plan) => owner.Execute(() =>
    {
        var fact = plan.Fact;
        var identity = plan.Identity;
        int dungeon = checked((int)plan.Run.Entry.DungeonId);
        int reward = dungeon switch { 5359 => 490012735, 5363 => 490012736, 5360 => 490012737, _ => 0 };
        if (reward == 0 || plan.Created < 0 || plan.Expected.Id != identity.CharacterId ||
            plan.Item.Uid != 0 || plan.Item.Core.ItemId != reward || !plan.Item.Core.IsStackable || plan.Item.Core.Count != 1 ||
            plan.Item.IsExpired(plan.Created))
            throw new InvalidDataException("首期奖励缺少匹配地下城、角色或完整新附件");
        plan.Item.ValidateDetails(allocating: true);
        Strongway2019Participant? participant = null;
        if (dungeon == 5360)
        {
            if (plan.Competition is not { } competition || plan.Terminal is not { } terminal ||
                !ReferenceEquals(competition.Snapshot.Terminal, terminal))
                throw new InvalidDataException("匹配奖励必须使用原比赛冻结的权威终局");
            participant = competition.Participants.SingleOrDefault(member => ReferenceEquals(member.Run, plan.Run));
            if (participant is null || !SameStrongway2019Attempt(participant.Identity, identity) ||
                fact is not null && (!ReferenceEquals(plan.Run.ClearFact, fact) || fact.Identity != identity))
                throw new InvalidDataException("匹配奖励成员或个人终点不是原固定Run事实");
            if (terminal.Reason == Strongway2019FinishReason.Timeout || terminal.WinnerSide != participant.Side)
                return new Strongway2019RewardCommit(0, false, false);
            if (terminal.Reason is not (Strongway2019FinishReason.Clear or Strongway2019FinishReason.Exit) ||
                !terminal.RewardParticipants.Any(member => ReferenceEquals(member, participant)))
                throw new InvalidDataException("匹配奖励收件人不在原终局胜队中");
        }
        else if (plan.Competition is not null || plan.Terminal is not null || fact is null ||
            !ReferenceEquals(plan.Run.ClearFact, fact) || fact.Identity != identity)
            throw new InvalidDataException("普通首期奖励必须使用原Run清图事实");
        var bucket = dungeon == 5360 ? Strongway2019RewardBucket.Matching : Strongway2019RewardBucket.Challenge;
        var period = Strongway2019RewardPeriod.At(DateTimeOffset.FromUnixTimeSeconds(plan.Created));
        string rewardJson = JsonSerializer.Serialize(new { plan.Item });
        string factJson = JsonSerializer.Serialize(new { Identity = identity, Fact = fact, Competition = plan.Competition?.Id,
            Reason = plan.Terminal?.Reason, WinnerSide = plan.Terminal?.WinnerSide,
            TerminalFact = plan.Terminal?.ClearFact, Member = participant?.Identity, Side = participant?.Side });
        if (dungeon == 5360) ClaimStrongway2019MatchingFacts(owner, plan.Expected.Id);
        using var db = database.Open();
        using (var frozen = db.BeginRead())
        {
            var completion = ReadStrongway2019Completion(db, frozen, owner.Account, identity.InstanceId);
            frozen.Commit();
            if (completion is not null)
            {
                RequireStrongway2019Completion(completion, plan, bucket, period, rewardJson, factJson);
                return DeliverStrongway2019Completion(owner, completion);
            }
        }
        using (var read = db.BeginRead())
        {
            var receipt = ReadStrongway2019RewardReceipt(db, read, owner.Account, plan, bucket, period, rewardJson, factJson);
            read.Commit();
            if (receipt is { } existing) return new Strongway2019RewardCommit(existing, true, true);
        }
        using var write = db.BeginWrite("CharacterRepository.CommitStrongway2019Reward");
        using var tx = write.Transaction;
        if (ReadStrongway2019RewardReceipt(db, tx, owner.Account, plan, bucket, period, rewardJson, factJson) is { } completed)
        {
            tx.Commit();
            return new Strongway2019RewardCommit(completed, true, true);
        }
        var run = plan.Run;
        if (identity != run.Snapshot.Identity || run.HasPendingDeath || run.Entry.CharacterId != plan.Expected.Id ||
            (dungeon == 5360 ? run.Phase is not (SinglePlayerDungeonPhase.Dungeon or SinglePlayerDungeonPhase.Settlement) &&
                !(run.Phase == SinglePlayerDungeonPhase.Loading && plan.Terminal?.Reason == Strongway2019FinishReason.Exit) :
                run.Phase != SinglePlayerDungeonPhase.Settlement) ||
            fact is not null && (fact.DungeonId != run.Entry.DungeonId ||
                fact.ClearMap is not { } clearMap || !run.Entry.IsTerminalMap(clearMap) || fact.ConfirmedDeathEvents.Count == 0))
            throw new InvalidDataException("首期首次奖励必须来自当前Run确认的真实终点");
        var recipient = FindMailRecipientById(db, tx, plan.Expected.Id);
        if (recipient is null || recipient.Account != owner.Account)
            throw new InvalidOperationException("首期奖励固定收件角色不属于当前账号");
        var current = Require(db, tx, owner.Account, plan.Expected.Name);
        if (current.Id != plan.Expected.Id || current.Revision != plan.Expected.Revision ||
            ReadDungeonState(db, current.Id, tx)?.LastAttempt != identity.TaskAttempt)
            throw new InvalidOperationException("首期奖励角色版本或正式入场尝试已变化");
        var state = ReadStrongway2019RewardState(db, tx, owner.Account, period, bucket);
        if (state.DailyUsed >= 1 || state.WeeklyUsed >= 2)
        {
            tx.Commit();
            return new Strongway2019RewardCommit(0, false, false);
        }
        string instance = identity.InstanceId.ToString("N");
        var delivery = new MailboxDelivery($"strongway2019:{owner.Account}:{(int)bucket}:{instance}", recipient,
            "强者之路", "通关奖励，请领取附件。", 0, [plan.Item], plan.Created);
        var (error, mail) = DeliverMailInTransaction(db, tx, delivery);
        if (error != MailboxError.None || mail == 0)
            throw new InvalidOperationException("首期通关奖励邮件投递失败: " + error);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO account_strongway2019_rewards(account,instance_id,character_id,task_attempt,run_epoch,
              dungeon_id,bucket,day,week,created_at,reward_json,fact_json,mail_id)
            VALUES($account,$instance,$character,$attempt,$epoch,$dungeon,$bucket,$day,$week,$created,$reward,$fact,$mail)
            """;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$instance", instance);
        command.Parameters.AddWithValue("$character", plan.Expected.Id);
        command.Parameters.AddWithValue("$attempt", identity.TaskAttempt);
        command.Parameters.AddWithValue("$epoch", identity.Epoch);
        command.Parameters.AddWithValue("$dungeon", dungeon);
        command.Parameters.AddWithValue("$bucket", (int)bucket);
        command.Parameters.AddWithValue("$day", period.Day);
        command.Parameters.AddWithValue("$week", period.Week);
        command.Parameters.AddWithValue("$created", plan.Created);
        command.Parameters.AddWithValue("$reward", rewardJson);
        command.Parameters.AddWithValue("$fact", factJson);
        command.Parameters.AddWithValue("$mail", mail);
        command.ExecuteNonQuery();
        tx.Commit();
        return new Strongway2019RewardCommit(mail, false, true);
    });

    static Strongway2019RewardState ReadStrongway2019RewardState(SqliteConnection db, SqliteTransaction tx,
        string account, Strongway2019RewardPeriod period, Strongway2019RewardBucket bucket)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT (SELECT count(*) FROM account_strongway2019_rewards WHERE account=$account AND bucket=$bucket AND day=$day)
              +(SELECT count(*) FROM character_strongway2019_completions WHERE account=$account AND bucket=$bucket AND day=$day AND state=0)
              +(SELECT count(*) FROM character_strongway2019_match_facts f WHERE f.account=$account AND f.bucket=$bucket AND f.day=$day AND f.state=0
                AND NOT EXISTS(SELECT 1 FROM character_strongway2019_completions c WHERE c.account=f.account AND c.instance_id=f.instance_id)),
              (SELECT count(*) FROM account_strongway2019_rewards WHERE account=$account AND bucket=$bucket AND week=$week)
              +(SELECT count(*) FROM character_strongway2019_completions WHERE account=$account AND bucket=$bucket AND week=$week AND state=0)
              +(SELECT count(*) FROM character_strongway2019_match_facts f WHERE f.account=$account AND f.bucket=$bucket AND f.week=$week AND f.state=0
                AND NOT EXISTS(SELECT 1 FROM character_strongway2019_completions c WHERE c.account=f.account AND c.instance_id=f.instance_id))
            """;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$bucket", (int)bucket);
        command.Parameters.AddWithValue("$day", period.Day);
        command.Parameters.AddWithValue("$week", period.Week);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("首期奖励次数读取失败");
        return new(reader.GetInt32(0), reader.GetInt32(1));
    }

    static uint? ReadStrongway2019RewardReceipt(SqliteConnection db, SqliteTransaction tx, string account,
        Strongway2019RewardPlan plan, Strongway2019RewardBucket bucket, Strongway2019RewardPeriod period,
        string rewardJson, string factJson)
    {
        var identity = plan.Identity;
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT character_id,task_attempt,run_epoch,dungeon_id,bucket,day,week,created_at,reward_json,fact_json,mail_id
            FROM account_strongway2019_rewards WHERE account=$account AND instance_id=$instance
            """;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$instance", identity.InstanceId.ToString("N"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetInt32(0) != plan.Expected.Id || reader.GetInt64(1) != identity.TaskAttempt ||
            reader.GetInt64(2) != identity.Epoch || reader.GetInt64(3) != plan.Run.Entry.DungeonId ||
            reader.GetInt32(4) != (int)bucket || reader.GetInt32(5) != period.Day || reader.GetInt32(6) != period.Week ||
            reader.GetInt64(7) != plan.Created || reader.GetString(8) != rewardJson || reader.GetString(9) != factJson)
            throw new InvalidDataException("首期已提交回执的角色、终点、日期或完整奖励发生变化");
        return checked((uint)reader.GetInt64(10));
    }

    static bool SameStrongway2019Attempt(DungeonRunIdentity first, DungeonRunIdentity second) =>
        first.CharacterId == second.CharacterId && first.InstanceId == second.InstanceId &&
        first.TaskAttempt == second.TaskAttempt && first.QuestId == second.QuestId;
}
