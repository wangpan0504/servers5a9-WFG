using DfoServer.Infrastructure;
using System.Buffers.Binary;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;

namespace DfoServer.Testing.Inventory;

internal static class InventoryStackLimitTests
{
    public static void Run(Character character, ItemCatalog catalog, ItemResources itemResources, Action<bool, string> check)
    {
        // 该真实模板有绝对期限，用有效期内的时刻单独验证堆叠规则，不修改资源或生产期限。
        var now = new DateTimeOffset(2016, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var empty = Role();
        var original = catalog.Get(10008885);
        try
        {
            LegacyExpiryFixture.Restore(catalog, (10008885, "2016-03-10 06:00:00"));
            var limited = catalog.Get(10008885);
            var first = Grant(empty, new InventoryReward(limited.Id, 1));
            check(limited.StackLimit == 1 && first.Accepted && first.Plan!.After.Items.Length == 1,
                "matched limit-one box grants its first instance during its valid period");
            var limitedItem = first.Plan!.After.Items[0] with { Uid = 101, Position = Bag(3), Core = first.Plan.After.Items[0].Core with { SortLockFlag = 1 } };
            var limitedRole = Role(limitedItem);
            check(Grant(limitedRole, new InventoryReward(limited.Id, 1)) is { Accepted: false, Error: 17, Plan: null } && limitedRole.Inventory.Items[0] == limitedItem,
                "limit-one grant detects quickbar stack and rejects without allocating a second instance");
            check(!Grant(empty, new InventoryReward(limited.Id, 2)).Accepted && !Grant(empty, new(limited.Id, 1), new(limited.Id, 1)).Accepted,
                "single-row and repeated-row limit-one batches are rejected atomically");
            check(!Grant(limitedRole, new(10400, 1), new(limited.Id, 1)).Accepted && limitedRole.Inventory.Items.Length == 1,
                "earlier planned rewards are not published when a later stack fails");
            check(Grant(empty, new InventoryReward(10400, 2)).Plan!.After.Items.Length == 2,
                "equipment stack limit one is not a unique possession rule");
            check(!InventoryRewardGrantService.TryGrantBatch(empty, [new(limited.Id, 1)], catalog, itemResources.GoldLimits, itemResources.EpicPieces, now.AddYears(1)).Accepted,
                "stack fix retains absolute expiration rejection");
        }
        finally
        {
            // 历史期限仅属于本段夹具，不能污染后续使用同一目录的真实资源用例。
            catalog.PrefillTemplates([KeyValuePair.Create(original.Id, original)]);
        }
        check(ReferenceEquals(catalog.Get(original.Id), original), "历史期限夹具完整恢复共享目录的原模板");

        var potion = catalog.Get(1106);
        var core = InventoryRewardGrantService.Create(potion, 5, now);
        var old = new InventoryItem(101, Bag(3), core with { Value = potion.StackLimit - 2, SortLockFlag = 1 });
        var later = new InventoryItem(102, Bag(65), core);
        var current = Role(later, old);
        var granted = Grant(current, new InventoryReward(potion.Id, 2));
        check(granted.Accepted && granted.Plan!.After.Find(old.Position) == old with { Core = old.Core with { Value = potion.StackLimit } } &&
            granted.Plan.After.Find(later.Position) == later, "grant chooses first compatible slot and preserves its UID and sorting lock");
        check(!Grant(current, new InventoryReward(potion.Id, 3)).Accepted && !Grant(empty, new InventoryReward(potion.Id, potion.StackLimit + 1)).Accepted,
            "automatic grant does not partially fill or split an oversized insertion");
        var differentExpiry = old with { Core = old.Core with { ExpireTime = (int)now.AddDays(1).ToUnixTimeSeconds() } };
        check(Grant(Role(differentExpiry), new InventoryReward(potion.Id, 1)).Plan!.After.Items.Length == 2,
            "different asset properties remain separate stacks rather than being overwritten");
        check(!InventoryStackRuleService.CanMerge(core, core with { Value = int.MaxValue }, 1, int.MaxValue),
            "unbounded PVF fallback still rejects integer overflow");

        foreach (var cargo in new[] { InventoryListType.PersonalCargo, InventoryListType.AccountCargo })
        {
            var source = new InventoryItem(201, Bag(65), core with { SortLockFlag = 1 });
            var target = new InventoryItem(202, new(cargo, 0), core with { Value = 7, SortLockFlag = 1 });
            current = Role(source, target);
            var request = Request(source, new(cargo, 7), 0, 2);
            var moved = Move(current, request);
            check(moved.Accepted && moved.Plan!.After.Find(target.Position) == target with { Core = target.Core with { Value = 9 } } &&
                moved.Plan.After.Find(source.Position) == source with { Core = source.Core with { Value = 3 } } &&
                moved.Plan.After.Find(request.Destination) is null, "cargo empty target redirects to compatible stack " + cargo);
            var ack = InventoryPackets.Move(request, moved);
            check(ack.Length == 11 && ack[1] == 0 && U16(ack, 2) == 65 && ack[8] == (byte)cargo && U16(ack, 9) == 0,
                "redirect ACK reports actual source and destination " + cargo);
            var reverse = new InventoryMoveRequest(request.Destination, 0, 2, source.Position, source.Core.ItemId, 0, -1, 0, 0);
            var reversed = Move(current, reverse);
            check(reversed.Accepted && reversed.Plan!.After.Items.SequenceEqual(moved.Plan!.After.Items) &&
                InventoryPackets.Move(reverse, reversed).SequenceEqual(ack), "empty-first cross-container request resolves the same transfer " + cargo);

            target = target with { Core = target.Core with { Value = potion.StackLimit } };
            var freeStack = target with { Uid = 203, Position = new(cargo, 1), Core = target.Core with { Value = 8 } };
            moved = Move(Role(source, target, freeStack), Request(source, target.Position, target.Core.ItemId, 2));
            check(moved.Accepted && moved.Plan!.After.Find(target.Position) == target && moved.Plan.After.Find(freeStack.Position)?.Core.Count == 10,
                "cargo skips full existing stack for a later stack that accepts the entire amount " + cargo);
            moved = Move(Role(source, target), request);
            check(moved.Accepted && moved.Plan!.After.Find(target.Position) == target && moved.Plan.After.Find(request.Destination)?.Core.Count == 2,
                "cargo permits a new stack when old stack is full " + cargo);
            moved = Move(Role(source, target), Request(source, target.Position, target.Core.ItemId, 2));
            check(moved.Accepted && moved.Plan!.After.Find(target.Position) == target && moved.Plan.After.Find(new(cargo, 1))?.Core.Count == 2,
                "occupied cargo target falls back to an empty slot without swapping unrelated assets " + cargo);
            var filled = Enumerable.Range(0, 8).Select(index => target with { Uid = 300 + index, Position = new(cargo, (short)index) });
            moved = Move(Role([source, .. filled]), Request(source, target.Position, target.Core.ItemId, 2));
            check(moved is { Accepted: false, Error: 23, Plan: null } && InventoryPackets.Move(request, moved)[0] == 0,
                "full cargo rejects with a visible failure response and no partial move " + cargo);

            source = new(201, new(cargo, 0), core);
            target = new(202, Bag(3), core with { SortLockFlag = 1 });
            moved = Move(Role(source, target), Request(source, Bag(66), 0, 2));
            check(moved.Accepted && moved.Plan!.After.Find(target.Position)?.Core.Count == 7 && moved.Plan.After.Find(Bag(66)) is null,
                "withdrawal finds compatible quickbar stack " + cargo);
        }
        var splitSource = new InventoryItem(401, Bag(65), core);
        check(Move(Role(splitSource), Request(splitSource, Bag(66), 0, 2)).Plan!.After.Items.Length == 2,
            "manual same-container splitting remains available");
        check(Move(Role(splitSource), Request(splitSource, Bag(3), 0, 2)).Plan!.After.Items.Length == 2,
            "manual quickbar splitting remains available");

        Character Role(params InventoryItem[] items) => character with
        { Inventory = new([.. items]) { AccountCargo = new(Capacity: 8) } };
        InventoryResult Grant(Character role, params InventoryReward[] rewards) => InventoryRewardGrantService.TryGrantBatch(role, rewards, catalog, itemResources.GoldLimits, itemResources.EpicPieces, now);
        InventoryResult Move(Character role, InventoryMoveRequest request) => InventoryMoveService.Plan(role, request, catalog, now.ToUnixTimeSeconds());
    }

    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    static InventoryMoveRequest Request(InventoryItem source, InventorySlot destination, int destinationId, int count) =>
        new(source.Position, source.Core.ItemId, count, destination, destinationId, 0, -1, 0, 0);
}
