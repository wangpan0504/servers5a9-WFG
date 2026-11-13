using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Game.Quests;

namespace DfoServer.Game.Inventory.Services;

internal sealed class InventoryRepairService(ItemCatalog catalog, EquipmentRepairCatalog repair, TownCatalog? towns = null, TimeProvider? clock = null)
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;

    // 02355D60的一键修理跳过旧符咒，036E9160也拒绝其单件修理；Heraldry仍按普通耐久修复。
    static bool SupportsRepair(ItemTemplate template) => !template.IsQuickSlotEquipment ||
        template.QuickSlotEquipment is { Heraldry: true };

    public InventoryResult Repair(Character character, RepairEquipmentRequest request, DateTimeOffset? utcNow = null,
        bool clientBattleWear = false, bool inDungeon = false)
    {
        if (request.Position.List is not (InventoryListType.Main or InventoryListType.Equipment or InventoryListType.PersonalCargo))
            return InventoryResult.Reject(17);
        bool automatic = request.Mode == 1;
        if (request.Mode > 1 || request.RepairItemSlot < -1 || automatic && (request.RepairItemSlot != -1 ||
            request.Position != new InventorySlot(InventoryListType.Main, -1) || request.Quick))
            return InventoryResult.Reject(19);
        var before = character.Inventory;
        var time = utcNow ?? clock.GetUtcNow();
        if (automatic) return Automatic(character, catalog, time, clock, clientBattleWear);
        var planned = before.Items;
        long total = 0;
        long now = time.ToUnixTimeSeconds();
        bool all = request.Position.Index == -1;
        long? deadline = null;
        ActionConsumableDefinition? tool = null;
        InventoryResult? paidTool = null;
        if (request.RepairItemSlot >= 0)
        {
            var position = new InventorySlot(InventoryListType.Main, request.RepairItemSlot);
            var source = before.Find(position);
            tool = source is null ? null : catalog.ActionConsumable(source.Core.ItemId);
            if (tool is not { Label: "[repair coupon]" or "[repair equipment]" } || request.Quick ||
                (tool.Label == "[repair coupon]" ? all : !all)) return InventoryResult.Reject(19);
            string place = inDungeon ? "[dungeon]" : towns?.IsRoom(character.Position, QuestEligibility.Completed(character)) == true
                ? "[seria room]" : "[village]";
            paidTool = InventoryActionConsumableService.Plan(character, position, tool, place, clock, time);
            if (!paidTool.Accepted) return paidTool;
            if (paidTool.Plan!.CharacterPrecondition?.QualificationExpiresAt is { } sourceDeadline)
                deadline = Math.Min(deadline ?? long.MaxValue, sourceDeadline);
        }
        var candidates = all ? before.Items.Where(item => item.Position.List == InventoryListType.Equipment ||
            item.Position.List == InventoryListType.Main && item.Position.Index is >= 3 and <= 8).ToArray() :
            before.Find(request.Position) is { } single ? [single] : Array.Empty<InventoryItem>();
        if (!all && candidates.Length == 0)
            return InventoryResult.Reject(17);
        foreach (var item in candidates)
        {
            if (item.Core.Kind != ItemKind.Equipment || item.Core.IsExpired(now))
            {
                if (!all)
                    return InventoryResult.Reject(19);
                continue;
            }
            var template = catalog.Get(item.Core.ItemId);
            if (!SupportsRepair(template))
            {
                if (!all) return InventoryResult.Reject(19) with { RejectReason = "old_charm_requires_charging" };
                continue;
            }
            if (tool?.Label == "[repair coupon]" && template.Durability == 0) return InventoryResult.Reject(19);
            // 一键修理覆盖普通穿戴与快捷装备，不把背包装备页、仓库或特殊盾牌一起收费。
            if (all && template.EquipmentSlot is 13 or 24 or 30)
                continue;
            // 单件的无变化目标仍须有效；批量只绑定实际修理的有效候选。
            if ((!all || item.Core.Durability < template.Durability) &&
                item.ExpireTime > 0 && (deadline is null || item.ExpireTime < deadline)) deadline = item.ExpireTime;
            if (item.Core.Durability >= template.Durability)
                continue;
            int price = 0;
            if (tool?.Label != "[repair coupon]" &&
                !repair.TryPrice(template, item.Core.Durability, item.Core.Upgrade, request.Quick, out price))
                return InventoryResult.Reject(10);
            total += price;
            if (total > before.Gold)
                return InventoryResult.Reject(10);
            // 修理只恢复耐久；安全锁、排序锁、封装和全部扩展属性原样保留。
            planned = planned.Replace(item, item with { Core = item.Core with { Durability = template.Durability } });
        }
        if (paidTool is { Plan: { } sourcePlan })
        {
            // 全部装备、金币和源券共用一个计划，不能在扣券后再尝试独立修理事务。
            var after = sourcePlan.After;
            foreach (var item in planned)
                if (sourcePlan.BeforeIndex.Find(item.Position) != item)
                    after = after with { Items = after.Items.Replace(after.Find(item.Position)!, item) };
            after = after with { Gold = checked(before.Gold - (uint)total) };
            return new(true, 0, new(before, after,
                tool!.Label == "[repair coupon]" ? "equipment_repair_coupon" : "equipment_repair_item", new(character, deadline, clock, requirePosition: true))
            { EquipmentRepair = EquipmentRepairFact.Capture(before, after, request, time, clock) });
        }
        var repaired = before with { Items = planned, Gold = checked(before.Gold - (uint)total) };
        return new(true, 0, new(before, repaired, "equipment_repair",
            new(character, deadline, clock, requirePosition: true))
        { EquipmentRepair = EquipmentRepairFact.Capture(before, repaired, request, time, clock) });
    }

    internal static InventoryResult Automatic(Character character, ItemCatalog catalog, DateTimeOffset time,
        TimeProvider clock, bool allowRestatement = false)
    {
        var before = character.Inventory;
        long now = time.ToUnixTimeSeconds();
        long deadline = Math.Min(DevilContractPolicy.Expiry(before.AccountWallet, 6),
            DailyBoosterUsage.NextReset(time).ToUnixTimeSeconds());
        if (deadline <= now || DevilContractPolicy.Used(before, 6, time) >= 6)
            return InventoryResult.Reject(19);
        var candidates = before.Items.Where(item =>
                (item.Position.List == InventoryListType.Equipment ||
                 item.Position.List == InventoryListType.Main && item.Position.Index is >= 3 and <= 8) &&
                item.Core.Kind == ItemKind.Equipment && !item.IsExpired(now))
            .Select(item => (Item: item, Template: catalog.Get(item.Core.ItemId)))
            .Where(row => row.Template is { Durability: > 0, EquipmentSlot: not (13 or 24 or 30) } &&
                SupportsRepair(row.Template)).ToArray();
        if (!candidates.Any(row => row.Item.Core.Durability == 0))
        {
            // 48已完成修理后，迟到的23只确认满耐久现状，不再次消费免费次数。
            return allowRestatement && candidates.Length != 0 &&
                candidates.All(row => row.Item.Core.Durability >= row.Template.Durability)
                ? new(true, 0, new(before, before, "equipment_repair_restatement", new(character, deadline, clock, requirePosition: true)))
                : InventoryResult.Reject(19);
        }
        if (!DevilContractPolicy.TryConsume(before, 6, 6, time, out var charged))
            return InventoryResult.Reject(19);
        var items = before.Items;
        foreach (var (item, template) in candidates)
        {
            if (item.Core.Durability >= template.Durability) continue;
            if (item.ExpireTime > 0) deadline = Math.Min(deadline, item.ExpireTime);
            items = items.Replace(item, item with { Core = item.Core with { Durability = template.Durability } });
        }
        return new(true, 0, new(before, charged with { Items = items }, "equipment_auto_repair",
            new(character, deadline, clock, requirePosition: true)));
    }
}
