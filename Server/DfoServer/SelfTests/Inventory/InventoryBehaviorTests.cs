using DfoServer.Infrastructure;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using PvfLib;

namespace DfoServer.Testing.Inventory;

internal static class InventoryBehaviorTests
{
    public static void Run(Character character, PvfArchive pvf, CharacterCatalog characters, ItemResources itemResources, Action<bool, string> check)
    {
        var catalog = itemResources.Items;
        var service = new InventoryService(catalog, itemResources.GoldLimits, itemResources.EpicPieces);
        var stacks = PvfScript.List(pvf.ReadText("stackable/stackable.lst"));
        var ordinary = stacks.Keys.Where(id => id > 2).Select(TryGet).Where(item => item is { Kind: ItemKind.Consumable, StackLimit: > 30, Expiration: { AbsoluteTime: 0, Days: 0 } }).Take(2).ToArray();
        var template = ordinary[0]!;
        var stack = InventoryRewardGrantService.Create(template, 5);
        var gear = character.Inventory.Items.First(item => item.Position.Index == 12).Core;
        var first = new InventoryItem(101, Bag(65), stack);
        var target = new InventoryItem(102, Bag(66), stack with { Value = template.StackLimit - 2 });
        var mergeCharacter = character with { Inventory = new([first, target]) };
        var mergeRequest = Request(first, target.Position, target.Core.ItemId, 5);
        check(!service.Move(mergeCharacter, mergeRequest).Accepted, "merge capacity failure rejects entire requested count");
        var merge = service.Move(mergeCharacter, mergeRequest with { Count = 2 });
        check(merge.Accepted && merge.MoveCount == 2 && merge.Plan!.After.Find(first.Position)?.Core.Count == 3 &&
            merge.Plan.After.Find(target.Position)?.Core.Count == template.StackLimit, "explicit partial merge reaches exact PVF stack limit");
        target = target with { Core = stack with { Value = 2, EnchantUpgrade = 1 } };
        mergeCharacter = character with { Inventory = new([first, target]) };
        check(!service.Move(mergeCharacter, mergeRequest).Accepted, "incompatible same-template stacks never silently swap or lose attributes");
        target = target with { Core = stack with { Value = 2, SortLockFlag = 1 } };
        mergeCharacter = character with { Inventory = new([first, target]) };
        merge = service.Move(mergeCharacter, mergeRequest);
        check(merge.Accepted && merge.Plan!.After.Find(target.Position) == target with { Core = target.Core with { Value = 7 } },
            "sort lock allows merge and preserves target UID and lock");
        target = target with { Core = InventoryRewardGrantService.Create(ordinary[1]!, 2) };
        var exchange = service.Move(character with { Inventory = new([first, target]) }, Request(first, target.Position, target.Core.ItemId, 0));
        check(exchange.Accepted && exchange.MoveCount == 1 && exchange.Plan!.After.Find(first.Position) == target with { Position = first.Position } &&
            exchange.Plan.After.Find(target.Position) == first with { Position = target.Position }, "different stacks swap whole cores with exchange ACK value one");

        var filled = Enumerable.Range(65, 32).Select(slot => new InventoryItem(slot, Bag((short)slot),
            stack with { Value = slot == 65 ? 5 : template.StackLimit, SortLockFlag = 1 })).ToArray();
        var grant = service.Grant(character with { Inventory = new([.. filled]) }, [new(template.Id, 1)]);
        check(grant.Accepted && grant.Plan!.After.Items.Length == 32 && grant.Plan.After.Find(Bag(65)) ==
            filled[0] with { Core = filled[0].Core with { Value = 6 } }, "full page reward merges into sorted-locked stack without allocating or moving it");

        var lockedGear = new InventoryItem(201, Bag(20), gear with { SortLockFlag = 1, EquipmentLockId = 7 });
        var lockedCharacter = character with { Inventory = new([lockedGear]) };
        var quick = service.Move(lockedCharacter, Request(lockedGear, Bag(3)));
        var inQuick = quick.Plan!.After.Find(Bag(3))!;
        check(quick.Accepted && inQuick == lockedGear with { Position = Bag(3), Core = lockedGear.Core with { SortLockFlag = 0 } },
            "page to quick slot clears only sorting lock and keeps equipment lock and UID");
        var returned = service.Move(character with { Inventory = quick.Plan.After }, Request(inQuick, Bag(20)));
        check(returned.Accepted && returned.Plan!.After.Find(Bag(20))?.Core == inQuick.Core, "quick slot return does not restore removed sorting lock");
        var inPage = service.Move(lockedCharacter, Request(lockedGear, Bag(9)));
        check(inPage.Accepted && inPage.Plan!.After.Find(Bag(9))?.Core == lockedGear.Core, "manual page movement keeps both locks");
        var worn = service.Move(lockedCharacter, Request(lockedGear, new(InventoryListType.Equipment, 12)));
        check(worn.Accepted && worn.Plan!.After.Find(new(InventoryListType.Equipment, 12))?.Core == lockedGear.Core, "wearing retains sorting and equipment locks");
        var unpinned = lockedGear with { Core = lockedGear.Core with { SortLockFlag = 0 } };
        var sorted = service.Sort(character with { Inventory = new([unpinned]) }, ItemKind.Equipment, 0);
        check(sorted.Accepted && sorted.Plan!.After.Find(Bag(9)) == unpinned with { Position = Bag(9) }, "equipment safety lock does not block sorting");
        var fallback = service.Sort(character with { Inventory = new([unpinned]) }, ItemKind.Equipment, 255);
        check(fallback.Accepted && fallback.Plan!.After.Items.SequenceEqual(sorted.Plan!.After.Items), "unknown sorting condition falls back to default");
        var expired = lockedGear with { Core = lockedGear.Core with { ExpireTime = 1 } };
        var expiredQuick = expired with { Uid = 202, Position = Bag(3) };
        var cleaned = service.Sort(character with { Inventory = new([expired, expiredQuick]) }, ItemKind.Equipment, 0);
        check(cleaned.Accepted && cleaned.Plan!.After.Items is [var remaining] && remaining == expiredQuick,
            "page sorting expires both locked items first while leaving quick slots outside the range");

        foreach (int requested in new[] { -1, 0, 6, int.MaxValue })
        {
            var normalized = service.Move(character with { Inventory = new([first]) }, Request(first, Bag(66), count: requested));
            check(normalized.Accepted && normalized.MoveCount == 5 && normalized.Plan!.After.Find(Bag(66)) == first with { Position = Bag(66) },
                "whole-stack move count convention " + requested);
            var discarded = InventoryDeleteService.Plan(new([first]), first.Position, stack.Value, requested);
            check(discarded.Accepted && discarded.Plan!.After.Items.IsEmpty, "whole-stack discard count convention " + requested);
        }
        var zeroQuality = lockedGear with { Core = gear with { Value = 0 } };
        new InventorySnapshot([zeroQuality]).Validate();
        check(InventoryDeleteService.Plan(new([zeroQuality]), zeroQuality.Position, 0, 0).Accepted, "quality zero equipment is a real deletable instance");
        var equipped = character.Inventory.Items.First();
        var deleteWorn = new InventoryDeleteRequest(InventoryListType.Equipment, [new(0, equipped.Position.Index, equipped.Core.Value, 0)], 0);
        var discardedWorn = service.Delete(character, deleteWorn);
        check(discardedWorn.Accepted && discardedWorn.Plan!.After.Find(equipped.Position) is null,
            "ordinary discard accepts worn equipment container");
        var secondWorn = character.Inventory.Items[1];
        check(!service.Delete(character, deleteWorn with { Items = [deleteWorn.Items[0], new(0, secondWorn.Position.Index, secondWorn.Core.Value + 1, 1)] }).Accepted &&
            character.Inventory.Find(equipped.Position) == equipped, "failed worn discard batch cannot remove its first item");

        var female = characters.Get(11);
        var femaleWeapon = new InventoryItem(301, Bag(9), InventoryRewardGrantService.Create(female.Equipment.Single(item => item.Slot == 12).Template));
        var femaleCharacter = character with { Job = 11, GrowType = 0x24, Skills = female.Skills, Inventory = new([femaleWeapon]) };
        var offhand = service.Move(femaleCharacter, Request(femaleWeapon, new(InventoryListType.Equipment, 24)));
        check(offhand.Accepted && offhand.Plan!.After.Find(new(InventoryListType.Equipment, 24))?.Uid == femaleWeapon.Uid,
            "awakened vagabond can equip ordinary weapon in support slot");
        check(!service.Move(femaleCharacter with { GrowType = 3 }, Request(femaleWeapon, new(InventoryListType.Equipment, 24))).Accepted,
            "support slot retains actual job qualification");
        var unequip = service.Move(femaleCharacter with { Inventory = offhand.Plan!.After },
            new(Bag(8), 0, 0, new(InventoryListType.Equipment, 24), femaleWeapon.Core.ItemId, 0, -1, 0, 0));
        check(unequip.Accepted && unequip.Plan!.After.Find(Bag(8))?.Core == femaleWeapon.Core, "support weapon can return directly to quick slot");

        var sealedTemplate = PvfScript.List(pvf.ReadText("equipment/equipment.lst")).Keys.Select(TryGet)
            .First(item => item is { EquipmentSlot: 12 } && item.AttachType.Contains("[sealing]", StringComparison.OrdinalIgnoreCase))!;
        var sealedItem = new InventoryItem(401, Bag(9), InventoryRewardGrantService.Create(sealedTemplate) with { TradeRestriction = 2 });
        byte job = (byte)Enumerable.Range(0, 16).First(job => (sealedTemplate.Jobs & (1 << job)) != 0);
        var eligible = character with { Job = job, Level = 255, Skills = [new(sealedTemplate.RequiredSkill, 1)], Inventory = new([sealedItem]) };
        var unsealed = service.Move(eligible, Request(sealedItem, new(InventoryListType.Equipment, 12)));
        check(unsealed.Accepted && unsealed.Plan!.After.Find(new(InventoryListType.Equipment, 12))?.Core == sealedItem.Core with { SealFlag = 0 },
            "wear unseals equipment without inventing an extra trade restriction");

        var unlimited = stacks.Where(pair => DfoServer.Game.Currency.CurrencyWallet.RewardKind(pair.Key) is null && pvf.Contains("stackable/" + pair.Value))
            .Where(pair => PvfScript.Integers(PvfScript.Section(pvf.ReadText("stackable/" + pair.Value), "stack limit")).FirstOrDefault() <= 0)
            .Select(pair => TryGet(pair.Key)).First(item => item is { Kind: ItemKind.Consumable, Expiration: { AbsoluteTime: 0, Days: 0 } })!;
        check(unlimited.StackLimit == int.MaxValue && service.Grant(character with { Inventory = InventorySnapshot.Empty }, [new(unlimited.Id, 1001)]).Plan!.After.Items.Length == 1,
            "missing PVF stack limit has no invented 1000-item cap");
        check(TryGet(25)?.Kind == ItemKind.CreatureConsumable && TryGet(90000)?.Kind == ItemKind.GuardianGem,
            "pet consumables and guardian gems retain their dedicated categories instead of ordinary consumables");

        ItemTemplate? TryGet(int id)
        {
            try { return catalog.Get(id); }
            catch (InvalidDataException) { return null; }
        }
    }

    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static InventoryMoveRequest Request(InventoryItem item, InventorySlot destination, int targetId = 0, int count = 0) =>
        new(item.Position, item.Core.ItemId, count, destination, targetId, 0, -1, 0, 0);
}
