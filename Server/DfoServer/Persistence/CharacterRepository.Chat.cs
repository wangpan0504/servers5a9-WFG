using DfoServer.Game.Chat;

namespace DfoServer.Persistence;

/// <summary>
/// 聊天的只读查询：悄悄话按角色 id 找账号，公会聊天按公会 id 取成员账号。
/// 两条都只是 SELECT，不建事务、不写库、不缓存——成员资格的写入口仍在建会/入会事务里，
/// 聊天不另建第二套公会成员或在线状态。
/// </summary>
internal sealed partial class CharacterRepository : IChatDirectoryStore
{
    /// <summary>角色所属账号（<c>characters.id</c> 主键，最多一行）；角色不存在返回 null。</summary>
    public string? AccountOfCharacter(ushort characterId)
    {
        using var db = database.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT account FROM characters WHERE id=$id";
        command.Parameters.AddWithValue("$id", characterId);
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// 公会成员账号（<c>guild_members JOIN characters</c>）：公会聊天的成员来源。
    /// 同一账号有多个角色在会时账号会重复，由调用方按账号去重（在线会话本来就按账号唯一）。
    /// </summary>
    public string[] GuildMemberAccounts(long guildId)
    {
        using var db = database.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT c.account FROM guild_members m JOIN characters c ON c.id=m.character_id
            WHERE m.guild_id=$guild ORDER BY c.account
            """;
        command.Parameters.AddWithValue("$guild", guildId);
        using var reader = command.ExecuteReader();
        var accounts = new List<string>();
        while (reader.Read())
            accounts.Add(reader.GetString(0));
        return accounts.ToArray();
    }
}
