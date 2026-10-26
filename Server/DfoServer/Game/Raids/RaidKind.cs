using DfoServer.Game.Accounts;

namespace DfoServer.Game.Raids;

// 仅当前实际接入的两种攻坚；不同容量边界不扩大普通队伍ExecuteGroup。
internal enum RaidKind : byte { Anton = 0, Luke = 1 }

internal static class RaidOwnerGroup
{
    public static T Execute<T>(RaidKind kind, IReadOnlyList<(AccountOwners.Lease Owner, ushort Character)> members, Func<T> action) => kind switch
    {
        RaidKind.Anton => AccountOwners.Lease.ExecuteRaid(members, action),
        RaidKind.Luke => AccountOwners.Lease.ExecuteRaidGroup(members, 8, action),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
