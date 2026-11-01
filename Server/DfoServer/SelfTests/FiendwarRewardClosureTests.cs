using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.PrivilegeServices;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Raids;
using DfoServer.Infrastructure;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

// 预期来自已核对的95末期资源；只检验本地权重，不将其解释为官方公开概率。
internal static class FiendwarRewardClosureTests
{
    const int Particle = 10306750;
    static readonly int[] Armor =
    [
        100050817, 100250681, 100150687, 100100718, 100200662,
        100060676, 100260643, 100160603, 100110662, 100210627,
        100070630, 100270594, 100170597, 100120622, 100220597,
        100080654, 100280598, 100180603, 100130635, 100230603,
        100090459, 100290445, 100190425, 100140454, 100240432
    ];
    static readonly int[] RareCards =
    [490007851, 490007852, 490007853, 490008625, 490008626, 490008627, 490008624, 490008629, 490008628];
    static readonly int[] AdvancedCards = [10306741, 10306743, 10306745, 10306747, 10306818];

    sealed record Root(int Id, int Item, int Count, int[]? Candidates = null, bool Lottery = false);
    static readonly Root[] Team =
    [
        new(10306817, 10308807, 150), new(10309047, Particle, 2), new(490703924, Particle, 3, Lottery: true),
        new(10306753, Particle, 18), new(10306754, Particle, 19), new(10306755, Particle, 20),
        new(10306756, Particle, 30), new(10309043, 10309041, 30),
        new(10306757, Particle, 18, Armor), new(10306758, Particle, 18, RareCards), new(10306759, Particle, 18, AdvancedCards)
    ];
    static readonly Root[] Guide =
    [
        new(10309090, 10308807, 90), new(10306751, Particle, 1, Lottery: true), new(490705073, Particle, 1, Lottery: true),
        new(10309086, Particle, 10), new(10309087, Particle, 12), new(10309088, Particle, 13),
        new(10309089, Particle, 20), new(10309165, 10309041, 20),
        new(10309166, Particle, 10, Armor), new(10309167, Particle, 10, RareCards), new(10309168, Particle, 10, AdvancedCards)
    ];

    internal static void Run(TcpTestResources resources)
    {
        int before = TestCheck.Count;
        var archive = resources.Archive;
        string path = RaidCatalog.Load(archive).Find(2)!.Path;
        var tokens = archive.ReadTokens(path);
        var symbols = archive.ReadTokens(path + ".symbol");
        var team = FiendwarScript.Parse(path, tokens, symbols, archive.ResolveString);
        var guide = FiendwarScript.ParseGuide(path, tokens, symbols, archive.ResolveString);
        RunMode(team, Team, [4400, 2100, 2100, 200, 500, 200, 300, 200], resources.ItemResources);
        RunMode(guide, Guide, [4900, 2150, 2150, 200, 180, 120, 180, 120], resources.ItemResources);

        That(Team.Concat(Guide).Select(root => root.Id).Distinct().Count() == 22 &&
            Armor.Distinct().Count() == 25 && RareCards.Concat(AdvancedCards).Distinct().Count() == 14,
            "团队及引导22奖励根、25防具和14卡片预期表互不遗漏");
        var now = new DateTimeOffset(2026, 11, 3, 6, 0, 0, TimeSpan.FromHours(8));
        var character = new Character(1, 0, "FiendwarReward", 0, resources.Towns.Room, resources.Towns.InitialReturn)
        { Level = 95, Job = 0 };
        foreach (var root in Team.Concat(Guide))
            RunConsumer(root, character, now, resources.ItemResources);
        foreach (int id in RareCards.Concat(AdvancedCards))
        {
            var card = resources.ItemResources.Items.Get(id);
            var core = InventoryRewardGrantService.Create(card, utcNow: now);
            That(card is { AccountBoundAfterTrade: true, AccountBindingTradeLimit: 1 } &&
                StackableTradePolicy.CanTransfer(core, card, sameAccount: false) &&
                StackableTradePolicy.AfterTransfer(core, card).Extension120 == 0x51,
                "奖励卡片首次交易与交易后账号绑定均可消费：" + id);
        }
        That(!resources.ItemResources.Boosters.Get(10306741).Available,
            "罗什卡片是终叶，不能因出现在奖励池内被当成另一礼盒");
        Console.WriteLine($"PASS {TestCheck.Count - before} 漩涡奖励闭包断言");
    }

