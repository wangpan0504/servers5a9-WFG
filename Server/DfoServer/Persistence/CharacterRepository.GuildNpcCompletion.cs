using DfoServer.Game.Guild;
using DfoServer.GameWorld.Guilds;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public GuildNpcState[] CompleteGuildNpcUpgrades(GuildNpcCatalog catalog, GuildParameterCatalog parameters, long now)
    {
        using var db = database.Open();
        var due = new List<(uint Guild, uint Npc, int Target, long Deadline)>();
        using (var query = db.CreateCommand())
        {
            // 空轮询不占SQLite写入闸门；每轮限制数量，剩余到期项由下一轮继续处理。
            query.CommandText = """
                SELECT guild_id,npc_id,upgrade_target,upgrade_until FROM guild_npc_progress
                WHERE upgrade_until>0 AND upgrade_until<=$now ORDER BY upgrade_until,guild_id,npc_id LIMIT 64
                """;
            query.Parameters.AddWithValue("$now", now);
            using var reader = query.ExecuteReader();
            while (reader.Read()) due.Add((checked((uint)reader.GetInt64(0)), checked((uint)reader.GetInt64(1)),
                reader.GetInt32(2), reader.GetInt64(3)));
        }
        if (due.Count == 0) return [];
        using var write = db.BeginWrite("CharacterRepository.CompleteGuildNpcUpgrades");
        using var tx = write.Transaction;
        var changed = new HashSet<uint>();
        foreach (var row in due)
        {
            if (catalog.Find(row.Npc)?.At(row.Target) is not { } next)
                throw new InvalidDataException("到期公会NPC升级目标不在资源名册中");
            using var update = ManagementCommand(db, tx, """
                UPDATE guild_npc_progress SET level=upgrade_target,upgrade_target=0,upgrade_until=0
                WHERE guild_id=$guild AND npc_id=$npc AND level=$previous AND upgrade_target=$target
                  AND upgrade_until=$deadline AND upgrade_until<=$now
                """, ("$guild", row.Guild), ("$npc", row.Npc), ("$previous", row.Target - 1),
                ("$target", row.Target), ("$deadline", row.Deadline), ("$now", now));
            if (update.ExecuteNonQuery() != 1) continue;
            if (row.Npc == GuildNpcCatalog.Guardian)
            {
                using var guild = ManagementCommand(db, tx,
                    "SELECT tier,experience FROM guilds WHERE guild_id=$guild", ("$guild", row.Guild));
                int tier;
                long experience;
                using (var reader = guild.ExecuteReader())
                {
                    if (!reader.Read()) throw new InvalidDataException("到期公会NPC缺少所属公会");
                    tier = reader.GetInt32(0); experience = reader.GetInt64(1);
                }
                if (next.ClassTier > tier)
                {
                    guild.CommandText = "UPDATE guilds SET tier=$tier,level=$level WHERE guild_id=$guild";
                    guild.Parameters.AddWithValue("$tier", next.ClassTier);
                    guild.Parameters.AddWithValue("$level", parameters.LevelAtExperience(next.ClassTier, experience));
                    guild.ExecuteNonQuery();
                }
            }
            changed.Add(row.Guild);
        }
        var states = new List<GuildNpcState>(changed.Count);
        foreach (uint guildId in changed)
        {
            TouchManagement(db, tx, guildId);
            using var owner = ManagementCommand(db, tx, """
                SELECT c.account,c.id FROM guilds g JOIN characters c ON c.id=g.master_character_id
                WHERE g.guild_id=$guild
                """, ("$guild", guildId));
            string account;
            ushort id;
            using (var reader = owner.ExecuteReader())
            {
                if (!reader.Read()) throw new InvalidDataException("到期公会NPC缺少现任会长");
                account = reader.GetString(0); id = checked((ushort)reader.GetInt32(1));
            }
            if (ReadNpcs(db, tx, account, id, catalog) is not { } state)
                throw new InvalidDataException("到期公会NPC缺少现任会籍");
            states.Add(state);
        }
        tx.Commit();
        return states.ToArray();
    }
}
