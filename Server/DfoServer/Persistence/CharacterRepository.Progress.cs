using DfoServer.Game.Accounts;
using Microsoft.Data.Sqlite;
using DfoServer.Game.Characters;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    // 单字段进度编辑保留原合同；GM等级（保留技能重算余额）、转职（重建技能树）入口使用ResetProgress原子提交并递增revision。
    public Character UpdateProgress(AccountOwners.Lease owner, CharacterProgressEdit request) => owner.Execute(() =>
    {
        if (request.Account != owner.Account)
            throw new InvalidDataException("账号与所有者不一致");
        request.Validate();
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.UpdateProgress");
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, request.Name);
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$id", current.Id);
        cmd.Parameters.AddWithValue("$account", owner.Account);
        cmd.Parameters.AddWithValue("$revision", current.Revision);
        cmd.CommandText = "SELECT revision FROM characters WHERE id=$id AND account=$account AND revision=$revision";
        if (cmd.ExecuteScalar() is null)
            throw new InvalidOperationException("角色 revision 或所有者已变化");
        switch (request.Field)
        {
            case CharacterProgressField.BonusSp:
                if (!current.Learning.BonusSp.HasValue)
                    throw new InvalidDataException("旧角色点数来源尚未接管，请先重新进入角色；不覆盖未知余额");
                cmd.Parameters.AddWithValue("$bonus", request.Primary);
                cmd.CommandText = "UPDATE character_skill_point_sources SET bonus_sp=$bonus WHERE character_id=$id";
                if (cmd.ExecuteNonQuery() != 1) throw new InvalidDataException("额外SP来源缺失");
                break;
            case CharacterProgressField.BonusTp:
                if (!current.Learning.BonusTp.HasValue)
                    throw new InvalidDataException("旧角色TP来源尚未接管，请先重新进入角色；不覆盖未知余额");
                cmd.Parameters.AddWithValue("$bonus", request.Primary);
                cmd.CommandText = "UPDATE character_tp_sources SET bonus_tp=$bonus WHERE character_id=$id";
                if (cmd.ExecuteNonQuery() != 1) throw new InvalidDataException("额外TP来源缺失");
                break;
            case CharacterProgressField.ExpertContract:
                cmd.Parameters.AddWithValue("$expires", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (request.Primary == 0 ? -1L : request.Primary));
                cmd.CommandText = "INSERT INTO account_expert_contracts(account,expires_at) VALUES($account,$expires) ON CONFLICT(account) DO UPDATE SET expires_at=$expires";
                cmd.ExecuteNonQuery();
                break;
            case CharacterProgressField.Level:
                cmd.CommandText = "UPDATE characters SET level=$value WHERE id=$id AND account=$account AND revision=$revision";
                cmd.Parameters.AddWithValue("$value", request.Primary);
                if (cmd.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("角色 revision 或所有者已变化");
                // GM改等级属于排除来源：按改后有效位置改写里程，账号冒险团总量不变。
                CreditExcludedState(db, tx, owner.Account, current, request.Primary,
                    EffectivePosition(request.Primary, current.Learning.Experience));
                break;
            case CharacterProgressField.Story:
                cmd.CommandText = "UPDATE characters SET accepted_story_level=$value WHERE id=$id AND account=$account AND revision=$revision";
                cmd.Parameters.AddWithValue("$value", request.Primary);
                if (cmd.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("角色 revision 或所有者已变化");
                break;
            case CharacterProgressField.Grow:
                cmd.CommandText = "UPDATE characters SET grow_type=$value WHERE id=$id AND account=$account AND revision=$revision";
                cmd.Parameters.AddWithValue("$value", request.Primary);
                if (cmd.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("角色 revision 或所有者已变化");
                break;
            case CharacterProgressField.QuestAdd:
                // 完成标记与进行中槽同一事务：只写 completed 会让手册同时显示进行中和已完成，交付再被 AlreadyCompleted 拒绝。
                // daily_period=0 是永久完成，跨日 AdvanceQuestCycle 不会清掉；每日/重复必须由调用方写入当天周期。
                var quest = cmd.Parameters.Add("$quest", SqliteType.Integer);
                cmd.Parameters.AddWithValue("$period", request.DailyPeriod);
                foreach (int id in request.QuestIds)
                {
                    quest.Value = id;
                    cmd.CommandText = "DELETE FROM character_active_main_quests WHERE character_id=$id AND quest_id=$quest";
                    cmd.ExecuteNonQuery();
                    cmd.CommandText = "INSERT OR IGNORE INTO character_completed_quests(character_id,quest_id,daily_period) VALUES($id,$quest,$period)";
                    cmd.ExecuteNonQuery();
                    if (request.DailyPeriod > 0)
                    {
                        cmd.CommandText = "UPDATE character_completed_quests SET daily_period=$period WHERE character_id=$id AND quest_id=$quest AND daily_period=0";
                        cmd.ExecuteNonQuery();
                    }
                }
                break;
            case CharacterProgressField.QuestRemove:
                cmd.Parameters.AddWithValue("$quest", request.Primary);
                cmd.CommandText = "DELETE FROM character_completed_quests WHERE character_id=$id AND quest_id=$quest";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "DELETE FROM character_active_main_quests WHERE character_id=$id AND quest_id=$quest";
                cmd.ExecuteNonQuery();
                break;
            case CharacterProgressField.QuestClear:
                cmd.CommandText = "DELETE FROM character_completed_quests WHERE character_id=$id";
                cmd.ExecuteNonQuery();
                break;
            case CharacterProgressField.Skill:
                if (current.Learning.Initialized)
                {
                    if (request.Secondary != 0 && current.Learning.Trees.Any(tree =>
                        tree.Skills.All(skill => skill.Id != request.Primary)))
                        throw new InvalidDataException("已有双树的角色只能由学习业务新增绑定，GM不猜资源槽位");
                    if (request.Secondary == 0 && current.Learning.Trees.Any(tree =>
                        tree.Skills.Count == 1 && tree.Skills[0].Id == request.Primary))
                        throw new InvalidDataException("GM不能删除完整技能投影的最后绑定");
                }
                cmd.Parameters.AddWithValue("$skill", request.Primary);
                cmd.Parameters.AddWithValue("$level", request.Secondary);
                if (request.Secondary == 0)
                {
                    cmd.CommandText = "DELETE FROM character_skills WHERE character_id=$id AND skill_id=$skill";
                    cmd.ExecuteNonQuery();
                    cmd.CommandText = "DELETE FROM character_skill_bindings WHERE character_id=$id AND skill_id=$skill";
                    cmd.ExecuteNonQuery();
                    if (current.Learning.Initialized)
                    {
                        var learning = current.Learning with { Trees = Array.AsReadOnly(current.Learning.Trees.Select(tree => tree with
                        {
                            Skills = Array.AsReadOnly(tree.Skills.Where(skill => skill.Id != request.Primary).ToArray()),
                            Combos = Array.AsReadOnly(tree.Combos.Where(combo => combo.Root != request.Primary).Select(combo => combo with
                            { Children = Array.AsReadOnly(combo.Children.Where(id => id != request.Primary).ToArray()) }).ToArray())
                        }).ToArray()) };
                        WriteLearning(db, tx, current.Id, learning);
                    }
                    break;
                }

                cmd.CommandText = "SELECT count(*) FROM character_skills WHERE character_id=$id AND skill_id=$skill";
                if ((long)cmd.ExecuteScalar()! == 0)
                {
                    cmd.CommandText = "SELECT count(*) FROM character_skills WHERE character_id=$id";
                    if ((long)cmd.ExecuteScalar()! >= 255)
                        throw new InvalidDataException("技能数量已达 255");
                }

                cmd.CommandText = "INSERT INTO character_skills(character_id,skill_id,level) VALUES($id,$skill,$level) ON CONFLICT(character_id,skill_id) DO UPDATE SET level=$level";
                cmd.ExecuteNonQuery();
                // 工具覆盖已有等级时同步现有绑定，不修改SP/TP；普通学习仍由独立事务处理。
                cmd.CommandText = "UPDATE character_skill_bindings SET level=$level WHERE character_id=$id AND skill_id=$skill";
                cmd.ExecuteNonQuery();
                break;
            default:
                throw new InvalidDataException("不是写入命令");
        }

        if (request.Field == CharacterProgressField.Grow)
            ResetSkillSwitchProfession(db, tx, current.Id, current.GrowType, checked((byte)request.Primary));
        var updated = Require(db, tx, owner.Account, request.Name);
        if (updated.Level != current.Level || updated.GrowType != current.GrowType ||
            updated.AcceptedStoryLevel != current.AcceptedStoryLevel || !updated.CompletedQuests.SequenceEqual(current.CompletedQuests))
            InvalidateQuestStages(db, tx, current.Id, "progress_edit");
        tx.Commit();
        return updated;
    });

    public Character ResetProgress(AccountOwners.Lease owner, CharacterProgressResetPlan plan) => owner.Execute(() =>
    {
        plan.Learning.Validate();
        if (!plan.Learning.Initialized || plan.Level is < 1 or > 95 || plan.GrowType > 127 ||
            plan.Learning.SelectedTree != plan.Before.Learning.SelectedTree ||
            plan.Learning.BonusSp != (plan.Before.Learning.BonusSp ?? 0) || plan.Learning.BonusTp != (plan.Before.Learning.BonusTp ?? 0) ||
            plan.Learning.ExpertContractExpiresAt != plan.Before.Learning.ExpertContractExpiresAt)
            throw new InvalidDataException("技能重置规划或额外来源无效");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.ResetProgress");
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, plan.Before.Name);
        if (current.Id != plan.Before.Id || current.Revision != plan.Before.Revision || current.Job != plan.Before.Job ||
            current.Level != plan.Before.Level || current.GrowType != plan.Before.GrowType ||
            !current.Skills.SequenceEqual(plan.Before.Skills) || !SameLearning(current.Learning, plan.Before.Learning))
            throw new InvalidOperationException("角色或技能快照已变化，请重试");
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE characters SET level=$level,grow_type=$grow,experience=$experience,revision=revision+1 WHERE id=$id AND account=$account AND revision=$revision";
        cmd.Parameters.AddWithValue("$id", current.Id);
        cmd.Parameters.AddWithValue("$account", owner.Account);
        cmd.Parameters.AddWithValue("$revision", current.Revision);
        cmd.Parameters.AddWithValue("$level", plan.Level);
        cmd.Parameters.AddWithValue("$grow", plan.GrowType);
        cmd.Parameters.AddWithValue("$experience", checked((long)plan.Learning.Experience));
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("角色revision或所有者已变化");
        WriteLearning(db, tx, current.Id, plan.Learning);
        WriteCanonicalSkills(db, tx, current.Id, plan.Learning);
        ResetSkillSwitchProfession(db, tx, current.Id, current.GrowType, plan.GrowType);
        // GM等级/转职重置属于排除来源：改写里程但不改变账号冒险团总量。
        CreditExcludedState(db, tx, owner.Account, current, plan.Level, EffectivePosition(plan.Level, plan.Learning.Experience));
        var updated = Require(db, tx, owner.Account, current.Name);
        if (updated.Level != current.Level || updated.GrowType != current.GrowType ||
            updated.Learning.Experience != current.Learning.Experience)
            InvalidateQuestStages(db, tx, current.Id, "progress_reset");
        tx.Commit();
        return updated;
    });
    Character Require(SqliteConnection db, SqliteTransaction tx, string account, string name)
    {
        Character character;
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT * FROM characters WHERE account=$account AND name=$name";
            cmd.Parameters.AddWithValue("$account", account);
            cmd.Parameters.AddWithValue("$name", name);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                throw new InvalidDataException("角色不存在");
            character = Read(reader);
        }

        return ReadProgress(db, character, tx);
    }
}
