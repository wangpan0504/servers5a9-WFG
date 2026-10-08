using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    // 通关只提交冻结的经验，不重新计算击杀奖励，也不生成翻牌物品、金币或费用。
    public DungeonSettlementCommit CommitDungeonSettlement(AccountOwners.Lease owner, Character expected,
        SinglePlayerDungeonRun run, PreparedDungeonSettlement prepared, CharacterGrowthCatalog catalog) => owner.Execute(() =>
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(catalog);
        if (!run.IsPendingSettlement(prepared) || prepared.Fact.Identity.CharacterId != expected.Id ||
            prepared.Fact.DungeonId != run.DungeonId || prepared.Fact.ConfirmedDeathEvents.Count == 0)
            throw new InvalidDataException("通关提交需要本角色当前实例的冻结清图事实与首份报告");
        var identity = prepared.Fact.Identity;
        var reward = prepared.Reward;
        byte[] deaths = prepared.Fact.ConfirmedDeathEvents.SelectMany(value => value.ToByteArray()).ToArray();
        using var db = database.Open();
        // 同份冻结报告的恢复只读账本；新报告在同一事务保存经验、双树SP和receipt。
        using var tx = db.BeginTransaction(deferred: true);
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id || current.DungeonState?.LastAttempt != identity.TaskAttempt)
            throw new InvalidOperationException("通关角色、账号或副本尝试已变化");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$instance", identity.InstanceId.ToString("N"));
        command.CommandText = """
            SELECT account,event_id,quest_id,task_attempt,epoch,dungeon_id,map_id,death_events,report_body,
              base_experience,rank_experience,total_experience,rank_bonus_index,rule_id,resource_source
            FROM character_dungeon_settlement_receipts WHERE character_id=$id AND instance_id=$instance
            """;
        bool duplicate;
        using (var reader = command.ExecuteReader())
        {
            duplicate = reader.Read();
            if (duplicate && (reader.GetString(0) != owner.Account || reader.GetString(1) != prepared.EventId.ToString("N") ||
                reader.GetInt64(2) != identity.QuestId || reader.GetInt64(3) != identity.TaskAttempt ||
                reader.GetInt64(4) != identity.Epoch || reader.GetInt64(5) != prepared.Fact.DungeonId ||
                reader.GetInt64(6) != prepared.MapId || !((byte[])reader[7]).AsSpan().SequenceEqual(deaths) ||
                !((byte[])reader[8]).AsSpan().SequenceEqual(prepared.ReportBody) ||
                reader.GetInt64(9) != reward.BaseExperience || reader.GetInt64(10) != reward.RankExperience ||
                reader.GetInt64(11) != reward.TotalExperience || reader.GetInt64(12) != reward.RankBonusIndex ||
                reader.GetString(13) != reward.RuleId || reader.GetString(14) != reward.ResourceSource))
                throw new InvalidDataException("同一通关实例的事实、报告或奖励被修改");
        }
        if (duplicate)
        {
            tx.Commit();
            return new DungeonSettlementCommit(current, true, reward);
        }
        var result = CharacterGrowthPlan.AddExperience(expected, reward.TotalExperience, catalog);
        if (!result.Success)
            throw new InvalidDataException("通关成长候选被拒绝: " + result.Failure);
        var plan = result.Plan!;
        if (current.Revision != plan.Revision || current.Level != plan.PreviousLevel || current.Job != plan.Job ||
            current.GrowType != plan.GrowType || !SameLearning(current.Learning, plan.Before) ||
            !current.Skills.OrderBy(skill => skill.Id).SequenceEqual(expected.Skills.OrderBy(skill => skill.Id)))
            throw new InvalidOperationException("通关成长revision或完整学习前提已变化");
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$revision", plan.Revision);
        command.Parameters.AddWithValue("$level", plan.TargetLevel);
        command.Parameters.AddWithValue("$experience", checked((long)plan.Learning.Experience));
        command.CommandText = """
            UPDATE characters SET level=$level,experience=$experience,revision=revision+1
            WHERE id=$id AND account=$account AND revision=$revision
            """;
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("通关成长revision或账号已变化");
        command.Parameters.AddWithValue("$context", 0);
        command.Parameters.AddWithValue("$sp", 0);
        command.CommandText = "UPDATE character_skill_points SET sp=$sp WHERE character_id=$id AND context=$context";
        foreach (var tree in plan.Learning.Trees)
        {
            if (tree.Sp == plan.Before.Trees.Single(before => before.Context == tree.Context).Sp)
                continue;
            command.Parameters["$context"].Value = tree.Context;
            command.Parameters["$sp"].Value = tree.Sp;
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("通关成长技能树余额行不完整");
        }
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$event", prepared.EventId.ToString("N"));
        command.Parameters.AddWithValue("$instance", identity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$quest", identity.QuestId);
        command.Parameters.AddWithValue("$attempt", identity.TaskAttempt);
        command.Parameters.AddWithValue("$epoch", identity.Epoch);
        command.Parameters.AddWithValue("$dungeon", prepared.Fact.DungeonId);
        command.Parameters.AddWithValue("$map", prepared.MapId);
        command.Parameters.AddWithValue("$deaths", deaths);
        command.Parameters.AddWithValue("$report", prepared.ReportBody.ToArray());
        command.Parameters.AddWithValue("$generation", owner.Generation);
        command.Parameters.AddWithValue("$base", reward.BaseExperience);
        command.Parameters.AddWithValue("$rank", reward.RankExperience);
        command.Parameters.AddWithValue("$total", reward.TotalExperience);
        command.Parameters.AddWithValue("$bonus", reward.RankBonusIndex);
        command.Parameters.AddWithValue("$rule", reward.RuleId);
        command.Parameters.AddWithValue("$resource", reward.ResourceSource);
        command.Parameters.AddWithValue("$beforeLevel", plan.PreviousLevel);
        command.Parameters.AddWithValue("$afterLevel", plan.TargetLevel);
        command.Parameters.AddWithValue("$beforeExperience", checked((long)plan.Before.Experience));
        command.Parameters.AddWithValue("$afterExperience", checked((long)plan.Learning.Experience));
        command.Parameters.AddWithValue("$revision", checked(plan.Revision + 1));
        command.CommandText = """
            INSERT INTO character_dungeon_settlement_receipts(character_id,account,event_id,instance_id,quest_id,
              task_attempt,epoch,dungeon_id,map_id,death_events,report_body,generation,base_experience,rank_experience,
              total_experience,rank_bonus_index,rule_id,resource_source,before_level,after_level,
              before_experience,after_experience,revision)
            VALUES($id,$account,$event,$instance,$quest,$attempt,$epoch,$dungeon,$map,$deaths,$report,$generation,
              $base,$rank,$total,$bonus,$rule,$resource,$beforeLevel,$afterLevel,$beforeExperience,$afterExperience,$revision)
            """;
        command.ExecuteNonQuery();
        var committed = Require(db, tx, owner.Account, expected.Name);
        if (committed.Level != plan.TargetLevel || committed.Revision != plan.Revision + 1 || !SameLearning(committed.Learning, plan.Learning))
            throw new InvalidOperationException("持久通关成长结果与候选不一致");
        tx.Commit();
        return new DungeonSettlementCommit(committed, false, reward);
    });
}
