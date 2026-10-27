using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.ImperialArena;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.Dungeons;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IImperialArenaChallengeStore
{
    public ImperialArenaChallenge ReadImperialArenaChallenge(AccountOwners.Lease owner, ImperialArenaCatalog catalog, ImperialArenaPeriod period) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var tx = db.BeginRead();
        var result = ReadImperialArenaChallenge(db, tx, owner.Account, catalog, period);
        tx.Commit();
        return result;
    });

    static ImperialArenaChallenge ReadImperialArenaChallenge(SqliteConnection db, SqliteTransaction tx, string account,
        ImperialArenaCatalog catalog, ImperialArenaPeriod period)
    {
        var previous = DateTime.ParseExact(period.Week.ToString(CultureInfo.InvariantCulture), "yyyyMMdd", CultureInfo.InvariantCulture).AddDays(-7);
        int previousWeek = previous.Year * 10000 + previous.Month * 100 + previous.Day;
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$week", period.Week);
        command.Parameters.AddWithValue("$previous", previousWeek);
        command.Parameters.AddWithValue("$offset", catalog.ChallengeStandardRank - 1);
        // 本服确定性排行：每冒险团取一条最快完整记录，同分按账号排序；不足配置名次时用配置基准时间。
        command.CommandText = "SELECT min(elapsed_ms) AS best FROM account_imperial_arena_runs WHERE ranked_week=$previous GROUP BY account ORDER BY best,account LIMIT 1 OFFSET $offset";
        uint target = command.ExecuteScalar() is long threshold ? checked((uint)threshold) : checked((uint)catalog.ChallengeDefaultMilliseconds);
        command.CommandText = """
            WITH best AS (SELECT account,min(elapsed_ms) AS elapsed FROM account_imperial_arena_runs WHERE ranked_week=$previous GROUP BY account),
            ranked AS (SELECT account,row_number() OVER(ORDER BY elapsed,account) AS place FROM best)
            SELECT place FROM ranked WHERE account=$account
            """;
        uint rank = command.ExecuteScalar() is long place ? checked((uint)place) : 0;
        command.CommandText = "SELECT instance_id,elapsed_ms,team_json FROM account_imperial_arena_runs WHERE account=$account AND ranked_week=$week ORDER BY elapsed_ms,admitted_at,instance_id LIMIT 1";
        Guid instance = Guid.Empty;
        uint elapsed = 0;
        ImmutableArray<byte> team = [16, 16, 0, 0];
        using (var row = command.ExecuteReader()) if (row.Read())
        {
            instance = Guid.ParseExact(row.GetString(0), "N"); elapsed = checked((uint)row.GetInt64(1));
            team = JsonSerializer.Deserialize<ImmutableArray<byte>>(row.GetString(2));
            if (team.Length != 4 || team[0] > 15 || team[1] > 15 || team[2] > 127 || team[3] > 127)
                throw new InvalidDataException("帝国每周成绩的职业快照无效");
        }
        command.CommandText = "SELECT 1 FROM account_imperial_arena_challenge_claims WHERE account=$account AND week=$week";
        bool claimed = command.ExecuteScalar() is not null;
        return new(period, instance, elapsed, team, target, instance != Guid.Empty && elapsed <= target && !claimed, rank);
    }

    public ImperialArenaChallengeCommit ClaimImperialArenaChallenge(AccountOwners.Lease owner, ImperialArenaCatalog catalog,
        ImperialArenaChallengeClaim claim) => owner.Execute(() =>
    {
        if (claim.Character.Level < 90 || claim.Item.Uid != 0 || claim.Item.Core.ItemId != catalog.ChallengeRewardItemId ||
            !claim.Item.Core.IsStackable || claim.Item.Core.Count != 1 || claim.Item.ExpireTime <= claim.Created ||
            ImperialArenaPeriod.At(DateTimeOffset.FromUnixTimeSeconds(claim.Created)) != claim.Challenge.Period)
            throw new InvalidDataException("帝国每周领奖缺少合法本周成绩及完整荣誉道具");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.ClaimImperialArenaChallenge");
        using var tx = write.Transaction;
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$week", claim.Challenge.Period.Week);
        command.CommandText = "SELECT mail_id FROM account_imperial_arena_challenge_claims WHERE account=$account AND week=$week";
        if (command.ExecuteScalar() is long existing)
        {
            tx.Commit(); return new ImperialArenaChallengeCommit(claim, checked((uint)existing), true);
        }
        var actual = Require(db, tx, owner.Account, claim.Character.Name);
        if (actual.Id != claim.Character.Id || actual.Revision != claim.Character.Revision ||
            !claim.Challenge.Claimable || !claim.Challenge.Matches(ReadImperialArenaChallenge(db, tx, owner.Account, catalog, claim.Challenge.Period)))
            throw new InvalidOperationException("帝国每周领奖角色、成绩或资格已变化");
        var recipient = FindMailRecipientById(db, tx, actual.Id) ?? throw new InvalidOperationException("帝国荣誉收件人不存在");
        var delivery = new MailboxDelivery($"imperial-arena:challenge:{owner.Account}:{claim.Challenge.Period.Week}", recipient,
            "帝国竞技场", "本周困难挑战已达成，请领取荣誉奖励。", 0, [claim.Item], claim.Created);
        var (error, mail) = DeliverMailInTransaction(db, tx, delivery);
        if (error != MailboxError.None || mail == 0) throw new InvalidOperationException("帝国荣誉奖励邮件投递失败: " + error);
        command.Parameters.AddWithValue("$instance", claim.Challenge.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$mail", mail);
        command.Parameters.AddWithValue("$reward", JsonSerializer.Serialize(new { claim.Item, claim.Created, Recipient = actual.Id }));
        command.CommandText = "INSERT INTO account_imperial_arena_challenge_claims(account,week,instance_id,mail_id,reward_json) VALUES($account,$week,$instance,$mail,$reward)";
        command.ExecuteNonQuery();
        tx.Commit();
        return new ImperialArenaChallengeCommit(claim, mail, false);
    });
}
