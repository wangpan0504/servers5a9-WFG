using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public TournamentCardCommit CommitTournamentCards(AccountOwners.Lease owner, Character expected,
        PreparedDungeonSettlement prepared, TournamentCardClaim claim) => owner.Execute(() =>
    {
        if (!ReferenceEquals(claim.Expected, expected) || !ReferenceEquals(prepared.Tournament, claim.Reward))
            throw new InvalidDataException("大会领取必须使用原实例冻结牌面与角色快照");
        var frozen = claim.Reward.Selected(claim.Choices);
        string plan = JsonSerializer.Serialize(claim.Reward);
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitTournamentCards", deferred: true);
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id || prepared.Fact.Identity.CharacterId != expected.Id ||
            current.DungeonState?.LastAttempt != prepared.Fact.Identity.TaskAttempt)
            throw new InvalidOperationException("大会领奖账号、角色或实例已变化");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        foreach (var (key, value) in new (string, object)[] { ("$id", expected.Id), ("$account", owner.Account),
            ("$instance", prepared.Fact.Identity.InstanceId.ToString("N")) })
            command.Parameters.AddWithValue(key, value);
        command.CommandText = """
            SELECT account,plan_json,first_choice,second_choice,rewards_json,mail_id
            FROM character_dungeon_tournament_rewards WHERE character_id=$id AND instance_id=$instance
            """;
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != owner.Account || reader.GetString(1) != plan ||
                    reader.GetInt32(2) != claim.Choices[0] || reader.GetInt32(3) != claim.Choices[1])
                    throw new InvalidDataException("同次大会不能替换冻结牌面或领取选择");
                var actual = JsonSerializer.Deserialize<ImmutableArray<DungeonCardReward>>(reader.GetString(4));
                if (actual.IsDefault || actual.Length != 2) throw new InvalidDataException("大会资产回执不完整");
                for (int i = 0; i < 2; i++) VerifyActualCard(actual[i], frozen[i]);
                uint? previousMail = reader.IsDBNull(5) ? null : checked((uint)reader.GetInt64(5));
                reader.Close();
                tx.Commit();
                return new TournamentCardCommit(current, new(expected.Inventory, current.Inventory, "dungeon_card_reward"), previousMail, true);
            }
        }
        command.CommandText = "SELECT event_id FROM character_dungeon_settlement_receipts WHERE character_id=$id AND instance_id=$instance AND account=$account";
        if (command.ExecuteScalar() as string != prepared.EventId.ToString("N") || current.Revision != expected.Revision)
            throw new InvalidOperationException("大会领奖缺少本次已提交通关或角色快照已变化");
        ValidateCardDelivery(claim.Delivery, frozen, 0);
        if (claim.Delivery.Inventory.Changed)
            current = CommitInventoryInTransaction(db, tx, owner, current, claim.Delivery.Inventory);
        uint? mail = DeliverCardItems(db, tx, owner, current, claim.Delivery.MailItems, prepared, "tournament", frozen[0].CreatedAt);
        command.Parameters.AddWithValue("$plan", plan);
        command.Parameters.AddWithValue("$first", claim.Choices[0]);
        command.Parameters.AddWithValue("$second", claim.Choices[1]);
        command.Parameters.AddWithValue("$rewards", JsonSerializer.Serialize(claim.Delivery.Rewards));
        command.Parameters.AddWithValue("$mail", (object?)mail ?? DBNull.Value);
        command.Parameters.AddWithValue("$revision", current.Revision);
        command.CommandText = """
            INSERT INTO character_dungeon_tournament_rewards
              (character_id,account,instance_id,plan_json,first_choice,second_choice,rewards_json,mail_id,revision)
            VALUES($id,$account,$instance,$plan,$first,$second,$rewards,$mail,$revision)
            """;
        command.ExecuteNonQuery();
        tx.Commit();
        return new TournamentCardCommit(current, new(expected.Inventory, current.Inventory, "dungeon_card_reward"), mail, false);
    });
}
