using DfoServer.Game.Accounts;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Events;

internal interface IEndlessAdventureStore
{
    int ReadEndlessAdventure(AccountOwners.Lease owner, int day);
}

internal sealed record EndlessAdventureClear(int Day, int Amount, DateTimeOffset At);

// 本服产币规则：已加载房间的应耗疲劳，燃烧翻倍；无限疲劳不改变应耗统计。
internal sealed class EndlessAdventurePolicy(ItemTemplate coin, GameDebugSettings settings, TimeProvider clock)
{
    public const ushort EventId = 2257;
    public const int CoinId = 490011740;
    public const int NpcId = 400000023;
    public const int ShopId = 400000008;
    public const int DailyLimit = 200;
    public TimeProvider Clock => clock;
    public ItemTemplate Coin => coin;
    public bool Available => coin is { Id: CoinId, Kind: ItemKind.Material, AccountMaterial: not null } &&
        coin.Expiration.SupportsCreation && !InventoryRewardGrantService.Create(coin, utcNow: clock.GetUtcNow(),
            shopExpiration: settings.IgnoreCashShopAbsoluteExpiration,
            ignoreUsablePeriod: settings.IgnoreCashShopUsablePeriod).IsExpired(clock.GetUtcNow().ToUnixTimeSeconds());

    public static int Day(DateTimeOffset now) => DfoServer.Game.Inventory.Models.ShopPurchaseCount.Today(now);
    public static int Award(uint fatigue, bool burning, int earned, int available = int.MaxValue)
    {
        if (earned is < 0 or > DailyLimit || available < 0) throw new InvalidDataException("无尽大冒险日额度无效");
        return (int)Math.Min(Math.Min((ulong)fatigue * (burning ? 2ul : 1ul), (ulong)(DailyLimit - earned)), (ulong)available);
    }

    public EndlessAdventureClear? Prepare(SinglePlayerDungeonRun run, bool recommended, bool enabled)
    {
        var entry = run.Entry;
        if (!enabled || !Available || !recommended || !entry.UsesFatigue || entry.IsTutorial || entry.IsPractice ||
            entry.IsClassExperience || entry.IsTrainingRoom || entry.IsTournament || entry.RaidEntry is not null ||
            entry.Maze.IsGriefTower || entry.IsBossRush || entry.DisableClearReward || run.Snapshot.ConsumedFatigue == 0)
            return null;
        var at = clock.GetUtcNow();
        return new(Day(at), Award(run.Snapshot.ConsumedFatigue, run.FatigueAccelerationPercent > 0, 0), at);
    }
}
