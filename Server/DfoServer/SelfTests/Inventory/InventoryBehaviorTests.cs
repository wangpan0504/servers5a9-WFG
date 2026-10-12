using DfoServer.Infrastructure;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using PvfLib;

namespace DfoServer.Testing.Inventory;

internal static class InventoryBehaviorTests
{
    public static void RunCore()
    {
        var broom = new ItemTemplate(1, ItemKind.Equipment, 12, 15, 45, 1, 25, 1, 0, "[free]", 8, 91, "Broom")
        { WeaponSubType = 4 };
        var enchantress = new Character(1, 0, "Broom", 0, new(0, 0, 0, 0), null)
        { Job = 3, GrowType = 5, Level = 15, Skills = [new(368, 1)] };
        foreach (byte grow in new byte[] { 5, 0x15, 0x25 })
            Testing.TestCheck.That(InventoryMoveService.RequiredSkillSatisfied(enchantress with { GrowType = grow }, broom),
                "enchantress broom qualification retains awakening stage " + grow);
        foreach (var invalid in new[]
        {
            enchantress with { Job = 10 }, enchantress with { GrowType = 4 },
            enchantress with { Skills = [] }, enchantress with { Skills = [new(368, 0)] }
        })
            Testing.TestCheck.That(!InventoryMoveService.RequiredSkillSatisfied(invalid, broom),
                "broom substitution requires actual enchantress skill");
        foreach (var other in new[]
        {
            broom with { WeaponSubType = -1 }, broom with { WeaponSubType = 0 },
            broom with { EquipmentSlot = 14 }, broom with { Kind = ItemKind.Avatar }, broom with { RequiredSkill = 92 }
        })
            Testing.TestCheck.That(!InventoryMoveService.RequiredSkillSatisfied(enchantress, other),
                "broom substitution does not widen another item requirement");
        Testing.TestCheck.That(InventoryMoveService.RequiredSkillSatisfied(enchantress with { GrowType = 4, Skills = [new(91, 1)] }, broom) &&
            InventoryMoveService.RequiredSkillSatisfied(enchantress with { Skills = [] }, broom with { RequiredSkill = 0 }),
            "original learned skill and unrestricted equipment qualifications remain valid");

        const long now = 2_000_000_000;
        var active = AccountWallet.Empty.ActivateContract(22, 60, DateTimeOffset.FromUnixTimeSeconds(now));
        var withContract = enchantress with { Inventory = enchantress.Inventory with { AccountWallet = active } };
        var groups = new Dictionary<int, IReadOnlyList<string>>
        {
            [1] = ["[weapon]"], [2] = ["[coat]"], [3] = ["[hat avatar]"], [4] = ["[hair avatar]"]
        };
        var contracts = new PremiumContractCatalog(new Dictionary<int, PremiumContract>(),
            conquerorEquipmentLevels: new Dictionary<int, int> { [1] = 3, [2] = 7, [3] = 0, [4] = 7 },
            conquerorEquipmentGroups: groups);
        foreach (var (kind, slot, allowance) in new[]
        {
            (ItemKind.Equipment, (byte)12, 3), (ItemKind.Equipment, (byte)14, 7), (ItemKind.Avatar, (byte)1, 7)
        })
        {
            var higher = broom with { Kind = kind, EquipmentSlot = slot, MinimumLevel = checked((byte)(enchantress.Level + allowance)) };
            Testing.TestCheck.That(!InventoryMoveService.WearLevelSatisfied(enchantress, higher, now, contracts) &&
                InventoryMoveService.WearLevelSatisfied(withContract, higher, now, contracts) &&
                !InventoryMoveService.WearLevelSatisfied(withContract, higher with { MinimumLevel = (byte)(higher.MinimumLevel + 1) }, now, contracts),
                "霸王按当前声明组的3或7级增量判定，不能借另一组较大值 " + kind + "/" + slot);
            Testing.TestCheck.That(InventoryMoveService.WearLevelSatisfied(withContract, higher, now + 59, contracts) &&
                !InventoryMoveService.WearLevelSatisfied(withContract, higher, now + 60, contracts) &&
                !InventoryMoveService.WearLevelSatisfied(withContract, higher, now + 61, contracts),
                "契约穿戴资格在准确到期秒失效 " + kind);
            foreach (int type in new[] { 27, 84, 97 })
            {
                var otherContract = enchantress with { Inventory = enchantress.Inventory with
                { AccountWallet = AccountWallet.Empty.ActivateContract(type, 60, DateTimeOffset.FromUnixTimeSeconds(now)) } };
                Testing.TestCheck.That(!InventoryMoveService.WearLevelSatisfied(otherContract, higher, now, contracts),
                    "其他普通契约不放宽穿戴等级 " + type + "/" + kind);
            }
        }
        var highWeapon = broom with { MinimumLevel = 18 };
        var noGroups = new PremiumContractCatalog(new Dictionary<int, PremiumContract>(),
            conquerorEquipmentLevels: new Dictionary<int, int> { [1] = 7 });
        Testing.TestCheck.That(!InventoryMoveService.WearLevelSatisfied(withContract, highWeapon, now) &&
            !InventoryMoveService.WearLevelSatisfied(withContract, highWeapon, now, noGroups) &&
            !InventoryMoveService.WearLevelSatisfied(withContract, broom with { Kind = ItemKind.Avatar, EquipmentSlot = 0, MinimumLevel = 16 }, now, contracts),
            "未装配目录、没有显式名单或声明增量零时不放宽等级");
        foreach (byte slot in new byte[] { 9, 10, 11 })
            Testing.TestCheck.That(!InventoryMoveService.WearLevelSatisfied(withContract,
                broom with { Kind = ItemKind.Avatar, EquipmentSlot = slot, MinimumLevel = 16 }, now, contracts),
                "未在名单声明的光环或武器装扮不借普通Avatar类别获得加成 " + slot);
        foreach (var kind in new[] { ItemKind.Creature, ItemKind.CreatureEquipment, ItemKind.GuildMedal, ItemKind.GuardianGem })
            Testing.TestCheck.That(!InventoryMoveService.WearLevelSatisfied(withContract, broom with { Kind = kind, MinimumLevel = 16 }, now, contracts),
                "霸王等级加成不扩展宠物、勋章或守护珠规则 " + kind);
        var largeAllowance = new PremiumContractCatalog(new Dictionary<int, PremiumContract>(),
            conquerorEquipmentLevels: new Dictionary<int, int> { [1] = int.MaxValue },
            conquerorEquipmentGroups: new Dictionary<int, IReadOnlyList<string>> { [1] = ["[weapon]"] });
        Testing.TestCheck.That(InventoryMoveService.WearLevelSatisfied(withContract, broom with { MinimumLevel = 255 }, now, largeAllowance) &&
            InventoryMoveService.WearLevelSatisfied(enchantress with { Level = 255 }, broom with { MinimumLevel = 255 }, now),
            "极大PVF增量用宽整数计算，byte原等级上界仍保留原本合格资格");
    }

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
        var capturedTypeDiscard = new InventoryDeleteRequest(InventoryListType.Equipment,
            [new(1, equipped.Position.Index, equipped.Core.Value, 1)], 0);
        var discardedCapturedType = service.Delete(character, capturedTypeDiscard);
        check(discardedCapturedType.Accepted && discardedCapturedType.Plan!.After.Find(equipped.Position) is null,
            "captured discard frame type one is accepted for an ordinary item");
        var clientIdItem = character.Inventory.Items.FirstOrDefault(item => item.Position.List == InventoryListType.Main)
            ?? new InventoryItem(501, Bag(91), new ItemCore(490704576, ItemKind.Consumable, 1));
        var clientIdCharacter = character with { Inventory = new([clientIdItem]) };
        var clientIdDiscard = new InventoryDeleteRequest(InventoryListType.Main,
            [new(1, clientIdItem.Position.Index, clientIdItem.Core.ItemId, 1)], 0);
        var discardedClientId = service.Delete(clientIdCharacter, clientIdDiscard);
        check(discardedClientId.Accepted && discardedClientId.Plan!.After.Find(clientIdItem.Position) is null,
            "captured discard accepts client item-id value for every role and container");
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

