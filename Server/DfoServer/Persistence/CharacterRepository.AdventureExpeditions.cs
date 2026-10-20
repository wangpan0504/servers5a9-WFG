using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;
using DfoServer.GameWorld.Adventure;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public AdventureSnapshot CommitAdventureExpedition(AccountOwners.Lease owner, AdventureExpeditionPlan plan,
        AdventureCatalog catalog, DateTimeOffset now) => owner.Execute(() =>
    {
        plan.After.Validate(catalog);
        var allowed = plan.Action switch
        {
            "expedition_start" or "expedition_cancel" => plan.Before.State with { Expeditions = plan.After.Expeditions },
            "expedition_claim" => plan.Before.State with { Expeditions = plan.After.Expeditions, Points = plan.After.Points },
            _ => throw new InvalidDataException("未定义的远征操作")
        };
        if (JsonSerializer.Serialize(allowed) != JsonSerializer.Serialize(plan.After)) throw new InvalidDataException("远征修改了未参与的账号状态");
        using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.CommitAdventureExpedition", deferred: true);
        using var tx = write.Transaction;
        var current = ReadAdventure(db, tx, owner.Account, catalog, now);
        if (!current.Members.Any(member => member.Id == plan.ActorId) || !current.Members.SequenceEqual(plan.Before.Members) ||
            JsonSerializer.Serialize(current.State) != JsonSerializer.Serialize(plan.Before.State))
            throw new InvalidOperationException("远征或账号角色在规划后已变化");
        WriteAdventureBalances(db, tx, owner.Account, current.State, plan.After, catalog);
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.CommandText = "DELETE FROM account_adventure_expeditions WHERE account=$account";
        command.ExecuteNonQuery();
        foreach (var run in plan.After.Expeditions)
        {
            if (run.Members.Any(id => !current.Members.Any(member => member.Id == id))) throw new InvalidDataException("远征成员不属于本账号");
            command.Parameters.AddWithValue("$area", run.Area); command.Parameters.AddWithValue("$id", run.Id.ToString("N"));
            command.Parameters.AddWithValue("$start", run.Started); command.Parameters.AddWithValue("$end", run.Ends);
            command.Parameters.AddWithValue("$reward", run.Reward); command.Parameters.AddWithValue("$extra", run.ExtraReward);
            command.Parameters.AddWithValue("$attributes", JsonSerializer.Serialize(run.Attributes));
            command.CommandText = "INSERT INTO account_adventure_expeditions VALUES($account,$area,$id,$start,$end,$reward,$extra,$attributes)";
            command.ExecuteNonQuery();
            foreach (string key in new[] { "$id", "$start", "$end", "$reward", "$extra", "$attributes" }) command.Parameters.RemoveAt(key);
            command.Parameters.AddWithValue("$ordinal", 0); command.Parameters.AddWithValue("$member", 0); command.Parameters.AddWithValue("$snapshot", "");
            for (int index = 0; index < run.Members.Length; index++)
            {
                command.Parameters["$ordinal"].Value = index; command.Parameters["$member"].Value = run.Members[index];
                command.Parameters["$snapshot"].Value = JsonSerializer.Serialize(run.FrozenMembers[index]);
                command.CommandText = "INSERT INTO account_adventure_expedition_members VALUES($account,$area,$ordinal,$member,$snapshot)";
                command.ExecuteNonQuery();
            }
            foreach (string key in new[] { "$area", "$ordinal", "$member", "$snapshot" }) command.Parameters.RemoveAt(key);
        }
        var changed = plan.Action == "expedition_start" ? plan.After.Expeditions.Single(run => !current.State.Expeditions.Any(old => old.Id == run.Id)) :
            current.State.Expeditions.Single(run => !plan.After.Expeditions.Any(after => after.Id == run.Id));
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO account_adventure_receipts VALUES($account,$event,$character,$action,$amount,$before,$revision)";
        command.Parameters.AddWithValue("$account", owner.Account); command.Parameters.AddWithValue("$event", changed.Id.ToString("N") + ":" + plan.Action);
        command.Parameters.AddWithValue("$character", plan.ActorId); command.Parameters.AddWithValue("$action", plan.Action);
        command.Parameters.AddWithValue("$amount", plan.Action == "expedition_claim" ? changed.Reward : 0);
        command.Parameters.AddWithValue("$before", current.State.Revision); command.Parameters.AddWithValue("$revision", current.State.Revision + 1);
        command.ExecuteNonQuery();
        var committed = ReadAdventure(db, tx, owner.Account, catalog, now);
        tx.Commit(); return committed;
    });
}
