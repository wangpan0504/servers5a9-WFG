using DfoServer.Game.Inventory.Models;
using System.Collections.Immutable;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;

namespace DfoServer.Game.Inventory.Services;

internal sealed record InventoryReward(int ItemId, int Count, byte Upgrade = 0, byte Refinement = 0, int? CustomAbilityGrade = null, int? Quality = null);
internal static class InventoryRewardGrantService
{
    // 普通创建策略：满耐久、100%品级种子；强化/锻造默认零，显式奖励可指定，期限与部位来自 CHR/PVF。
    public const int InitialQuality = 999_999_998;
    public static ItemCore Create(ItemTemplate template, int count = 1, DateTimeOffset? utcNow = null, byte upgrade = 0, byte refinement = 0,
        bool shopExpiration = false, bool ignoreUsablePeriod = false, Func<int, int>? talismanColorDraw = null,
        byte? echonGrade = null, Func<int, int>? echonDraw = null, int? quality = null)
    {
        if (count <= 0 || count > template.StackLimit || template.Kind is (ItemKind.Equipment or ItemKind.Avatar or ItemKind.Creature or ItemKind.CreatureEquipment or ItemKind.GuildMedal or ItemKind.Talisman) && count != 1)
            throw new InvalidDataException("无效发放数量");
        if (upgrade > 31 || refinement > 127 || template.Kind != ItemKind.Equipment && (upgrade != 0 || refinement != 0))
            throw new InvalidDataException("无效装备创建属性");
        // quality 是实例品级种子（999999893+pct / 999999998），只有普通装备有；其它类型带了就拒绝，不静默丢弃。
        if (quality is not null && template.Kind != ItemKind.Equipment)
            throw new InvalidDataException("无效装备创建属性");
        var core = new ItemCore(template.Id, template.Kind, template.Kind switch
        {
            ItemKind.Avatar or ItemKind.Creature => 0,
            ItemKind.Equipment => quality ?? InitialQuality,
            ItemKind.CreatureEquipment or ItemKind.Talisman => InitialQuality,
            _ => count
        })
        {
            // Client 将强化保存在 Attr 低五位；附魔卡升级次数是另一字段。新实例的封装交易计数为零。
            Attr = upgrade,
            GenuineUpgrade = refinement,
            Durability = template.Kind == ItemKind.Avatar ? (ushort)0 : template.Durability,
            SealFlag = template.Kind != ItemKind.Creature && template.AttachType.Contains("[sealing]", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0,
            ExpireTime = template.Expiration.Resolve(utcNow ?? DateTimeOffset.UtcNow, shopExpiration, ignoreUsablePeriod)
        };
        if (template.Echon is { } echon)
        {
            if (echon.Rules.IsModern)
                core = new EchonEquipmentState(echon.InitialFirst, echon.InitialSecond, 0, 0, 0).Apply(core);
            else
            {
                var rolled = echon.Rules.Roll(echonDraw, echonGrade);
                core = new EchonEquipmentState(rolled.First, rolled.Second, 0, 0, rolled.Grade).Apply(core);
            }
        }
        return template.Kind == ItemKind.Talisman && template.Rarity > 0
            ? TalismanSocketPolicy.Generate(talismanColorDraw).Apply(core) : core;
    }

    public static InventorySnapshot CreateStarter(CharacterProfile profile) => new(
        profile.Equipment.Select(item => CreateItem(item.Template, new(InventoryListType.Equipment, item.Slot), Create(item.Template))).ToImmutableArray());

    public static InventoryResult TryGrantBatch(Character character, IReadOnlyList<InventoryReward> rewards, ItemCatalog catalog, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces,
        DateTimeOffset? utcNow = null, bool shopExpiration = false, PremiumContractCatalog? contracts = null, bool allowOverflowMail = false,
        bool ignoreUsablePeriod = false)
        => Grant(character, rewards, null, catalog, goldLimits, pieces, utcNow, shopExpiration, contracts, allowOverflowMail, ignoreUsablePeriod);

    public static InventoryResult TryGrantItems(Character character, IReadOnlyList<InventoryItem> instances, ItemCatalog catalog,
        GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, DateTimeOffset? utcNow = null, bool allowOverflowMail = false)
        => Grant(character, instances.Select(item => new InventoryReward(item.Core.ItemId, item.Core.Count)).ToArray(),
            instances, catalog, goldLimits, pieces, utcNow, allowOverflowMail: allowOverflowMail);

    static InventoryResult Grant(Character character, IReadOnlyList<InventoryReward> rewards, IReadOnlyList<InventoryItem>? instances,
        ItemCatalog catalog, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, DateTimeOffset? utcNow, bool shopExpiration = false,
        PremiumContractCatalog? contracts = null, bool allowOverflowMail = false, bool ignoreUsablePeriod = false)
    {
        var inventory = character.Inventory;
        var wallet = inventory;
        var currencies = new CurrencyService(goldLimits);
        var overflow = new List<InventoryItem>();
        var placement = new RewardPlacement(inventory, rewards.Select(row => row.ItemId), allowOverflowMail, overflow);
        var now = utcNow ?? DateTimeOffset.UtcNow;
        bool acquiredCargo = false;
        long? acquisitionDeadline = null;
        for (int rewardIndex = 0; rewardIndex < rewards.Count; rewardIndex++)
        {
            var reward = rewards[rewardIndex];
            if (reward.Count <= 0 || reward.Upgrade > 31 || reward.Refinement > 127)
                return InventoryResult.Reject();
            if (pieces.ByPiece.TryGetValue(reward.ItemId, out var piece))
            {
                if (reward.Upgrade != 0 || reward.Refinement != 0)
                    return InventoryResult.Reject();
                wallet.EpicPieces.Validate(pieces);
                long balance = (long)wallet.EpicPieces.Get(piece.Index) + reward.Count;
                if (balance > int.MaxValue)
                    return InventoryResult.Reject(17);
                wallet = wallet with { EpicPieces = wallet.EpicPieces.Set(pieces, piece.Index, (int)balance) };
                continue;
            }
            if (CurrencyWallet.RewardKind(reward.ItemId) is { } currency)
            {
                if (reward.Upgrade != 0 || reward.Refinement != 0)
                    return InventoryResult.Reject();
                // 代币券是足额兑换，不沿普通奖励的饱和加法吞掉超额面额。
                if (CurrencyWallet.TokenKind(reward.ItemId) is not null &&
                    reward.Count > int.MaxValue - CurrencyWallet.Get(wallet, currency))
                    return InventoryResult.Reject(17);
                wallet = currencies.Grant(character with { Inventory = wallet }, currency, reward.Count).Plan!.After;
                continue;
            }
            if (contracts?.Recognizes(reward.ItemId) == true)
            {
                if (!contracts.TryAcquisitionContract(reward.ItemId, out var contract) || reward.Upgrade != 0 || reward.Refinement != 0)
                    return InventoryResult.Reject(19);
                try
                {
                    wallet = wallet with { AccountWallet = wallet.AccountWallet.ActivateContract(contract.Type, contract.DurationSeconds, now, reward.Count) };
                }
                catch (OverflowException) { return InventoryResult.Reject(19); }
                continue;
            }
            var template = catalog.Get(reward.ItemId);
            if (template.HasAcquisitionEffect)
            {
                if (reward.Upgrade != 0 || reward.Refinement != 0) return InventoryResult.Reject(19);
                long expires = 0;
                if (instances is not null)
                {
                    var incoming = instances[rewardIndex];
                    if (incoming.Core.Kind != template.Kind || incoming.IsExpired(now.ToUnixTimeSeconds()) ||
                        character.Inventory.Items.Any(item => item.Uid != 0 && item.Uid == incoming.Uid))
                        return InventoryResult.Reject(19);
                    try { incoming.Core.Validate(); }
                    catch (InvalidDataException) { return InventoryResult.Reject(19); }
                    expires = incoming.ExpireTime;
                }
                else if (template.AcquisitionContract is null)
                {
                    if (!template.Expiration.SupportsCreation) return InventoryResult.Reject(19);
                    var effectCore = Create(template, utcNow: now, shopExpiration: shopExpiration, ignoreUsablePeriod: ignoreUsablePeriod);
                    if (effectCore.IsExpired(now.ToUnixTimeSeconds())) return InventoryResult.Reject(19);
                    expires = effectCore.ExpireTime;
                }
                var acquired = InventoryAcquisitionService.Plan(character with { Inventory = wallet }, template, reward.Count, now);
                if (!acquired.Accepted) return acquired;
                if (template.AcquisitionContract is null)
                {
                    acquiredCargo = true;
                    if (expires > 0 && (acquisitionDeadline is null || expires < acquisitionDeadline)) acquisitionDeadline = expires;
                }
                wallet = acquired.Plan!.After;
                continue;
            }
            if (instances is null && (template.IsPremiumService || !template.Expiration.SupportsCreation))
                return InventoryResult.Reject(19);
            if (template.Kind != ItemKind.Equipment && (reward.Upgrade != 0 || reward.Refinement != 0))
                return InventoryResult.Reject();
            InventoryItem? incomingInstance = instances?[rewardIndex];
            if (incomingInstance is null && template.Echon is not null && reward.CustomAbilityGrade is < 0 or > 3)
                return InventoryResult.Reject(19);
            bool legacyRune = incomingInstance?.Core.Kind == ItemKind.Consumable && template.Kind == ItemKind.Rune;
            if (incomingInstance is not null)
            {
                if (!InventoryKindCompatibility.AcceptsKind(incomingInstance.Core.Kind, template))
                    return InventoryResult.Reject(19);
                incomingInstance = incomingInstance with { Core = InventoryKindCompatibility.Normalize(incomingInstance.Core, template) };
            }
            var prototype = incomingInstance is null ? Create(template, utcNow: now, upgrade: reward.Upgrade, refinement: reward.Refinement,
                shopExpiration: shopExpiration, ignoreUsablePeriod: ignoreUsablePeriod,
                echonGrade: template.Echon is not null && reward.CustomAbilityGrade is { } grade ? (byte)grade : null,
                quality: reward.Quality) : incomingInstance.Core;
            if (prototype.ItemId != template.Id || prototype.Kind != template.Kind || instances?[rewardIndex].IsExpired(now.ToUnixTimeSeconds()) == true)
                return InventoryResult.Reject(19);
            if (prototype.IsExpired(now.ToUnixTimeSeconds()))
                return InventoryResult.Reject(19);
            if (template.Echon is not null && incomingInstance is null)
            {
                // 每件斗气装备独立抽取；不能把同一原型复制成整批相同属性。
                for (int at = 0; at < reward.Count; at++)
                {
                    var core = at == 0 ? prototype : Create(template, utcNow: now, upgrade: reward.Upgrade, refinement: reward.Refinement,
                        shopExpiration: shopExpiration, ignoreUsablePeriod: ignoreUsablePeriod,
                        echonGrade: reward.CustomAbilityGrade is { } fixedGrade ? (byte)fixedGrade : null, quality: reward.Quality);
                    if (!placement.TryAdd(template, core, 1)) return InventoryResult.Reject(17);
                }
            }
            else if (!placement.TryAdd(template, prototype, reward.Count, incomingInstance, splitLegacyRune: legacyRune))
                return InventoryResult.Reject(17);
        }

        var after = wallet with { Items = placement.Items() };
        after.Validate(allocating: true);
        return new(true, 0, new(inventory, after, "reward_grant",
            acquiredCargo ? new(character, acquisitionDeadline) : null, overflowItems: overflow));
    }

    // 地面等业务提供已冻结的完整核心；此入口不接存量UID，不确认来源资格或重复拾取。
    public static InventoryResult TryGrantCore(Character character, ItemCore core, ItemTemplate template,
        EpicPieceCatalog pieces, DateTimeOffset? utcNow = null, bool usePickupSlotPolicy = false)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(pieces);
        bool legacyRune = core.Kind == ItemKind.Consumable && template.Kind == ItemKind.Rune;
        if (core.ItemId != template.Id || !InventoryKindCompatibility.AcceptsKind(core.Kind, template))
            return InventoryResult.Reject();
        core = InventoryKindCompatibility.Normalize(core, template);
        if (
            core.Kind is not (ItemKind.Equipment or ItemKind.Talisman or ItemKind.GuildMedal) && !core.IsStackable ||
            CurrencyWallet.RewardKind(core.ItemId) is not null || pieces.ByPiece.ContainsKey(core.ItemId))
            return InventoryResult.Reject();
        try
        {
            core.Validate();
        }
        catch (InvalidDataException)
        {
            return InventoryResult.Reject();
        }
        if (core.IsExpired((utcNow ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds()))
            return InventoryResult.Reject(19);
        if (!legacyRune && core.Count > template.StackLimit)
            return InventoryResult.Reject(17);
        // 地面拾取回执要求真实目标槽，尚不能提交无实体的即时资格；必须在写库前拒绝。
        if (usePickupSlotPolicy && template.HasAcquisitionEffect)
            return InventoryResult.Reject(19);
        if (template.HasAcquisitionEffect)
            return InventoryAcquisitionService.Plan(character, template, core.Count, utcNow ?? DateTimeOffset.UtcNow,
                core.ExpireTime > 0 ? core.ExpireTime : null);
        if (template.IsPremiumService) return InventoryResult.Reject(19);

        // ItemCore及其附属值均不可变，直接共享完整核心；不重新Create或计算期限。
        var inventory = character.Inventory;
        var placement = new RewardPlacement(inventory, [core.ItemId]);
        if (!placement.TryAdd(template, core, core.Count, usePickupSlotPolicy: usePickupSlotPolicy, splitLegacyRune: legacyRune))
            return InventoryResult.Reject(17);
        var after = inventory with { Items = placement.Items() };
        after.Validate(allocating: true);
        return new(true, 0, new(inventory, after, "reward_grant_core"));
    }

    // 两个grant入口共用一次规划内的放置集合与索引；不是另一份在线背包owner。
    sealed class RewardPlacement
    {
        readonly InventorySnapshot inventory;
        readonly IEnumerable<int> itemIds;
        readonly bool allowOverflowMail;
        readonly List<InventoryItem>? overflowItems;
        readonly Dictionary<InventorySlot, InventoryItem> planned;
        Dictionary<(InventoryListType List, int ItemId), List<short>>? stacks;
        readonly Dictionary<(InventoryListType List, short Start, short End), short> freeSlots = [];

        public RewardPlacement(InventorySnapshot inventory, IEnumerable<int> itemIds, bool allowOverflowMail = false,
            List<InventoryItem>? overflowItems = null)
        {
            this.inventory = inventory;
            this.itemIds = itemIds;
            this.allowOverflowMail = allowOverflowMail;
            this.overflowItems = overflowItems;
            planned = inventory.Items.ToDictionary(item => item.Position);
        }

        public ImmutableArray<InventoryItem> Items() => planned.Values.OrderBy(item => item.Position.List)
            .ThenBy(item => item.Position.Index).ToImmutableArray();

        public bool TryAdd(ItemTemplate template, ItemCore prototype, int remaining, InventoryItem? instance = null,
            bool usePickupSlotPolicy = false, bool splitLegacyRune = false)
        {
            var range = ItemSlotBoundService.Range(template.Kind, inventory);
            var list = ItemSlotBoundService.List(template.Kind);
            bool pickupQuickSlots = usePickupSlotPolicy && ItemSlotBoundService.Allows(inventory,
                new(InventoryListType.Main, ItemSlotBoundService.QuickSlotStart), template.Kind);
            if (prototype.IsStackable)
            {
                // 自动发放只向首个兼容堆叠完整入库，不能靠拆成新格绕过该次入库上限。
                if (!allowOverflowMail && !splitLegacyRune && remaining > template.StackLimit)
                    return false;
                // 索引只缩小候选范围，完整属性兼容仍由同一个堆叠规则决定。
                if (stacks is null)
                {
                    stacks = [];
                    var wanted = itemIds.ToHashSet();
                    foreach (var existing in planned.Values)
                    {
                        if (!wanted.Contains(existing.Core.ItemId) || !existing.Core.IsStackable || existing.Core.EquipmentLockId != 0)
                            continue;
                        var key = (existing.Position.List, existing.Core.ItemId);
                        if (!stacks.TryGetValue(key, out var slots))
                            stacks.Add(key, slots = []);
                        slots.Add(existing.Position.Index);
                    }
                    foreach (var slots in stacks.Values)
                        slots.Sort();
                }
                InventoryItem? item = null;
                int stackLists = pickupQuickSlots && list != InventoryListType.Main ? 2 : 1;
                for (int pass = 0; item is null && pass < stackLists; pass++)
                {
                    var stackList = stackLists == 2 && pass == 0 ? InventoryListType.Main : list;
                    if (stacks.TryGetValue((stackList, prototype.ItemId), out var candidates))
                        foreach (short candidate in candidates)
                        {
                            var current = planned[new(stackList, candidate)];
                            // 装载槽也是list46真实资产，但不属于自动入包可合堆的背包分页。
                            if (current.Position.List == InventoryListType.Talisman && current.Position.Index >= 210)
                                continue;
                            if (InventoryStackRuleService.CanShareStack(prototype, current.Core))
                            {
                                item = current;
                                break;
                            }
                        }
                }
                if (item is not null)
                {
                    if (InventoryStackRuleService.CanMerge(prototype, item.Core, remaining, template.StackLimit))
                    {
                        planned[item.Position] = item with { Core = item.Core with { Value = checked(item.Core.Value + remaining) } };
                        remaining = 0;
                    }
                    else if (allowOverflowMail || splitLegacyRune)
                    {
                        int moved = Math.Min(Math.Max(0, template.StackLimit - item.Core.Count), remaining);
                        planned[item.Position] = item with { Core = item.Core with { Value = checked(item.Core.Value + moved) } };
                        remaining -= moved;
                    }
                    else
                        return false;
                }
            }

            // 先合已有兼容堆；消耗品新堆只进所属分页，不能借空快捷槽绕过背包容量。
            bool allowQuickSlotFallback = pickupQuickSlots && template.Kind != ItemKind.Consumable;
            for (int pass = 0; remaining > 0 && pass < (allowQuickSlotFallback ? 2 : 1); pass++)
            {
                bool quick = allowQuickSlotFallback && pass == 1;
                var targetList = quick ? InventoryListType.Main : list;
                (short Start, short End) targetRange = quick ? (ItemSlotBoundService.QuickSlotStart, ItemSlotBoundService.QuickSlotEnd) : range;
                var page = (targetList, targetRange.Start, targetRange.End);
                short slot = freeSlots.GetValueOrDefault(page, targetRange.Start);
                for (; remaining > 0 && slot <= targetRange.End; slot++)
                {
                    var position = new InventorySlot(targetList, slot);
                    if (planned.ContainsKey(position))
                        continue;
                    int count = Math.Min(remaining, template.StackLimit);
                    var core = prototype.IsStackable ? prototype with { Value = count } : prototype;
                    planned.Add(position, instance is null ? CreateItem(template, position, core) : instance with { Position = position, Core = core });
                    // 同一外部实例拆为多个堆叠时，只能第一份继续使用其持久UID。
                    if (instance is not null)
                        instance = instance with { Uid = 0 };
                    if (prototype.IsStackable)
                    {
                        var key = (targetList, prototype.ItemId);
                        if (!stacks!.TryGetValue(key, out var candidates))
                            stacks.Add(key, candidates = []);
                        int index = candidates.BinarySearch(slot);
                        candidates.Insert(~index, slot);
                    }
                    remaining -= count;
                }
                // 发放只新增或合并，不产生空格；本次操作内的页面游标可单调前进。
                freeSlots[page] = slot;
            }

            if (remaining != 0 && allowOverflowMail)
            {
                while (remaining > 0)
                {
                    int count = Math.Min(remaining, template.StackLimit);
                    var core = prototype.IsStackable ? prototype with { Value = count } : prototype;
                    overflowItems!.Add(instance is null ? CreateItem(template, new(list, range.Start), core) :
                        instance with { Position = new(list, range.Start), Core = core });
                    remaining -= count;
                }
                return true;
            }
            return remaining == 0;
        }
    }

    public static InventoryItem CreateItem(ItemTemplate template, InventorySlot position, ItemCore core)
    {
        if (core.ItemId != template.Id || core.Kind != template.Kind)
            throw new InvalidDataException("创建模板与核心不一致");
        var item = new InventoryItem(0, position, core);
        if (template.Kind == ItemKind.Avatar)
        {
            var sockets = template.AvatarSockets;
            item = item with { Core = core with { ExpireTime = 0 }, Avatar = new()
            {
                ExpireTime = core.ExpireTime,
                Socket0 = AvatarSocket.Empty(sockets.First), Socket1 = AvatarSocket.Empty(sockets.Second),
                Socket2 = AvatarSocket.Empty(sockets.Third), Socket3 = AvatarSocket.Empty(sockets.Fourth), Socket4 = AvatarSocket.Empty(sockets.Fifth)
            } };
        }
        else if (template.Kind == ItemKind.Creature)
            item = item with { Core = core with { ExpireTime = 0 }, Creature = new()
            {
                Name = DfoServer.Game.Creatures.CreatureRules.DefaultName(template.CreatureName ?? template.Name),
                ExpireTime = core.ExpireTime, Mode = template.CreatureInitialMode,
                Mode1Field0 = template.CreatureInitialMode == 1 ? (byte)10 : (byte)0,
                Mode1Field1 = template.CreatureInitialMode == 1 ? (byte)5 : (byte)0
            } };
        return item;
    }
}
