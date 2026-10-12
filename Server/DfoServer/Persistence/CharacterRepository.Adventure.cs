using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Adventure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public AdventureSnapshot ReadAdventure(AccountOwners.Lease owner, AdventureCatalog catalog, DateTimeOffset now) => owner.Execute(() =>
    {
        using var db = database.Open(); using var tx = db.BeginTransaction(deferred: true);
        return ReadAdventure(db, tx, owner.Account, catalog, now);
    });

    public void InitializeAdventure(AccountOwners.Lease owner, AdventureCatalog catalog, DateTimeOffset now) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var query = db.CreateCommand(); query.Parameters.AddWithValue("$account", owner.Account);
        query.CommandText = "SELECT count(*) FROM account_adventures WHERE account=$account";
        if ((long)query.ExecuteScalar()! != 0) return true;
        using var tx = db.BeginTransaction(deferred: false);
        var state = AdventureState.Initial(owner.Account, now);
        state.Validate(catalog);
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            INSERT INTO account_adventures VALUES($account,0,$name,$date,1,0,'0',0,0,$points,$month)
            """;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$name", state.Name);
        command.Parameters.AddWithValue("$date", state.CreatedDate);
        command.Parameters.AddWithValue("$points", new byte[20]);
        command.Parameters.AddWithValue("$month", state.Month);
        command.ExecuteNonQuery();
        tx.Commit();
        return true;
    });

    static AdventureSnapshot ReadAdventure(SqliteConnection db, SqliteTransaction tx, string account, AdventureCatalog catalog, DateTimeOffset now)
    {
        using var query = db.CreateCommand(); query.Transaction = tx; query.Parameters.AddWithValue("$account", account);
        query.CommandText = "SELECT revision,name,created_date,level,experience,remainder,capsule_experience,brave_experience,points,month FROM account_adventures WHERE account=$account";
        var state = AdventureState.Initial(account, now);
        using (var reader = query.ExecuteReader())
        {
            if (reader.Read())
            {
                byte[] bytes = (byte[])reader[8];
                var points = ImmutableArray.CreateBuilder<uint>(5);
                for (int i = 0; i < 5; i++) points.Add(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4, 4)));
                state = state with { Revision = reader.GetInt64(0), Name = reader.GetString(1), CreatedDate = reader.GetInt32(2),
                    Level = reader.GetInt32(3), Experience = checked((ulong)reader.GetInt64(4)),
                    Remainder = decimal.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                    CapsuleExperience = checked((ulong)reader.GetInt64(6)), BraveExperience = checked((ulong)reader.GetInt64(7)),
                    Points = points.ToImmutable(), Month = reader.GetInt32(9) };
            }
        }
        query.CommandText = "SELECT item_id,count FROM account_adventure_purchases WHERE account=$account ORDER BY item_id";
        var purchases = ImmutableDictionary.CreateBuilder<int, uint>();
        using (var reader = query.ExecuteReader()) while (reader.Read()) purchases.Add(reader.GetInt32(0), checked((uint)reader.GetInt64(1)));
        state = state with { Purchases = purchases.ToImmutable() };
        query.CommandText = "SELECT job,grow,character_id FROM account_adventure_representatives WHERE account=$account ORDER BY job,grow";
        var selections = ImmutableArray.CreateBuilder<AdventureSelection>();
        using (var reader = query.ExecuteReader()) while (reader.Read()) selections.Add(new(checked((byte)reader.GetInt32(0)),
            checked((byte)reader.GetInt32(1)), reader.IsDBNull(2) ? null : checked((ushort)reader.GetInt32(2))));
        state = state with { Representatives = selections.ToImmutable() };
        query.CommandText = "SELECT area,ordinal,character_id,member_snapshot FROM account_adventure_expedition_members WHERE account=$account ORDER BY area,ordinal";
        var members = new Dictionary<byte, List<ushort>>();
        var frozen = new Dictionary<byte, List<ExpeditionMember>>();
        using (var reader = query.ExecuteReader()) while (reader.Read())
        {
            byte area = checked((byte)reader.GetInt32(0));
            if (!members.TryGetValue(area, out var rows)) { members.Add(area, rows = []); frozen.Add(area, []); }
            rows.Add(checked((ushort)reader.GetInt32(2)));
            frozen[area].Add(JsonSerializer.Deserialize<ExpeditionMember>(reader.GetString(3)) ?? throw new InvalidDataException("远征冻结成员缺失"));
        }
        query.CommandText = "SELECT area,run_id,started,ends,reward,extra_reward,attributes FROM account_adventure_expeditions WHERE account=$account ORDER BY area";
        var expeditions = ImmutableArray.CreateBuilder<AdventureExpedition>();
        using (var reader = query.ExecuteReader()) while (reader.Read())
        {
            byte area = checked((byte)reader.GetInt32(0));
            expeditions.Add(new(area, Guid.ParseExact(reader.GetString(1), "N"), reader.GetInt64(2), reader.GetInt64(3),
                checked((uint)reader.GetInt64(4)), checked((uint)reader.GetInt64(5)),
                JsonSerializer.Deserialize<ImmutableArray<byte>>(reader.GetString(6)), members[area].ToImmutableArray())
                { FrozenMembers = frozen[area].ToImmutableArray() });
        }
        state = state with { Expeditions = expeditions.ToImmutable() };
        state = AdventurePeriod.Apply(state, catalog, now);
        state.Validate(catalog);
        // 账号联动只读取身份、成长和版本，不装载其他角色的背包或技能树。
        query.CommandText = """
            SELECT c.id,c.slot,c.name,c.job,c.grow_type,c.level,coalesce(d.proficiency_level,0),c.revision
            FROM characters c LEFT JOIN character_dungeon_state d ON d.character_id=c.id AND d.account=c.account
            WHERE c.account=$account ORDER BY c.slot,c.id
            """;
        var roster = ImmutableArray.CreateBuilder<AdventureMember>();
        using (var reader = query.ExecuteReader()) while (reader.Read()) roster.Add(new(checked((ushort)reader.GetInt32(0)),
            checked((ushort)reader.GetInt32(1)), reader.GetString(2), checked((byte)reader.GetInt32(3)),
            checked((byte)reader.GetInt32(4)), checked((byte)reader.GetInt32(5)), checked((uint)reader.GetInt64(6)), reader.GetInt64(7)));
        return new(state, roster.ToImmutable());
    }

}
