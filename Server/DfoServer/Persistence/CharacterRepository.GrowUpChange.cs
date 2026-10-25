using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static int ReadGrowUpChangeCount(SqliteConnection db, SqliteTransaction? tx, ushort id, string account)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT change_count FROM character_growup_change WHERE character_id=$id AND account=$account";
        query.Parameters.AddWithValue("$id", id);
        query.Parameters.AddWithValue("$account", account);
        // 没有行表示本服从未成功提交过技能窗职业变更；本菜单此前没有实现，不存在需要推断的历史次数。
        return query.ExecuteScalar() is long value ? checked((int)value) : 0;
    }

    public int LoadGrowUpChangeCount(AccountOwners.Lease owner, ushort characterId) => owner.Execute(() =>
    {
        using var db = database.Open();
        return ReadGrowUpChangeCount(db, null, characterId, owner.Account);
    });

    public GrowUpChangeCommit CommitGrowUpChange(AccountOwners.Lease owner, GrowUpChangePlan plan) => owner.Execute(() =>
    {
        var expected = plan.Expected;
        var wallet = plan.Inventory;
        plan.Learning.Validate();
        if (plan.Target is < 1 or > 5 || (expected.GrowType & 15) == plan.Target || (expected.GrowType & 15) == 0 ||
            (plan.GrowType & 15) != plan.Target || (plan.GrowType & 240) != (expected.GrowType & 240) ||
            plan.Learning.Experience != expected.Learning.Experience || plan.Learning.BonusSp != expected.Learning.BonusSp ||
            plan.Learning.BonusTp != expected.Learning.BonusTp || plan.Learning.ExpertContractExpiresAt != expected.Learning.ExpertContractExpiresAt ||
            plan.CountBefore is < 0 or >= GrowUpChangeService.MaximumCount || plan.Fee < 0 ||
            wallet.Reason != GrowUpChangeService.InventoryReason || !wallet.ChangedSlots.IsEmpty || wallet.OverflowItems.Length != 0 ||
            wallet.ChangedCurrencies.Any(kind => kind != CurrencyKind.Gold) ||
            (long)wallet.Before.Gold - wallet.After.Gold != plan.Fee || wallet.Changed != (plan.Fee != 0) ||
            plan.Completed.Any(id => id is <= 0 or >= 30000) || plan.Completed.Distinct().Count() != plan.Completed.Length)
            throw new InvalidDataException("职业变更计划改变了未授权字段");
        using var db = database.Open();
        using var tx = db.BeginTransaction();
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id || current.Revision != expected.Revision || current.Job != expected.Job ||
            current.GrowType != expected.GrowType || current.Level != expected.Level || current.Inventory.Gold != expected.Inventory.Gold ||
            !SameLearning(current.Learning, expected.Learning) ||
            !current.Skills.OrderBy(skill => skill.Id).SequenceEqual(expected.Skills.OrderBy(skill => skill.Id)) ||
            !current.CompletedQuests.SequenceEqual(expected.CompletedQuests) ||
            !current.ActiveMainQuests.SequenceEqual(expected.ActiveMainQuests))
            throw new InvalidOperationException("职业变更的角色、金币或学习快照已变化");
        if (ReadGrowUpChangeCount(db, tx, expected.Id, owner.Account) != plan.CountBefore)
            throw new InvalidOperationException("职业变更次数已变化");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        if (wallet.Changed)
            CommitInventoryInTransaction(db, tx, owner, expected, wallet);
        else
        {
            // 免费变更没有库存差额；仍单独推进revision，旧计划不能重复提交。
            command.Parameters.AddWithValue("$before", expected.Revision);
            command.CommandText = "UPDATE characters SET revision=revision+1 WHERE id=$id AND account=$account AND revision=$before";
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("职业变更的提交版本已变化");
            command.Parameters.RemoveAt("$before");
        }
        command.Parameters.AddWithValue("$revision", expected.Revision + 1);
        command.Parameters.AddWithValue("$grow", plan.GrowType);
        command.CommandText = "UPDATE characters SET grow_type=$grow WHERE id=$id AND account=$account AND revision=$revision";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("职业变更的提交版本已变化");
        WriteLearning(db, tx, expected.Id, plan.Learning);
        WriteCanonicalSkills(db, tx, expected.Id, plan.Learning);
        ResetSkillSwitchProfession(db, tx, expected.Id, expected.GrowType, plan.GrowType);
        foreach (int id in plan.Completed)
        {
            command.Parameters.AddWithValue("$quest", id);
            command.CommandText = "INSERT INTO character_completed_quests(character_id,quest_id,daily_period) VALUES($id,$quest,0)";
            command.ExecuteNonQuery();
            command.CommandText = "DELETE FROM character_active_main_quests WHERE character_id=$id AND quest_id=$quest";
            command.ExecuteNonQuery();
            command.Parameters.RemoveAt("$quest");
        }
        command.Parameters.AddWithValue("$countBefore", plan.CountBefore);
        command.Parameters.AddWithValue("$count", plan.CountBefore + 1);
        command.CommandText = """
            INSERT INTO character_growup_change(character_id,account,change_count) VALUES($id,$account,$count)
            ON CONFLICT(character_id) DO UPDATE SET change_count=excluded.change_count
            WHERE character_growup_change.account=excluded.account AND character_growup_change.change_count=$countBefore
            """;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("职业变更次数已变化");
        var committed = Require(db, tx, owner.Account, expected.Name);
        InvalidateQuestStages(db, tx, expected.Id, "growup_change");
        tx.Commit();
        return new GrowUpChangeCommit(committed, plan.CountBefore + 1);
    });
}
