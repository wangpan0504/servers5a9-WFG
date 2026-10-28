using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Guilds;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Guild;

internal sealed record GuildDungeonRewardChoice(GuildDungeonReward Definition, ItemCore Core);
internal sealed record GuildDungeonRewardGrant(int Ratio, GuildDungeonRewardChoice Choice, long Created, ItemCore? RepeatCore = null, int Version = 1);
internal sealed record GuildDungeonClearReward(uint DungeonId, ImmutableArray<GuildDungeonRewardChoice> Choices, long Created,
    ItemCore? RepeatCore = null)
{
    // 资源解析及完整物品核心在账号锁和SQL事务外准备，事务内只按实际净化比例选择。
    public static GuildDungeonClearReward Prepare(GuildDungeonDefinition dungeon, ItemCatalog items, DateTimeOffset now,
        GuildDungeonCatalog catalog)
    {
        var choices = dungeon.Rewards.Select(row =>
        {
            var core = InventoryRewardGrantService.Create(items.Get(row.ItemId), row.Amount, now);
            if (!core.IsStackable || core.IsExpired(now.ToUnixTimeSeconds()))
                throw new InvalidDataException("公会副本奖励必须是当前有效的堆叠物品");
            return new GuildDungeonRewardChoice(row, core);
        }).ToImmutableArray();
        var repeat = InventoryRewardGrantService.Create(items.Get(catalog.RepeatRewardItem), catalog.RepeatRewardAmount, now);
        if (!repeat.IsStackable || repeat.IsExpired(now.ToUnixTimeSeconds()))
            throw new InvalidDataException("公会副本重复奖励必须是当前有效的堆叠物品");
        return new(dungeon.Id, choices, now.ToUnixTimeSeconds(), repeat);
    }

    public GuildDungeonRewardGrant Select(int ratio)
    {
        if (ratio is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(ratio));
        var choice = Choices.FirstOrDefault(row => row.Definition.Ratio >= ratio)
            ?? throw new InvalidDataException("公会副本奖励缺少当前净化比例分档");
        return new(ratio, choice, Created, ratio == 100 ? RepeatCore : null, RepeatCore is null ? 1 : 2);
    }
}
