using DfoServer.Game.Accounts;
using Microsoft.Data.Sqlite;
using DfoServer.Game.Characters;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    // 进度编辑只改进度列。不增加 revision，这样城镇里的移动保存仍能按原 revision 提交位置。
    public Character UpdateProgress(AccountOwners.Lease owner, CharacterProgressEdit request) => owner.Execute(() =>
    {
        if (request.Account != owner.Account)
            throw new InvalidDataException("账号与所有者不一致");
        request.Validate();
        using var db = database.Open();
        using var tx = db.BeginTransaction();
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
            case CharacterProgressField.Level:
                cmd.CommandText = "UPDATE characters SET level=$value WHERE id=$id AND account=$account AND revision=$revision";
                cmd.Parameters.AddWithValue("$value", request.Primary);
                if (cmd.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("角色 revision 或所有者已变化");
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
                cmd.CommandText = "INSERT OR IGNORE INTO character_completed_quests(character_id,quest_id) VALUES($id,$quest)";
                cmd.Parameters.AddWithValue("$quest", request.Primary);
                cmd.ExecuteNonQuery();
                break;
            case CharacterProgressField.QuestRemove:
                cmd.CommandText = "DELETE FROM character_completed_quests WHERE character_id=$id AND quest_id=$quest";
                cmd.Parameters.AddWithValue("$quest", request.Primary);
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

        var updated = Require(db, tx, owner.Account, request.Name);
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
