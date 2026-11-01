using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.PrivilegeServices;

internal sealed record PrivilegeServiceRosterEntry(ushort Slot, long ExpiresAt);
internal sealed record PrivilegeServiceSnapshot(InventoryCommitReceipt Receipt, DateTimeOffset At,
    ImmutableArray<PrivilegeServiceRosterEntry> Roster);

// 选角/商城名册只读取角色槽和期限，不装载其他角色的完整资产。
internal interface IPrivilegeServiceRoster
{
    ImmutableArray<PrivilegeServiceRosterEntry> ReadPrivilegeServiceRoster(AccountOwners.Lease owner);
}