    static void RunMode(FiendwarScript script, Root[] roots, int[] weights, ItemResources resources)
    {
        var declared = script.Script.Phases.Single().Rewards.Select(row => row.ItemId).Distinct().Order();
        That(script.Script.Issues.IsEmpty && declared.SequenceEqual(roots.Select(root => root.Id).Order()),
            "真实模式奖励声明完整覆盖独立11根预期：guide=" + script.Guide);
        var factory = new FiendwarRewards(script, resources.Items, resources.Boosters);
        var resolver = new RaidRewardContainers(script.Script, resources.Items, resources.Boosters);
        foreach (var root in roots)
        {
            var grant = resolver.Resolve(root.Id, _ => 0);
            That(grant == new InventoryReward(root.Lottery ? root.Item : root.Id, root.Lottery ? root.Count : 1),
                "奖励消费者只提前解析Lottery、普通多池礼盒原样冻结：" + root.Id);
        }
        int lower = 0;
        for (int index = 0; index < weights.Length; index++)
        {
            foreach (int roll in new[] { lower, lower + weights[index] - 1 })
            {
                var policy = factory.Create(false, false, bound => bound == 10000 ? roll : 0);
                var offers = policy.Phases.Single().Rewards.Offers;
                That(offers is [{ Kind: 1, Card: 0 }, { Kind: 0, Card: 0 }] &&
                    offers[0].Grants.SequenceEqual(new[] { new InventoryReward(roots[0].Id, 1) }) &&
                    offers[1].Grants.SequenceEqual(new[] { new InventoryReward(roots[index + 3].Id, 1) }),
                    "真实分队奖池区间两端可达且固定奖励不受抽样污染：" + roots[index + 3].Id + "/" + roll);
                foreach (var offer in offers)
                    That(offer.PreyDisplay.Length == offer.Grants.Length &&
                        offer.PreyDisplay.Zip(offer.Grants).All(pair => pair.First.Matches(pair.Second)),
                        "翻牌展示绑定已冻结的实际奖励数量");
            }
            lower += weights[index];
        }
        That(lower == 10000, "本地外层权重总量保持10000");
        Reject<InvalidDataException>(() => factory.Create(false, false, _ => -1), "负随机结果拒绝冻结");
        Reject<InvalidDataException>(() => factory.Create(false, false, bound => bound), "上界随机结果拒绝冻结");

        // 真实开团逐成员取值由PrivilegeServiceTests.RaidRewards覆盖；此处交错检查政策输出及时间边界。
        const long at = 1_793_654_400;
        PrivilegeServiceState[] states =
        [
            new(at - 60, at + 2, 1, 1, 1, 1, 0, 0), new(at - 60, at, 1, 1, 1, 1, 0, 0),
            PrivilegeServiceState.Empty, new(at, at + 2, 1, 1, 1, 1, 0, 0),
            new(at + 1, at + 3, 1, 1, 1, 1, 0, 0), new(at - 60, at + 2, 1, 1, 1, 1, 0, 0),
            new(at - 60, at, 1, 1, 1, 1, 0, 0), PrivilegeServiceState.Empty
        ];
        bool[] entitled = [true, false, false, true, false, true, false, false];
        foreach (bool pcroom in new[] { false, true, false })
        {
            var frozen = states.Select(state => factory.Create(pcroom, state.IsActive(at), _ => 0)).ToArray();
            for (int member = 0; member < states.Length; member++)
            {
                var expected = ImmutableArray.CreateBuilder<InventoryReward>();
                expected.Add(new(roots[0].Id, 1));
                if (pcroom) expected.Add(new(roots[1].Lottery ? roots[1].Item : roots[1].Id, roots[1].Lottery ? roots[1].Count : 1));
                if (entitled[member]) expected.Add(new(roots[2].Item, roots[2].Count));
                That(states[member].IsActive(at) == entitled[member] &&
                    frozen[member].Phases.Single().Rewards.Offers.Single(offer => offer.Kind == 1).Grants.SequenceEqual(expected),
                    "逐成员礼遇开始包含、到期排除，网吧资格与礼遇独立：" + member + "/" + pcroom);
            }
        }
    }

