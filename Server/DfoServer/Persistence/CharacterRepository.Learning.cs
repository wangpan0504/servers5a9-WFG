using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public Character CommitLearning(AccountOwners.Lease owner, Character expected, CharacterLearningState learning) => owner.Execute(() =>
    {
        learning.Validate();
        if (!learning.Initialized || learning.Experience != expected.Learning.Experience)
            throw new InvalidDataException("学习事务必须包含两树且不能修改经验");
        if (!expected.Learning.Initialized && learning.Trees.Any(tree => tree.Sp != 0 || tree.Tp != 0))
            throw new InvalidDataException("初始化技能树不能补发点数");
        if (expected.Learning.Initialized && learning.Trees.Any(tree =>
            tree.Sp > expected.Learning.Trees.Single(old => old.Context == tree.Context).Sp ||
            tree.Tp != expected.Learning.Trees.Single(old => old.Context == tree.Context).Tp))
            throw new InvalidDataException("普通SP学习不能发点或修改TP");
        using var db = database.Open();
        using var transaction = db.BeginTransaction();
        Character current;
        try
        {
            current = Require(db, transaction, owner.Account, expected.Name);
        }
        catch (InvalidDataException)
        {
            throw new InvalidOperationException("Character revision or owner changed");
        }
        // GM不递增revision，故同时比较学习前提，禁止旧计划覆盖已提交工具进度。
        if (current.Id != expected.Id || current.Revision != expected.Revision || current.Job != expected.Job ||
            current.GrowType != expected.GrowType || current.Level != expected.Level ||
            !current.Skills.OrderBy(skill => skill.Id).SequenceEqual(expected.Skills.OrderBy(skill => skill.Id)) ||
            !SameLearning(current.Learning, expected.Learning))
            throw new InvalidOperationException("Character learning snapshot changed");
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE characters SET revision=revision+1 WHERE id=$id AND account=$account AND revision=$revision";
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$revision", expected.Revision);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Character revision or owner changed");
        WriteLearning(db, transaction, expected.Id, learning);
        // context0为既有角色技能投影及工具读取的主树；费用与全部绑定同事务写入。
        CharacterSkill[] skills = learning.Trees.Single(tree => tree.Context == 0).Skills
            .Select(skill => new CharacterSkill(skill.Id, skill.Level)).OrderBy(skill => skill.Id).ToArray();
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$id", expected.Id);
        command.CommandText = "DELETE FROM character_skills WHERE character_id=$id";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO character_skills(character_id,skill_id,level) VALUES($id,$skill,$level)";
        command.Parameters.AddWithValue("$skill", 0);
        command.Parameters.AddWithValue("$level", 0);
        foreach (var skill in skills)
        {
            command.Parameters["$skill"].Value = skill.Id;
            command.Parameters["$level"].Value = skill.Level;
            command.ExecuteNonQuery();
        }
        CharacterLearningState committed = ReadLearning(db, expected.Id, transaction);
        transaction.Commit();
        return expected with { Revision = expected.Revision + 1, Skills = skills, Learning = committed };
    });

    private static bool SameLearning(CharacterLearningState current, CharacterLearningState expected) =>
        current.Experience == expected.Experience && current.Trees.Count == expected.Trees.Count &&
        current.Trees.OrderBy(tree => tree.Context).Zip(expected.Trees.OrderBy(tree => tree.Context)).All(pair =>
            pair.First.Context == pair.Second.Context && pair.First.Sp == pair.Second.Sp && pair.First.Tp == pair.Second.Tp &&
            pair.First.Skills.OrderBy(skill => skill.Slot).SequenceEqual(pair.Second.Skills.OrderBy(skill => skill.Slot)));

    private static void WriteLearning(SqliteConnection db, SqliteTransaction transaction, ushort id, CharacterLearningState learning)
    {
        learning.Validate();
        if (!learning.Initialized)
            return;
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", id);
        command.CommandText = "DELETE FROM character_skill_bindings WHERE character_id=$id";
        command.ExecuteNonQuery();
        command.CommandText = "DELETE FROM character_skill_points WHERE character_id=$id";
        command.ExecuteNonQuery();
        foreach (var tree in learning.Trees.OrderBy(tree => tree.Context))
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$context", tree.Context);
            command.Parameters.AddWithValue("$sp", tree.Sp);
            command.Parameters.AddWithValue("$tp", tree.Tp);
            command.CommandText = "INSERT INTO character_skill_points(character_id,context,sp,tp) VALUES($id,$context,$sp,$tp)";
            command.ExecuteNonQuery();
            command.Parameters.RemoveAt("$sp");
            command.Parameters.RemoveAt("$tp");
            command.Parameters.AddWithValue("$slot", 0);
            command.Parameters.AddWithValue("$skill", 0);
            command.Parameters.AddWithValue("$level", 0);
            command.CommandText = "INSERT INTO character_skill_bindings(character_id,context,slot,skill_id,level) VALUES($id,$context,$slot,$skill,$level)";
            foreach (var skill in tree.Skills.OrderBy(skill => skill.Slot))
            {
                command.Parameters["$slot"].Value = skill.Slot;
                command.Parameters["$skill"].Value = skill.Id;
                command.Parameters["$level"].Value = skill.Level;
                command.ExecuteNonQuery();
            }
        }
    }

    private static CharacterLearningState ReadLearning(SqliteConnection db, ushort id, SqliteTransaction? transaction)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", id);
        command.CommandText = "SELECT experience FROM characters WHERE id=$id";
        ulong experience = checked((ulong)(long)command.ExecuteScalar()!);
        command.CommandText = """
            SELECT p.context,p.sp,p.tp,b.slot,b.skill_id,b.level
            FROM character_skill_points p LEFT JOIN character_skill_bindings b
            ON b.character_id=p.character_id AND b.context=p.context
            WHERE p.character_id=$id ORDER BY p.context,b.slot
            """;
        var balances = new Dictionary<byte, (ushort Sp, ushort Tp, List<LearnedSkill> Skills)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            byte context = checked((byte)reader.GetInt32(0));
            ushort sp = checked((ushort)reader.GetInt32(1));
            ushort tp = checked((ushort)reader.GetInt32(2));
            if (!balances.TryGetValue(context, out var tree))
            {
                tree = (sp, tp, []);
                balances.Add(context, tree);
            }
            if (!reader.IsDBNull(3))
                tree.Skills.Add(new(checked((byte)reader.GetInt32(3)), checked((ushort)reader.GetInt32(4)), checked((byte)reader.GetInt32(5))));
        }
        if (balances.Count == 0 && experience == 0)
            return CharacterLearningState.Uninitialized;
        var state = new CharacterLearningState(experience, Array.AsReadOnly(balances.OrderBy(pair => pair.Key)
            .Select(pair => new CharacterSkillTree(pair.Key, pair.Value.Sp, pair.Value.Tp, pair.Value.Skills.AsReadOnly())).ToArray()));
        state.Validate();
        return state;
    }
}
