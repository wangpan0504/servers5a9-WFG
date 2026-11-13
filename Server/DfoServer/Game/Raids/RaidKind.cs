using DfoServer.Game.Accounts;

namespace DfoServer.Game.Raids;

// 类型编号来自 raid.lst；普通普雷的全团操作复用同一固定组锁序。
internal enum RaidKind : byte { Anton = 0, Luke = 1, Fiendwar = 2, Prey = 3 }

internal static class RaidOwnerGroup
{
    public static T Execute<T>(RaidKind kind, IReadOnlyList<(AccountOwners.Lease Owner, ushort Character)> members, Func<T> action) => kind switch
    {
        RaidKind.Anton => AccountOwners.Lease.ExecuteRaid(members, action),
        RaidKind.Luke or RaidKind.Fiendwar => AccountOwners.Lease.ExecuteRaidGroup(members, 8, action),
        RaidKind.Prey when members.Count is >= 1 and <= 12 => AccountOwners.Lease.ExecuteRaid(members, action),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
