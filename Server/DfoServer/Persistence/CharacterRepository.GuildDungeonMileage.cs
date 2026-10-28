using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    uint CommitGuildDungeonMileage(SqliteConnection db, SqliteTransaction tx, AccountOwners.Lease owner,
        SinglePlayerDungeonRun run, PreparedDungeonSettlement prepared)
    {
        var entry = run.Entry;
        if (guildDungeonMileage <= 0 || !entry.GuildMileageEligible || !entry.IsOrdinary || entry.IsPractice ||
            entry.IsTutorial || entry.IsClassExperience || entry.IsTrainingRoom || entry.IsTournament || entry.IsAltar ||
            entry.IsDespair || entry.Maze.IsGriefTower || entry.IsQuestReplay || entry.IsCosmo || entry.HellParty is not null ||
            entry.IsHellLegend || entry.DisableClearReward)
            return 0;
        string key = GuildDungeonMileageEvent(prepared);
        var membership = prepared.GuildMemberships.IsDefaultOrEmpty ? null :
            prepared.GuildMemberships.SingleOrDefault(row => row.CharacterId == prepared.Fact.Identity.CharacterId);
        if (membership is null) throw new InvalidDataException("公会公共积分缺少本人入场会籍快照");
        return CreditGuildDungeonMileageInTransaction(db, tx, owner, prepared.Fact.Identity.CharacterId, key,
            guildDungeonMileage, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), membership);
    }

    static string GuildDungeonMileageEvent(PreparedDungeonSettlement prepared) =>
        $"dungeon:{prepared.Fact.Identity.CharacterId}:{prepared.Fact.Identity.InstanceId:N}:{prepared.EventId:N}";

    static uint ReadGuildDungeonMileage(SqliteConnection db, SqliteTransaction tx, PreparedDungeonSettlement prepared)
    {
        using var command = ManagementCommand(db, tx,
            "SELECT guild_id FROM guild_economy_events WHERE event_id=$event AND character_id=$id",
            ("$event", GuildDungeonMileageEvent(prepared)), ("$id", prepared.Fact.Identity.CharacterId));
        return Convert.ToUInt32(command.ExecuteScalar() ?? 0L);
    }
}
