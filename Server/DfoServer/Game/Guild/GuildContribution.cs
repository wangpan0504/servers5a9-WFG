using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Guild;

internal enum GuildContributionSource : byte { Login = 1, Chat = 2, RecommendedDungeon = 3, SpecialDungeon = 4, Raid = 5, GuildDungeon = 6, HallWait = 7 }
internal sealed record GuildDungeonMembership(ushort CharacterId, uint GuildId, long JoinedAtUnix);
internal sealed record GuildContributionWeek(long StartUnix, int Points);
internal sealed record GuildContributionCommit(ushort Error, uint GuildId = 0, long Revision = 0, int Points = 0,
    int Coins = 0, Character? Character = null, InventoryPlan? Inventory = null, GuildManagementTarget[]? Members = null)
{
    public bool Success => Error == 0;
}

internal static class GuildContributionRules
{
    public const int CoinItemId = 490011648;
    // 本版公会主界面资源明确列出的自然贡献；公共积分另有独立业务，不能混用。
    public static int Points(GuildContributionSource source) => source switch
    {
        GuildContributionSource.Login => 100,
        GuildContributionSource.Chat => 50,
        GuildContributionSource.RecommendedDungeon => 10,
        GuildContributionSource.SpecialDungeon => 50,
        GuildContributionSource.Raid => 250,
        _ => throw new InvalidDataException("公会贡献来源尚未接通")
    };
    public static int Points(GuildContributionSource source, uint dungeonId, int sameGuildCount)
    {
        int basis = source == GuildContributionSource.GuildDungeon ? dungeonId switch
        {
            8500 => 60, 8503 or 8504 => 80, 8505 or 8507 => 100, 8508 => 150,
            _ => throw new InvalidDataException("公会地下城贡献资源未声明此地下城")
        } : Points(source);
        // 本版资源的同会人数倍率为2人1.5倍、3人3倍、4人4倍，人数包含本人。
        int halves = sameGuildCount switch
        {
            0 or 1 => 2, 2 => 3, 3 => 6, 4 => 8,
            _ => throw new InvalidDataException("公会贡献同会人数无效")
        };
        return checked(basis * halves / 2);
    }
    public static int Coins(int points) => points switch
    {
        < 0 => throw new InvalidDataException("公会周贡献不能为负"),
        0 => 0, <= 1000 => 300, <= 3000 => 500, <= 5000 => 700,
        <= 8000 => 1000, <= 10000 => 1500, <= 15000 => 2000, _ => 2500
    };
    public static int CoinLimit(byte tier) => tier switch
    {
        0 => 10000, 1 => 12000, 2 => 15000, 3 => 20000, 4 => 30000,
        _ => throw new InvalidDataException("公会级别无效")
    };
    public static long WeekStart(long unix)
    {
        long day = GuildAttendanceDay.Start(unix);
        // 1970-01-01为周四；本版在UTC+8每周四06:00结算。
        long days = Math.DivRem(checked(day + 2 * 3600), 86400, out _);
        long weekday = (days % 7 + 7) % 7;
        return checked(day - weekday * 86400);
    }
}

internal interface IGuildContributionStore
{
    GuildContributionCommit ObserveGuildContribution(GuildManagementIdentity actor, GuildContributionSource source, long now);
    GuildContributionCommit SettleGuildContribution(GuildManagementIdentity actor, Character expected, long now,
        Func<Character, int, InventoryPlan?> prepare);
}

internal sealed class GuildContributionService(IGuildContributionStore store, ItemCatalog items, GoldLimitCatalog gold,
    EpicPieceCatalog pieces, GuildManagementService management, TimeProvider? clock = null)
{
    public GuildContributionCommit Observe(GameSession session, GuildContributionSource source)
    {
        var actor = session.CaptureGuildManagement(requireTown: false);
        return actor is null ? new(3) : store.ObserveGuildContribution(actor, source,
            (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds());
    }
    public GuildContributionCommit Settle(GuildManagementIdentity actor, Character expected)
    {
        long now = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
        return store.SettleGuildContribution(actor, expected, now, (character, amount) =>
        {
            var grant = InventoryRewardGrantService.TryGrantBatch(character, [new(GuildContributionRules.CoinItemId, amount)],
                items, gold, pieces, DateTimeOffset.FromUnixTimeSeconds(now));
            return grant.Accepted ? new(grant.Plan!.Before, grant.Plan.After, "guild_weekly_coins",
                new(character)) : null;
        });
    }
    public void Publish(GuildContributionCommit result)
    {
        if (result.Success && result.GuildId != 0 && result.Members is { } members)
            management.Publish(new(0, result.GuildId, result.Revision, Members: members));
    }
}
