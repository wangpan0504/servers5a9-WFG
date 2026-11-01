using System.Collections.Immutable;
using System.Security.Cryptography;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

// 末期材料数量由实际箱内容给出；外层权重只代表本地资源，不宣称官方公开概率。
internal sealed class FiendwarRewards(FiendwarScript definition, ItemCatalog items, BoosterCatalog boosters, FiendwarScript? guideDefinition = null)
{
    internal const string Source = "fiendwar-cn95-resource-v1;weighted-squad=1;per-character=1;privilege=entitled";
    internal static readonly RaidWeeklyCycle Cycle = new(DayOfWeek.Tuesday, new(6, 0), TimeSpan.FromHours(8));
    readonly RaidRewardContainers containers = new(definition.Script, items, boosters);

    readonly FiendwarRewards? guide = guideDefinition is null ? null : new(guideDefinition, items, boosters);
    internal LukeRaidCompletionPolicy CreateGuide(bool pcroom, bool privilege = false) => guide?.Create(pcroom, privilege) ??
        throw new InvalidDataException("漩涡引导奖励尚未装配");

    internal LukeRaidCompletionPolicy Create(bool pcroom, bool privilege = false, Func<int, int>? draw = null)
    {
        var script = definition.Script;
        if (!script.Issues.IsEmpty || script.Phases.Length != 1) throw new InvalidDataException("漩涡奖励声明不完整");
        var phase = script.Phases[0];
        var fixedGrants = ImmutableArray.CreateBuilder<InventoryReward>();
        foreach (string kind in new[] { "party_card", "pcroom_card", "privilege_card" })
        {
            var rows = phase.Rewards.Where(row => row.Kind == kind).ToArray();
            if (rows is not [{ RawA: -1, RawB: 100, ItemId: > 0 } row])
                throw new InvalidDataException("漩涡固定奖励声明不唯一：" + kind);
            Require(row.ItemId);
            if (kind == "pcroom_card" && !pcroom || kind == "privilege_card" && !privilege) continue;
            fixedGrants.Add(containers.Resolve(row.ItemId, draw));
        }
        var squad = phase.Rewards.Where(row => row.Kind == "squad_item").ToArray();
        if (squad.Length is < 1 or > 32 || squad.Any(row => row.RawA != -1 || row.RawB <= 0 || row.RawD is not (0 or 1)))
            throw new InvalidDataException("漩涡分队奖励声明无效");
        foreach (var row in squad) Require(row.ItemId);
        int total = squad.Aggregate(0, (sum, row) => checked(sum + row.RawB));
        int roll = (draw ?? RandomNumberGenerator.GetInt32)(total);
        if (roll < 0 || roll >= total) throw new InvalidDataException("漩涡奖励随机数越界");
        var selected = squad.First(row => (roll -= row.RawB) < 0);
        RaidRewardOffer Offer(byte kind, ImmutableArray<InventoryReward> grants) => new(kind, 0, grants, [])
        {
            // type2使用原生通用16B结果行；展示绑定冻结的实际产物，不先广播未领取卡片。
            PreyDisplay = grants.Select(grant => new LukeRewardResultSpec(0, checked((uint)grant.ItemId),
                checked((ushort)grant.Count), 0, 0,
                InventoryRewardGrantService.Create(items.Get(grant.ItemId), upgrade: grant.Upgrade, refinement: grant.Refinement).EnchantUpgrade)).ToImmutableArray()
        };
        var reward = new RaidPhaseRewardPolicy(Source,
            [Offer(1, fixedGrants.ToImmutable()), Offer(0, [containers.Resolve(selected.ItemId, draw)])],
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
