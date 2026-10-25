using System.Buffers.Binary;
using System.Globalization;
using Microsoft.Data.Sqlite;
using DfoServer.Game.Adventure;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Adventure;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Persistence;

// 国服95冒险团经验账本的唯一写入口：所有角色经验来源在同一事务内推进账号里程与排除额。
// 里程存在 character_adventure_credits；开启时的精确总量存在 account_adventures.opening_experience。
internal sealed partial class CharacterRepository
{
    internal sealed record AdventureOpeningCommit(bool Ran, int Accounts, int Characters, decimal Opening, decimal Honor, decimal Roster);

    bool AdventureCreditEnabled => adventureCatalog is not null && growthCatalog is not null;

    ulong AdventureCap => growthCatalog!.GetLevelStartExperience(CharacterGrowthCatalog.MaximumSupportedLevel);

    // 角色有效经验位置：经验字段与等级入口线取较大者。满级角色的经验条钉在入口线，
    // 真实里程由账本继续推进，不能从角色行反推。
    ulong EffectivePosition(int level, ulong experience)
    {
        // 未装配成长资源（离线 GM、部分自测）时账本整体停用，位置值不参与写入，直接返回经验字段。
        if (growthCatalog is not { } growth) return experience;
        ulong entry = level switch
        {
            <= 0 => 0,
            > CharacterGrowthCatalog.MaximumSupportedLevel => growth.GetLevelStartExperience(CharacterGrowthCatalog.MaximumSupportedLevel),
            _ => growth.GetLevelStartExperience(level)
        };
        return Math.Max(experience, entry);
    }

    // 登录时 InitializeAdventure 已建立账号行；GM离线路径或测试可能没有，这里按初始行补齐。
    static void EnsureAdventureRow(SqliteConnection db, SqliteTransaction tx, string account, DateTimeOffset now)
    {
        var initial = AdventureState.Initial(account, now);
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$name", initial.Name);
        command.Parameters.AddWithValue("$date", initial.CreatedDate);
        command.Parameters.AddWithValue("$points", new byte[20]);
        command.Parameters.AddWithValue("$month", initial.Month);
        command.CommandText = """
            INSERT INTO account_adventures(account,revision,name,created_date,level,experience,remainder,
              capsule_experience,brave_experience,points,month,name_initialized,capsule_week,login_streak_days,last_login_day,
              opening_experience,excluded_experience)
            VALUES($account,0,$name,$date,1,0,'0',0,0,$points,$month,0,0,0,0,'0','0')
            ON CONFLICT(account) DO NOTHING
            """;
        command.ExecuteNonQuery();
    }

