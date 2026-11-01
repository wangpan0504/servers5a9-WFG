using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Services;

internal interface IAccountMaterialStore
{
    Character ReconcileAccountMaterials(AccountOwners.Lease owner, Character expected, InventoryPlan legacy,
        IReadOnlyDictionary<int, AccountMaterialDefinition> limits, DateTimeOffset now);
}

internal static class AccountMaterialService
{
    // 材料额度与团本通关次数使用不同周期，不能借用漩涡的周二次数周。
    internal static int RaidWeek(DateTimeOffset now)
    {
        var local = now.ToOffset(TimeSpan.FromHours(8)).AddHours(-6);
        int elapsed = ((int)local.DayOfWeek - (int)DayOfWeek.Thursday + 7) % 7;
        return DateOnly.FromDateTime(local.DateTime.AddDays(-elapsed)).DayNumber;
    }

    internal static DateTimeOffset WeekStart(int week) => new(DateOnly.FromDayNumber(week).ToDateTime(new TimeOnly(6, 0)),
        TimeSpan.FromHours(8));

    internal static InventoryPlan Legacy(Character character, ItemCatalog catalog)
    {
        var before = character.Inventory;
        var wallet = before.AccountWallet;
        var remaining = ImmutableArray.CreateBuilder<InventoryItem>(before.Items.Length);
        foreach (var item in before.Items)
        {
            if (item.Position.List is not (InventoryListType.Main or InventoryListType.PersonalCargo or InventoryListType.AccountCargo) ||
                item.Core.Kind != ItemKind.Material || item.Core.ExpireTime != 0 || item.Core.EquipmentLockId != 0 ||
                !catalog.TryGet(item.Core.ItemId, out var template) || template.AccountMaterial is null)
            {
                remaining.Add(item);
                continue;
            }
            var current = wallet.Material(item.Core.ItemId);
            // 旧实体存量可以超过新增持有上限，不能截断、删除或补计为本周新获得。
            if (item.Core.Count > int.MaxValue - current.Balance)
            {
                remaining.Add(item);
                continue;
            }
            wallet = wallet.SetMaterial(item.Core.ItemId, current with { Balance = current.Balance + item.Core.Count });
        }
        return new(before, before with { Items = remaining.ToImmutable(), AccountWallet = wallet }, "account_material_legacy", new(character));
    }

    internal static InventoryResult Grant(InventorySnapshot before, ushort character, int itemId, int count,
        AccountMaterialDefinition definition, DateTimeOffset now, bool raidReward)
    {
        if (itemId <= 0 || count <= 0 || definition.HoldingLimit <= 0 || definition.WeeklyAccountLimit < 0 || definition.WeeklyCharacterLimit < 0)
            return InventoryResult.Reject();
        var current = before.AccountWallet.Material(itemId);
        current.Validate();
        int available = Math.Max(0, definition.HoldingLimit - current.Balance);
        bool limitedRaid = raidReward && (definition.WeeklyAccountLimit != 0 || definition.WeeklyCharacterLimit != 0);
        int week = limitedRaid ? RaidWeek(now) : 0;
        if (limitedRaid && definition.WeeklyAccountLimit != 0)
            available = Math.Min(available, Math.Max(0, definition.WeeklyAccountLimit - current.EarnedIn(week)));
        if (limitedRaid && definition.WeeklyCharacterLimit != 0)
            available = Math.Min(available, Math.Max(0, definition.WeeklyCharacterLimit - current.EarnedBy(character, week)));
        // 攻坚仍发其他奖励，仅将本材料裁到剩余额度；普通购买/邮件领取保持足额获取或拒绝。
        if (!raidReward && count > available) return InventoryResult.Reject(17);
        int granted = Math.Min(count, available);
        var next = current with { Balance = checked(current.Balance + granted) };
        if (limitedRaid) next = next.EarnRaid(character, week, granted);
        var after = before with { AccountWallet = before.AccountWallet.SetMaterial(itemId, next) };
        return new(true, 0, new(before, after, "account_material_grant"));
    }
}
