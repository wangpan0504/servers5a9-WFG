using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Titles;
using DfoServer.Game.Raids;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public ImmutableArray<FiendwarTitleCounter> LoadFiendwarTitleFacts(AccountOwners.Lease owner, ushort character) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT quest_id,total,applied FROM character_fiendwar_title_facts WHERE character_id=$id AND account=$account AND applied<total ORDER BY quest_id";
        command.Parameters.AddWithValue("$id", character); command.Parameters.AddWithValue("$account", owner.Account);
        using var reader = command.ExecuteReader();
        var facts = ImmutableArray.CreateBuilder<FiendwarTitleCounter>();
        while (reader.Read()) facts.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)));
        return facts.ToImmutable();
    });

    static void AddFiendwarTitleFact(SqliteConnection db, SqliteTransaction tx, ushort character, string account, int quest)
    {
        if (!FiendwarTitleFacts.IsQuest(quest)) throw new InvalidDataException("非漩涡成就事实");
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            INSERT INTO character_fiendwar_title_facts(character_id,account,quest_id,total)
            VALUES($id,$account,$quest,1)
            ON CONFLICT(character_id,quest_id) DO UPDATE SET total=min(65535,total+1)
            WHERE account=$account AND total<65535;
            """;
        command.Parameters.AddWithValue("$id", character); command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$quest", quest); command.ExecuteNonQuery();
    }

    static void RecordFiendwarTitleDeath(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        AntonRaidBattle.FiendwarTitleDeath? fact)
    {
        if (fact is null) return;
        var death = fact.Death;
        ushort character = death.Death.Identity.CharacterId;
        if (death.Monster.AiCharacter is null) AddFiendwarTitleFact(db, tx, character, owner.Account, 7007);
        if (death.Result.ClearFact is not null && FiendwarTitleFacts.IsWorld(fact.Run.Entry.DungeonId))
        {
            AddFiendwarTitleFact(db, tx, character, owner.Account, 7003);
            if (fact.NoCoinClear) AddFiendwarTitleFact(db, tx, character, owner.Account, 7008);
        }
    }

    static void ApplyFiendwarTitleFacts(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        ushort character, ImmutableArray<FiendwarTitleCounter> facts)
    {
        if (facts.IsDefaultOrEmpty) return;
        if (facts.Length > 6 || facts.Select(row => row.Quest).Distinct().Count() != facts.Length || facts.Any(row => !row.Valid))
            throw new InvalidDataException("漩涡成就消费快照不完整");
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "UPDATE character_fiendwar_title_facts SET applied=$total WHERE character_id=$id AND account=$account AND quest_id=$quest AND applied=$applied AND total>=$total";
        command.Parameters.AddWithValue("$id", character); command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.Add("$quest", SqliteType.Integer); command.Parameters.Add("$applied", SqliteType.Integer);
        command.Parameters.Add("$total", SqliteType.Integer);
        foreach (var fact in facts)
        {
            command.Parameters["$quest"].Value = fact.Quest; command.Parameters["$applied"].Value = fact.Applied;
            command.Parameters["$total"].Value = fact.Total;
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("漩涡成就事实已被原会话消费或来源变化");
        }
    }
}
