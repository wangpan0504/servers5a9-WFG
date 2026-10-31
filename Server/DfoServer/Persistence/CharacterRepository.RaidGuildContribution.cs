using System.Collections.Immutable;
using DfoServer.Game.Guild;
using DfoServer.Game.Raids;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    void CommitRaidGuildContribution(SqliteConnection db, SqliteTransaction tx, Guid instance, RaidKind kind,
        IReadOnlyCollection<RaidPhaseEligibility> receipts,
        ImmutableDictionary<ushort, ImmutableArray<ushort>> parties, long now)
    {
        var eligible = receipts.Where(row => row.CountsAsClear && row.Eligible).Select(row => row.Character).ToHashSet();
        if (eligible.Count == 0) return;
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT r.character_id,r.account,r.guild_id,r.guild_joined_at,
                EXISTS(SELECT 1 FROM guild_members m WHERE m.character_id=r.character_id
                    AND m.guild_id=r.guild_id AND m.joined_at_unix=r.guild_joined_at)
            FROM character_raid_runs r WHERE r.instance_id=$instance AND r.raid_type=$type
            """;
        command.Parameters.AddWithValue("$instance", instance.ToString("N"));
        command.Parameters.AddWithValue("$type", (byte)kind);
        var members = new Dictionary<ushort, (string Account, GuildDungeonMembership Frozen, bool Current)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                // 旧run没有原入场会籍，不能按当前关系补造或追发贡献。
                if (reader.IsDBNull(2) || reader.IsDBNull(3)) continue;
                ushort id = checked((ushort)reader.GetInt32(0));
                members.Add(id, (reader.GetString(1), new(id, checked((uint)reader.GetInt64(2)), reader.GetInt64(3)), reader.GetBoolean(4)));
            }
        foreach (ushort id in eligible.Order())
        {
            if (!members.TryGetValue(id, out var own) || !parties.TryGetValue(id, out var party)) continue;
            if (party.IsDefaultOrEmpty || party.Length > 4 || !party.Contains(id) || party.Distinct().Count() != party.Length)
                throw new InvalidDataException("攻坚贡献缺少真实清图队伍");
            int count = party.Count(character => eligible.Contains(character) && members.TryGetValue(character, out var member) &&
                member.Current && member.Frozen.GuildId != 0 && member.Frozen.GuildId == own.Frozen.GuildId);
            int points = GuildContributionRules.Points(GuildContributionSource.Raid, 0, count);
            var result = CreditGuildContributionInTransaction(db, tx, own.Account, id, GuildContributionSource.Raid,
                $"raid:{(byte)kind}:{instance:N}:{id}", now, points, own.Frozen);
            if (!result.Success) throw new InvalidDataException("攻坚通关公会贡献未能原子提交");
        }
    }
}