        var broom = catalog.Get(106040040);
        check(broom is { Kind: ItemKind.Equipment, EquipmentSlot: 12, WeaponSubType: 4, RequiredSkill: 91 },
            "matched broom retains weapon subtype and original skill declaration");
        var broomItem = new InventoryItem(302, Bag(9), InventoryRewardGrantService.Create(broom));
        var enchantress = character with { Job = 3, GrowType = 0x25, Level = 15, Skills = [new(368, 1)], Inventory = new([broomItem]) };
        var wornBroom = service.Move(enchantress, Request(broomItem, new(InventoryListType.Equipment, 12)));
        check(wornBroom.Accepted && wornBroom.Plan!.After.Find(new(InventoryListType.Equipment, 12)) ==
            broomItem with { Position = new(InventoryListType.Equipment, 12) }, "enchantress wears matched broom with full core and UID intact");
        check(!service.Move(enchantress with { Level = (byte)(broom.MinimumLevel - 1) }, Request(broomItem, new(InventoryListType.Equipment, 12))).Accepted &&
            !service.Move(enchantress, Request(broomItem, new(InventoryListType.Equipment, 14))).Accepted,
            "broom alternate skill does not bypass wear level or equipment slot");

        long wearNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long contractExpiry = wearNow + 60;
        // 实际资源参数允许零或很大数值；到期事务代表另用显式合成正增量目录。
        var levelTemplate = catalog.Get(100050791);
        check(levelTemplate is { Kind: ItemKind.Equipment, EquipmentSlot: 14, MinimumLevel: > 2 },
            "霸王完整规划复用实际资源的普通上衣模板");
        var actualContracts = itemResources.Contracts;
        int actualAllowance = actualContracts.EquipmentLevelAllowance(levelTemplate);
        int allowance = Math.Min(3, levelTemplate.MinimumLevel - 2);
        var contracts = new PremiumContractCatalog(actualContracts.ByItem, actualContracts.ExpertSkillLevels,
            new Dictionary<int, int> { [1] = allowance },
            new Dictionary<int, IReadOnlyList<string>> { [1] = new[] { "[coat]" } });
        var levelGear = new InventoryItem(304, Bag(9), InventoryRewardGrantService.Create(levelTemplate));
        byte levelJob = (byte)Enumerable.Range(0, 16).First(job => (levelTemplate.Jobs & (1 << job)) != 0);
        var conqueror = character with
        {
            Job = levelJob, Level = checked((byte)(levelTemplate.MinimumLevel - allowance)),
            Skills = levelTemplate.RequiredSkill == 0 ? [] : [new(levelTemplate.RequiredSkill, 1)],
            Inventory = new([levelGear])
            { AccountWallet = AccountWallet.Empty.ActivateContract(22, 60, DateTimeOffset.FromUnixTimeSeconds(wearNow)) }
        };
        var destination = new InventorySlot(InventoryListType.Equipment, 14);
        var actualRole = conqueror with { Level = 1 };
        check(InventoryMoveService.Plan(actualRole, Request(levelGear, destination), catalog, wearNow, actualContracts).Accepted ==
            ((long)actualRole.Level + actualAllowance >= levelTemplate.MinimumLevel),
            "当前PVF的零、小值或大等级增量均按完整long门槛生效，不收窄合法配置");
        var contractWear = InventoryMoveService.Plan(conqueror, Request(levelGear, destination), catalog, wearNow, contracts);
        var wornCore = levelGear.Core.SealFlag != 0 && levelTemplate.AttachType.Contains("[sealing]", StringComparison.OrdinalIgnoreCase)
            ? levelGear.Core with { SealFlag = 0 } : levelGear.Core;
        check(contractWear.Accepted && contractWear.Plan!.After.Find(destination)?.Uid == levelGear.Uid &&
            contractWear.Plan.After.Find(destination)?.Core == wornCore &&
            contractWear.Plan.CharacterPrecondition!.Matches(conqueror, contractExpiry - 1) &&
            !contractWear.Plan.CharacterPrecondition.Matches(conqueror, contractExpiry),
            "越级穿戴保留原UID和合法解除封装后的完整Core，规划记录的霸王期限在提交前到期即失效");
        check(!InventoryMoveService.Plan(conqueror with { Level = (byte)(conqueror.Level - 1) }, Request(levelGear, destination), catalog, wearNow, contracts).Accepted,
            "有效霸王契约仍拒绝超过本组资源增量一等级的实际装备");
        var activeBroomRole = enchantress with { Inventory = enchantress.Inventory with { AccountWallet = conqueror.Inventory.AccountWallet } };
        foreach (var invalid in new[]
        {
            activeBroomRole with { Job = 0 }, activeBroomRole with { Skills = [] }, activeBroomRole with { GrowType = 4 }
        })
            check(!InventoryMoveService.Plan(invalid, Request(broomItem, new(InventoryListType.Equipment, 12)), catalog, wearNow, contracts).Accepted,
                "有效霸王契约仍保留匹配扫把的职业、实际技能及转职资格限制");
        check(!InventoryMoveService.Plan(conqueror, Request(levelGear, new(InventoryListType.Equipment, 12)), catalog, wearNow, contracts).Accepted &&
            !InventoryMoveService.Plan(conqueror, Request(levelGear, destination), catalog, contractExpiry, contracts).Accepted,
            "霸王不能改变真实部位或延长已到期穿戴资格");
        var withoutBonus = conqueror with { Level = levelTemplate.MinimumLevel };
        var ordinaryWear = InventoryMoveService.Plan(withoutBonus, Request(levelGear, destination), catalog, wearNow, contracts);
        check(ordinaryWear.Accepted && ordinaryWear.Plan!.CharacterPrecondition!.Matches(withoutBonus, contractExpiry),
            "原等级已合格的普通穿戴不额外依赖霸王期限");
        var wornWithExpiredContract = conqueror with { Inventory = contractWear.Plan!.After };
        var wornItem = wornWithExpiredContract.Inventory.Find(destination)!;
        var unequipExpired = InventoryMoveService.Plan(wornWithExpiredContract, Request(wornItem, Bag(9)), catalog, contractExpiry, contracts);
        check(unequipExpired.Accepted && unequipExpired.Plan!.After.Find(Bag(9))?.Core == wornCore &&
            unequipExpired.Plan.CharacterPrecondition!.Matches(wornWithExpiredContract, contractExpiry + 1),
            "契约到期后允许原越级装备正常卸下，不制造强制删除或永久穿戴锁");
        var swapSource = levelGear with { Uid = 305 };
        var swapRole = wornWithExpiredContract with { Inventory = wornWithExpiredContract.Inventory with
        { Items = wornWithExpiredContract.Inventory.Items.Add(swapSource) } };
        var reverseSwap = InventoryMoveService.Plan(swapRole, Request(wornItem, swapSource.Position, swapSource.Core.ItemId), catalog, wearNow, contracts);
        check(reverseSwap.Accepted && reverseSwap.Plan!.After.Find(destination)?.Uid == swapSource.Uid &&
            !reverseSwap.Plan.CharacterPrecondition!.Matches(swapRole, contractExpiry),
            "从穿戴栏拖向背包的交换也冻结反向越级穿戴期限");
        var expiredRental = levelGear with { Core = levelGear.Core with { IsRental = true, ExpireTime = checked((int)wearNow) } };
        check(!InventoryMoveService.Plan(conqueror with { Inventory = conqueror.Inventory with { Items = [expiredRental] } },
            Request(expiredRental, destination), catalog, wearNow, contracts).Accepted,
            "有效霸王契约不允许穿戴已经到期的租赁实例");
        VerifyExpiredWearCommit();

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

