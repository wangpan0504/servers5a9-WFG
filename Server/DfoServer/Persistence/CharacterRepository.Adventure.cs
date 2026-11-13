using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Adventure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public AdventureSnapshot ReadAdventure(AccountOwners.Lease owner, AdventureCatalog catalog, DateTimeOffset now) => owner.Execute(() =>
    {
        using var db = database.Open(); using var tx = db.BeginRead();
        return ReadAdventure(db, tx, owner.Account, catalog, now);
    });

    // 718名册与719次数一次读出，只读本账号角色，不装载背包、技能或冒险团进度。
    // 本周没有入场记录时已用次数为0；5号列没有副本来源，保持-1（客户端显示"-"）。
    public ImmutableArray<AdventureWeeklyCharacter> ReadAdventureWeeklyRoster(AccountOwners.Lease owner, int week) => owner.Execute(() =>
    {
        using var db = database.Open();
        var usage = new Dictionary<ushort, int[]>();
        using (var query = db.CreateCommand())
        {
            query.Parameters.AddWithValue("$account", owner.Account);
            query.Parameters.AddWithValue("$week", week);
            query.CommandText = "SELECT character_id,slot,used FROM character_weekly_dungeon WHERE account=$account AND week=$week";
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                ushort id = checked((ushort)reader.GetInt64(0));
                int slot = reader.GetInt32(1);
                if (slot is < 0 or >= WeeklyDungeonPolicy.SlotCount) throw new InvalidDataException("每周地下城列号越界");
                if (!usage.TryGetValue(id, out int[]? row)) usage.Add(id, row = EmptyWeeklyUsage());
                row[slot] = checked((int)reader.GetInt64(2));
            }
        }
        using var roster = db.CreateCommand();
        roster.Parameters.AddWithValue("$account", owner.Account);
        roster.CommandText = "SELECT id,name,level,job,grow_type FROM characters c WHERE account=$account AND NOT EXISTS(SELECT 1 FROM account_pvp_private_characters p WHERE p.character_id=c.id) ORDER BY slot,id";
        var rows = ImmutableArray.CreateBuilder<AdventureWeeklyCharacter>();
        using var reader2 = roster.ExecuteReader();
        while (reader2.Read())
        {
            ushort id = checked((ushort)reader2.GetInt64(0));
            rows.Add(new(id, reader2.GetString(1), checked((ushort)reader2.GetInt32(2)), checked((byte)reader2.GetInt32(3)),
                checked((byte)reader2.GetInt32(4)), usage.TryGetValue(id, out int[]? row) ? ImmutableArray.Create(row) : ImmutableArray.Create(EmptyWeeklyUsage())));
        }
        return rows.ToImmutable();
    });

    static int[] EmptyWeeklyUsage()
    {
        var row = new int[WeeklyDungeonPolicy.SlotCount];
        Array.Fill(row, -1);
        for (int slot = 0; slot < row.Length; slot++) if (WeeklyDungeonPolicy.Mapped(slot)) row[slot] = 0;
        return row;
    }

    public ImmutableArray<byte> ReadAdventureWeeklyConfig(AccountOwners.Lease owner) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var query = db.CreateCommand();
        query.Parameters.AddWithValue("$account", owner.Account);
        query.CommandText = "SELECT slots FROM account_adventure_weekly_config WHERE account=$account";
        return query.ExecuteScalar() is byte[] slots && slots.Length == AdventureWeekly.Slots
            ? ImmutableArray.Create(slots) : AdventureWeekly.Default;
    });

    // 配置是账号级5字节排列，与账号revision及其他冒险团写入解耦，避免互相冲突。
    public void WriteAdventureWeeklyConfig(AccountOwners.Lease owner, ImmutableArray<byte> slots) => owner.Execute(() =>
    {
        AdventureWeekly.Validate(slots);
        using var db = database.Open(); using var tx = db.BeginTransaction();
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            INSERT INTO account_adventure_weekly_config(account,slots) VALUES($account,$slots)
            ON CONFLICT(account) DO UPDATE SET slots=excluded.slots
            """;
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$slots", slots.ToArray());
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("每周地下城配置未写入");
        tx.Commit();
        return true;
    });

    public int ReadAdventureLevel(AccountOwners.Lease owner) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var query = db.CreateCommand();
        query.Parameters.AddWithValue("$account", owner.Account);
        query.CommandText = "SELECT level FROM account_adventures WHERE account=$account";
        // 尚未初始化账号的首次结算按初始等级1处理：该级没有属性行，不产生加成。
        return query.ExecuteScalar() is long level ? checked((int)level) : 1;
    });

    public void InitializeAdventure(AccountOwners.Lease owner, AdventureCatalog catalog, DateTimeOffset now) => owner.Execute(() =>
    {
        using var db = database.Open();
        int day = AdventurePeriod.LoginDay(now);
        using var query = db.CreateCommand(); query.Parameters.AddWithValue("$account", owner.Account);
        query.CommandText = "SELECT login_streak_days,last_login_day FROM account_adventures WHERE account=$account";
        bool exists = false; int streak = 0, lastDay = 0;
        using (var reader = query.ExecuteReader())
            if (reader.Read()) { exists = true; streak = reader.GetInt32(0); lastDay = reader.GetInt32(1); }
        // 冒险团和当前战令日来源均已初始化时保持只读；时钟回拨不新增旧日来源。
        if (exists && day <= lastDay && !BattlePassDailyLoginPending(db, null, owner.Account, lastDay, day, now)) return true;
        using var write = db.BeginWrite("CharacterRepository.InitializeAdventure");
        using var tx = write.Transaction;
        query.Transaction = tx;
        using (var reader = query.ExecuteReader())
        {
            exists = reader.Read();
            if (exists) { streak = reader.GetInt32(0); lastDay = reader.GetInt32(1); }
        }
        if (exists && day <= lastDay && !BattlePassDailyLoginPending(db, tx, owner.Account, lastDay, day, now))
        { tx.Rollback(); return true; }
        if (!exists)
        {
            var state = AdventureState.Initial(owner.Account, now) with { LoginStreakDays = 1, LastLoginDay = day };
            state.Validate(catalog);
            query.CommandText = """
                INSERT INTO account_adventures(account,revision,name,created_date,level,experience,remainder,
                    capsule_experience,brave_experience,points,month,name_initialized,login_streak_days,last_login_day)
                VALUES($account,0,$name,$date,1,0,'0',0,0,$points,$month,0,$streak,$day)
                """;
            query.Parameters.AddWithValue("$name", state.Name);
            query.Parameters.AddWithValue("$date", state.CreatedDate);
            query.Parameters.AddWithValue("$points", new byte[20]);
            query.Parameters.AddWithValue("$month", state.Month);
            query.Parameters.AddWithValue("$streak", state.LoginStreakDays);
            query.Parameters.AddWithValue("$day", day);
            query.ExecuteNonQuery();
        }
        else if (day > lastDay)
        {
            // 新的06:00周期才推进连续天数与账号版本；断一天或更久归零重计。
            query.Parameters.AddWithValue("$streak", AdventurePeriod.NextLoginStreak(lastDay, streak, day));
            query.Parameters.AddWithValue("$day", day);
            query.Parameters.AddWithValue("$previous", lastDay);
            query.CommandText = "UPDATE account_adventures SET login_streak_days=$streak,last_login_day=$day,revision=revision+1 WHERE account=$account AND last_login_day=$previous";
            if (query.ExecuteNonQuery() != 1) throw new InvalidOperationException("冒险团登录周期已变化");
        }
        // 本次真实登录与冒险团日事实共用事务；同日首次配置战令也只补当日的一次来源。
        ApplyBattlePassDailyLogin(db, tx, owner.Account, day, now);
        tx.Commit();
        return true;
    });

    static AdventureSnapshot ReadAdventure(SqliteConnection db, SqliteTransaction tx, string account, AdventureCatalog catalog, DateTimeOffset now)
    {
        using var query = db.CreateCommand(); query.Transaction = tx; query.Parameters.AddWithValue("$account", account);
        query.CommandText = "SELECT revision,name,created_date,level,experience,remainder,capsule_experience,brave_experience,points,month,name_initialized,capsule_week,login_streak_days,last_login_day,opening_experience,excluded_experience FROM account_adventures WHERE account=$account";
        var state = AdventureState.Initial(account, now);
        using (var reader = query.ExecuteReader())
        {
            if (reader.Read())
            {
                byte[] bytes = (byte[])reader[8];
                var points = ImmutableArray.CreateBuilder<uint>(5);
                for (int i = 0; i < 5; i++) points.Add(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4, 4)));
                state = state with { Revision = reader.GetInt64(0), Name = reader.GetString(1), CreatedDate = reader.GetInt32(2),
                    Level = reader.GetInt32(3), Experience = checked((ulong)reader.GetInt64(4)),
                    Remainder = decimal.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                    CapsuleExperience = checked((ulong)reader.GetInt64(6)), BraveExperience = checked((ulong)reader.GetInt64(7)),
                    Points = points.ToImmutable(), Month = reader.GetInt32(9), NameInitialized = reader.GetBoolean(10),
                    CapsuleWeek = reader.GetInt32(11), LoginStreakDays = reader.GetInt32(12), LastLoginDay = reader.GetInt32(13),
                    OpeningExperience = decimal.Parse(reader.GetString(14), CultureInfo.InvariantCulture),
                    ExcludedExperience = decimal.Parse(reader.GetString(15), CultureInfo.InvariantCulture) };
            }
        }
        query.CommandText = "SELECT item_id,count FROM account_adventure_purchases WHERE account=$account ORDER BY item_id";
        var purchases = ImmutableDictionary.CreateBuilder<int, uint>();
        using (var reader = query.ExecuteReader()) while (reader.Read()) purchases.Add(reader.GetInt32(0), checked((uint)reader.GetInt64(1)));
        state = state with { Purchases = purchases.ToImmutable() };
        query.CommandText = "SELECT job,grow,character_id FROM account_adventure_representatives WHERE account=$account ORDER BY job,grow";
        var selections = ImmutableArray.CreateBuilder<AdventureSelection>();
        using (var reader = query.ExecuteReader()) while (reader.Read()) selections.Add(new(checked((byte)reader.GetInt32(0)),
            checked((byte)reader.GetInt32(1)), reader.IsDBNull(2) ? null : checked((ushort)reader.GetInt32(2))));
        state = state with { Representatives = selections.ToImmutable() };
        query.CommandText = "SELECT area,ordinal,character_id,member_snapshot FROM account_adventure_expedition_members WHERE account=$account ORDER BY area,ordinal";
        var members = new Dictionary<byte, List<ushort>>();
        var frozen = new Dictionary<byte, List<ExpeditionMember>>();
        using (var reader = query.ExecuteReader()) while (reader.Read())
        {
            byte area = checked((byte)reader.GetInt32(0));
            if (!members.TryGetValue(area, out var rows)) { members.Add(area, rows = []); frozen.Add(area, []); }
            rows.Add(checked((ushort)reader.GetInt32(2)));
            frozen[area].Add(JsonSerializer.Deserialize<ExpeditionMember>(reader.GetString(3)) ?? throw new InvalidDataException("远征冻结成员缺失"));
        }
        query.CommandText = "SELECT area,run_id,started,ends,reward,extra_reward,attributes FROM account_adventure_expeditions WHERE account=$account ORDER BY area";
        var expeditions = ImmutableArray.CreateBuilder<AdventureExpedition>();
        using (var reader = query.ExecuteReader()) while (reader.Read())
        {
            byte area = checked((byte)reader.GetInt32(0));
            expeditions.Add(new(area, Guid.ParseExact(reader.GetString(1), "N"), reader.GetInt64(2), reader.GetInt64(3),
                checked((uint)reader.GetInt64(4)), checked((uint)reader.GetInt64(5)),
                JsonSerializer.Deserialize<ImmutableArray<byte>>(reader.GetString(6)), members[area].ToImmutableArray())
                { FrozenMembers = frozen[area].ToImmutableArray() });
        }
        state = state with { Expeditions = expeditions.ToImmutable() };
        state = AdventurePeriod.Apply(state, catalog, now);
        state.Validate(catalog);
        // 账号联动只读取身份、成长和版本，不装载其他角色的背包或技能树。
        query.CommandText = """
            SELECT c.id,c.slot,c.name,c.job,c.grow_type,c.level,coalesce(d.proficiency_level,0),c.revision
            FROM characters c LEFT JOIN character_dungeon_state d ON d.character_id=c.id AND d.account=c.account
            WHERE c.account=$account AND NOT EXISTS(SELECT 1 FROM account_pvp_private_characters p WHERE p.character_id=c.id) ORDER BY c.slot,c.id
            """;
        var roster = ImmutableArray.CreateBuilder<AdventureMember>();
        using (var reader = query.ExecuteReader()) while (reader.Read()) roster.Add(new(checked((ushort)reader.GetInt32(0)),
            checked((ushort)reader.GetInt32(1)), reader.GetString(2), checked((byte)reader.GetInt32(3)),
            checked((byte)reader.GetInt32(4)), checked((byte)reader.GetInt32(5)), checked((uint)reader.GetInt64(6)), reader.GetInt64(7)));
        // 推荐地下城保留原有u16饱和；团本只汇总既有合资格终局，不把入场、阶段奖励或失败计为通关。
        query.CommandText = "SELECT coalesce(sum(recommended_dungeon_clears),0) FROM character_dungeon_state WHERE account=$account";
        var counts = new ushort[AdventureContent.Count];
        counts[AdventureContent.RecommendedDungeon] = (ushort)Math.Min((long)query.ExecuteScalar()!, ushort.MaxValue);
        query.CommandText = """
            SELECT raid_type,count(*) FROM character_raid_runs
            WHERE account=$account AND state=2 AND raid_type IN(1,2,3) GROUP BY raid_type
            """;
        var raids = new AdventureRaidClears();
        using (var reader = query.ExecuteReader()) while (reader.Read())
        {
            uint clears = (uint)Math.Min(reader.GetInt64(1), uint.MaxValue);
            raids = reader.GetInt32(0) switch
            {
                1 => raids with { Luke = clears },
                2 => raids with { Fiendwar = clears },
                3 => raids with { Prey = clears },
                _ => raids
            };
        }
        query.CommandText = """
            SELECT max(
              (SELECT count(DISTINCT match_id) FROM character_pvp_rating_claims WHERE account=$account AND outcome=0),
              (SELECT coalesce(sum(CAST(json_extract(state_json,'$.Wins') AS INTEGER)),0) FROM character_pvp_ratings WHERE account=$account)
            )
            """;
        uint duelWins = (uint)Math.Clamp((long)query.ExecuteScalar()!, 0L, uint.MaxValue);
        query.CommandText = "SELECT count(*) FROM dungeon_despair_clear_receipts WHERE account=$account AND dungeon_id=11107";
        uint despairClears = (uint)Math.Min((long)query.ExecuteScalar()!, uint.MaxValue);
        query.CommandText = "SELECT count(*) FROM character_grief_tower_clears WHERE account=$account AND layer=100";
        uint lamentClears = (uint)Math.Min((long)query.ExecuteScalar()!, uint.MaxValue);
        return new(state, roster.ToImmutable())
        {
            ContentCounts = ImmutableArray.Create(counts), RaidClears = raids,
            DuelWins = duelWins, DespairTowerClears = despairClears, LamentTowerClears = lamentClears
        };
    }

}
