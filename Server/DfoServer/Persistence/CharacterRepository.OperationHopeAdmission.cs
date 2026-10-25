using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    // 只参与原入场写事务；调用方先预留尝试号，之后任务、疲劳和入场票一起提交。
    private void CommitHopeAdmission(SqliteConnection db,SqliteTransaction tx,AccountOwners.Lease owner,
        Character expected,DungeonEntryCharge charge)
    {
        if (charge.Hope is not { } admission)
        {
            if (charge.DungeonId == 8523) throw new InvalidDataException("希望入场必须绑定本次选定任务");
            return;
        }
        var acceptance = admission.Acceptance;
        if (charge.DungeonId != 8523 || admission.SourceRevision != expected.Revision || charge.Identity.CharacterId != expected.Id ||
            expected.DungeonState is not { } previous || charge.Identity.TaskAttempt != previous.LastAttempt+1 ||
            acceptance.Slot >= 30 || acceptance.AcceptanceId == Guid.Empty || acceptance.ConditionState != 1 ||
            expected.ActiveMainQuests.Any(row => row.Slot == acceptance.Slot || row.AcceptanceId == acceptance.AcceptanceId ||
                admission.Catalog.Missions.ContainsKey(row.QuestId)) ||
            !admission.Catalog.Maps.ContainsKey(charge.MapId) ||
            OperationHopePlan.TryBind(admission.Catalog,acceptance.QuestId,admission.MazeOrdinal,expected.Level,charge.Identity,acceptance) is null)
            throw new InvalidDataException("希望任务必须来自本次原角色、空槽和可达资源候选");
        var current = Require(db,tx,owner.Account,expected.Name);
        long reservedRevision = checked(expected.Revision+1+(charge.Items?.Changed == true ? 1 : 0));
        if (current.Id != expected.Id || current.Revision != reservedRevision || current.Level != expected.Level ||
            current.Job != expected.Job || current.GrowType != expected.GrowType || current.Position != expected.Position ||
            current.DungeonState?.LastAttempt != charge.Identity.TaskAttempt ||
            !current.ActiveMainQuests.SequenceEqual(expected.ActiveMainQuests) || !current.CompletedQuests.SequenceEqual(expected.CompletedQuests))
            throw new InvalidOperationException("希望接取没有紧随原入场身份预留");
        using var command = db.CreateCommand(); command.Transaction = tx;
        foreach (var (name,value) in new (string,object)[] { ("$id",expected.Id),("$account",owner.Account),
            ("$instance",charge.Identity.InstanceId.ToString("N")),("$acceptance",acceptance.AcceptanceId.ToString("N")),
            ("$slot",acceptance.Slot),("$quest",acceptance.QuestId) }) command.Parameters.AddWithValue(name,value);
        command.CommandText = """
            SELECT count(*) FROM character_operation_hope_reward_receipts
            WHERE character_id=$id AND account=$account AND (instance_id=$instance OR acceptance_id=$acceptance)
            """;
        if (Convert.ToInt64(command.ExecuteScalar()) != 0) throw new InvalidOperationException("希望入场不能重用已领奖实例或接取");
        command.CommandText = """
            INSERT INTO character_active_main_quests(character_id,slot,quest_id,condition_state,acceptance_id)
            VALUES($id,$slot,$quest,1,$acceptance)
            """;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("希望任务接取没有写入原角色");
    }

    // 断线重选或退出时移除目录明确声明的孤立实例任务，绝不删除普通完成历史或无关接取。
    public Character ClearOperationHopeAcceptances(AccountOwners.Lease owner,Character expected,OperationHopeCatalog catalog,
        IReadOnlyList<ActiveMainQuest> removals) => owner.Execute(() =>
    {
        using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.ClearOperationHopeAcceptances",deferred: true);
        using var tx = write.Transaction;
        var current = Require(db,tx,owner.Account,expected.Name);
        if (current.Id != expected.Id || current.Revision != expected.Revision ||
            !current.ActiveMainQuests.SequenceEqual(expected.ActiveMainQuests) || !current.CompletedQuests.SequenceEqual(expected.CompletedQuests))
            throw new InvalidOperationException("希望任务清理的角色、版本或原活动任务已变化");
        var removed = removals.ToArray();
        if (removed.Any(row => row.AcceptanceId == Guid.Empty || !catalog.Missions.ContainsKey(row.QuestId) ||
            !current.ActiveMainQuests.Contains(row)) || removed.Select(row => row.AcceptanceId).Distinct().Count() != removed.Length)
            throw new InvalidDataException("希望任务清理只能指定原角色的精确实例接取");
        var ids = removed.Select(row => row.AcceptanceId).ToHashSet();
        if (removed.Length == 0) return current;
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$id",current.Id); command.Parameters.AddWithValue("$account",owner.Account);
        command.Parameters.AddWithValue("$revision",current.Revision);
        command.CommandText = "UPDATE characters SET revision=revision+1 WHERE id=$id AND account=$account AND revision=$revision";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("希望任务清理版本冲突");
        foreach (var active in removed)
        {
            command.Parameters.AddWithValue("$slot",active.Slot); command.Parameters.AddWithValue("$quest",active.QuestId);
            command.Parameters.AddWithValue("$acceptance",active.AcceptanceId.ToString("N")); command.Parameters.AddWithValue("$scalar",active.ConditionState);
            command.CommandText = """
                DELETE FROM character_active_main_quests
                WHERE character_id=$id AND slot=$slot AND quest_id=$quest AND acceptance_id=$acceptance AND condition_state=$scalar
                """;
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("希望任务清理不能替换原接取");
            foreach (string name in new[] { "$slot","$quest","$acceptance","$scalar" }) command.Parameters.RemoveAt(name);
        }
        var committed = Require(db,tx,owner.Account,expected.Name);
        if (committed.Revision != current.Revision+1 || !committed.ActiveMainQuests.SequenceEqual(
            current.ActiveMainQuests.Where(row => !ids.Contains(row.AcceptanceId))) ||
            !committed.CompletedQuests.SequenceEqual(current.CompletedQuests))
            throw new InvalidOperationException("希望任务清理影响了无关进度");
        tx.Commit();
        return committed;
    });
}
