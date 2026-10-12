using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public Character CommitSkillLayout(AccountOwners.Lease owner, Character expected, CharacterLearningState learning)
    {
        learning.Validate();
        if (!expected.Learning.Initialized || !learning.Initialized || learning.Experience != expected.Learning.Experience ||
            learning.BonusSp != expected.Learning.BonusSp || learning.BonusTp != expected.Learning.BonusTp || learning.ExpertContractExpiresAt != expected.Learning.ExpertContractExpiresAt ||
            learning.Trees.Any(tree =>
            {
                var before = expected.Learning.Trees.Single(value => value.Context == tree.Context);
                return tree.Sp != before.Sp || tree.Tp != before.Tp || !tree.SameCombos(before) || !tree.Skills.OrderBy(skill => skill.Id)
                    .Select(skill => (skill.Id, skill.Level, skill.Command)).SequenceEqual(before.Skills.OrderBy(skill => skill.Id)
                        .Select(skill => (skill.Id, skill.Level, skill.Command)));
            }))
            throw new InvalidDataException("换槽只能改变绑定位置，不能修改经验、点数或技能等级");
        // 复用既有owner/CAS与完整技能事务，避免另建槽位存档或独立写事务。
        return CommitLearning(owner, expected, learning);
    }

    public Character CommitLearning(AccountOwners.Lease owner, Character expected, CharacterLearningState learning, int? totalSp = null, int? totalTp = null) => owner.Execute(() =>
    {
        learning.Validate();
        if (!learning.Initialized || learning.Experience != expected.Learning.Experience)
            throw new InvalidDataException("学习事务必须包含两树且不能修改经验");
        if (totalSp is < 0 or > 65535 || totalSp.HasValue && (!learning.BonusSp.HasValue || learning.Trees.Any(tree => tree.Sp > totalSp.Value)))
            throw new InvalidDataException("技能余额超出规划的总来源");
        if (totalTp is < 0 or > 65535 || totalTp.HasValue && (!learning.BonusTp.HasValue || learning.Trees.Any(tree => tree.Tp > totalTp.Value)))
            throw new InvalidDataException("TP余额超出规划来源");
        if (!expected.Learning.Initialized && learning.Trees.Any(tree => tree.Tp != 0 ||
            tree.Sp != 0 && !(expected.Level == 1 && totalSp.HasValue)))
            throw new InvalidDataException("初始化技能树不能补发点数");
        if (expected.Learning.Initialized && (expected.Learning.BonusSp.HasValue && learning.BonusSp != expected.Learning.BonusSp ||
            expected.Learning.BonusTp.HasValue && learning.BonusTp != expected.Learning.BonusTp ||
            learning.ExpertContractExpiresAt != expected.Learning.ExpertContractExpiresAt || learning.Trees.Any(tree =>
                !totalSp.HasValue && tree.Sp > expected.Learning.Trees.Single(old => old.Context == tree.Context).Sp ||
                (!totalTp.HasValue && tree.Tp != expected.Learning.Trees.Single(old => old.Context == tree.Context).Tp))))
            throw new InvalidDataException("普通技能事务不能修改额外来源或TP；未知来源不允许退点");
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
        CharacterSkill[] skills = WriteCanonicalSkills(db, transaction, expected.Id, learning);
        CharacterLearningState committed = ReadLearning(db, expected.Id, transaction);
        transaction.Commit();
        return expected with { Revision = expected.Revision + 1, Skills = skills, Learning = committed };
    });

    private static CharacterSkill[] WriteCanonicalSkills(SqliteConnection db, SqliteTransaction transaction, ushort id, CharacterLearningState learning)
    {
        CharacterSkill[] skills = learning.Trees.Single(tree => tree.Context == 0).Skills
            .Select(skill => new CharacterSkill(skill.Id, skill.Level)).OrderBy(skill => skill.Id).ToArray();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$id", id);
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
        return skills;
    }

    private static bool SameLearning(CharacterLearningState current, CharacterLearningState expected) =>
        current.Experience == expected.Experience && current.BonusSp == expected.BonusSp && current.BonusTp == expected.BonusTp &&
        current.ExpertContractExpiresAt == expected.ExpertContractExpiresAt && current.Trees.Count == expected.Trees.Count &&
        current.Trees.OrderBy(tree => tree.Context).Zip(expected.Trees.OrderBy(tree => tree.Context)).All(pair =>
            pair.First.SameCombos(pair.Second) && pair.First.Context == pair.Second.Context && pair.First.Sp == pair.Second.Sp && pair.First.Tp == pair.Second.Tp &&
            pair.First.Skills.OrderBy(skill => skill.Slot).SequenceEqual(pair.Second.Skills.OrderBy(skill => skill.Slot)));

    private static void WriteLearning(SqliteConnection db, SqliteTransaction transaction, ushort id, CharacterLearningState learning)
    {
        learning.Validate();
        if (!learning.Initialized)
            return;
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", id);
        if (learning.BonusSp is int bonus)
        {
            command.CommandText = "INSERT INTO character_skill_point_sources(character_id,bonus_sp) VALUES($id,$bonus) ON CONFLICT(character_id) DO UPDATE SET bonus_sp=$bonus";
            command.Parameters.AddWithValue("$bonus", bonus);
            command.ExecuteNonQuery();
            command.Parameters.RemoveAt("$bonus");
        }
        if (learning.BonusTp is int tpBonus)
        {
            command.CommandText = "INSERT INTO character_tp_sources(character_id,bonus_tp) VALUES($id,$bonus) ON CONFLICT(character_id) DO UPDATE SET bonus_tp=$bonus";
            command.Parameters.AddWithValue("$bonus", tpBonus);
            command.ExecuteNonQuery();
            command.Parameters.RemoveAt("$bonus");
        }
        command.CommandText = "DELETE FROM character_skill_commands WHERE character_id=$id; DELETE FROM character_skill_combos WHERE character_id=$id";
        command.ExecuteNonQuery();
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
            foreach (var skill in tree.Skills.Where(skill => skill.Command.Length > 0))
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$context", tree.Context);
                command.Parameters.AddWithValue("$skill", skill.Id);
                command.Parameters.AddWithValue("$keys", Convert.FromHexString(skill.Command));
                command.CommandText = "INSERT INTO character_skill_commands(character_id,context,skill_id,keys) VALUES($id,$context,$skill,$keys)";
                command.ExecuteNonQuery();
            }
            foreach (var combo in tree.Combos)
            {
                byte[] children = new byte[combo.Children.Count * 2];
                for (int i = 0; i < combo.Children.Count; i++) System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(children.AsSpan(i * 2), combo.Children[i]);
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$context", tree.Context);
                command.Parameters.AddWithValue("$root", combo.Root);
                command.Parameters.AddWithValue("$children", children);
                command.CommandText = "INSERT INTO character_skill_combos(character_id,context,root_id,children) VALUES($id,$context,$root,$children)";
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
        command.CommandText = "SELECT bonus_sp FROM character_skill_point_sources WHERE character_id=$id";
        int? bonus = command.ExecuteScalar() is long value ? checked((int)value) : null;
        command.CommandText = "SELECT bonus_tp FROM character_tp_sources WHERE character_id=$id";
        int? tpBonus = command.ExecuteScalar() is long tpValue ? checked((int)tpValue) : null;
        long expires = ReadEffectiveExpertContractExpiry(db, id, transaction);
        command.CommandText = """
            SELECT p.context,p.sp,p.tp,b.slot,b.skill_id,b.level,c.keys
            FROM character_skill_points p LEFT JOIN character_skill_bindings b
            ON b.character_id=p.character_id AND b.context=p.context
            LEFT JOIN character_skill_commands c ON c.character_id=b.character_id AND c.context=b.context AND c.skill_id=b.skill_id
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
                tree.Skills.Add(new(checked((byte)reader.GetInt32(3)), checked((ushort)reader.GetInt32(4)), checked((byte)reader.GetInt32(5))) { Command = reader.IsDBNull(6) ? "" : Convert.ToHexString((byte[])reader[6]) });
        }
        reader.Close();
        var combos = new Dictionary<byte, List<SkillCombo>>();
        command.CommandText = "SELECT context,root_id,children FROM character_skill_combos WHERE character_id=$id ORDER BY context,root_id";
        using (var comboReader = command.ExecuteReader())
            while (comboReader.Read())
            {
                byte context = checked((byte)comboReader.GetInt32(0));
                byte[] bytes = (byte[])comboReader[2];
                var children = Enumerable.Range(0, bytes.Length / 2).Select(i => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2))).ToArray();
                if (!combos.TryGetValue(context, out var list)) combos[context] = list = [];
                list.Add(new(checked((ushort)comboReader.GetInt32(1)), Array.AsReadOnly(children)));
            }
        if (balances.Count == 0 && experience == 0 && !bonus.HasValue && !tpBonus.HasValue && expires == 0)
            return CharacterLearningState.Uninitialized;
        var state = new CharacterLearningState(experience, Array.AsReadOnly(balances.OrderBy(pair => pair.Key)
            .Select(pair => new CharacterSkillTree(pair.Key, pair.Value.Sp, pair.Value.Tp, pair.Value.Skills.AsReadOnly()) { Combos = combos.TryGetValue(pair.Key, out var list) ? list.AsReadOnly() : Array.Empty<SkillCombo>() }).ToArray()))
        { BonusSp = bonus, BonusTp = tpBonus, ExpertContractExpiresAt = expires };
        state.Validate();
        return state;
    }

    private static long ReadEffectiveExpertContractExpiry(SqliteConnection db, ushort id, SqliteTransaction? transaction)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", id);
        command.CommandText = """
            SELECT e.expires_at,json_extract(w.premium_contracts,'$."27"'),json_type(w.premium_contracts,'$."27"')
            FROM characters c
            LEFT JOIN account_expert_contracts e ON e.account=c.account
            LEFT JOIN account_wallets w ON w.account=c.account
            WHERE c.id=$id
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("达人契约角色不存在");
        long legacy = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
        long purchased = 0;
        if (!reader.IsDBNull(2))
        {
            if (reader.GetString(2) != "integer" || reader.IsDBNull(1))
                throw new InvalidDataException("账号达人契约期限必须是正整数");
            purchased = reader.GetInt64(1);
            if (purchased <= 0) throw new InvalidDataException("账号达人契约期限必须是正整数");
        }
        if (legacy < 0) throw new InvalidDataException("账号达人契约期限非法");
        // 两个来源均属于账号；学习状态只携带有效期限，不向角色表复制权益。
        return Math.Max(legacy, purchased);
    }
}
