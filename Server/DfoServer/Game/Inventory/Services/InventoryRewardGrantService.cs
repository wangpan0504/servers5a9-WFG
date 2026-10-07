using DfoServer.Game.Inventory.Models;
using System.Collections.Immutable;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;

namespace DfoServer.Game.Inventory.Services;

internal sealed record InventoryReward(int ItemId, int Count, byte Upgrade = 0, byte Refinement = 0);
internal static class InventoryRewardGrantService
{
    // 普通创建策略：满耐久、100%品级种子；强化/锻造默认零，显式奖励可指定，期限与部位来自 CHR/PVF。
    public const int InitialQuality = 999_999_998;
    public static ItemCore Create(ItemTemplate template, int count = 1, DateTimeOffset? utcNow = null, byte upgrade = 0, byte refinement = 0)
    {
        if (count <= 0 || count > template.StackLimit || template.Kind is (ItemKind.Equipment or ItemKind.Avatar or ItemKind.Creature or ItemKind.CreatureEquipment or ItemKind.GuildMedal) && count != 1)
            throw new InvalidDataException("无效发放数量");
        if (upgrade > 31 || refinement > 127 || template.Kind != ItemKind.Equipment && (upgrade != 0 || refinement != 0))
            throw new InvalidDataException("无效装备创建属性");
        return new(template.Id, template.Kind, template.Kind switch
        {
            ItemKind.Avatar or ItemKind.Creature => 0,
            ItemKind.Equipment or ItemKind.CreatureEquipment => InitialQuality,
            _ => count
        })
        {
            // Client 将强化保存在 Attr 低五位；附魔卡升级次数是另一字段。新实例的封装交易计数为零。
            Attr = upgrade,
            GenuineUpgrade = refinement,
            Durability = template.Kind == ItemKind.Avatar ? (ushort)0 : template.Durability,
            SealFlag = template.Kind != ItemKind.Creature && template.AttachType.Contains("[sealing]", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0,
            ExpireTime = template.Expiration.Resolve(utcNow ?? DateTimeOffset.UtcNow)
        };
    }

    public static InventorySnapshot CreateStarter(CharacterProfile profile) => new(
        profile.Equipment.Select(item => CreateItem(item.Template, new(InventoryListType.Equipment, item.Slot), Create(item.Template))).ToImmutableArray());

    public static InventoryResult TryGrantBatch(Character character, IReadOnlyList<InventoryReward> rewards, ItemCatalog catalog, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces, DateTimeOffset? utcNow = null)
    {
        var inventory = character.Inventory;
        var wallet = inventory;
        var currencies = new CurrencyService(goldLimits);
        var planned = inventory.Items.ToDictionary(item => item.Position);
        Dictionary<(InventoryListType List, int ItemId), List<short>>? stacks = null;
        var freeSlots = new Dictionary<(InventoryListType List, short Start, short End), short>();
        var now = utcNow ?? DateTimeOffset.UtcNow;
        foreach (var reward in rewards)
        {
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
                wallet = currencies.Grant(character with { Inventory = wallet }, currency, reward.Count).Plan!.After;
                continue;
            }
            var template = catalog.Get(reward.ItemId);
            if (template.Kind != ItemKind.Equipment && (reward.Upgrade != 0 || reward.Refinement != 0))
                return InventoryResult.Reject();
            int remaining = reward.Count;
            var prototype = Create(template, utcNow: now, upgrade: reward.Upgrade, refinement: reward.Refinement);
            if (prototype.IsExpired(now.ToUnixTimeSeconds()))
                return InventoryResult.Reject(19);
            var range = ItemSlotBoundService.Range(template.Kind, inventory.Expansion);
            var list = ItemSlotBoundService.List(template.Kind);
            if (prototype.IsStackable)
            {
                // 自动发放只向首个兼容堆叠完整入库，不能靠拆成新格绕过该次入库上限。
                if (remaining > template.StackLimit)
                    return InventoryResult.Reject(17);
                // 索引只缩小候选范围，完整属性兼容仍由同一个堆叠规则决定。
                if (stacks is null)
                {
                    stacks = [];
                    var wanted = rewards.Select(row => row.ItemId).ToHashSet();
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
                if (stacks.TryGetValue((list, prototype.ItemId), out var candidates))
                    foreach (short candidate in candidates)
                    {
                        var current = planned[new(list, candidate)];
                        if (InventoryStackRuleService.CanShareStack(prototype, current.Core))
                        {
                            item = current;
                            break;
                        }
                    }
                if (item is not null)
                {
                    if (!InventoryStackRuleService.CanMerge(prototype, item.Core, remaining, template.StackLimit))
                        return InventoryResult.Reject(17);
                    planned[item.Position] = item with { Core = item.Core with { Value = checked(item.Core.Value + remaining) } };
                    remaining = 0;
                }
            }

            var page = (list, range.Start, range.End);
            short slot = freeSlots.GetValueOrDefault(page, range.Start);
            for (; remaining > 0 && slot <= range.End; slot++)
            {
                var position = new InventorySlot(list, slot);
                if (planned.ContainsKey(position))
                    continue;
                int count = Math.Min(remaining, template.StackLimit);
                planned.Add(position, CreateItem(template, position, prototype.IsStackable ? prototype with { Value = count } : prototype));
                if (prototype.IsStackable)
                {
                    var key = (list, prototype.ItemId);
                    if (!stacks!.TryGetValue(key, out var candidates))
                        stacks.Add(key, candidates = []);
                    int index = candidates.BinarySearch(slot);
                    candidates.Insert(~index, slot);
                }
                remaining -= count;
            }
            // 发放只新增或合并，不产生空格；本次操作内的页面游标可单调前进。
            freeSlots[page] = slot;

            if (remaining != 0)
                return InventoryResult.Reject(17);
        }

        var after = wallet with { Items = planned.Values.OrderBy(item => item.Position.List).ThenBy(item => item.Position.Index).ToImmutableArray() };
        after.Validate(allocating: true);
        return new(true, 0, new(inventory, after, "reward_grant"));
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
            item = item with { Core = core with { ExpireTime = 0 }, Creature = new() { ExpireTime = core.ExpireTime } };
        return item;
    }
}