    static void RunConsumer(Root root, Character character, DateTimeOffset now, ItemResources resources)
    {
        var definition = resources.Boosters.Get(root.Id);
        That(definition.Available && definition.Lottery == root.Lottery &&
            definition.Pools.All(pool => pool is { Draws: 1, JobTag: null }),
            "实际奖励盒消费者与全职业单次奖池完整：" + root.Id);
        if (root.Candidates is { } candidates)
            That(definition.Pools.Length == 2 && definition.Pools[1].Rewards.SequenceEqual(
                candidates.Select(id => new BoosterReward(id, 1000, 1))),
                "全部25防具或9/5卡片保持原候选顺序与本地权重：" + root.Id);

        var targets = root.Candidates is null ? new[] { 0L } :
            Enumerable.Range(0, root.Candidates.Length).SelectMany(index => new[] { index * 1000L, index * 1000L + 999 }).ToArray();
        if (root.Id == 10309047) targets = [0, 3999, 4000, 9999];
        foreach (long target in targets)
        foreach (byte job in root.Candidates is null ? new byte[] { 0 } : new byte[] { 0, 15 })
        {
            var random = new DrawRandom(target);
            var service = new InventoryBoosterService(resources.Items, resources.Boosters, resources.GoldLimits, resources.EpicPieces, random);
            var role = character with { Job = job, Inventory = new([
                new(1, new(InventoryListType.Main, 65), InventoryRewardGrantService.Create(resources.Items.Get(root.Id), utcNow: now))]) };
            int count = root.Id == 10309047 && target >= 4000 ? 3 : root.Count;
            var expected = new List<BoosterGrantedItem> { new(root.Item, count) };
            if (root.Candidates is { } choices) expected.Add(new(choices[(int)(target / 1000)], 1));
            InventoryResult opened;
            if (root.Lottery)
            {
                That(!service.Open(role, new(65, 1), out _, now).Accepted, "Lottery不能走普通开盒入口：" + root.Id);
                opened = service.OpenLottery(role, new(0, 65), out var receipt, now);
                That(opened.Accepted && receipt is not null && receipt.ItemId == root.Item && receipt.Count == count,
                    "Lottery正式消费者与开战前解析产物一致：" + root.Id);
            }
            else
            {
                That(!service.OpenLottery(role, new(0, 65), out _, now).Accepted, "普通Booster不能误走Lottery入口：" + root.Id);
                opened = service.Open(role, new(65, 1), out var receipt, now);
                That(opened.Accepted && receipt is not null && receipt.Rewards.OrderBy(row => row.ItemId).SequenceEqual(expected.OrderBy(row => row.ItemId)),
                    "真实开盒同时产出固定材料与命中候选：" + root.Id + "/" + target + "/" + job);
            }
            That(opened.Plan is not null && !opened.Plan.After.Items.Any(item => item.Core.ItemId == root.Id) &&
                opened.Plan.UsedItems is [{ ItemId: var usedId, Count: 1 }] && usedId == root.Id,
                "消费者恰好扣除一个源盒且不保留幽灵源：" + root.Id);
            foreach (var grant in expected)
            {
                var after = opened.Plan!.After;
                int delivered = resources.Items.Get(grant.ItemId).AccountMaterial is not null
                    ? after.AccountWallet.Material(grant.ItemId).Balance
                    : after.Items.Where(item => item.Core.ItemId == grant.ItemId).Sum(item => item.Core.Count);
                That(delivered == grant.Count, "规划的实际到账数量与回执一致：" + grant.ItemId);
            }
            That(random.Calls == (root.Candidates is not null || root.Id == 10309047 ? 1 : 0),
                "独立固定材料池不额外抽样，多候选池只抽一次：" + root.Id);
        }
    }

    sealed class DrawRandom(long value) : Random
    {
        internal int Calls { get; private set; }
        public override long NextInt64(long maxValue)
        {
            That(value >= 0 && value < maxValue, "测试抽样必须落在正式消费者给出的边界内");
            Calls++;
            return value;
        }
    }
}
