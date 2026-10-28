using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IGuildDirectoryStore
{
    // 公会名/地址查重只读 `guilds` 的唯一索引，不加载公会行本身：
    // 创建窗口的两次查重是高频低价值请求，不为此打开写事务或读整张表。
    public bool NameTaken(string name) => GuildTextTaken("SELECT 1 FROM guilds WHERE name=$text LIMIT 1", name);

    public bool AddressTaken(string address) => GuildTextTaken("SELECT 1 FROM guilds WHERE address=$text LIMIT 1", address);

    public long? GuildOf(ushort characterId)
    {
        using var db = database.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT guild_id FROM guild_members WHERE character_id=$id";
        command.Parameters.AddWithValue("$id", characterId);
        return command.ExecuteScalar() is long guild && guild != 0 ? guild : null;
    }

    public GuildMembership? GuildMembershipOf(ushort characterId)
    {
        using var db = database.Open();
        return ReadGuildMembership(db, characterId, null);
    }

    static GuildMembership? ReadGuildMembership(SqliteConnection db, ushort characterId, SqliteTransaction? transaction)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT g.guild_id,g.name,g.level,
                (SELECT count(*) FROM guild_members members WHERE members.guild_id=g.guild_id),
                c.name,g.notice,g.experience,g.created_at_unix,g.tier,g.guild_crystal,g.master_character_id,m.grade
            FROM guild_members m JOIN guilds g ON g.guild_id=m.guild_id
            JOIN characters c ON c.id=g.master_character_id WHERE m.character_id=$id
            """;
        command.Parameters.AddWithValue("$id", characterId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var guild = new GuildMembership(checked((uint)reader.GetInt64(0)), reader.GetString(1), checked((byte)reader.GetInt32(2)),
            checked((ushort)reader.GetInt32(3)), reader.GetString(4), reader.GetString(5),
            checked((uint)reader.GetInt64(6)), checked((uint)reader.GetInt64(7)))
            {
                Tier = checked((byte)reader.GetInt32(8)), Crystals = checked((uint)reader.GetInt64(9)),
                Grade = GuildRoleRules.NormalizeGrade(characterId, checked((ushort)reader.GetInt32(10)),
                    checked((byte)reader.GetInt32(11)))
            };
        reader.Close();
        return guild with { Appearance = ReadAppearanceInTransaction(db, transaction, guild.Id) };
    }

    /// <summary>
    /// 认证邀请的目标查询：只读一行角色（id、所属账号）并顺带问一次成员行是否存在。
    /// 不加载背包或任何资产快照——邀请阶段只做同账号 / 不存在 / 已在公会这三种判定。
    /// 名称比较用列上的 `COLLATE NOCASE`（与邮件收件人查询同一口径）。
    /// </summary>
    public GuildPermitTarget? FindPermitTarget(string name)
    {
        using var db = database.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.account, EXISTS(SELECT 1 FROM guild_members m WHERE m.character_id=c.id)
            FROM characters c WHERE c.name=$name LIMIT 1
            """;
        command.Parameters.AddWithValue("$name", name);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new GuildPermitTarget(checked((ushort)reader.GetInt32(0)), reader.GetString(1), reader.GetInt32(2) != 0)
            : null;
    }

    /// <summary>
    /// 顺序模式的离线建会读取入口：按角色 id 读该账号下的最新快照（与 <c>LoadCharacter</c> 同一读取口径，
    /// 含进度、完整背包与 revision）。读不到返回 null，由用例层按「计划作废」处理，不抛给连接。
    /// </summary>
    public Character? LoadCharacterById(AccountOwners.Lease owner, ushort characterId) => owner.Execute(() =>
    {
        using var db = database.Open();
        using var transaction = db.BeginRead();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM characters WHERE account=$account AND id=$id";
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$id", characterId);
        Character character;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
                return (Character?)null;
            character = Read(reader);
        }
        return ReadProgress(db, character, transaction);
    });

    /// <summary>
    /// 创建公会提交：**扣建会花费 + 写公会行 + 写成员行（会长与被邀请角色）在同一个外层事务里**，
    /// 事务由本方法拥有。
    ///
    /// 顺序与[拍卖行上架](CharacterRepository.Inventory.cs)一致：
    ///   ① 先在事务内复核四条唯一性（名称、地址、会长尚未入会、被邀请角色尚未入会）——都不通过就返回错误，
    ///      此时**尚未提交背包变更**，事务随 `using` 回滚，金币不会被扣；
    ///   ② 再用既有的 <c>CommitInventoryInTransaction</c> 扣费（含 owner / revision / 完整背包快照比对）；
    ///   ③ 最后插入公会行与两条成员行。
    /// 任一步抛异常 ⇒ 不提交 ⇒ 既不会出现「扣了钱没建会」，也不会出现「建了会没扣钱」。
    /// </summary>
    public GuildCreateResult CreateGuild(AccountOwners.Lease owner, Character expected, GuildCreatePlan plan) =>
        owner.Execute(() =>
        {
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.CreateGuild");
            using var transaction = write.Transaction;
            if (GuildTextTaken(db, transaction, "SELECT 1 FROM guilds WHERE name=$text LIMIT 1", plan.Name))
                return new GuildCreateResult(GuildCreateError.NameTaken, null, 0);
            if (GuildTextTaken(db, transaction, "SELECT 1 FROM guilds WHERE address=$text LIMIT 1", plan.Address))
                return new GuildCreateResult(GuildCreateError.AddressTaken, null, 0);
            if (GuildMembershipExists(db, transaction, expected.Id))
                return new GuildCreateResult(GuildCreateError.AlreadyInGuild, null, 0);
            // 被邀请角色也可能在申请等待期间入了别的公会：同一事务内复核，失败整笔回滚（不扣费）。
            if (plan.InviteeCharacterId != 0 && plan.InviteeCharacterId != expected.Id &&
                GuildMembershipExists(db, transaction, plan.InviteeCharacterId))
                return new GuildCreateResult(GuildCreateError.AlreadyInGuild, null, 0);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (ApplicationCooldown(db, transaction, expected.Id, now) ||
                plan.InviteeCharacterId != 0 && ApplicationCooldown(db, transaction, plan.InviteeCharacterId, now))
                return new GuildCreateResult(GuildCreateError.AlreadyInGuild, null, 0);

            var character = CommitInventoryInTransaction(db, transaction, owner, expected, plan.Inventory);
            long guildId = InsertGuild(db, transaction, plan, character.Id);
            RemoveAllGuildApplications(db, transaction, character.Id, checked((uint)guildId));
            if (plan.InviteeCharacterId != 0)
                RemoveAllGuildApplications(db, transaction, plan.InviteeCharacterId, checked((uint)guildId));
            character = character with { Guild = ReadGuildMembership(db, character.Id, transaction) };
            transaction.Commit();
            return new GuildCreateResult(GuildCreateError.None, character, guildId);
        });

    bool GuildTextTaken(string sql, string text)
    {
        using var db = database.Open();
        return GuildTextTaken(db, null, sql, text);
    }

    static bool GuildTextTaken(SqliteConnection db, SqliteTransaction? transaction, string sql, string text)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$text", text);
        using var reader = command.ExecuteReader();
        return reader.Read();
    }

    static bool GuildMembershipExists(SqliteConnection db, SqliteTransaction transaction, ushort characterId)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM guild_members WHERE character_id=$id LIMIT 1";
        command.Parameters.AddWithValue("$id", characterId);
        using var reader = command.ExecuteReader();
        return reader.Read();
    }

    /// <summary>
    /// 写入公会行与成员行：会长（计划所属角色）固定职位 0，并在有随行被邀请角色时**同一事务**写入它的成员行
    /// （职位同样先固定 0——客户端职位编号与「会长」的对应关系尚未取证，成员玩法接入后按实际职位表校正）。
    /// 公会等级/经验/基金/结晶一律从 0 起，不预置成长值。
    /// </summary>
    static long InsertGuild(SqliteConnection db, SqliteTransaction transaction, GuildCreatePlan plan, ushort masterId)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO guilds(name,address,master_character_id,tier,level,experience,public_funds,guild_crystal,notice,memo,created_at_unix)
            VALUES($name,$address,$master,0,1,0,0,0,'','',$created);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$name", plan.Name);
        command.Parameters.AddWithValue("$address", plan.Address);
        command.Parameters.AddWithValue("$master", masterId);
        command.Parameters.AddWithValue("$created", plan.CreatedAtUnix);
        long guildId = Convert.ToInt64(command.ExecuteScalar());

        command.CommandText = """
            INSERT INTO guild_members(guild_id,character_id,grade,contribution,member_memo,joined_at_unix,last_login_unix)
            VALUES($guild,$id,0,0,'',$created,0)
            """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$guild", guildId);
        command.Parameters.AddWithValue("$id", masterId);
        command.Parameters.AddWithValue("$created", plan.CreatedAtUnix);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("创建公会的会长成员行写入失败");
        if (plan.InviteeCharacterId == 0 || plan.InviteeCharacterId == masterId)
            return guildId;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$guild", guildId);
        command.Parameters.AddWithValue("$id", plan.InviteeCharacterId);
        command.Parameters.AddWithValue("$created", plan.CreatedAtUnix);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("创建公会的被邀请成员行写入失败");
        return guildId;
    }
}
