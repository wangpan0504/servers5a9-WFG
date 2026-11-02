using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Session;

internal sealed record DungeonEnterCountReceipt(long Generation, long Epoch, Character Character, int Day,
    ImmutableArray<DungeonEnterCount> Counts);

internal sealed partial class GameSession
{
    public DungeonEnterCountReceipt? CaptureDungeonEnterCounts(DungeonEnterCountCatalog catalog, bool onlyForSelection = false)
    {
        // 仅投影已有准入账本且资源与现有规则一致的两祭坛；其它行不以满额冒充真实剩余。
        var limits = new[] { 11006, 11007 }.Select(id => (Id: id, Limit: catalog.Limits.GetValueOrDefault(id))).ToArray();
        return owner.Execute(() =>
        {
            if (Selected is not { } character) return null;
            if (onlyForSelection && (!towns.Areas.TryGetValue((character.Position.Town, character.Position.Area), out var area) ||
                area.DungeonWorldMap is not { } world || !towns.DungeonMaps.TryGetValue(world, out var map) ||
                !map.Options.Any(option => option.DungeonId is 11006 or 11007))) return null;
            int day = ShopPurchaseCount.Today((timeProvider ?? TimeProvider.System).GetUtcNow());
            var counts = ImmutableArray.CreateBuilder<DungeonEnterCount>();
            foreach (var (id, limit) in limits)
            {
                if (limit is not { ResetKey: 0, Daily: DungeonAltarEntryPolicy.DailyLimit, Flag: 1, Weekly: -1, GroupSize: 1 })
                    continue;
                var kind = id == 11006 ? DungeonSpecialEntryKind.Blood : DungeonSpecialEntryKind.Ultimate;
                var usage = repository.LoadSpecialEntryUsage(owner, character, day, kind);
                counts.Add(new(checked((uint)id), checked((byte)Math.Max(0, limit.Daily - usage.CharacterEntries)), 0));
            }
            return new DungeonEnterCountReceipt(owner.Generation, SelectionEpoch, character, day, counts.ToImmutable());
        });
    }

    public DungeonEnterCountReceipt? CaptureGriefTowerEnterCounts(GriefTowerCatalog catalog, bool onlyForSelection = false)
    {
        // 复用现有每日一次基础挑战规则，不把旧enterable count声明解释成剩余或邀请函重试额度。
        int entrance = catalog.FloorsByDungeon.Values.Single(floor => floor.Layer == 1).DungeonId;
        return owner.Execute(() =>
        {
            if (Selected is not { } character) return null;
            if (onlyForSelection && (!towns.Areas.TryGetValue((character.Position.Town, character.Position.Area), out var area) ||
                area.DungeonWorldMap is not { } world || !towns.DungeonMaps.TryGetValue(world, out var map) ||
                !map.Options.Any(option => option.DungeonId == entrance))) return null;
            var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            var state = repository.ReadGriefTower(owner, character.Id).State;
            // 入场事务已保存挑战日；放弃不退款，邀请函重试也不增加基础日额度。
            byte remaining = state.CharacterDay < QuestCycle.Day(now) && state.Layer <= catalog.TopLayer ? (byte)1 : (byte)0;
            int displayedLayer = Math.Min(state.Layer, catalog.TopLayer);
            int dungeon = catalog.FloorsByDungeon.Values.Single(floor => floor.Layer == displayedLayer).DungeonId;
            var counts = ImmutableArray.CreateBuilder<DungeonEnterCount>();
            counts.Add(new(checked((uint)entrance), remaining, 0));
            // 首层入口与当前层详情使用各自的副本键，不能把计数写到绝望之塔或只更新旧楼层。
            if (dungeon != entrance) counts.Add(new(checked((uint)dungeon), remaining, 0));
            return new DungeonEnterCountReceipt(owner.Generation, SelectionEpoch, character, ShopPurchaseCount.Today(now), counts.ToImmutable());
        });
    }

    public DungeonEnterCountReceipt? CaptureDespairTowerEnterCounts(TowerOfDespairCatalog catalog, bool onlyForSelection = false) =>
        owner.Execute(() =>
        {
            if (Selected is not { } character) return null;
            if (onlyForSelection && (!towns.Areas.TryGetValue((character.Position.Town, character.Position.Area), out var area) ||
                area.DungeonWorldMap is not { } world || !towns.DungeonMaps.TryGetValue(world, out var map) ||
                !map.Options.Any(option => option.DungeonId == catalog.RootDungeonId))) return null;
            int day = ShopPurchaseCount.Today((timeProvider ?? TimeProvider.System).GetUtcNow());
            var progress = repository.LoadDespairProgress(owner, character);
            var usage = repository.LoadSpecialEntryUsage(owner, character, day, DungeonSpecialEntryKind.Despair);
            // 首次入场即用掉基础日额度，放弃不退还；4473重试与通关日限制继续由原准入规则处理。
            byte remaining = usage.CharacterEntries == 0 && progress.LastClearDay != day ? (byte)1 : (byte)0;
            int dungeon = catalog.Floors[progress.NextLayer - 1].DungeonId;
            var counts = ImmutableArray.CreateBuilder<DungeonEnterCount>();
            counts.Add(new(checked((uint)catalog.RootDungeonId), remaining, 0));
            if (dungeon != catalog.RootDungeonId) counts.Add(new(checked((uint)dungeon), remaining, 0));
            return new DungeonEnterCountReceipt(owner.Generation, SelectionEpoch, character, day, counts.ToImmutable());
        });

    // 黎明裂缝使用独立 crackofdaybreak.etc 次数行；只投影 530 的日剩余。
    public DungeonEnterCount? DaybreakEnterCount() => owner.Execute(() =>
    {
        if (incounts is null || Selected is not { } character || incounts.Find(530) is not { Limit: > 0 } row)
            return (DungeonEnterCount?)null;
        var usage = repository.LoadDungeonIncount(owner, character.Id, row.CountKey, IncountPeriod(row.CountKey));
        return new DungeonEnterCount(530, checked((byte)Math.Min(DungeonIncountPolicy.DailyRemaining(row, usage), DungeonIncountCatalog.DaybreakDailyLimit)), 0);
    });

    public bool InDaybreakArea() => owner.Execute(() => Selected is { } character &&
        towns.Areas.TryGetValue((character.Position.Town, character.Position.Area), out var area) &&
        area.DungeonWorldMap is { } world && towns.DungeonMaps.TryGetValue(world, out var map) &&
        map.Options.Any(option => option.DungeonId == 530));
    public bool CanPublishDungeonEnterCounts(DungeonEnterCountReceipt receipt) => owner.Execute(() =>
        owner.Generation == receipt.Generation && SelectionEpoch == receipt.Epoch && ReferenceEquals(Selected, receipt.Character) &&
        ShopPurchaseCount.Today((timeProvider ?? TimeProvider.System).GetUtcNow()) == receipt.Day);
}