    // 只读账号进度行本身；不装载名册、购买与远征。周期边界（胶囊周重置、商店月重置）照常投影。
    static AdventureState? ReadAdventureState(SqliteConnection db, SqliteTransaction tx, string account,
        AdventureCatalog catalog, DateTimeOffset now)
    {
        using var query = db.CreateCommand(); query.Transaction = tx; query.Parameters.AddWithValue("$account", account);
        query.CommandText = """
            SELECT revision,name,created_date,level,experience,remainder,capsule_experience,brave_experience,points,month,
              name_initialized,capsule_week,login_streak_days,last_login_day,opening_experience,excluded_experience
            FROM account_adventures WHERE account=$account
            """;
        AdventureState state;
        using (var reader = query.ExecuteReader())
        {
            if (!reader.Read()) return null;
            byte[] bytes = (byte[])reader[8];
            var points = System.Collections.Immutable.ImmutableArray.CreateBuilder<uint>(5);
            for (int i = 0; i < 5; i++) points.Add(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4, 4)));
            state = new AdventureState(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
                checked((ulong)reader.GetInt64(4)), decimal.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                checked((ulong)reader.GetInt64(6)), checked((ulong)reader.GetInt64(7)), points.ToImmutable(), reader.GetInt32(9))
            {
                NameInitialized = reader.GetBoolean(10), CapsuleWeek = reader.GetInt32(11),
                LoginStreakDays = reader.GetInt32(12), LastLoginDay = reader.GetInt32(13),
                OpeningExperience = decimal.Parse(reader.GetString(14), CultureInfo.InvariantCulture),
                ExcludedExperience = decimal.Parse(reader.GetString(15), CultureInfo.InvariantCulture)
            };
        }
        state = AdventurePeriod.Apply(state, catalog, now);
        state.Validate(catalog);
        return state;
    }

    // 计算本次事件的里程增量并写入里程行；账号进度由调用方与本次业务同一事务写入。
    // effectivePosition 必须是本次业务更新前的有效位置（事务内读到的角色快照）。
    // levelBefore/levelAfter 用于40级门槛：40级前只推进里程，自然升级到40时整段追溯，
    // 直升类跨越40级不追溯历史。
    AdventureCreditPlan? CreditAdventure(SqliteConnection db, SqliteTransaction tx, string account, uint characterId,
        AdventureCreditSource source, ulong amount, ulong effectivePosition, int levelBefore, int levelAfter, DateTimeOffset now)
    {
        if (adventureCatalog is not { } catalog || growthCatalog is null) return null;
        // 专用角色原经验照常提交，但不计入账号冒险团；复用当前事务，不另开写锁。
        using(var identity=db.CreateCommand())
        {
            identity.Transaction=tx;identity.CommandText="SELECT EXISTS(SELECT 1 FROM account_pvp_private_characters WHERE character_id=$id)";
            identity.Parameters.AddWithValue("$id",characterId);if(identity.ExecuteScalar() is 1L) return null;
        }
        EnsureAdventureRow(db, tx, account, now);
        var before = ReadAdventureState(db, tx, account, catalog, now) ??
            throw new InvalidOperationException("冒险团账号行缺失");
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.Parameters.AddWithValue("$id", characterId);
        query.Parameters.AddWithValue("$account", account);
        query.CommandText = "SELECT baseline_experience,position_experience FROM character_adventure_credits WHERE character_id=$id AND account=$account";
        ulong stored, storedBaseline;
        using (var reader = query.ExecuteReader())
        {
            if (reader.Read())
            {
                storedBaseline = checked((ulong)reader.GetInt64(0));
                stored = checked((ulong)reader.GetInt64(1));
            }
            else
            {
                // 未经过开启账本的角色（导入、旧存档）：40级以下按自然经验处理，升到40时追溯；
                // 已到40级从当前有效位置起算，历史经验不追溯计入。
                stored = effectivePosition;
                storedBaseline = levelBefore < AdventureCredit.CountingLevel ? 0 : stored;
                reader.Close();
                query.CommandText = "INSERT INTO character_adventure_credits(character_id,account,baseline_experience,position_experience) VALUES($id,$account,$baseline,$position)";
                query.Parameters.AddWithValue("$baseline", checked((long)storedBaseline));
                query.Parameters.AddWithValue("$position", checked((long)stored));
                query.ExecuteNonQuery();
                query.Parameters.RemoveAt("$baseline"); query.Parameters.RemoveAt("$position");
            }
        }
        var plan = AdventureCredit.Apply(before, source, stored, storedBaseline, effectivePosition, amount,
            AdventureCap, catalog, levelBefore, levelAfter);
        if (plan.PositionAfter != stored || plan.BaselineAfter != storedBaseline)
        {
            query.CommandText = "UPDATE character_adventure_credits SET baseline_experience=$baseline,position_experience=$position WHERE character_id=$id AND account=$account";
            query.Parameters.AddWithValue("$baseline", checked((long)plan.BaselineAfter));
            query.Parameters.AddWithValue("$position", checked((long)plan.PositionAfter));
            if (query.ExecuteNonQuery() != 1) throw new InvalidOperationException("冒险团角色里程行缺失");
        }
        return plan;
    }

    // 升级券、状态表、跳跃表与GM等级/经验改写：直接按改后有效位置改写里程，账号总量保持不变。
    void CreditExcludedState(SqliteConnection db, SqliteTransaction tx, string account, Character character,
        int levelAfter, ulong position)
    {
        if (adventureCatalog is not { } catalog) return;
        if (CreditAdventure(db, tx, account, character.Id, AdventureCreditSource.ExcludedState, position,
                EffectivePosition(character.Level, character.Learning.Experience), character.Level, levelAfter,
                DateTimeOffset.UtcNow) is { } credit)
            WriteAdventureCredit(db, tx, account, credit, AdventureCreditSource.ExcludedState, catalog);
    }

    // 计入来源走完整进度写入（等级、经验、余数、结晶、胶囊、排除额）；排除来源只写排除额。
    static void WriteAdventureCredit(SqliteConnection db, SqliteTransaction tx, string account,
        AdventureCreditPlan plan, AdventureCreditSource source, AdventureCatalog catalog)
    {
        if (plan.After == plan.Before) return;
        if (source == AdventureCreditSource.Earned)
        {
            WriteAdventureProgression(db, tx, account, plan.Before, plan.After, catalog);
            return;
        }
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$excluded", plan.After.ExcludedExperience.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$before", plan.Before.Revision);
        command.CommandText = "UPDATE account_adventures SET revision=revision+1,excluded_experience=$excluded WHERE account=$account AND revision=$before";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("冒险团排除经验版本失配");
    }

    // 一次性开启账本：自开启时已到40级的角色经验计入，开启总量取「已有精确总量」与「名册经验 + 熟练度换算」的较大者。
    // 40级以下角色的历史经验按自然经验处理：只建 pending 行（基准0），升到40时再追溯。
    // 标记与写入同一事务；已有标记直接跳过，不会每次登录重复补发。
    public AdventureOpeningCommit OpenAdventureLedger(DateTimeOffset now) => AdventureCreditEnabled
        ? OpenAdventureLedger(database, adventureCatalog!, growthCatalog!, now)
        : new(false, 0, 0, 0, 0, 0);

    static AdventureOpeningCommit OpenAdventureLedger(SqliteGameDatabase database, AdventureCatalog catalog,
        CharacterGrowthCatalog growth, DateTimeOffset now)
    {
        ulong cap = growth.GetLevelStartExperience(CharacterGrowthCatalog.MaximumSupportedLevel);
        decimal rate = catalog.ExperienceRate;
        // 熟练度累计：第 N 行是 N-1 升 N 所需经验，289 级没有下一行。
        var prefix = new ulong[ProficiencyCatalog.MaximumLevel + 1];
        for (int level = 0; level < ProficiencyCatalog.MaximumLevel; level++)
            prefix[level + 1] = checked(prefix[level] + growth.Proficiency.Requirement((uint)level));
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.OpenAdventureLedger", deferred: true);
        using var tx = write.Transaction;
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT value FROM adventure_ledger_meta WHERE key='opening'";
        if (command.ExecuteScalar() is not null) { tx.Rollback(); return new(false, 0, 0, 0, 0, 0); }
        command.CommandText = "SELECT account FROM characters UNION SELECT account FROM account_adventures ORDER BY account";
        var accounts = new List<string>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) accounts.Add(reader.GetString(0));
        int characters = 0;
        decimal openingTotal = 0, honorTotal = 0, rosterTotal = 0;
        foreach (string account in accounts)
        {
            var existing = ReadAdventureState(db, tx, account, catalog, now);
            decimal oldExact = existing is null ? 0 : AdventureCredit.ExactTotal(existing, catalog);
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$account", account);
            command.Parameters.AddWithValue("$cap", checked((long)cap));
            // 官方规则：只有40级以上的角色提供冒险团经验。
            command.CommandText = "SELECT coalesce(sum(min(experience,$cap)),0) FROM characters c WHERE account=$account AND level>=$level AND NOT EXISTS(SELECT 1 FROM account_pvp_private_characters p WHERE p.character_id=c.id)";
            command.Parameters.AddWithValue("$level", AdventureCredit.CountingLevel);
            decimal roster = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$account", account);
            command.CommandText = """
                SELECT c.id,c.level,c.experience,coalesce(d.proficiency_level,0),coalesce(d.proficiency_experience,0)
                FROM characters c LEFT JOIN character_dungeon_state d ON d.character_id=c.id AND d.account=c.account
                WHERE c.account=$account AND NOT EXISTS(SELECT 1 FROM account_pvp_private_characters p WHERE p.character_id=c.id) ORDER BY c.id
                """;
            var rows = new List<(uint Id, int Level, ulong Experience, uint ProficiencyLevel, ulong ProficiencyExperience)>();
            using (var reader = command.ExecuteReader()) while (reader.Read())
                rows.Add((checked((uint)reader.GetInt64(0)), reader.GetInt32(1), checked((ulong)reader.GetInt64(2)),
                    checked((uint)reader.GetInt64(3)), checked((ulong)reader.GetInt64(4))));
            decimal honor = 0;
            foreach (var row in rows)
            {
                // 40级以下角色不提供冒险团经验，熟练度换算同样不计入。
                if (row.Level < AdventureCredit.CountingLevel) continue;
                uint level = Math.Min(row.ProficiencyLevel, ProficiencyCatalog.MaximumLevel);
                ulong within = level >= ProficiencyCatalog.MaximumLevel ? 0 :
                    Math.Min(row.ProficiencyExperience, growth.Proficiency.Requirement(level));
                honor += prefix[level] + within;
            }
            honor *= rate;
            decimal opening = Math.Max(oldExact, roster + honor);
            var template = existing ?? AdventureState.Initial(account, now);
            var state = AdventureCredit.FromExactTotal(opening, template, catalog);
            command.CommandText = existing is null
                ? """
                  INSERT INTO account_adventures(account,revision,name,created_date,level,experience,remainder,
                    capsule_experience,brave_experience,points,month,name_initialized,capsule_week,login_streak_days,last_login_day,
                    opening_experience,excluded_experience)
                  VALUES($account,0,$name,$date,$level,$experience,$remainder,0,0,$points,$month,0,0,0,0,$opening,'0')
                  """
                : """
                  UPDATE account_adventures SET revision=revision+1,level=$level,experience=$experience,remainder=$remainder,
                    opening_experience=$opening,excluded_experience='0' WHERE account=$account
                  """;
            if (existing is null)
            {
                command.Parameters.AddWithValue("$name", state.Name);
                command.Parameters.AddWithValue("$date", state.CreatedDate);
                command.Parameters.AddWithValue("$points", Points(state));
                command.Parameters.AddWithValue("$month", state.Month);
            }
            command.Parameters.AddWithValue("$level", state.Level);
            command.Parameters.AddWithValue("$experience", checked((long)state.Experience));
            command.Parameters.AddWithValue("$remainder", state.Remainder.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$opening", opening.ToString(CultureInfo.InvariantCulture));
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("冒险团开启账号行缺失");
            openingTotal += opening;
            honorTotal += honor;
            rosterTotal += roster;
            foreach (var row in rows)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$id", row.Id);
                command.Parameters.AddWithValue("$account", account);
                ulong entry = row.Level > 0
                    ? growth.GetLevelStartExperience(Math.Min(row.Level, CharacterGrowthCatalog.MaximumSupportedLevel))
                    : 0;
                command.Parameters.AddWithValue("$position", checked((long)Math.Max(row.Experience, entry)));
                // 40级以下的历史经验在开启时不计入，按自然经验待追溯：基准0、里程取当前值。
                command.Parameters.AddWithValue("$baseline", row.Level < AdventureCredit.CountingLevel
                    ? 0L : checked((long)Math.Max(row.Experience, entry)));
                command.CommandText = """
                    INSERT INTO character_adventure_credits(character_id,account,baseline_experience,position_experience)
                    VALUES($id,$account,$baseline,$position) ON CONFLICT(character_id) DO NOTHING
                    """;
                command.ExecuteNonQuery();
            }
            characters += rows.Count;
        }
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$value", SqliteMigrations.CurrentVersion.ToString(CultureInfo.InvariantCulture));
        command.CommandText = "INSERT INTO adventure_ledger_meta(key,value) VALUES('opening',$value)";
        command.ExecuteNonQuery();
        tx.Commit();
        return new(true, accounts.Count, characters, openingTotal, honorTotal, rosterTotal);
    }

    static byte[] Points(AdventureState state)
    {
        byte[] points = new byte[20];
        for (int index = 0; index < 5; index++) BinaryPrimitives.WriteUInt32LittleEndian(points.AsSpan(index * 4), state.Points[index]);
        return points;
    }
}
