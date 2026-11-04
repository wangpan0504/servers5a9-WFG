using System.Collections.Immutable;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Raids;

internal sealed record AntonTeamRewardEntitlement(ushort Owner, byte Card, ImmutableArray<InventoryReward> Grants);

internal sealed partial class AntonRaidBattle
{
    // class2必须一次安装完整阶段权益表；资格事务已冻结结果，展示不等于本人资产领取。
    internal ImmutableArray<AntonTeamRewardEntitlement> AntonTeamEntitlements(AntonRaidBattleView view)
    {
        lock (gate)
        {
            if (!HasAntonRewards || Kind != RaidKind.Anton || !ReferenceEquals(current, view) ||
                !antonPhaseCommits.TryGetValue(view.Phase, out var committed)) return default;
            var rows = ImmutableArray.CreateBuilder<AntonTeamRewardEntitlement>();
            foreach (var member in committed.Where(row => row.Eligible).OrderBy(row => row.Character))
            {
                if (member.Source != AntonResourceRewards.Source || member.Offers.IsDefault) return default;
                var offers = member.Offers.Where(offer => offer.Kind == 3).ToArray();
                if (offers is not [var offer] || offer.Grants.IsDefaultOrEmpty) return default;
                rows.Add(new(member.Character, offer.Card, offer.Grants));
            }
            return rows.ToImmutable();
        }
    }
}
