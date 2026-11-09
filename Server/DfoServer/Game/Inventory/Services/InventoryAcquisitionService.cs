using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

// 只处理新获得实例的即时效果；存量移动和登录不调用此入口。
internal static class InventoryAcquisitionService
{
    public static InventoryResult Plan(Character character, ItemTemplate template, int count, DateTimeOffset now, long? expiresAt = null)
    {
        var before = character.Inventory;
        if (count <= 0) return InventoryResult.Reject(19);
        if (template.AcquisitionContract is { } contract)
        {
            try
            {
                var after = before with { AccountWallet = before.AccountWallet.ActivateContract(contract.Type, contract.DurationSeconds, now, count) };
                return new(true, 0, new(before, after, "reward_grant_contract", new(character)));
            }
            catch (OverflowException) { return InventoryResult.Reject(19); }
        }
        // 扩容工具每张对应一次合法处理；个人金库已达或超过目标时消费来源并保持当前更高容量，不退成实体或静默吞掉多张。
        if (count != 1 || !template.AllowsActivation(character.Level, character.Job) ||
            !InventoryMoveService.RequiredSkillSatisfied(character, template)) return InventoryResult.Reject(19);
        InventorySnapshot upgraded;
        if (CargoCatalog.SupportsPersonalCapacity(template.PersonalCargoUpgradeTarget) &&
            (template.PersonalCargoUpgradeTarget > before.CargoCapacity || CargoCatalog.SupportsPersonalCapacity(before.CargoCapacity)))
            upgraded = before with { CargoCapacity = Math.Max(before.CargoCapacity, template.PersonalCargoUpgradeTarget) };
        else if (template.AcquisitionAccountCargo is { } account && account.PreviousCapacity == before.AccountCargo.Capacity &&
            account.Tier.Capacity > before.AccountCargo.Capacity)
            upgraded = before with { AccountCargo = before.AccountCargo with { Capacity = account.Tier.Capacity } };
        else return InventoryResult.Reject(19);
        return new(true, 0, new(before, upgraded, "reward_grant_cargo", new(character, expiresAt)));
    }
}
