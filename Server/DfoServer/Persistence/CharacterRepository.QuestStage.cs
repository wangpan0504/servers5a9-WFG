using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public QuestStageState LoadQuestStageState(AccountOwners.Lease owner, Character expected) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var tx = db.BeginRead();
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id || current.Revision != expected.Revision)
            throw new InvalidOperationException("阶段资格原角色或revision已变化");
        return ReadQuestStage(db, tx, owner.Account, expected);
    });

    static QuestStageState ReadQuestStage(SqliteConnection db, SqliteTransaction tx, string account, Character expected)
    {
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT source_acceptance_id,source_quest_id,source_revision,target_level,rule_version,status,proof FROM character_quest_stage_witnesses WHERE character_id=$id AND account=$account ORDER BY source_revision,source_acceptance_id";
        query.Parameters.AddWithValue("$id", expected.Id); query.Parameters.AddWithValue("$account", account);
        var witnesses = ImmutableArray.CreateBuilder<QuestStageWitness>();
        using var reader = query.ExecuteReader();
        while (reader.Read())
        {
            var witness = JsonSerializer.Deserialize<QuestStageWitness>(reader.GetString(6)) ?? throw new InvalidDataException("阶段依据缺失");
            if (witness.SourceAcceptanceId.ToString("N") != reader.GetString(0) || witness.SourceQuestId != reader.GetInt32(1) ||
                witness.SourceCompletionRevision != reader.GetInt64(2) || witness.TargetLevel != reader.GetInt32(3) ||
                witness.RuleVersion != reader.GetInt32(4) || witness.Status.ToString() != reader.GetString(5) ||
                witness.BoundaryQuestIds.IsDefaultOrEmpty || witness.Evidence.IsDefaultOrEmpty ||
                witness.Evidence.Any(edge => edge.PrerequisiteGroup.IsDefaultOrEmpty))
                throw new InvalidDataException("阶段依据与持久来源不一致");
            witnesses.Add(witness);
        }
        return new(expected.Id, expected.Revision, witnesses.ToImmutable());
    }

    // 确定性JSON逐层包含ImmutableArray内容；record默认相等不能比较反序列化后的数组。
    static string StageProof(QuestStageWitness witness) => JsonSerializer.Serialize(witness);
    static bool SameStage(IEnumerable<QuestStageWitness> left, IEnumerable<QuestStageWitness> right) =>
        left.OrderBy(row => row.SourceCompletionRevision).ThenBy(row => row.SourceAcceptanceId).Select(StageProof).SequenceEqual(
            right.OrderBy(row => row.SourceCompletionRevision).ThenBy(row => row.SourceAcceptanceId).Select(StageProof));

    static void WriteQuestStage(SqliteConnection db, SqliteTransaction tx, string account, Character expected, QuestStagePlan stage)
    {
        if (stage.Expected.CharacterId != expected.Id || stage.Expected.Revision != expected.Revision ||
            stage.Expected.Witnesses.IsDefault || stage.WitnessesAfter.IsDefault ||
            !SameStage(ReadQuestStage(db, tx, account, expected).Witnesses, stage.Expected.Witnesses) ||
            stage.WitnessesAfter.Select(row => row.SourceAcceptanceId).Distinct().Count() != stage.WitnessesAfter.Length ||
            stage.Expected.Witnesses.Any(old => !stage.WitnessesAfter.Any(row => row.SourceAcceptanceId == old.SourceAcceptanceId &&
                StageProof(row with { Status = old.Status }) == StageProof(old) && (old.Status == QuestStageStatus.Deferred || row.Status == old.Status))))
            throw new InvalidOperationException("阶段资格已变化或固定依据被改写");
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id); command.Parameters.AddWithValue("$account", account);
        var oldProofs = stage.Expected.Witnesses.ToDictionary(row => row.SourceAcceptanceId,StageProof);
        foreach (var witness in stage.WitnessesAfter)
        {
            bool unchanged = oldProofs.GetValueOrDefault(witness.SourceAcceptanceId) == StageProof(witness);
            if (unchanged && witness.Status != QuestStageStatus.Deferred) continue;
            // 来源ordinary receipt和阶段写入共享原领奖事务；绝不为旧无证receipt推断资格。
            command.Parameters.AddWithValue("$source", witness.SourceAcceptanceId.ToString("N"));
            command.CommandText = "SELECT quest_id,revision,plan_snapshot FROM character_quest_completion_receipts WHERE character_id=$id AND account=$account AND acceptance_id=$source";
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read() || reader.GetInt32(0) != witness.SourceQuestId || reader.GetInt64(1) != witness.SourceCompletionRevision || reader.IsDBNull(2))
                    throw new InvalidDataException("阶段来源缺少真实普通完成回执");
                using var snapshot = JsonDocument.Parse(reader.GetString(2));
                if (!snapshot.RootElement.TryGetProperty("Stage", out var frozen) ||
                    !frozen.GetProperty("StagePlan").GetProperty("WitnessesAfter").EnumerateArray().Any(node =>
                        node.Deserialize<QuestStageWitness>() is { } original &&
                        StageProof(original with { Status = witness.Status }) == StageProof(witness)))
                    throw new InvalidDataException("阶段来源回执未冻结相同因果依据");
            }
            if (unchanged)
            {
                command.Parameters.RemoveAt("$source");
                continue;
            }
            command.Parameters.AddWithValue("$quest", witness.SourceQuestId);
            command.Parameters.AddWithValue("$sourceRevision", witness.SourceCompletionRevision);
            command.Parameters.AddWithValue("$target", witness.TargetLevel);
            command.Parameters.AddWithValue("$rule", witness.RuleVersion);
            command.Parameters.AddWithValue("$status", witness.Status.ToString());
            command.Parameters.AddWithValue("$proof", StageProof(witness));
            command.CommandText = """
                INSERT INTO character_quest_stage_witnesses(character_id,account,source_acceptance_id,source_quest_id,source_revision,target_level,rule_version,status,proof)
                VALUES($id,$account,$source,$quest,$sourceRevision,$target,$rule,$status,$proof)
                ON CONFLICT(character_id,source_acceptance_id) DO UPDATE SET status=excluded.status,proof=excluded.proof
                """;
            command.ExecuteNonQuery();
            foreach (string key in new[] { "$source", "$quest", "$sourceRevision", "$target", "$rule", "$status", "$proof" }) command.Parameters.RemoveAt(key);
        }
        if (stage.Claim is not { } claim) return;
        var source = stage.WitnessesAfter.SingleOrDefault(row => row.SourceAcceptanceId == claim.SourceAcceptanceId);
        if (source is null || source.TargetLevel != claim.TargetLevel ||
            source.Status != (claim.Amount == 0 ? QuestStageStatus.Satisfied : QuestStageStatus.Granted) ||
            claim.AfterExperience < claim.BeforeExperience || claim.AfterExperience - claim.BeforeExperience != claim.Amount ||
            stage.Expected.Witnesses.Any(row => row.TargetLevel == claim.TargetLevel && row.Status is QuestStageStatus.Granted or QuestStageStatus.Satisfied))
            throw new InvalidDataException("阶段消费与固定来源或金额不一致");
        command.Parameters.AddWithValue("$trigger", claim.TriggerAcceptanceId.ToString("N"));
        command.CommandText = "SELECT revision,after_experience FROM character_quest_completion_receipts WHERE character_id=$id AND account=$account AND acceptance_id=$trigger";
        using (var reader = command.ExecuteReader())
            if (!reader.Read() || reader.GetInt64(0) != expected.Revision + 1 || (ulong)reader.GetInt64(1) != claim.AfterExperience)
                throw new InvalidDataException("阶段触发必须是本次普通完成和同一最终经验");
        command.Parameters.RemoveAt("$trigger");
        foreach (var (key, value) in new (string, object)[] { ("$source", claim.SourceAcceptanceId.ToString("N")),
            ("$trigger", claim.TriggerAcceptanceId.ToString("N")), ("$target", claim.TargetLevel), ("$before", checked((long)claim.BeforeExperience)),
            ("$after", checked((long)claim.AfterExperience)), ("$amount", claim.Amount), ("$revision", checked(expected.Revision + 1)) })
            command.Parameters.AddWithValue(key, value);
        command.CommandText = """
            INSERT INTO character_quest_stage_claims(character_id,account,source_acceptance_id,trigger_acceptance_id,target_level,before_experience,after_experience,amount,revision)
            VALUES($id,$account,$source,$trigger,$target,$before,$after,$amount,$revision)
            """;
        command.ExecuteNonQuery();
    }

    static void InvalidateQuestStages(SqliteConnection db, SqliteTransaction tx, ushort characterId, string reason)
    {
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT source_acceptance_id,proof FROM character_quest_stage_witnesses WHERE character_id=$id AND status='Deferred'";
        command.Parameters.AddWithValue("$id", characterId);
        var rows = new List<(string Source, string Proof)>();
        using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            var witness = JsonSerializer.Deserialize<QuestStageWitness>(reader.GetString(1)) ?? throw new InvalidDataException("阶段依据缺失: " + reason);
            rows.Add((reader.GetString(0), StageProof(witness with { Status = QuestStageStatus.Invalidated })));
        }
        foreach (var row in rows)
        {
            command.Parameters.AddWithValue("$source", row.Source); command.Parameters.AddWithValue("$proof", row.Proof);
            command.CommandText = "UPDATE character_quest_stage_witnesses SET status='Invalidated',proof=$proof WHERE character_id=$id AND source_acceptance_id=$source AND status='Deferred'";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("阶段失效资格已变化");
            command.Parameters.RemoveAt("$source"); command.Parameters.RemoveAt("$proof");
        }
    }
}
