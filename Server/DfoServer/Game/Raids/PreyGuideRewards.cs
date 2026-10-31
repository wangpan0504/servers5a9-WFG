using System.Collections.Immutable;
using System.Security.Cryptography;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

// 单人引导使用自己的STATE REWARD；抽样在入场事务前冻结，领奖重试不重抽。
internal sealed class PreyGuideRewards(RaidScript script, ItemCatalog items, BoosterCatalog boosters)
{
    internal const string Source = "prey-guide-resource-v1;etc/raid/prey/prey.etc;single;weighted-squad=1;legacy=resolved;booster=box";
    readonly RaidRewardContainers containers = new(script, items, boosters);

    internal static bool ValidDisplay(RaidRewardOffer offer) => offer.Kind is 0 or 1 && offer.Card == 0 &&
        !offer.PreyDisplay.IsDefaultOrEmpty && offer.PreyDisplay.Length == offer.Grants.Length && offer.Grants.Length <= 32 &&
        offer.PreyDisplay.Select((row, index) => row.Matches(offer with { Grants = [offer.Grants[index]] })).All(value => value);

    internal LukeRaidCompletionPolicy Create(bool pcroom, Func<int, int>? draw = null)
    {
        if (!script.Issues.IsEmpty || script.Phases.Length != 2) throw new InvalidDataException("普雷引导奖励声明不完整");
        var phases = ImmutableArray.CreateBuilder<LukePhaseCompletionPolicy>(2);
        foreach (var (phase, index) in script.Phases.Select((phase, index) => (phase, index)))
        {
            var fixedGrants = ImmutableArray.CreateBuilder<InventoryReward>();
            foreach (string kind in new[] { "party_card", "pcroom_card", "privilege_card" })
            {
                var rows = phase.Rewards.Where(row => row.Kind == kind).ToArray();
                if (rows.Length > 1 || kind == "party_card" && rows.Length != 1) throw new InvalidDataException("普雷引导固定奖励不唯一");
                foreach (var row in rows)
                {
                    Require(row);
                    if (kind != "pcroom_card" || pcroom) fixedGrants.Add(containers.Resolve(row.ItemId, draw));
                }
            }
            var squad = phase.Rewards.Where(row => row.Kind == "squad_item").ToArray();
            if (squad.Length is < 1 or > 32) throw new InvalidDataException("普雷引导分队奖池不完整");
            foreach (var row in squad) Require(row);
            int total = squad.Aggregate(0, (sum, row) => checked(sum + row.RawB));
            int roll = (draw ?? RandomNumberGenerator.GetInt32)(total);
            if (roll < 0 || roll >= total) throw new InvalidDataException("普雷引导随机数越界");
            var selected = squad.First(row => (roll -= row.RawB) < 0);
            RaidRewardOffer Offer(byte kind, ImmutableArray<InventoryReward> grants) => new(kind, 0, grants, [])
            {
                // 本服普通产物显示：code/flag为0，不启用特殊牌效；尾列显式0，附魔升级取真实core。
                PreyDisplay = grants.Select(grant => new LukeRewardResultSpec(0, checked((uint)grant.ItemId),
                    checked((ushort)grant.Count), 0, 0,
                    InventoryRewardGrantService.Create(items.Get(grant.ItemId), upgrade: grant.Upgrade, refinement: grant.Refinement).EnchantUpgrade)).ToImmutableArray()
            };
            var policy = new RaidPhaseRewardPolicy(Source,
                [Offer(1, fixedGrants.ToImmutable()), Offer(0, [containers.Resolve(selected.ItemId, draw)])],
                new(0, checked((byte)index), 0, 0, 2, 0));
            phases.Add(new(1, 2, policy));
        }
        return new(Source, 2, new(DayOfWeek.Thursday, new(6, 0), TimeSpan.FromHours(8)),
            phases.MoveToImmutable(), LukeParticipationCutoff.TerminalCommittedFacts);

        void Require(RaidReward row)
        {
            if (row.RawA != -1 || row.RawB <= 0 || !items.TryGet(row.ItemId, out var item) || item.StackLimit < 1)
                throw new InvalidDataException("普雷引导奖励物品不可发放：" + row.ItemId);
        }
    }
}
