using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.EpicProduction;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static EpicProductionState ReadEpicProduction(SqliteConnection db,ushort id,SqliteTransaction? transaction)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT weapon_id,charge,service_week,weekly_uses,since_big_chance,special_uses,additional_weekly_uses,intermediate_material_remainder,ordinary_material_remainder FROM character_epic_production WHERE character_id=$id";
        command.Parameters.AddWithValue("$id",id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return EpicProductionState.Empty;
        var state = new EpicProductionState(reader.GetInt32(0),reader.GetInt32(1),reader.GetInt32(2),reader.GetInt32(3),
            reader.GetInt32(4),reader.GetInt32(5),reader.GetInt32(6))
            { IntermediateMaterialRemainder=reader.GetInt32(7),OrdinaryMaterialRemainder=reader.GetInt32(8) };
        state.Validate(); return state;
    }

    public Character CommitEpicProduction(AccountOwners.Lease owner,EpicProductionPlan plan) => owner.Execute(() =>
    {
        var expected = plan.Expected;
        ValidateInventoryPlan(expected,plan.Inventory); plan.Before.Validate(); plan.After.Validate();
        if (expected.EpicProduction != plan.Before) throw new InvalidOperationException("史诗制作计划原状态不符");
        using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.CommitEpicProduction");
        using var tx = write.Transaction;
        var current = Require(db,tx,owner.Account,expected.Name);
        if (current.Id != expected.Id || current.Revision != expected.Revision || current.EpicProduction != plan.Before)
            throw new InvalidOperationException("史诗制作角色版本或进度已变化");
        if (plan.Inventory.Changed) current = CommitInventoryInTransaction(db,tx,owner,current,plan.Inventory);
        else
        {
            using var revision = db.CreateCommand(); revision.Transaction = tx;
            revision.CommandText = "UPDATE characters SET revision=revision+1 WHERE id=$id AND account=$account AND revision=$revision";
            revision.Parameters.AddWithValue("$id",current.Id); revision.Parameters.AddWithValue("$account",owner.Account);
            revision.Parameters.AddWithValue("$revision",current.Revision);
            if (revision.ExecuteNonQuery()!=1) throw new InvalidOperationException("史诗制作角色版本失配");
            current = current with { Revision = current.Revision+1 };
        }
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            INSERT INTO character_epic_production VALUES($id,$weapon,$charge,$week,$uses,$since,$special,$additional,$intermediate,$ordinary)
            ON CONFLICT(character_id) DO UPDATE SET weapon_id=$weapon,charge=$charge,service_week=$week,
                weekly_uses=$uses,since_big_chance=$since,special_uses=$special,additional_weekly_uses=$additional,
                intermediate_material_remainder=$intermediate,ordinary_material_remainder=$ordinary;
            """;
        command.Parameters.AddWithValue("$id",current.Id); command.Parameters.AddWithValue("$weapon",plan.After.WeaponId);
        command.Parameters.AddWithValue("$charge",plan.After.Charge); command.Parameters.AddWithValue("$week",plan.After.Week);
        command.Parameters.AddWithValue("$uses",plan.After.WeeklyUses); command.Parameters.AddWithValue("$since",plan.After.SinceBigChance);
        command.Parameters.AddWithValue("$special",plan.After.SpecialUses); command.Parameters.AddWithValue("$additional",plan.After.AdditionalWeeklyUses);
        command.Parameters.AddWithValue("$intermediate",plan.After.IntermediateMaterialRemainder);
        command.Parameters.AddWithValue("$ordinary",plan.After.OrdinaryMaterialRemainder);
        command.ExecuteNonQuery();
        if (plan.Processed && quests is not null)
        {
            var active=DfoServer.Game.Quests.QuestEpicProductionProgress.Refresh(current.ActiveMainQuests,quests);
            WriteQuestConditions(db,tx,current.Id,current.ActiveMainQuests,active);
            current=current with { ActiveMainQuests=active };
        }
        tx.Commit(); return current with { EpicProduction = plan.After };
    });
}
