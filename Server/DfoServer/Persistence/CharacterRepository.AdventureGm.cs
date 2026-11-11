using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IAdventureGmStore
{
    public AdventureState SetAdventure(AccountOwners.Lease owner, long revision, int? level, string? name, DateTimeOffset now) => owner.Execute(() =>
    {
        var catalog = adventureCatalog ?? throw new InvalidOperationException("冒险团资源未装配");
        using var db = database.Open();
        AdventureState before;
        using (var read = db.BeginRead())
            before = ReadAdventureState(db, read, owner.Account, catalog, now) ?? throw new InvalidOperationException("请先登录一次以初始化冒险团");
        if (before.Revision != revision) throw new InvalidOperationException("冒险团状态已变化，请刷新后重试");
        var after = AdventureAdministration.Prepare(before, catalog, level, name);
        if (after == before) return before;
        using var write = db.BeginWrite("CharacterRepository.SetAdventure");
        using var tx = write.Transaction;
        var current = ReadAdventureState(db, tx, owner.Account, catalog, now) ?? throw new InvalidOperationException("冒险团记录已移除");
        if (JsonSerializer.Serialize(current) != JsonSerializer.Serialize(before)) throw new InvalidOperationException("冒险团状态已变化，请刷新后重试");
        if (name is not null && AdventureNameExists(db, tx, owner.Account, name)) throw new InvalidOperationException("冒险团名已被其他账号使用");
        WriteAdventureBalances(db, tx, owner.Account, before, after, catalog);
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "UPDATE account_adventures SET name=$name,name_initialized=$initialized WHERE account=$account AND revision=$revision";
        command.Parameters.AddWithValue("$name", after.Name);
        command.Parameters.AddWithValue("$initialized", after.NameInitialized);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$revision", revision + 1);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("冒险团版本失配");
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO account_adventure_receipts VALUES($account,$event,NULL,$action,0,$before,$revision)";
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$event", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$action", level is not null ? "gm_level" : "gm_name");
        command.Parameters.AddWithValue("$before", revision);
        command.Parameters.AddWithValue("$revision", revision + 1);
        command.ExecuteNonQuery();
        tx.Commit();
        return after with { Revision = revision + 1 };
    });
}
