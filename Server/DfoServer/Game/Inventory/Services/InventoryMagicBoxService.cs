using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryMagicBoxService(ItemCatalog catalog, MagicBoxCatalog boxes, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces,
    Random? random = null, PremiumContractCatalog? contracts = null, GameDebugSettings? debugSettings = null, TimeProvider? clock = null,
    DfoServer.Game.Events.ServerEventService? events = null)
{
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;
    public BoosterUsePlacesDefinition UsePlaces(int itemId) => boxes.UsePlaces(itemId);

    public InventoryResult Open(Character character, MagicBoxOpenRequest request, out MagicBoxReceipt? receipt, DateTimeOffset? utcNow = null)
    {
        receipt = null;
        bool ignoreAbsoluteExpiration = debugSettings?.IgnoreCashShopAbsoluteExpiration == true;
        bool ignoreUsablePeriod = debugSettings?.IgnoreCashShopUsablePeriod == true;
        if (request.BoxSlot.List != InventoryListType.Main || request.MaterialSlot.List != InventoryListType.Main ||
            request.Count <= 0 || request.Count > ushort.MaxValue)
            return InventoryResult.Reject(19);
        var before = character.Inventory;
        DateTimeOffset now = utcNow ?? Clock.GetUtcNow();
        var box = before.Find(request.BoxSlot);
        if (box is null || !box.Core.IsStackable || box.Core.EquipmentLockId != 0 || box.Core.IsExpired(now.ToUnixTimeSeconds()) ||
            request.BoxItemId != 0 && request.BoxItemId != box.Core.ItemId)
            return InventoryResult.Reject(19);
        var definition = boxes.Get(box.Core.ItemId);
        if (!definition.Available)
            return InventoryResult.Reject(19);
        // 全部正权重候选须具有完整交付能力；未知premium在抽取前拒绝，不能抽后重选或只写期限。
        try
        {
            if (!InventoryAvatarPackageService.SourceEligible(character, box, catalog.Get(box.Core.ItemId)))
                return InventoryResult.Reject(19);
            foreach (int id in definition.Pools.SelectMany(pool => pool.Rewards).Where(row => row.Weight > 0).Select(row => row.ItemId).Distinct())
            {
                if (Currency.CurrencyWallet.RewardKind(id) is not null || pieces.ByPiece.ContainsKey(id)) continue;
                if (contracts?.TryAcquisitionContract(id, out _) == true || catalog.Get(id).HasAcquisitionEffect) continue;
                if (contracts?.Recognizes(id) == true)
                    return InventoryResult.Reject(19);
                if (catalog.Get(id).IsPremiumService) return InventoryResult.Reject(19);
            }
        }
        catch (InvalidDataException) { return InventoryResult.Reject(19); }

        int opens = Math.Min(request.Count, box.Core.Count);
        int required = 0;
        InventoryItem? material = null;
        if (definition.MaterialItemId > 0)
        {
            required = checked(definition.MaterialCount * opens);
            material = request.MaterialSlot.Index >= 0 ? before.Find(request.MaterialSlot) :
                before.Items.FirstOrDefault(item => item.Position.List == InventoryListType.Main && item.Core.ItemId == definition.MaterialItemId);
            if (material is null || material.Position == box.Position || !material.Core.IsStackable || material.Core.EquipmentLockId != 0 ||
                material.Core.IsExpired(now.ToUnixTimeSeconds()) || material.Core.ItemId != definition.MaterialItemId ||
                request.MaterialItemId != 0 && request.MaterialItemId != material.Core.ItemId)
                return InventoryResult.Reject(19);
            opens = Math.Min(opens, material.Core.Count / definition.MaterialCount);
            if (opens <= 0)
                return InventoryResult.Reject(19);
            required = checked(definition.MaterialCount * opens);
        }

        var totals = new Dictionary<int, int>();
        var doubleTotals = new Dictionary<int, int>();
        var broadcastItems = new List<int>();
        bool seriaSource = box.Core.ItemId == 2682272;
        var luckEvent = seriaSource ? events?.Read(DfoServer.Game.Events.ServerEventService.SeriaLuckId) : null;
        bool luckEnabled = seriaSource && (luckEvent?.Active ?? true);
        int luckBefore = before.AccountWallet.SeriaLuck;
        int luck = luckBefore;
        try
        {
            for (int open = 0; open < opens; open++)
            {
                var drawn = new Dictionary<int, int>();
                foreach (var pool in definition.Pools)
                {
                    long weight = pool.Rewards.Sum(row => row.Weight);
                    if (weight <= 0)
                        return InventoryResult.Reject(19);
                    long value = (random ?? Random.Shared).NextInt64(weight);
                    foreach (var row in pool.Rewards)
                    {
                        if (value < row.Weight)
                        {
                            drawn[row.ItemId] = checked(drawn.GetValueOrDefault(row.ItemId) + row.Count);
                            if (row.Broadcast)
                                broadcastItems.Add(row.ItemId);
                            break;
                        }
                        value -= row.Weight;
                    }
                }
                foreach (var pair in drawn)
                    totals[pair.Key] = checked(totals.GetValueOrDefault(pair.Key) + pair.Value);
                if (luckEnabled && drawn.Count != 0)
                {
                    // PVF 规则在第八次累计完成后触发下一次开启的双倍。
                    if (luck >= 8)
                    {
                        foreach (var pair in drawn)
                            doubleTotals[pair.Key] = checked(doubleTotals.GetValueOrDefault(pair.Key) + pair.Value);
                        luck = 0;
                    }
                    else
                        luck++;
                }
            }
            if (totals.Count is 0 or > 256)
                return InventoryResult.Reject(19);
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return InventoryResult.Reject(19);
        }

        InventorySnapshot paid;
        var removedBox = InventoryDeleteService.Plan(before, box.Position, box.Core.Value, opens);
        if (!removedBox.Accepted)
            return InventoryResult.Reject(19);
        paid = removedBox.Plan!.After;
        if (material is not null)
        {
            var removedMaterial = InventoryDeleteService.Plan(paid, material.Position, material.Core.Value, required);
            if (!removedMaterial.Accepted)
                return InventoryResult.Reject(19);
            paid = removedMaterial.Plan!.After;
        }
        if (seriaSource)
            paid = paid with { AccountWallet = paid.AccountWallet.SetSeriaLuck(luck) };
        var rewards = totals.Concat(doubleTotals)
            .GroupBy(pair => pair.Key)
            .Select(group => new InventoryReward(group.Key, checked(group.Sum(pair => pair.Value))))
            .ToArray();
        if (!InventoryCashShopService.CanGrantCurrencies(paid, rewards, new Currency.CurrencyService(goldLimits).CarryLimit(character)))
            return InventoryResult.Reject(4);
        var grant = InventoryRewardGrantService.TryGrantBatch(character with { Inventory = paid }, rewards, catalog,
            goldLimits, pieces, now, shopExpiration: ignoreAbsoluteExpiration, contracts: contracts,
            allowOverflowMail: true, ignoreUsablePeriod: ignoreUsablePeriod);
        if (!grant.Accepted)
            return InventoryResult.Reject(grant.Error == 19 ? (ushort)19 : (ushort)4);

        var after = grant.Plan!.After;
        List<MagicBoxGrantedItem> BuildRows(IReadOnlyDictionary<int, int> source)
        {
            var result = new List<MagicBoxGrantedItem>(source.Count);
            foreach (var pair in source)
            {
                if (Currency.CurrencyWallet.RewardKind(pair.Key) is { } currency)
                {
                    result.Add(new(pair.Key, pair.Value, ItemSlotBoundService.CurrencySlot(currency) ?? (short)-1, 0, false));
                    continue;
                }
                if (pieces.ByPiece.ContainsKey(pair.Key))
                {
                    result.Add(new(pair.Key, pair.Value, -1, 0, false));
                    continue;
                }
                if (contracts?.TryAcquisitionContract(pair.Key, out _) == true || catalog.Get(pair.Key).HasAcquisitionEffect)
                {
                    result.Add(new(pair.Key, pair.Value, -1, 0, false));
                    continue;
                }
                var template = catalog.Get(pair.Key);
                var list = template.AccountCommonItem ? InventoryListType.AccountCommon : ItemSlotBoundService.List(template.Kind);
                // 一行只能写一个槽。取扣费后数量增加或新出现的槽里槽号最小的一个；
                // 数量仍是本次获得总量。全部进溢出邮件时没有这种槽，保持 -1，结果窗不展示该行。
                var landed = FirstLanded(paid, after, pair.Key, list);
                if (landed is null)
                {
                    result.Add(new(pair.Key, pair.Value, -1, 0, template.Kind == ItemKind.Equipment));
                    continue;
                }
                result.Add(new(pair.Key, pair.Value, landed.Position.Index, landed.Core.Durability, template.Kind == ItemKind.Equipment));
            }
            return result;
        }
        List<MagicBoxGrantedItem> rows;
        List<MagicBoxGrantedItem> displayRows;
        List<MagicBoxGrantedItem> doubleRows;
        try
        {
            rows = BuildRows(totals.Concat(doubleTotals).GroupBy(pair => pair.Key)
                .ToDictionary(group => group.Key, group => group.Sum(pair => pair.Value)));
            displayRows = BuildRows(totals);
            doubleRows = BuildRows(doubleTotals);
        }
        catch (InvalidDataException)
        {
            return InventoryResult.Reject(19);
        }
        // 赛丽亚无材料协议使用客户端约定的 -1 哨兵；回显盒子槽位会让结果读取器错位到奖励行，成功发奖后触发客户端崩溃。
        receipt = new(box.Core.ItemId, box.Position.Index, material?.Position.Index ?? (short)-1, opens, rows,
            request.RawListType, doubleRows, seriaSource ? luckBefore : 0, seriaSource ? luck : 0, displayRows,
            broadcastItems.Count == 0 ? Array.Empty<int>() : broadcastItems.ToArray());
        long? deadline = InventoryAvatarPackageService.EarliestDeadline(box, material is null ? [] : [material]);
        if (grant.Plan.CharacterPrecondition?.QualificationExpiresAt is { } acquiredUntil && (deadline is null || acquiredUntil < deadline))
            deadline = acquiredUntil;
        foreach (var item in after.Items.Concat(grant.Plan.OverflowItems))
            if (item.ExpireTime > 0 && grant.Plan.BeforeIndex.Find(item.Position) != item &&
                (deadline is null || item.ExpireTime < deadline)) deadline = item.ExpireTime;
        var plan = new InventoryPlan(before, after, "magic_box_open",
            new(character, deadline, Clock, requirePosition: true), overflowItems: grant.Plan.OverflowItems)
        { RequiredEvent = luckEvent is null ? null : new(luckEvent.Schedule, Clock, luckEvent.Active) };
        return new(true, 0, plan);
    }

    // 对照开箱扣费后的 paid 与发放提交后的 after。未变化的同编号首堆不是本次落点。
    static InventoryItem? FirstLanded(InventorySnapshot paid, InventorySnapshot after, int itemId, InventoryListType list)
    {
        var before = new Dictionary<short, int>();
        foreach (var item in paid.Items)
            if (item.Position.List == list && item.Core.ItemId == itemId)
                before[item.Position.Index] = item.Core.Count;
        InventoryItem? chosen = null;
        foreach (var item in after.Items)
        {
            if (item.Position.List != list || item.Core.ItemId != itemId)
                continue;
            if (before.TryGetValue(item.Position.Index, out int count) && item.Core.Count <= count)
                continue;
            if (chosen is null || item.Position.Index < chosen.Position.Index)
                chosen = item;
        }
        return chosen;
    }
}
