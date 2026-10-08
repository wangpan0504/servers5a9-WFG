using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    // reward必须来自明确业务规则。此入口不生成额度，不接诊断死亡或网络字段。
    // 成功返回后调用方在原owner内确认死亡并发布Selected；这里不修改实例或发送通知。
    public DungeonGrowthCommit CommitDungeonGrowth(AccountOwners.Lease owner, Character expected,
        SinglePlayerDungeonRun run, PreparedDungeonDeath prepared, DungeonExperienceReward reward, CharacterGrowthCatalog catalog,
        FrozenDungeonGroundDeath? loot = null) => owner.Execute(() =>
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(reward);
        ArgumentNullException.ThrowIfNull(catalog);
        ulong amount = reward.Amount;
        if (!run.IsPendingDeath(prepared) || prepared.Death.Identity.CharacterId != expected.Id || amount == 0 ||
            string.IsNullOrWhiteSpace(reward.RuleId) || reward.RuleId.Length > 128 || reward.MonsterId != prepared.Monster.MonsterId ||
            reward.MonsterLevel != prepared.Monster.CandidateLevel || reward.ResourceSource != DungeonKillExperienceCatalog.ResourcePath)
            throw new InvalidDataException("成长提交需要本角色当前实例的待提交死亡和明确正经验");
        var death = prepared.Death;
        var identity = death.Identity;
        using var db = database.Open();
        // 精确重放只读receipt，不申请writer；新事实升级写事务失败时由原ID恢复。
        using var tx = db.BeginTransaction(deferred: true);
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id)
            throw new InvalidOperationException("成长角色或账号已变化");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$event", death.EventId.ToString("N"));
        command.CommandText = """
            SELECT account,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,combat_sequence,amount,
              rule_id,resource_source,monster_id,monster_level
            FROM character_dungeon_growth_receipts WHERE character_id=$id AND event_id=$event
            """;
        bool duplicate;
        using (var reader = command.ExecuteReader())
        {
            duplicate = reader.Read();
            if (duplicate && (reader.GetString(0) != owner.Account || reader.GetString(1) != identity.InstanceId.ToString("N") ||
                reader.GetInt64(2) != identity.QuestId || reader.GetInt64(3) != identity.TaskAttempt || reader.GetInt64(4) != identity.Epoch ||
                reader.GetInt64(5) != prepared.Result.Snapshot.CurrentMap.Id || reader.GetInt64(6) != death.MonsterKey ||
                reader.GetInt64(7) != death.CombatSequence || reader.GetInt64(8) != checked((long)amount) ||
                reader.GetString(9) != reward.RuleId || reader.GetString(10) != reward.ResourceSource ||
                reader.GetInt64(11) != reward.MonsterId || reader.GetInt64(12) != reward.MonsterLevel))
                throw new InvalidDataException("同一成长事件ID的来源或奖励被修改");
        }
        // 同ID重读返回当前持久角色；不根据旧expected再次加经验，也不重发清图事实。
        if (duplicate)
        {
            VerifyDungeonLoot(db, tx, owner.Account, expected.Id, prepared.Death.EventId, loot);
            tx.Commit();
            return new DungeonGrowthCommit(current, true);
        }
        var result = CharacterGrowthPlan.AddExperience(expected, amount, catalog);
        if (!result.Success)
            throw new InvalidDataException("成长候选被拒绝: " + result.Failure);
        var plan = result.Plan!;
        if (current.Revision != plan.Revision || current.Level != plan.PreviousLevel || current.Job != plan.Job ||
            current.GrowType != plan.GrowType || !SameLearning(current.Learning, plan.Before) ||
            !current.Skills.OrderBy(skill => skill.Id).SequenceEqual(expected.Skills.OrderBy(skill => skill.Id)))
            throw new InvalidOperationException("成长revision或完整学习前提已变化");
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$revision", plan.Revision);
        command.Parameters.AddWithValue("$level", plan.TargetLevel);
        command.Parameters.AddWithValue("$experience", checked((long)plan.Learning.Experience));
        command.CommandText = """
            UPDATE characters SET level=$level,experience=$experience,revision=revision+1
            WHERE id=$id AND account=$account AND revision=$revision
            """;
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("成长revision或账号已变化");
        // 自动等级、双树余额和既有主树投影随同一经验事实提交。
        WriteLearning(db, tx, expected.Id, plan.Learning);
        WriteCanonicalSkills(db, tx, expected.Id, plan.Learning);
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$event", death.EventId.ToString("N"));
        command.Parameters.AddWithValue("$instance", identity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$quest", identity.QuestId);
        command.Parameters.AddWithValue("$attempt", identity.TaskAttempt);
        command.Parameters.AddWithValue("$epoch", identity.Epoch);
        command.Parameters.AddWithValue("$map", prepared.Result.Snapshot.CurrentMap.Id);
        command.Parameters.AddWithValue("$key", death.MonsterKey);
        command.Parameters.AddWithValue("$sequence", death.CombatSequence);
        command.Parameters.AddWithValue("$generation", owner.Generation);
        command.Parameters.AddWithValue("$amount", checked((long)amount));
        command.Parameters.AddWithValue("$rule", reward.RuleId);
        command.Parameters.AddWithValue("$resource", reward.ResourceSource);
        command.Parameters.AddWithValue("$monster", reward.MonsterId);
        command.Parameters.AddWithValue("$monsterLevel", reward.MonsterLevel);
        command.Parameters.AddWithValue("$beforeLevel", plan.PreviousLevel);
        command.Parameters.AddWithValue("$afterLevel", plan.TargetLevel);
        command.Parameters.AddWithValue("$beforeExperience", checked((long)plan.Before.Experience));
        command.Parameters.AddWithValue("$afterExperience", checked((long)plan.Learning.Experience));
        command.Parameters.AddWithValue("$revision", checked(plan.Revision + 1));
        command.CommandText = """
            INSERT INTO character_dungeon_growth_receipts(character_id,account,event_id,instance_id,quest_id,
              task_attempt,epoch,map_id,monster_key,combat_sequence,generation,amount,
              rule_id,resource_source,monster_id,monster_level,before_level,after_level,before_experience,after_experience,revision)
            VALUES($id,$account,$event,$instance,$quest,$attempt,$epoch,$map,$key,$sequence,$generation,$amount,
              $rule,$resource,$monster,$monsterLevel,$beforeLevel,$afterLevel,$beforeExperience,$afterExperience,$revision)
            """;
        command.ExecuteNonQuery();
        SaveDungeonLoot(db, tx, owner.Account, expected, prepared, loot);
        var committed = Require(db, tx, owner.Account, expected.Name);
        if (committed.Level != plan.TargetLevel || committed.Revision != plan.Revision + 1 || !SameLearning(committed.Learning, plan.Learning))
            throw new InvalidOperationException("持久成长结果与候选不一致");
        tx.Commit();
        return new DungeonGrowthCommit(committed, false);
    });
}
