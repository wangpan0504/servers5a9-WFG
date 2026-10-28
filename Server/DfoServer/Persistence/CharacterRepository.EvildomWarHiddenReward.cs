using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Mailbox;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public ImmutableArray<EvildomWarCompletion> LoadUnrewardedEvildomWarHiddenCompletions(AccountOwners.Lease owner,
        ushort characterId) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var command = WarCommand(db, null, owner.Account, characterId, Guid.Empty);
        // 隐藏真实事实独立于最新进图、席位超时及王座奖励；后续进图不会抹掉资格。
        command.CommandText = """
            SELECT b.checkpoint_json FROM character_evildom_war_seats s
            JOIN evildom_war_battles b ON b.battle_id=s.battle_id
            JOIN characters c ON c.id=s.character_id AND c.account=s.account
            WHERE s.character_id=$id AND s.account=$account
              AND json_type(b.checkpoint_json,'$.HiddenCompletion')='object'
              AND NOT EXISTS(SELECT 1 FROM character_evildom_war_hidden_rewards r
                WHERE r.character_id=s.character_id AND r.battle_id=s.battle_id)
            ORDER BY b.deadline,s.battle_id
            """;
        using var rows = command.ExecuteReader();
        var result = ImmutableArray.CreateBuilder<EvildomWarCompletion>();
        while (rows.Read())
        {
            var checkpoint = ReadWarJson<EvildomWarCheckpoint>(rows.GetString(0));
            checkpoint.Validate();
            if (checkpoint.HiddenCompletion is { } completion && completion.Facts.Any(fact => fact.Identity.CharacterId == characterId))
                result.Add(completion);
        }
        return result.ToImmutable();
    });

    public EvildomWarHiddenRewardCommit CommitEvildomWarHiddenReward(AccountOwners.Lease owner, ushort characterId,
        EvildomWarRewardSnapshot snapshot) => owner.Execute(() =>
    {
        snapshot.Validate();
        if (snapshot.Rule != EvildomWarRewardPolicy.HiddenRule ||
            !snapshot.Completion.Facts.Any(fact => fact.Identity.CharacterId == characterId))
            throw new InvalidDataException("魔界实验室奖励缺少本人终点事实");
        string content = JsonSerializer.Serialize(snapshot);
        using var db = database.Open();
        using (var read = db.BeginRead())
        {
            var duplicate = ReadEvildomWarHiddenReward(db, read, owner.Account, characterId, snapshot.Completion.BattleId, content);
            read.Commit();
            if (duplicate is not null) return duplicate;
        }
        using var write = db.BeginWrite("CharacterRepository.CommitEvildomWarHiddenReward");
        using var tx = write.Transaction;
        if (ReadEvildomWarHiddenReward(db, tx, owner.Account, characterId, snapshot.Completion.BattleId, content) is { } repeated)
        { tx.Commit(); return repeated; }
        using var command = WarCommand(db, tx, owner.Account, characterId, snapshot.Completion.BattleId);
        command.CommandText = """
            SELECT b.checkpoint_json FROM character_evildom_war_seats s
            JOIN evildom_war_battles b ON b.battle_id=s.battle_id
            JOIN characters c ON c.id=s.character_id AND c.account=s.account
            WHERE s.character_id=$id AND s.account=$account AND s.battle_id=$battle
            """;
        string json = (string?)command.ExecuteScalar() ?? throw new InvalidOperationException("魔界实验室奖励角色或战役不匹配");
        var checkpoint = ReadWarJson<EvildomWarCheckpoint>(json);
        checkpoint.Validate();
        if (checkpoint.HiddenCompletion is null ||
            JsonSerializer.Serialize(checkpoint.HiddenCompletion) != JsonSerializer.Serialize(snapshot.Completion))
            throw new InvalidOperationException("魔界实验室奖励缺少原持久物件完成事实");
        command.CommandText = "SELECT reward_json FROM character_evildom_war_rewards WHERE character_id=$id AND battle_id=$battle";
        bool accounted = command.ExecuteScalar() is string finalJson &&
            ReadWarJson<EvildomWarRewardSnapshot>(finalJson) is { Rule: EvildomWarRewardPolicy.LegacyRule } legacy &&
            legacy.Reward.Items.Any(item => item.Group == 1);
        var mails = ImmutableArray.CreateBuilder<uint>();
        if (!accounted)
        {
            var recipient = FindMailRecipientById(db, tx, characterId);
            if (recipient is null || recipient.Account != owner.Account) throw new InvalidOperationException("魔界实验室收件归属变化");
            foreach (var batch in snapshot.Items.Chunk(10))
            {
                var delivery = new MailboxDelivery($"evildom-war-hidden:{snapshot.Completion.BattleId:N}:{characterId}:{mails.Count}",
                    recipient, "魔界大战实验室", "实验室额外奖励已永久保存，请领取附件。", 0, [..batch],
                    snapshot.Completion.Created.ToUnixTimeSeconds());
                var (error, mailId) = DeliverMailInTransaction(db, tx, delivery);
                if (error != MailboxError.None || mailId == 0) throw new InvalidOperationException("魔界实验室奖励邮件失败: " + error);
                mails.Add(mailId);
            }
        }
        var result = mails.ToImmutable();
        command.Parameters.AddWithValue("$reward", content);
        command.Parameters.AddWithValue("$mails", JsonSerializer.Serialize(result));
        command.CommandText = """
            INSERT INTO character_evildom_war_hidden_rewards(character_id,account,battle_id,reward_json,mail_ids)
            VALUES($id,$account,$battle,$reward,$mails)
            """;
        command.ExecuteNonQuery(); tx.Commit();
        return new EvildomWarHiddenRewardCommit(result, accounted);
    });

    static EvildomWarHiddenRewardCommit? ReadEvildomWarHiddenReward(SqliteConnection db, SqliteTransaction tx, string account,
        ushort characterId, Guid battleId, string content)
    {
        using var command = WarCommand(db, tx, account, characterId, battleId);
        command.CommandText = """
            SELECT r.account,r.reward_json,r.mail_ids FROM character_evildom_war_hidden_rewards r
            JOIN characters c ON c.id=r.character_id AND c.account=r.account
            WHERE r.character_id=$id AND r.battle_id=$battle
            """;
        using var row = command.ExecuteReader();
        if (!row.Read()) return null;
        if (row.GetString(0) != account || row.GetString(1) != content)
            throw new InvalidDataException("同一魔界实验室的冻结奖励发生变化");
        return new(ReadWarJson<ImmutableArray<uint>>(row.GetString(2)), true);
    }
}
