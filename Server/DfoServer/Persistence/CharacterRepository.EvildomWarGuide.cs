using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IEvildomWarGuideStore
{
    public bool TryBindEvildomWarGuide(AccountOwners.Lease owner, Character expected, int week) => owner.Execute(() =>
    {
        if (week <= 0) throw new InvalidDataException("魔界引导绑定需要有效业务周");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.TryBindEvildomWarGuide", deferred: true);
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id || current.Revision != expected.Revision)
            throw new InvalidOperationException("魔界引导绑定的原角色已变化");
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$week", week);
        command.Parameters.AddWithValue("$id", expected.Id);
        command.CommandText = "SELECT slot,character_id,week FROM account_evildom_war_guides WHERE account=$account ORDER BY slot";
        var slots = new List<int>();
        bool bound = false;
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                if (reader.GetInt32(2) == week)
                {
                    slots.Add(reader.GetInt32(0));
                    bound |= reader.GetInt32(1) == expected.Id;
                }
        if (bound) return true;
        if (slots.Count >= 2) return false;
        command.CommandText = "DELETE FROM account_evildom_war_guides WHERE account=$account AND week<>$week";
        command.ExecuteNonQuery();
        command.Parameters.AddWithValue("$slot", slots.Contains(0) ? 1 : 0);
        command.CommandText = "INSERT INTO account_evildom_war_guides(account,slot,week,character_id) VALUES($account,$slot,$week,$id)";
        command.ExecuteNonQuery();
        tx.Commit();
        return true;
    });
}
