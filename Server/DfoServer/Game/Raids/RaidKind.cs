using DfoServer.Game.Accounts;

namespace DfoServer.Game.Raids;

// 类型编号来自 raid.lst；普雷当前只接入单人引导，不扩大普通队伍容量。
internal enum RaidKind : byte { Anton = 0, Luke = 1, Prey = 3 }

internal static class RaidOwnerGroup
{
    public static T Execute<T>(RaidKind kind, IReadOnlyList<(AccountOwners.Lease Owner, ushort Character)> members, Func<T> action) => kind switch
    {
        RaidKind.Anton => AccountOwners.Lease.ExecuteRaid(members, action),
        RaidKind.Luke => AccountOwners.Lease.ExecuteRaidGroup(members, 8, action),
        RaidKind.Prey when members.Count == 1 => AccountOwners.Lease.ExecuteGroup(members, action),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