        void VerifyExpiredWearCommit()
        {
            string root = Path.Combine(Path.GetTempPath(), "S5A9-conqueror-wear-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var repository = new CharacterRepository(Path.Combine(root, "game.db"));
                using var owner = new AccountOwners().Acquire("conqueror-wear", () => { });
                var saved = repository.Create(owner, new("ExpiredWear", conqueror.Position, conqueror.Return)
                {
                    Job = conqueror.Job, GrowType = conqueror.GrowType, Level = conqueror.Level, Skills = conqueror.Skills,
                    Inventory = new([levelGear with { Uid = 0 }])
                });
                long actualNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long expiredAt = actualNow - 100;
                var wallet = saved.Inventory.AccountWallet.ActivateContract(22, 100, DateTimeOffset.FromUnixTimeSeconds(actualNow - 200));
                saved = repository.CommitInventory(owner, saved, new(saved.Inventory,
                    saved.Inventory with { AccountWallet = wallet }, "conqueror_expiry_fixture"));
                string before = JsonSerializer.Serialize(repository.List(owner).Single());
                var source = saved.Inventory.Find(Bag(9))!;
                var candidate = InventoryMoveService.Plan(saved, Request(source, destination), catalog, expiredAt - 1, contracts);
                check(candidate.Accepted, "期限前的冻结规划可生成完整越级穿戴候选，不靠等待构造时间边界");
                bool rejected = false;
                try { repository.CommitInventory(owner, saved, candidate.Plan!); }
                catch (InvalidOperationException) { rejected = true; }
                check(rejected && before == JsonSerializer.Serialize(new CharacterRepository(Path.Combine(root, "game.db")).List(owner).Single()),
                    "真实库存提交复验当前时间并拒绝已到期候选，重开后角色、源、账号期限和版本均不变");
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static InventoryMoveRequest Request(InventoryItem item, InventorySlot destination, int targetId = 0, int count = 0) =>
        new(item.Position, item.Core.ItemId, count, destination, targetId, 0, -1, 0, 0);
}
