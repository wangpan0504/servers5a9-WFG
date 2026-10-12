using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;
using DfoServer.GameWorld.Adventure;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public AdventureSnapshot CommitAdventureRepresentative(AccountOwners.Lease owner, AdventureRepresentativePlan plan,
        AdventureCatalog catalog, DateTimeOffset now) => owner.Execute(() =>
    {
        if (plan.ActorId == 0) throw new InvalidDataException("冒险团代表操作缺少真实角色上下文");
        using var db = database.Open(); using var tx = db.BeginTransaction(deferred: true);
        var current = ReadAdventure(db, tx, owner.Account, catalog, now);
        if (!current.Members.Any(member => member.Id == plan.ActorId) || !current.Members.SequenceEqual(plan.Before.Members) ||
            JsonSerializer.Serialize(current.State) != JsonSerializer.Serialize(plan.Before.State))
            throw new InvalidOperationException("冒险团或角色名册在代表规划后已变化");
        var after = current.State with { Representatives = plan.After };
        after.Validate(catalog);
        if (!plan.Changed) { tx.Commit(); return current; }
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$account", owner.Account); command.Parameters.AddWithValue("$before", current.State.Revision);
        byte[] points = new byte[20];
        for (int index = 0; index < 5; index++) System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(points.AsSpan(index * 4), current.State.Points[index]);
        command.Parameters.AddWithValue("$points", points); command.Parameters.AddWithValue("$month", current.State.Month);
        command.CommandText = "UPDATE account_adventures SET revision=revision+1,points=$points,month=$month WHERE account=$account AND revision=$before";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("冒险团代表提交revision失配");
        command.CommandText = "DELETE FROM account_adventure_representatives WHERE account=$account";
        command.ExecuteNonQuery();
        foreach (var row in plan.After)
        {
            if (row.CharacterId is { } id && !current.Members.Any(member => member.Id == id && member.Job == row.Job &&
                member.ProfessionGrow == row.Grow && catalog.Eligible(member.Job, member.Grow, member.Level)))
                throw new InvalidDataException("代表不属于当前账号或职业资格");
            command.Parameters.AddWithValue("$job", row.Job); command.Parameters.AddWithValue("$grow", row.Grow);
            command.Parameters.AddWithValue("$id", (object?)row.CharacterId ?? DBNull.Value);
            command.CommandText = "INSERT INTO account_adventure_representatives VALUES($account,$job,$grow,$id)";
            command.ExecuteNonQuery();
            command.Parameters.RemoveAt("$job"); command.Parameters.RemoveAt("$grow"); command.Parameters.RemoveAt("$id");
        }
        var committed = ReadAdventure(db, tx, owner.Account, catalog, now);
        tx.Commit(); return committed;
    });
}
