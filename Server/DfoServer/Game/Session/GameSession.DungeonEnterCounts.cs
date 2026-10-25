using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
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

    public bool CanPublishDungeonEnterCounts(DungeonEnterCountReceipt receipt) => owner.Execute(() =>
        owner.Generation == receipt.Generation && SelectionEpoch == receipt.Epoch && ReferenceEquals(Selected, receipt.Character) &&
        ShopPurchaseCount.Today((timeProvider ?? TimeProvider.System).GetUtcNow()) == receipt.Day);
}
