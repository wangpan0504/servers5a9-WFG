using DfoServer.Game.Guild;

namespace DfoServer.Persistence;

internal static class GuildRecommendedChannelSchema
{
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS guild_recommended_channels(
            guild_id INTEGER PRIMARY KEY REFERENCES guilds(guild_id) ON DELETE CASCADE,
            region INTEGER NOT NULL CHECK(typeof(region)='integer' AND region BETWEEN 0 AND 249),
            channel INTEGER NOT NULL CHECK(typeof(channel)='integer' AND channel BETWEEN 1 AND 255)
        );
        """;
}

internal sealed partial class CharacterRepository : IGuildGovernanceStore
{
    public GuildManagementCommit ChangeGuildGovernance(GuildManagementIdentity actor, GuildGovernanceChange change, long now) =>
        actor.Owner.Execute(() =>
        {
            if (!actor.IsCurrent()) return new GuildManagementCommit(3);
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.ChangeGuildGovernance");
            using var transaction = write.Transaction;
            var state = ReadManagement(db, transaction, actor.Owner.Account, actor.CharacterId);
            if (state is null) return new(34);
            ushort error = GuildGovernanceRules.Validate(state, actor.CharacterId, change);
            if (error != 0) return new(error);
            uint guildId = state.Guild.Id;
            using var command = ManagementCommand(db, transaction, "SELECT 1", ("$guild", guildId));
            bool changed;
            switch (change.Action)
            {
                case GuildGovernanceAction.MasterTransfer:
                {
                    var successor = change.Target!;
                    // 会长字段与双方职位在同一写事务变更；不改目标角色资产，也不取得目标账号锁。
                    command.CommandText = """
                        SELECT m.grade FROM guild_members m JOIN characters c ON c.id=m.character_id
                        WHERE m.guild_id=$guild AND c.id=$target AND c.account=$account AND c.name=$name
                        """;
                    command.Parameters.AddWithValue("$target", successor.Id);
                    command.Parameters.AddWithValue("$account", successor.Account);
                    command.Parameters.AddWithValue("$name", successor.Name);
                    if (command.ExecuteScalar() is not long grade || grade == 5) return new(86);
                    command.CommandText = "UPDATE guilds SET master_character_id=$target WHERE guild_id=$guild AND master_character_id=$actor";
                    command.Parameters.AddWithValue("$actor", actor.CharacterId);
                    if (command.ExecuteNonQuery() != 1) return new(36);
                    command.CommandText = """
                        UPDATE guild_members SET grade=CASE WHEN character_id=$target THEN 1 ELSE 4 END
                        WHERE guild_id=$guild AND character_id IN ($actor,$target)
                        """;
                    if (command.ExecuteNonQuery() != 2) throw new InvalidOperationException("会长转让成员状态不完整");
                    changed = true;
                    break;
                }
                case GuildGovernanceAction.RecommendedChannel:
                {
                    var channel = change.RecommendedChannel!.Identity;
                    if (channel != actor.Channel) return new(7);
                    changed = state.RecommendedChannel != channel;
                    if (!changed) break;
                    command.CommandText = """
                        INSERT INTO guild_recommended_channels(guild_id,region,channel) VALUES($guild,$region,$channel)
                        ON CONFLICT(guild_id) DO UPDATE SET region=excluded.region,channel=excluded.channel
                        """;
                    command.Parameters.AddWithValue("$region", channel.ServerIndex);
                    command.Parameters.AddWithValue("$channel", channel.Number);
                    command.ExecuteNonQuery();
                    break;
                }
                case GuildGovernanceAction.RolePermissions:
                case GuildGovernanceAction.RoleName:
                {
                    var role = state.Roles[change.Grade];
                    uint mask = change.Action == GuildGovernanceAction.RolePermissions ? change.Permissions : role.Permissions;
                    string name = change.Action == GuildGovernanceAction.RoleName ? change.Text : role.Name;
                    changed = role.Permissions != mask || role.Name != name;
                    if (!changed) break;
                    command.CommandText = """
                        INSERT INTO guild_roles(guild_id,grade,permissions,name) VALUES($guild,$grade,$mask,$name)
                        ON CONFLICT(guild_id,grade) DO UPDATE SET permissions=excluded.permissions,name=excluded.name
                        """;
                    command.Parameters.AddWithValue("$grade", change.Grade);
                    command.Parameters.AddWithValue("$mask", mask);
                    command.Parameters.AddWithValue("$name", name);
                    command.ExecuteNonQuery();
                    break;
                }
                case GuildGovernanceAction.Settings:
                {
                    var selected = change.Settings!;
                    command.CommandText = "SELECT COALESCE((SELECT webpage FROM guild_settings WHERE guild_id=$guild),0)";
                    bool currentWebpage = Convert.ToInt64(command.ExecuteScalar()) != 0;
                    changed = (selected.Mask & GuildGovernanceRules.Promotion) != 0 &&
                            (state.Promotion != selected.Promotion || currentWebpage != selected.Webpage) ||
                        (selected.Mask & GuildGovernanceRules.Tags) != 0 && state.Tags != selected.Tags ||
                        (selected.Mask & GuildGovernanceRules.JoinMode) != 0 && state.JoinMode != selected.JoinMode;
                    if (!changed) break;
                    command.CommandText = "INSERT INTO guild_settings(guild_id) VALUES($guild) ON CONFLICT(guild_id) DO NOTHING";
                    command.ExecuteNonQuery();
                    // 客户端会携带未选择字段的旧值；只有选中位参与比较、修改与权限判定。
                    command.CommandText = """
                        UPDATE guild_settings SET
                            promotion=CASE WHEN $promotion THEN $text ELSE promotion END,
                            webpage=CASE WHEN $promotion THEN $webpage ELSE webpage END,
                            tags=CASE WHEN $tags THEN $tagValue ELSE tags END,
                            join_mode=CASE WHEN $join THEN $mode ELSE join_mode END
                        WHERE guild_id=$guild AND
                            (($promotion AND (promotion<>$text OR webpage<>$webpage)) OR
                             ($tags AND tags<>$tagValue) OR ($join AND join_mode<>$mode))
                        """;
                    command.Parameters.AddWithValue("$promotion", (selected.Mask & GuildGovernanceRules.Promotion) != 0);
                    command.Parameters.AddWithValue("$tags", (selected.Mask & GuildGovernanceRules.Tags) != 0);
                    command.Parameters.AddWithValue("$join", (selected.Mask & GuildGovernanceRules.JoinMode) != 0);
                    command.Parameters.AddWithValue("$text", selected.Promotion);
                    command.Parameters.AddWithValue("$webpage", selected.Webpage);
                    command.Parameters.AddWithValue("$tagValue", selected.Tags);
                    command.Parameters.AddWithValue("$mode", selected.JoinMode);
                    changed = command.ExecuteNonQuery() != 0;
                    break;
                }
                case GuildGovernanceAction.MemberGrade:
                {
                    if (change.Region != actor.Channel.ServerIndex) return new(3);
                    var target = change.Target!;
                    command.CommandText = """
                        SELECT m.grade FROM guild_members m JOIN characters c ON c.id=m.character_id
                        WHERE m.guild_id=$guild AND c.id=$target AND c.account=$account AND c.name=$name
                        """;
                    command.Parameters.AddWithValue("$target", target.Id);
                    command.Parameters.AddWithValue("$account", target.Account);
                    command.Parameters.AddWithValue("$name", target.Name);
                    object? grade = command.ExecuteScalar();
                    if (grade is null || grade is DBNull) return new(3);
                    if (Convert.ToByte(grade) == 1) return new(87);
                    // 存量0号只作普通会员；会长资格始终同时依赖公会会长字段。
                    byte current = GuildRoleRules.NormalizeGrade(target.Id, state.MasterId, Convert.ToByte(grade));
                    if (current == 1) return new(87);
                    changed = current != change.Grade;
                    if (!changed) break;
                    command.CommandText = "UPDATE guild_members SET grade=$grade WHERE guild_id=$guild AND character_id=$target";
                    command.Parameters.AddWithValue("$grade", change.Grade);
                    if (command.ExecuteNonQuery() != 1) return new(3);
                    break;
                }
                default:
                    return new(23);
            }
            long revision = changed ? TouchManagement(db, transaction, guildId) : state.Revision;
            if (!actor.IsCurrent()) return new(3);
            transaction.Commit();
            return new(0, guildId, revision, Members: state.Members);
        });
}
