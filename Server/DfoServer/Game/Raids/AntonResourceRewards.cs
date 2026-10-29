using System.Collections.Immutable;
using System.Security.Cryptography;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

// 资源依据：etc/raid/anton.etc两阶段STATE REWARD。数量和抽样是本服明确政策，
// 不是对原四整数的原厂语义还原：固定箱每种1个，squad_item按RawB相对权重抽1项。
// party及全员privilege福利走kind1；squad走kind0；PC房福利取既有Session.PremiumPcRoom。
internal sealed class AntonResourceRewards(RaidScript script, ItemCatalog items)
{
    internal const string Source = "anton-local-reward-v1;etc/raid/anton.etc;box=1;weighted-squad=1;privilege=all";

    internal ImmutableArray<RaidPhaseRewardPolicy> Create(bool pcroom, Func<int, int>? draw = null)
    {
        if (script.Phases.Length != 2 || !script.Issues.IsEmpty) throw new InvalidDataException("安徒恩奖励缺少两阶段声明");
        var result = ImmutableArray.CreateBuilder<RaidPhaseRewardPolicy>(2);
        foreach (var (phase, index) in script.Phases.Select((phase, index) => (phase, index)))
        {
            var fixedGrants = ImmutableArray.CreateBuilder<InventoryReward>();
            foreach (string kind in new[] { "party_card", "pcroom_card", "privilege_card" })
            {
                var rows = phase.Rewards.Where(row => row.Kind == kind).ToArray();
                if (rows is not [{ RawA: -1, RawB: 100, RawD: 0, ItemId: > 0 } row])
                    throw new InvalidDataException("安徒恩固定奖励声明变化：" + kind);
                RequireItem(row.ItemId);
                if (kind != "pcroom_card" || pcroom) fixedGrants.Add(new(row.ItemId, 1));
            }
            var squad = phase.Rewards.Where(row => row.Kind == "squad_item").ToArray();
            if (squad.Length is < 1 or > 32 || squad.Any(row => row.RawA != -1 || row.RawB <= 0 || row.RawD is not (0 or 1)))
                throw new InvalidDataException("安徒恩分队奖励声明无效");
            foreach (var row in squad) RequireItem(row.ItemId);
            int weight = squad.Aggregate(0, (sum, row) => checked(sum + row.RawB));
            int selected = (draw ?? RandomNumberGenerator.GetInt32)(weight);
            if (selected < 0 || selected >= weight) throw new InvalidDataException("安徒恩奖励随机值越界");
            var winner = squad.First(row => (selected -= row.RawB) < 0);
            // 023B2A54..2AAA无条件连续读取601全部10B。KB-20261026T063752-4691836ed4
            // 仅证明C秒显示、D整数、E动画，D/E/F没有正式取值来源；下列占位不发布，不能发短前缀。
            // UNIMPLEMENTED：587 state3仅调整战况窗口，不能代替601保存结果及打开0xE5窗口。
            // 需要匹配构建双腿结束的完整601/660/600样本，不能为触发翻牌而发布本占位值。
            result.Add(new(Source,
                [new(1, 0, fixedGrants.ToImmutable(), []), new(0, 0, [new(winner.ItemId, 1)], [])],
                new(0, checked((byte)index), 0, 0, 0, 0)) { PublishResult = false });
        }
        return result.MoveToImmutable();
    }

    void RequireItem(int id)
    {
        if (!items.TryGet(id, out var item) || item.StackLimit < 1)
            throw new InvalidDataException("安徒恩奖励物品不可发放：" + id);
    }
}
