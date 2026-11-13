using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.PrivilegeServices;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Raids;
using DfoServer.Infrastructure;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
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
        RunRewardTransactions(team, resources);
        Console.WriteLine($"PASS {TestCheck.Count - before} 漩涡奖励闭包断言");
    }

    // 只种入明确的已结算资格；真实资源策略、type2恢复、库存/邮件/战令事务与重放沿正式入口。
    static void RunRewardTransactions(FiendwarScript script, TcpTestResources resources)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-fiendwar-grants-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var items = resources.ItemResources;
            var now = new DateTimeOffset(2026, 11, 3, 6, 1, 0, TimeSpan.FromHours(8));
            var catalog = BattlePassCatalog.Load(resources.Archive);
            BattlePassMissionPolicy? missions = null;
            for (int week = 0; week < catalog.WeeklyMissionIds.Length / 4; week++)
            {
                var candidate = new BattlePassMissionPolicy("fiendwar-direct-song",
                    new DateTimeOffset(2026, 10, 29, 6, 0, 0, TimeSpan.FromHours(8)).AddDays(-7 * week), catalog);
                if (candidate.Active(now).Any(row => BattlePassUndefeatedSongSource.Matches(row.Key.Category, row.Definition)))
                { missions = candidate; break; }
            }
            if (missions is null) throw new InvalidDataException("匹配资源未提供活跃周任务48");
            var repository = new CharacterRepository(path);
            repository.ConfigureBattlePassMissions(missions);
            using var owner = new AccountOwners().Acquire("fw-direct-grants", () => { });
            // 各页填满真实完整堆，避免靠同ID未满堆掩盖金牌附加装备或卡片的溢出路径。
            var inventory = InventorySnapshot.Empty;
            var full = new List<InventoryItem>();
            foreach (var template in new[] { items.Items.Get(Armor[0]), items.Items.Get(RareCards[0]) })
            {
                var range = ItemSlotBoundService.Range(template.Kind, inventory);
                foreach (int slot in Enumerable.Range(range.Start, range.End - range.Start + 1))
                    full.Add(new(0, new(InventoryListType.Main, (short)slot),
                        InventoryRewardGrantService.Create(template, template.StackLimit, now)));
            }
            repository.Create(owner, new NewCharacter("FwDirect", resources.Towns.Room, resources.Towns.InitialReturn)
                { Level = 95, Inventory = inventory with { Items = [.. full] } });
            var role = repository.LoadCharacter(owner, 0);
            _ = repository.ReadBattlePassMissions(owner, now);
            int completed = 0;
            foreach (var sample in new[] { (Outer: 9300, Extra: Armor[^1]), (Outer: 9500, Extra: RareCards[^1]) })
            {
                var extra = items.Items.Get(sample.Extra);
                var range = ItemSlotBoundService.Range(extra.Kind, role.Inventory);
                That(role.Inventory.Expansion == inventory.Expansion && !extra.AccountCommonItem &&
                    !role.Inventory.Items.Any(item => item.Core.ItemId == sample.Extra) &&
                    Enumerable.Range(range.Start, range.End - range.Start + 1).All(slot =>
                        role.Inventory.Find(new(ItemSlotBoundService.List(extra.Kind), (short)slot)) is not null),
                    "投邮前按实际持久化扩展填满附加奖励所属页，且不存在可合堆的同ID实例");
                int calls = 0;
                var offers = new FiendwarRewards(script, items.Items, items.Boosters)
                    .Create(false, false, bound => calls++ == 1 ? sample.Outer : bound - 1).Phases.Single().Rewards.Offers;
                That(offers[0].Grants.SequenceEqual(new[] { new InventoryReward(10308807, 150) }) &&
                    offers[1].Grants.SequenceEqual(new[] { new InventoryReward(Particle, 18), new InventoryReward(sample.Extra, 1) }.OrderBy(row => row.ItemId)),
                    "满包事务夹具取实际漩涡固定牌与金牌完整产物，不手工替换源盒奖励");
                Guid instance = Guid.NewGuid(), eventId = Guid.NewGuid();
                long anchor = now.ToUnixTimeSeconds();
                int week = FiendwarRewards.Cycle.Key(now);
                var eligibility = new RaidPhaseEligibility(instance, RaidKind.Fiendwar, 0, eventId, anchor,
                    role.Id, true, true, "fixture-qualified", FiendwarRewards.Source, week, offers);
                Sql("INSERT INTO character_raid_runs(character_id,account,instance_id,raid_type,source,admitted_at,state,clear_week,phase0_event,phase0_anchor,phase0_receipt) VALUES($p0,$p1,$p2,2,$p3,$p4,2,$p5,$p6,$p4,$p7)",
                    role.Id, owner.Account, instance.ToString("N"), FiendwarRewards.Source, anchor, week, eventId.ToString("N"), JsonSerializer.Serialize(eligibility));
                foreach (var offer in offers)
                {
                    var target = new LukeRewardRecoveryTarget(role.Id, instance, 0, offer.Kind, 0) { RaidType = RaidKind.Fiendwar };
                    string before = Facts();
                    Sql(offer.Kind == 1
                        ? "CREATE TRIGGER fw_grant_failure BEFORE UPDATE OF revision ON account_battle_pass_seasons BEGIN SELECT RAISE(ABORT,'fixture late song failure'); END"
                        : "CREATE TRIGGER fw_grant_failure BEFORE INSERT ON character_raid_reward_receipts BEGIN SELECT RAISE(ABORT,'fixture late mail failure'); END");
                    try { Reject<SqliteException>(() => Claim(), "漩涡直接多产物的末段SQL失败不能提交部分材料、邮件或来源"); }
                    finally { Sql("DROP TRIGGER fw_grant_failure"); }
                    That(Facts() == before, "type2失败完整回滚角色/账号材料、UID、邮件、回执及战令来源进度");
                    var previous = role;
                    var commit = Claim();
                    role = commit.Character;
                    var mail = repository.LoadMailbox(owner, role.Id, now.ToUnixTimeSeconds()).Entries
                        .Where(entry => commit.MailIds.Contains(entry.Id)).SelectMany(entry => entry.Attachments).ToArray();
                    That(!commit.Duplicate && JsonSerializer.Serialize(repository.LoadCharacter(owner, 0)) == JsonSerializer.Serialize(role),
                        "type2恢复返回实际已持久化资产");
                    foreach (var grant in offer.Grants)
                    {
                        int physical = role.Inventory.Items.Where(item => item.Core.ItemId == grant.ItemId).Sum(item => item.Core.Count)
                            - previous.Inventory.Items.Where(item => item.Core.ItemId == grant.ItemId).Sum(item => item.Core.Count);
                        int account = role.Inventory.AccountWallet.Material(grant.ItemId).Balance - previous.Inventory.AccountWallet.Material(grant.ItemId).Balance;
                        That(physical + account + mail.Where(row => row.Item.Core.ItemId == grant.ItemId).Sum(row => row.Item.Core.Count) == grant.Count,
                            "直接冻结的每项材料/装备/卡片在原子提交中完整到账或投邮：" + grant.ItemId);
                    }
                    That(role.Inventory.Items.All(item => previous.Inventory.Items.Any(old => old == item) ||
                        offer.Grants.Any(grant => grant.ItemId == item.Core.ItemId)) &&
                        previous.Inventory.Items.Where(item => offer.Grants.All(grant => grant.ItemId != item.Core.ItemId))
                            .All(item => role.Inventory.Items.Contains(item)) &&
                        !role.Inventory.Items.Any(item => Team.Any(entry => entry.Id == item.Core.ItemId)),
                        "满包发放保留旧实例且不生成源盒");
                    if (offer.Kind == 0)
                        That(!commit.MailIds.IsEmpty && mail.Any(row => row.Item.Core.ItemId == sample.Extra && row.Item.Core.Count == 1),
                            "金牌附加装备或卡片在满页时沿原永久溢出邮件投递，不截断其它产物");
                    int sources = completed + 1;
                    That(MissionCount() == Math.Min(300, sources * 150) &&
                        Number("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='raid_undefeated_song_acquisition'") == sources,
                        "type2固定牌直接取得150不渝只记一次实际来源，金牌不重复推进任务48");
                    string saved = Facts();
                    var replay = LukeRewardRecovery.Claim(owner, role, target, eventId, anchor, repository,
                        () => throw new InvalidOperationException("重放不得重读资源或重抽多产物"), now);
                    That(replay.Duplicate && replay.MailIds.SequenceEqual(commit.MailIds) && Facts() == saved,
                        "漩涡同牌恢复重放不再增材料、物品、邮件、回执或战令进度");

                    RaidRewardCommit Claim() => LukeRewardRecovery.Claim(owner, role, target, eventId, anchor, repository,
                        () => (items.Items, items.GoldLimits, items.EpicPieces), now);
                }
                completed++;
            }

            long MissionCount() => repository.ReadBattlePassMissions(owner, now)!.Slots
                .Single(row => BattlePassUndefeatedSongSource.Matches(row.Instance.Key.Category, row.Instance.Definition)).Count;
            string Facts() => JsonSerializer.Serialize(repository.LoadCharacter(owner, 0)) + "/" + MissionCount() + "/" +
                Number("SELECT count(*) FROM mailbox_messages") + "/" + Number("SELECT count(*) FROM mailbox_attachments") + "/" +
                Number("SELECT count(*) FROM character_inventory_items") + "/" + Number("SELECT count(*) FROM inventory_audit_log") + "/" +
                Number("SELECT count(*) FROM character_raid_reward_receipts") + "/" + Number("SELECT count(*) FROM account_battle_pass_source_events");
            long Number(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
            void Sql(string sql, params object[] values)
            {
                using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql;
                for (int i = 0; i < values.Length; i++) command.Parameters.AddWithValue("$p" + i, values[i]);
                command.ExecuteNonQuery();
            }
            SqliteConnection Open()
            {
                var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
                db.Open(); return db;
            }
        }
        catch
        {
            Console.WriteLine("Fiendwar reward transaction failure artifacts retained: " + root);
            throw;
        }
        TestFiles.DeleteDirectory(root, recursive: true);
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
                "既有单产物解析合同保持，漩涡改用完整容器展开：" + root.Id);
            That(resolver.ResolveAll(root.Id, _ => 0).SequenceEqual(Expected(root)),
                "22根奖励的一层完整展开包含材料及首个候选，不发源盒：" + root.Id);
        }
        int lower = 0;
        for (int index = 0; index < weights.Length; index++)
        {
            foreach (int roll in new[] { lower, lower + weights[index] - 1 })
            foreach (int target in roots[index + 3].Candidates is { } candidates
                ? Enumerable.Range(0, candidates.Length).SelectMany(choice => new[] { choice * 1000, choice * 1000 + 999 })
                : new[] { 0 })
            {
                int calls = 0;
                int Draw(int bound)
                {
                    int call = calls++;
                    if (call == 1) { That(bound == 10000, "固定奖励展开后仅抽一次原外层奖池"); return roll; }
                    return call == 3 ? target : 0;
                }
                var policy = factory.Create(false, false, Draw);
                var offers = policy.Phases.Single().Rewards.Offers;
                That(offers is [{ Kind: 1, Card: 0 }, { Kind: 0, Card: 0 }] &&
                    offers[0].Grants.SequenceEqual(Expected(roots[0])) &&
                    offers[1].Grants.SequenceEqual(Expected(roots[index + 3], target / 1000)),
                    "真实外层及装备卡片内层区间两端直接冻结全部产物：" + roots[index + 3].Id + "/" + roll + "/" + target);
                That(calls == (roots[index + 3].Candidates is null ? 3 : 4),
                    "每个声明池只抽一次，材料与额外装备卡片不互斥");
                foreach (var offer in offers)
                    That(offer.PreyDisplay.Length == offer.Grants.Length &&
                        offer.PreyDisplay.Zip(offer.Grants).All(pair => pair.First.Matches(pair.Second)),
                        "翻牌展示绑定已冻结的实际奖励数量");
                byte expectedCode = index >= 3 ? (byte)1 : (byte)0;
                That(offers[0].PreyDisplay.All(spec => spec.RawCode == 0) &&
                    offers[1].PreyDisplay.All(spec => spec.RawCode == expectedCode),
                    "固定牌保持code=0，分队牌全部展开项同步squad_item的RawD金牌标记：" + roots[index + 3].Id);
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
                expected.Add(new(roots[0].Item, roots[0].Count));
                if (pcroom) expected.Add(new(roots[1].Item, roots[1].Count));
                if (entitled[member]) expected.Add(new(roots[2].Item, roots[2].Count));
                var fixedOffer = frozen[member].Phases.Single().Rewards.Offers.Single(offer => offer.Kind == 1);
                That(states[member].IsActive(at) == entitled[member] &&
                    fixedOffer.Grants.SequenceEqual(expected) &&
                    fixedOffer.PreyDisplay.All(spec => spec.RawCode == 0),
                    "逐成员礼遇开始包含、到期排除，网吧资格与礼遇独立且固定牌code恒为0：" + member + "/" + pcroom);
            }
        }

        if (!script.Guide)
            foreach (int roll in new[] { 0, 3999, 4000, 9999 })
            {
                int calls = 0;
                var offers = factory.Create(true, false, _ => calls++ == 1 ? roll : 0).Phases.Single().Rewards.Offers;
                That(offers.Single(offer => offer.Kind == 1).Grants.SequenceEqual(new[]
                {
                    new InventoryReward(10308807, 150), new InventoryReward(Particle, roll < 4000 ? 2 : 3)
                }), "网吧原奖池边界保持2或3粒子，与固定150不渝同时冻结：" + roll);
            }
    }

    static IEnumerable<InventoryReward> Expected(Root root, int choice = 0)
    {
        var grants = new List<InventoryReward> { new(root.Item, root.Count) };
        if (root.Candidates is { } candidates) grants.Add(new(candidates[choice], 1));
        return grants.OrderBy(grant => grant.ItemId);
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
