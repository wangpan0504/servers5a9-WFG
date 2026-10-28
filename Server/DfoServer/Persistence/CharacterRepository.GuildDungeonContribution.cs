using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Guild;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    GuildContributionCommit? CommitGuildDungeonContribution(SqliteConnection db, SqliteTransaction tx,
        AccountOwners.Lease owner, PreparedDungeonSettlement prepared, int rewardContribution = 0)
    {
        if (prepared.GuildContributionSource is not { } source || prepared.GuildMemberships.IsDefaultOrEmpty) return null;
        ushort id = prepared.Fact.Identity.CharacterId;
        var frozen = prepared.GuildMemberships.SingleOrDefault(row => row.CharacterId == id);
        if (frozen is null) throw new InvalidDataException("通关公会贡献缺少本人入场会籍");
        if (prepared.GuildMemberships.Length > 4 ||
            prepared.GuildMemberships.Select(row => row.CharacterId).Distinct().Count() != prepared.GuildMemberships.Length)
            throw new InvalidDataException("通关公会贡献名册无效");
        int count = 0;
        using var command = ManagementCommand(db, tx,
            "SELECT 1 FROM guild_members WHERE character_id=$id AND guild_id=$guild AND joined_at_unix=$joined",
            ("$id", 0), ("$guild", frozen.GuildId), ("$joined", 0L));
        // 人数只计原入场名册、原公会、原入会代次，退会重入不能延续旧队伍倍率。
        foreach (var member in prepared.GuildMemberships)
        {
            if (frozen.GuildId == 0 || member.GuildId != frozen.GuildId) continue;
            command.Parameters["$id"].Value = member.CharacterId;
            command.Parameters["$joined"].Value = member.JoinedAtUnix;
            if (command.ExecuteScalar() is not null) count++;
        }
        if (rewardContribution < 0 || rewardContribution > 0 && source != GuildContributionSource.GuildDungeon)
            throw new InvalidDataException("公会副本额外贡献来源无效");
        // 自然贡献先按人数结算，资源奖励贡献另加，不能把奖励20当作自然贡献的替代或再乘人数。
        int points = checked(GuildContributionRules.Points(source, prepared.Fact.DungeonId, count) + rewardContribution);
        string key = $"guild:{id}:{prepared.Fact.Identity.InstanceId:N}:{prepared.EventId:N}";
        var result = CreditGuildContributionInTransaction(db, tx, owner, id, source, key,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), points, frozen);
        if (!result.Success) throw new InvalidDataException("通关公会贡献未能原子提交");
        return result;
    }
}
