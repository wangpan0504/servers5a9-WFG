using System.Collections.Immutable;
using System.Security.Cryptography;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

// 末期材料数量由实际箱内容给出；外层权重只代表本地资源，不宣称官方公开概率。
internal sealed class FiendwarRewards(FiendwarScript definition, ItemCatalog items, BoosterCatalog boosters, FiendwarScript? guideDefinition = null)
{
    // 保留来源作为已有资格和收据身份；只有新开团规划展开容器，不重写历史冻结奖励。
    internal const string Source = "fiendwar-cn95-resource-v1;weighted-squad=1;per-character=1;privilege=entitled";
    internal static readonly RaidWeeklyCycle Cycle = new(DayOfWeek.Tuesday, new(6, 0), TimeSpan.FromHours(8));
    readonly RaidRewardContainers containers = new(definition.Script, items, boosters);

    readonly FiendwarRewards? guide = guideDefinition is null ? null : new(guideDefinition, items, boosters);
    internal LukeRaidCompletionPolicy CreateGuide(bool pcroom, bool privilege = false, decimal epicMultiplier = 1) => guide?.Create(pcroom, privilege, epicMultiplier: epicMultiplier) ??
        throw new InvalidDataException("漩涡引导奖励尚未装配");

    internal LukeRaidCompletionPolicy Create(bool pcroom, bool privilege = false, Func<int, int>? draw = null, decimal epicMultiplier = 1)
    {
        var script = definition.Script;
        if (!script.Issues.IsEmpty || script.Phases.Length != 1) throw new InvalidDataException("漩涡奖励声明不完整");
        var phase = script.Phases[0];
        var fixedGrants = ImmutableArray.CreateBuilder<InventoryReward>();
        foreach (string kind in new[] { "party_card", "pcroom_card", "privilege_card" })
        {
            var rows = phase.Rewards.Where(row => row.Kind == kind).ToArray();
            if (rows is not [{ RawA: -1, RawB: 100, ItemId: > 0, RawD: 0 } row])
                throw new InvalidDataException("漩涡固定奖励声明不唯一：" + kind);
            Require(row.ItemId);
            if (kind == "pcroom_card" && !pcroom || kind == "privilege_card" && !privilege) continue;
            fixedGrants.AddRange(containers.ResolveAll(row.ItemId, draw));
        }
        var squad = phase.Rewards.Where(row => row.Kind == "squad_item").ToArray();
        if (squad.Length is < 1 or > 32 || squad.Any(row => row.RawA != -1 || row.RawB <= 0 || row.RawD is not (0 or 1)))
            throw new InvalidDataException("漩涡分队奖励声明无效");
        foreach (var row in squad) Require(row.ItemId);
        var adjusted = containers.Weights(squad, row => row.ItemId, row => row.RawB, epicMultiplier);
        int total = squad.Aggregate(0, (sum, row) => checked(sum + row.RawB));
        int roll = (draw ?? RandomNumberGenerator.GetInt32)(total);
        if (roll < 0 || roll >= total) throw new InvalidDataException("漩涡奖励随机数越界");
        var selected = squad.Where((row, index) => (roll -= adjusted[index]) < 0).First();
        RaidRewardOffer Offer(byte kind, ImmutableArray<InventoryReward> grants, byte code = 0) => new(kind, 0, grants, [])
        {
            // type2使用原生通用16B结果行；客户端按红黑树首正数itemId读取+0x5C(code==1)判金牌，展开项统一带所选squad_item的RawD。
            PreyDisplay = grants.Select(grant => new LukeRewardResultSpec(code, checked((uint)grant.ItemId),
                checked((ushort)grant.Count), 0, 0,
                InventoryRewardGrantService.Create(items.Get(grant.ItemId), upgrade: grant.Upgrade, refinement: grant.Refinement).EnchantUpgrade)).ToImmutableArray()
        };
        var reward = new RaidPhaseRewardPolicy(Source,
            [Offer(1, fixedGrants.ToImmutable()), Offer(0, containers.ResolveAll(selected.ItemId, draw), checked((byte)selected.RawD))],
            new(0, 0, 0, 0, 2, 0));
        return new(Source, definition.WeeklyClearLimit, Cycle,
            [new(definition.MinimumClears, definition.WeeklyClearLimit, reward)], LukeParticipationCutoff.TerminalCommittedFacts);
    }

    void Require(int id)
    {
        if (!items.TryGet(id, out var item) || item.StackLimit < 1)
            throw new InvalidDataException("漩涡奖励物品不可发放：" + id);
    }
}
