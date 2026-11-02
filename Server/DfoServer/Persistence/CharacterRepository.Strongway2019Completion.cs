using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Strongway2019;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IStrongway2019CompletionStore
{
    sealed record Strongway2019StoredCompletion(string Account, Guid Instance, ushort Character, long Attempt, long Epoch,
        int Dungeon, Strongway2019RewardBucket Bucket, Strongway2019RewardPeriod Period, long Created,
        string RewardJson, string FactJson, int State, uint? Mail);
    sealed record Strongway2019StoredAttachment(InventoryItem Item);

    public void FreezeStrongway2019Completions(IReadOnlyList<(AccountOwners.Lease Owner, Strongway2019RewardPlan Plan)> plans)
    {
        if (plans.Count is < 1 or > 4) throw new InvalidDataException("首期完成只接受本人或原一至四人胜队");
        var first = plans[0].Plan;
        if (first.Competition is { } competition && (first.Terminal is not { } terminal ||
            terminal.RewardParticipants.Length != plans.Count || plans.Any(row => !ReferenceEquals(row.Plan.Competition, competition) ||
                !ReferenceEquals(row.Plan.Terminal, terminal)) || terminal.RewardParticipants.Any(member =>
                plans.Count(row => ReferenceEquals(row.Plan.Run, member.Run)) != 1)))
            throw new InvalidDataException("匹配终局必须一次冻结完整原胜队，不能逐人遗漏");
        if (first.Competition is null && plans.Any(row => row.Plan.Competition is not null))
            throw new InvalidDataException("普通与匹配完成不能混合");
        AccountOwners.Lease.ExecuteGroup(plans.Select(row => (row.Owner, row.Plan.Expected.Id)).ToArray(), () =>
        {
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.FreezeStrongway2019Completions");
            using var tx = write.Transaction;
            foreach (var row in plans) FreezeStrongway2019Completion(db, tx, row.Owner, row.Plan);
            tx.Commit(); return true;
        });
    }

    void FreezeStrongway2019Completion(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner, Strongway2019RewardPlan plan)
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
                throw new InvalidDataException("失败或超时不是可冻结奖励终局");
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

        var saved = ReadStrongway2019Completion(db, tx, owner.Account, identity.InstanceId);
        if (saved is not null)
        {
            RequireStrongway2019Completion(saved, plan, bucket, period, rewardJson, factJson);
            return;
        }
        // 迁移前已交付回执保持原样，不重投或重新占次。
        if (ReadStrongway2019RewardReceipt(db, tx, owner.Account, plan, bucket, period, rewardJson, factJson) is not null) return;
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
        int outcome = state.DailyUsed >= 1 || state.WeeklyUsed >= 2 ? 2 : 0;
        CommitStrongway2019StatisticsInTransaction(db, tx, owner, new(plan.Expected, plan.Run, plan.Fact)
            { Identity = identity, Competition = plan.Competition, Terminal = plan.Terminal });
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            INSERT INTO character_strongway2019_completions(account,instance_id,character_id,task_attempt,run_epoch,
              dungeon_id,bucket,day,week,created_at,reward_json,fact_json,state,mail_id)
            VALUES($account,$instance,$character,$attempt,$epoch,$dungeon,$bucket,$day,$week,$created,$reward,$fact,$state,NULL)
            """;
        AddStrongway2019CompletionParameters(command, new(owner.Account, identity.InstanceId, plan.Expected.Id,
            identity.TaskAttempt, identity.Epoch, dungeon, bucket, period, plan.Created, rewardJson, factJson, outcome, null));
        command.Parameters.AddWithValue("$state", outcome); command.ExecuteNonQuery();
    }

    public ImmutableArray<uint> RecoverStrongway2019Rewards(AccountOwners.Lease owner, ushort character) =>
        RecoverStrongway2019Completion(owner, character).MailIds;

    public Strongway2019Recovery RecoverStrongway2019Completion(AccountOwners.Lease owner, ushort character) => owner.Execute(() =>
    {
        bool changed = ClaimStrongway2019MatchingFacts(owner, character);
        var pending = new List<Strongway2019StoredCompletion>();
        using (var db = database.Open())
        using (var tx = db.BeginRead())
        {
            RequireStrongway2019StatisticsOwner(db, tx, owner.Account, character);
            using var command = db.CreateCommand(); command.Transaction = tx;
            command.CommandText = "SELECT instance_id FROM character_strongway2019_completions WHERE account=$account AND character_id=$character AND state=0 ORDER BY created_at,instance_id";
            command.Parameters.AddWithValue("$account", owner.Account); command.Parameters.AddWithValue("$character", character);
            var ids = new List<Guid>();
            using (var reader = command.ExecuteReader()) while (reader.Read()) ids.Add(Guid.ParseExact(reader.GetString(0), "N"));
            foreach (var id in ids) pending.Add(ReadStrongway2019Completion(db, tx, owner.Account, id)!);
            tx.Commit();
        }
        var result = ImmutableArray.CreateBuilder<uint>();
        foreach (var completion in pending)
        {
            var committed = DeliverStrongway2019Completion(owner, completion);
            if (committed is { Granted: true, Duplicate: false }) result.Add(committed.MailId);
        }
        return new Strongway2019Recovery(changed, result.ToImmutable());
    });

    Strongway2019RewardCommit DeliverStrongway2019Completion(AccountOwners.Lease owner, Strongway2019StoredCompletion expected)
    {
        using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.DeliverStrongway2019Completion");
        using var tx = write.Transaction;
        var saved = ReadStrongway2019Completion(db, tx, owner.Account, expected.Instance)
            ?? throw new InvalidDataException("首期原持久完成记录缺失");
        if ((saved with { State = expected.State, Mail = expected.Mail }) != expected)
            throw new InvalidDataException("首期原冻结完成快照发生变化");
        if (saved.State == 1) { tx.Commit(); return new(saved.Mail!.Value, true, true); }
        if (saved.State == 2) { tx.Commit(); return new(0, false, false); }
        var recipient = FindMailRecipientById(db, tx, saved.Character);
        if (recipient is null || recipient.Account != owner.Account) throw new InvalidOperationException("首期恢复奖励收件归属变化");
        var item = JsonSerializer.Deserialize<Strongway2019StoredAttachment>(saved.RewardJson)?.Item
            ?? throw new InvalidDataException("首期冻结完整附件缺失");
        int reward = saved.Dungeon switch { 5359 => 490012735, 5363 => 490012736, 5360 => 490012737, _ => 0 };
        if (reward == 0 || item.Uid != 0 || item.Core.ItemId != reward || !item.Core.IsStackable || item.Core.Count != 1 || item.IsExpired(saved.Created))
            throw new InvalidDataException("首期冻结附件与原模式不一致");
        item.ValidateDetails(allocating: true);
        var delivery = new MailboxDelivery($"strongway2019:{owner.Account}:{(int)saved.Bucket}:{saved.Instance:N}", recipient,
            "强者之路", "通关奖励，请领取附件。", 0, [item], saved.Created);
        var (error, mail) = DeliverMailInTransaction(db, tx, delivery);
        if (error != MailboxError.None || mail == 0) throw new InvalidOperationException("首期恢复邮件失败: " + error);
        using var command = db.CreateCommand(); command.Transaction = tx;
        AddStrongway2019CompletionParameters(command, saved); command.Parameters.AddWithValue("$mail", mail);
        command.CommandText = """
            INSERT INTO account_strongway2019_rewards(account,instance_id,character_id,task_attempt,run_epoch,
              dungeon_id,bucket,day,week,created_at,reward_json,fact_json,mail_id)
            VALUES($account,$instance,$character,$attempt,$epoch,$dungeon,$bucket,$day,$week,$created,$reward,$fact,$mail)
            """;
        command.ExecuteNonQuery();
        command.CommandText = "UPDATE character_strongway2019_completions SET state=1,mail_id=$mail WHERE account=$account AND instance_id=$instance AND state=0";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("首期原冻结完成状态变化");
        tx.Commit(); return new(mail, false, true);
    }

    static void RequireStrongway2019Completion(Strongway2019StoredCompletion saved, Strongway2019RewardPlan plan,
        Strongway2019RewardBucket bucket, Strongway2019RewardPeriod period, string rewardJson, string factJson)
    {
        if (saved.Instance != plan.Identity.InstanceId || saved.Character != plan.Expected.Id || saved.Attempt != plan.Identity.TaskAttempt ||
            saved.Epoch != plan.Identity.Epoch || saved.Dungeon != plan.Run.Entry.DungeonId || saved.Bucket != bucket || saved.Period != period ||
            saved.Created != plan.Created || saved.RewardJson != rewardJson || saved.FactJson != factJson)
            throw new InvalidDataException("首期已冻结完成的身份、日期、终局或完整奖励变化");
    }

    static Strongway2019StoredCompletion? ReadStrongway2019Completion(SqliteConnection db, SqliteTransaction tx, string account, Guid instance)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            SELECT character_id,task_attempt,run_epoch,dungeon_id,bucket,day,week,created_at,reward_json,fact_json,state,mail_id
            FROM character_strongway2019_completions WHERE account=$account AND instance_id=$instance
            """;
        command.Parameters.AddWithValue("$account", account); command.Parameters.AddWithValue("$instance", instance.ToString("N"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new(account, instance, checked((ushort)reader.GetInt64(0)), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt32(3),
            (Strongway2019RewardBucket)reader.GetInt32(4), new(reader.GetInt32(5), reader.GetInt32(6)), reader.GetInt64(7),
            reader.GetString(8), reader.GetString(9), reader.GetInt32(10), reader.IsDBNull(11) ? null : checked((uint)reader.GetInt64(11)));
    }

    static void AddStrongway2019CompletionParameters(SqliteCommand command, Strongway2019StoredCompletion value)
    {
        command.Parameters.AddWithValue("$account", value.Account); command.Parameters.AddWithValue("$instance", value.Instance.ToString("N"));
        command.Parameters.AddWithValue("$character", value.Character); command.Parameters.AddWithValue("$attempt", value.Attempt);
        command.Parameters.AddWithValue("$epoch", value.Epoch); command.Parameters.AddWithValue("$dungeon", value.Dungeon);
        command.Parameters.AddWithValue("$bucket", (int)value.Bucket); command.Parameters.AddWithValue("$day", value.Period.Day);
        command.Parameters.AddWithValue("$week", value.Period.Week); command.Parameters.AddWithValue("$created", value.Created);
        command.Parameters.AddWithValue("$reward", value.RewardJson); command.Parameters.AddWithValue("$fact", value.FactJson);
    }
}
