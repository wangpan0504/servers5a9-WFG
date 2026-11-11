using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 真实 SQLite 仓储。额度定义由夹具传入，不读 PVF，也不写死 1300。
internal static class RaidSongQuotaRepositoryTests
{
    static readonly DateTimeOffset Origin = new(2026, 10, 29, 6, 0, 0, TimeSpan.FromHours(8));
    static readonly AccountMaterialDefinition Standard = new(4000, 4000, 1300);
    const string Source = "fixture/song-cap";
    const int Song = RaidSongQuota.ItemId;

    internal static void RunCore()
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "s5a9-song-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ClipDown(root, "裁到0", openingEarned: 1260, laterBalance: 4000, laterEarned: 0, face: 40, settled: 0, battlePass: 0);
            ClipDown(root, "裁到部分", openingEarned: 1200, laterBalance: 0, laterEarned: 1260, face: 100, settled: 40, battlePass: 40);
            Rollback(root);
            Mix(root);
            ConsumeHolding(root);
            WeekChange(root);
            OtherClaim(root);
            Unclaimed(root);
            FailedPhase(root);
            TightDefinition(root);
            SameAccount(root);
        }
        finally { TestFiles.DeleteDirectory(root, recursive: true); }
        Console.WriteLine($"PASS {Count - before} 不渝之咏额度事务");
    }

    static void ClipDown(string root, string name, int openingEarned, int laterBalance, int laterEarned, int face, int settled, int battlePass)
    {
        using var world = new World(root, name, Standard, SongOffer(100));
        world.Seed(0, openingEarned);
        var row = world.Seal(0);
        That(Songs(row.Offers[0].Grants) == face, name + "发牌按当时余额写入资格");
        var replay = world.Repository.CommitLukePhase(world.Pending(0));
        That(Songs(replay.Members.Single(member => member.Character == world.Actor.Character).Offers[0].Grants) == face, name + "阶段回执重放不加发");
        world.Accept(0, replay);
        That(world.MemorySongs(0) == face, name + "受理后内存牌面与已发出的资格相同");
        world.SetBalance(laterBalance, laterEarned);
        var commit = world.Claim(0, row, out var prepared, out var plan, out var snapshot);
        That(!commit.Duplicate && Songs(commit.SettledGrants) == settled && GoldOf(commit.SettledGrants) == 19, name + "领取把正数裁到当时余额，金币不裁");
        world.Remember(commit, 0, prepared);
        world.Expect(name, settled, battlePass, laterBalance + settled, laterEarned + settled, snapshot.Gold + 19);
        if (settled == 0)
        {
            var stamped = world.Reload().Inventory.AccountWallet.Material(Song);
            That(stamped.RaidWeek == world.MaterialWeek && stamped.RaidEarned == laterEarned && stamped.Balance == laterBalance,
                name + "正数裁到0仍盖上当时攻坚周，持有和已获得不加");
        }
        var second = world.Repository.CommitRaidReward(world.Owner, snapshot.Role, prepared, plan, world.Now)!;
        var third = world.Repository.CommitRaidReward(world.Owner, snapshot.Role, prepared, plan, world.Now)!;
        That(second.Duplicate && third.Duplicate && Songs(second.SettledGrants) == settled && world.ReceiptCount() == 1, name + "重复领取幂等");
        world.Expect(name + "重放后", settled, battlePass, laterBalance + settled, laterEarned + settled, snapshot.Gold + 19);
    }

    static void Rollback(string root)
    {
        using var world = new World(root, "回滚", Standard, SongOffer(100));
        world.Seed(0, 1200);
        var row = world.Seal(0);
        world.Accept(0, world.Repository.CommitLukePhase(world.Pending(0)));
        That(Songs(row.Offers[0].Grants) == 100 && world.MemorySongs(0) == 100, "回滚前牌面按当时余额发出100");
        world.SetBalance(0, 1260);
        world.Sql("CREATE TRIGGER song_cap_rollback BEFORE INSERT ON character_raid_reward_receipts BEGIN SELECT RAISE(ABORT,'injected song cap rollback'); END");
        Exception? failed = null;
        try { world.Claim(0, row, out _, out _, out _); }
        catch (Exception error) { failed = error; }
        That(failed is not null && failed.ToString().Contains("injected song cap rollback"), "注入的回执失败中断领取");
        var role = world.Reload();
        That(role.Inventory.AccountWallet.Material(Song).Balance == 0 && role.Inventory.AccountWallet.Material(Song).EarnedBy(role.Id, world.MaterialWeek) == 1260 &&
            CurrencyWallet.Get(role.Inventory, CurrencyKind.Gold) == 0 && world.Mission() == 0 && world.ReceiptCount() == 0 &&
            world.SourceEvents() == 0 && world.StoredSongs(0) == 100 && world.MemorySongs(0) == 100,
            "注入失败回滚资格、余额、金币、回执和战令，内存牌面保持已发出的100");
    }

    static void Mix(string root)
    {
        var offer = Offer((Song, 0), (Song, 25), (0, 19));
        using var world = new World(root, "混合", Standard, offer);
        world.Seed(0, 0);
        var row = world.Seal(0);
        world.Accept(0, world.Repository.CommitLukePhase(world.Pending(0)));
        That(Songs(row.Offers[0].Grants) == 25 && row.Offers[0].Grants[0].Count == 0 && GoldOf(row.Offers[0].Grants) == 19, "未满时0和正数一起发出");
        world.SetBalance(0, 1290);
        var commit = world.Claim(0, row, out _, out _, out var snapshot);
        world.Remember(commit, 0, new PreparedRaidReward(world.Battle.Instance, 0, 1, 0, world.Actor.Character, Source, row.Offers[0].Grants, false, row.Week) { RaidType = RaidKind.Luke });
        That(Songs(commit.SettledGrants) == 10 && commit.SettledGrants[0].Count == 0 && GoldOf(commit.SettledGrants) == 19, "混合牌面只向下裁正数，0和金币保持");
        world.Expect("混合", 10, 10, 10, 1300, snapshot.Gold + 19);
    }

    static void ConsumeHolding(string root)
    {
        using var world = new World(root, "消耗持有", Standard, SongOffer(80), SongOffer(100));
        world.Seed(4000, 0);
        var first = world.Seal(0);
        world.Accept(0, world.Repository.CommitLukePhase(world.Pending(0)));
        That(!first.Eligible || Songs(first.Offers[0].Grants) == 0, "持有满额时一阶段发牌为0");
        That(Songs(first.Offers[0].Grants) == 0 && world.MemorySongs(0) == 0, "持有满额的一阶段牌面和内存都是0");
        world.SetBalance(0, 0);
        world.Advance(0, first);
        var second = world.Seal(1);
        world.Accept(1, world.Repository.CommitLukePhase(world.Pending(1)));
        That(second.Eligible && Songs(second.Offers[0].Grants) == 100 && world.MemorySongs(1) == 100, "消耗持有后二阶段按恢复的额度发出100");
    }

    static void WeekChange(string root)
    {
        using var world = new World(root, "跨周", Standard, SongOffer(80), SongOffer(100));
        world.Seed(0, 1260);
        var first = world.Seal(0);
        world.Accept(0, world.Repository.CommitLukePhase(world.Pending(0)));
        That(Songs(first.Offers[0].Grants) == 40, "跨周前一阶段只发出当时剩余40");
        world.Advance(0, first);
        world.Clock.AdvanceWithoutCallbacks(8 * 24 * 3600);
        var second = world.Seal(1);
        world.Accept(1, world.Repository.CommitLukePhase(world.Pending(1)));
        That(second.Eligible && Songs(second.Offers[0].Grants) == 100 && world.MemorySongs(1) == 100 && world.StoredSongs(0) == 40,
            "跨周后二阶段不沿用旧周已获得，一阶段已发出的40不加发");
    }

    static void OtherClaim(string root)
    {
        var cap = new AccountMaterialDefinition(4000, 4000, 100);
        using var world = new World(root, "另一次领取", cap, SongOffer(80), SongOffer(100));
        world.Seed(0, 0);
        var first = world.Seal(0);
        world.Accept(0, world.Repository.CommitLukePhase(world.Pending(0)));
        That(Songs(first.Offers[0].Grants) == 80, "角色周100时一阶段发出80");
        var commit = world.Claim(0, first, out _, out _, out _);
        That(commit.SettledGrants.IsDefault, "一阶段领取时余额仍够，不再往下裁");
        world.Advance(0, first);
        var second = world.Seal(1);
        world.Accept(1, world.Repository.CommitLukePhase(world.Pending(1)));
        That(Songs(second.Offers[0].Grants) == 20 && world.MemorySongs(1) == 20, "另一次领取入账后，二阶段按剩余20发出");
    }

    static void Unclaimed(string root)
    {
        using var world = new World(root, "漏领", Standard, SongOffer(80), SongOffer(100));
        world.Seed(0, 1200);
        var first = world.Seal(0);
        world.Accept(0, world.Repository.CommitLukePhase(world.Pending(0)));
        That(Songs(first.Offers[0].Grants) == 80, "漏领前一阶段发出80");
        world.Advance(0, first);
        var second = world.Seal(1);
        world.Accept(1, world.Repository.CommitLukePhase(world.Pending(1)));
        That(Songs(second.Offers[0].Grants) == 100 && world.StoredSongs(0) == 80 && world.MemorySongs(1) == 100,
            "一阶段未领取时二阶段仍按完整剩余发出100");
    }

    static void FailedPhase(string root)
    {
        var cap = new AccountMaterialDefinition(4000, 4000, 100);
        using var world = new World(root, "一阶段失败", cap, SongOffer(80), SongOffer(100), minimum: [2, 0]);
        world.Seed(0, 80);
        var first = world.Seal(0);
        world.Accept(0, world.Repository.CommitLukePhase(world.Pending(0)));
        That(!first.Eligible && first.Reason == "phase-participation" && Songs(first.Offers[0].Grants) == 80, "一阶段资格不足时不裁原始80");
        world.Advance(0, first);
        var second = world.Seal(1);
        world.Accept(1, world.Repository.CommitLukePhase(world.Pending(1)));
        That(second.Eligible && Songs(second.Offers[0].Grants) == 20 && world.MemorySongs(1) == 20, "一阶段失败后二阶段只按当时剩余20发出");
    }

    static void TightDefinition(string root)
    {
        var cap = new AccountMaterialDefinition(4000, 4000, 50);
        using var world = new World(root, "定义50", cap, SongOffer(100));
        world.Seed(0, 0);
        var row = world.Seal(0);
        var replay = world.Repository.CommitLukePhase(world.Pending(0));
        That(Songs(row.Offers[0].Grants) == 50 && Songs(replay.Members.Single().Offers[0].Grants) == 50, "角色周50的定义把100裁到50，重放不加发");
        world.Accept(0, replay);
        That(world.MemorySongs(0) == 50 && world.StoredSongs(0) == 50, "内存和资格使用传入定义的50，不是1300");
    }

    // 两个角色在任一领取之前先后密封。牌面按密封当时的同一余额；后领取再按领取当时的余额向下裁。
    static void SameAccount(string root)
    {
        var cap = new AccountMaterialDefinition(100, 80, 50);
        using var world = new World(root, "同账号", cap, SongOffer(100));
        var mate = world.OpenMate(SongOffer(100));
        world.Seed(0, 0);
        int week = AccountMaterialService.RaidWeek(world.Now);
        int face = Math.Min(100, AccountMaterialService.RaidRemaining(AccountMaterialBalance.Empty, world.Actor.Character, cap, week));
        That(face == AccountMaterialService.RaidRemaining(AccountMaterialBalance.Empty, mate.Actor.Character, cap, week) && face == 50,
            "领取前两人剩余相同，角色周50紧于账号周80和持有100");
        var first = world.Seal(0);
        var second = world.Seal(mate, 0);
        That(Songs(first.Offers[0].Grants) == face && Songs(second.Offers[0].Grants) == face,
            "同账号两人先后密封且都还没领，牌面都是当时剩余，不预扣对方");
        var firstCommit = world.Claim(0, first, out _, out _, out _);
        That(firstCommit.SettledGrants.IsDefault, "先领的一笔仍在剩余内，不再往下裁");
        var afterFirst = world.Material();
        int remain = AccountMaterialService.RaidRemaining(afterFirst, mate.Actor.Character, cap, week);
        That(afterFirst.Balance == face && afterFirst.EarnedIn(week) == face && afterFirst.EarnedBy(world.Actor.Character, week) == face &&
            remain < face, "先领入账整张牌面后，后领角色的剩余变少");
        var secondCommit = world.Claim(mate, 0, second, out _, out _, out _);
        int taken = Songs(secondCommit.SettledGrants);
        That(!secondCommit.Duplicate && taken == remain && taken < Songs(second.Offers[0].Grants), "第二笔按当时余额向下裁");
        var end = world.Material();
        int total = face + taken;
        That(end.Balance == total && end.Balance <= cap.HoldingLimit &&
            end.EarnedIn(week) == total && end.EarnedIn(week) <= cap.WeeklyAccountLimit &&
            end.EarnedBy(world.Actor.Character, week) == face && end.EarnedBy(world.Actor.Character, week) <= cap.WeeklyCharacterLimit &&
            end.EarnedBy(mate.Actor.Character, week) == taken && end.EarnedBy(mate.Actor.Character, week) <= cap.WeeklyCharacterLimit,
            "领取后总到账不超过持有和账号周，每个角色不超过角色周");
        var stored = JsonSerializer.Deserialize<RaidPhaseEligibility>(world.PhaseReceipt(mate.Actor.Character))!;
        That(Songs(stored.Offers[0].Grants) == taken && Songs(first.Offers[0].Grants) == face, "后领写回裁后的资格，先领的牌面不加回");
    }

    static RaidRewardOffer SongOffer(int song) => Offer((Song, song), (0, 19));

    static RaidRewardOffer Offer(params (int Item, int Count)[] rows) =>
        new(1, 0, rows.Select(row => new InventoryReward(row.Item, row.Count)).ToImmutableArray(), []);

    static int Songs(ImmutableArray<InventoryReward> grants) => grants.Where(row => row.ItemId == Song).Sum(row => row.Count);

    static int GoldOf(ImmutableArray<InventoryReward> grants) => grants.IsDefault ? -1 : grants.Single(row => row.ItemId == 0).Count;

    sealed class World : IDisposable
    {
        static readonly BattlePassMissionPolicy Policy = new("song-cap-core", Origin, Catalog());
        readonly AccountOwners owners = new();
        public string Database { get; }
        public CharacterRepository Repository { get; }
        public AccountOwners.Lease Owner { get; }
        public LukeRaidClockTests.Clock Clock { get; }
        public GameSession Session { get; }
        public Seat Primary { get; private set; } = null!;
        public RaidActor Actor { get; }
        public AntonRaidBattle Battle { get; }
        public AccountMaterialDefinition Cap { get; }
        public DateTimeOffset Now => Clock.GetUtcNow();
        public int MaterialWeek => AccountMaterialService.RaidWeek(Now);
        readonly Dictionary<int, AntonRaidBattle.LukePhaseSettlement> pending = [];

        public World(string root, string name, AccountMaterialDefinition cap, RaidRewardOffer first, RaidRewardOffer? second = null, int[]? minimum = null)
        {
            Cap = cap;
            Database = Path.Combine(root, name + ".db");
            Repository = new CharacterRepository(Database);
            Repository.ConfigureBattlePassMissions(Policy);
            Owner = owners.Acquire("song-cap", () => { });
            var origin = new Location(32, 4, 10, 10);
            var map = new TownMap("fixture/wait.map", [new(new(0, 0, 20, 20), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(32, 4)] = new(32, 4, 1, [], [], [], origin, map, [], []) },
                origin, origin, new(0, [], []), [], new Dictionary<uint, uint[]>());
            Repository.Create(Owner, new NewCharacter("SongCap", origin, origin) { Level = 90, CompletedQuests = [2903, 4872] });
            Clock = new LukeRaidClockTests.Clock(Origin);
            var channel = new DfoServer.GameWorld.Channels.ChannelEntry(new(2, 20), 32, "[raid]");
            Session = new GameSession(Repository, towns, Owner, entry: channel);
            Session.Select(0);
            Session.MarkReady();
            Actor = Session.CaptureRaidActor(new RaidRegistry())!;
            var offers = second is null ? new[] { first } : new[] { first, second };
            minimum ??= [0, 0];
            var policies = offers.Select((offer, phase) => new RaidPhaseRewardPolicy(Source, [offer], new(0, (byte)phase, 0, 0, 2, 0))).ToImmutableArray();
            if (policies.Length == 1)
                policies = policies.Add(new RaidPhaseRewardPolicy(Source, [first], new(0, 1, 0, 0, 2, 0)));
            var completion = new LukeRaidCompletionPolicy(Source, 2, new(DayOfWeek.Sunday, new TimeOnly(6, 0), TimeSpan.FromHours(8)),
                policies.Select((policy, index) => new LukePhaseCompletionPolicy(minimum[index], 2, policy)).ToImmutableArray(),
                LukeParticipationCutoff.TerminalCommittedFacts);
            Battle = new AntonRaidBattle(AntonRaidBattleTests.Script(10, 20), kind: RaidKind.Luke, clock: Clock, completion: completion,
                memberRewards: ImmutableDictionary<ushort, ImmutableArray<RaidPhaseRewardPolicy>>.Empty.Add(Actor.Character, policies),
                undefeatedSongLimit: cap);
            var binding = Session.PrepareRaidCombatStart(Actor, Battle);
            That(binding is not null, name + "绑定战斗");
            binding!.Install();
            Sql("INSERT INTO character_raid_runs(character_id,account,instance_id,raid_type,source,admitted_at,state) VALUES($p0,$p1,$p2,1,$p3,$p4,0)",
                Actor.Character, Owner.Account, Battle.Instance.ToString("N"), Source, Origin.ToUnixTimeSeconds());
            _ = Repository.ReadBattlePassMissions(Owner, Origin);
            Primary = new Seat(Session, Actor, Battle, 0, pending, Runs);
        }

        public Seat OpenMate(RaidRewardOffer offer)
        {
            var origin = new Location(32, 4, 10, 10);
            var map = new TownMap("fixture/wait.map", [new(new(0, 0, 20, 20), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(32, 4)] = new(32, 4, 1, [], [], [], origin, map, [], []) },
                origin, origin, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var created = Repository.Create(Owner, new NewCharacter("SongMate", origin, origin) { Level = 90, CompletedQuests = [2903, 4872] });
            var channel = new DfoServer.GameWorld.Channels.ChannelEntry(new(2, 20), 32, "[raid]");
            var session = new GameSession(Repository, towns, Owner, entry: channel);
            session.Select(created.Slot);
            session.MarkReady();
            var actor = session.CaptureRaidActor(new RaidRegistry())!;
            That(actor.Character == created.Id && created.Slot != 0, "第二角色在同一账号的另一个槽");
            var policies = ImmutableArray.Create(
                new RaidPhaseRewardPolicy(Source, [offer], new(0, 0, 0, 0, 2, 0)),
                new RaidPhaseRewardPolicy(Source, [offer], new(0, 1, 0, 0, 2, 0)));
            var completion = new LukeRaidCompletionPolicy(Source, 2, new(DayOfWeek.Sunday, new TimeOnly(6, 0), TimeSpan.FromHours(8)),
                policies.Select(policy => new LukePhaseCompletionPolicy(0, 2, policy)).ToImmutableArray(),
                LukeParticipationCutoff.TerminalCommittedFacts);
            var battle = new AntonRaidBattle(AntonRaidBattleTests.Script(10, 20), kind: RaidKind.Luke, clock: Clock, completion: completion,
                memberRewards: ImmutableDictionary<ushort, ImmutableArray<RaidPhaseRewardPolicy>>.Empty.Add(actor.Character, policies),
                undefeatedSongLimit: Cap);
            var binding = session.PrepareRaidCombatStart(actor, battle);
            That(binding is not null, "第二角色绑定战斗");
            binding!.Install();
            Sql("INSERT INTO character_raid_runs(character_id,account,instance_id,raid_type,source,admitted_at,state) VALUES($p0,$p1,$p2,1,$p3,$p4,0)",
                actor.Character, Owner.Account, battle.Instance.ToString("N"), Source, Origin.ToUnixTimeSeconds());
            return new Seat(session, actor, battle, created.Slot, [], []);
        }

        public void Seed(int balance, int earned) => SetBalance(balance, earned, insert: true);

        public void SetBalance(int balance, int earned, bool insert = false)
        {
            int week = earned == 0 ? 0 : AccountMaterialService.RaidWeek(Origin);
            string characters = earned == 0 ? "{}" : JsonSerializer.Serialize(ImmutableSortedDictionary<ushort, int>.Empty.Add(Actor.Character, earned));
            Sql("INSERT INTO account_wallets(account) VALUES($p0) ON CONFLICT(account) DO NOTHING", Owner.Account);
            string sql = insert
                ? "INSERT INTO account_material_balances(account,item_id,balance,raid_week,raid_earned,character_earned) VALUES($p0,$p1,$p2,$p3,$p4,$p5)"
                : "UPDATE account_material_balances SET balance=$p2,raid_week=$p3,raid_earned=$p4,character_earned=$p5 WHERE account=$p0 AND item_id=$p1";
            That(Sql(sql, Owner.Account, Song, balance, week, earned, characters) == 1, "材料余额写入一行");
        }

        public RaidPhaseEligibility Seal(int phase) => Seal(Primary, phase);

        public RaidPhaseEligibility Seal(Seat seat, int phase)
        {
            uint dungeon = phase == 0 ? 10u : 20u;
            var run = Guid.NewGuid();
            var identity = new DungeonRunIdentity(seat.Actor.Character, 0, 1, run, 1);
            using (var reservation = seat.Battle.ReserveDungeon(new(seat.Battle.Instance, phase, 1, RaidKind.Luke), dungeon, [(seat.Actor, identity)])!)
                reservation.Install();
            var clear = seat.Battle.CompleteLukeDungeon([(seat.Actor, new DungeonClearFact(identity, dungeon, [Guid.NewGuid()])
                { ClearMap = new DungeonMap("boss", new(0, 0), 77, "fixture/boss.map") })]);
            That(clear is not null, "阶段" + phase + "清图");
            var candidate = seat.Battle.PendingLukeSettlement();
            That(candidate is not null && candidate.Phase == phase && candidate.SongLimit == Cap, "阶段" + phase + "候选带着传入的额度定义");
            seat.Pending[phase] = candidate!;
            seat.Runs[phase] = run;
            return Repository.CommitLukePhase(candidate!).Members.Single(member => member.Character == seat.Actor.Character);
        }

        readonly Dictionary<int, Guid> Runs = [];

        public AntonRaidBattle.LukePhaseSettlement Pending(int phase) => pending[phase];

        public void Accept(int phase, LukePhaseCommit commit)
        {
            Battle.AcceptLukeSettlement(pending[phase], commit);
            Battle.ReleaseDungeon(Actor, Runs[phase]);
        }

        public void Advance(int phase, RaidPhaseEligibility row)
        {
            // 全员无资格时，释放 Run 会在受理里直接推进，不能再确认一次。
            if (Battle.View.Phase != phase)
            {
                That(!row.Eligible && Battle.View.Phase == phase + 1, "无资格阶段释放后进入下一阶段");
                return;
            }
            if (row.Eligible)
            {
                var prepared = Face(phase, row);
                That(Battle.ConfirmReward(Actor, prepared), "阶段" + phase + "确认已发出的牌面");
            }
            var moved = Battle.AdvanceAfterRewards(Battle.Instance, Battle.View.Revision);
            That(moved is not null && Battle.View.Phase == phase + 1, "进入下一阶段");
        }

        public RaidRewardCommit Claim(int phase, RaidPhaseEligibility row, out PreparedRaidReward prepared, out InventoryPlan plan, out ClaimSnapshot snapshot) =>
            Claim(Primary, phase, row, out prepared, out plan, out snapshot);

        public RaidRewardCommit Claim(Seat seat, int phase, RaidPhaseEligibility row, out PreparedRaidReward prepared, out InventoryPlan plan, out ClaimSnapshot snapshot)
        {
            var role = Repository.LoadCharacter(Owner, seat.Slot);
            prepared = Face(seat, phase, row);
            int song = Songs(prepared.Grants);
            var before = role.Inventory;
            long gold = CurrencyWallet.Get(before, CurrencyKind.Gold);
            var material = before.AccountWallet.Material(Song);
            var inflated = material with { Balance = checked(material.Balance + song) };
            var after = CurrencyWallet.Set(before with { AccountWallet = before.AccountWallet.SetMaterial(Song, inflated) }, CurrencyKind.Gold, gold + 19);
            plan = new InventoryPlan(before, after, "raid_reward", new InventoryCharacterPrecondition(role))
            {
                AccountMaterialRaidWeek = AccountMaterialService.RaidWeek(Now),
                AccountMaterialRaidDefinitions = ImmutableDictionary<int, AccountMaterialDefinition>.Empty.Add(Song, Cap)
            };
            snapshot = new(role, gold);
            return Repository.CommitRaidReward(Owner, role, prepared, plan, Now) ?? throw new InvalidDataException("领取未通过资格复验");
        }

        public void Remember(RaidRewardCommit commit, int phase, PreparedRaidReward prepared)
        {
            if (commit.SettledGrants.IsDefault || commit.SettledGrants.SequenceEqual(prepared.Grants)) return;
            var offer = RaidSongQuota.ApplyGrants(Battle.RewardPolicy(Actor, phase)!.Offers.Single(row => row.Kind == prepared.Kind), commit.SettledGrants);
            Battle.ApplySettledSongOffer(Actor, phase, offer);
        }

        public void Expect(string name, int songs, int battlePass, int balance, int earned, long gold)
        {
            var role = Reload();
            var material = role.Inventory.AccountWallet.Material(Song);
            int week = earned == 0 ? 0 : AccountMaterialService.RaidWeek(Origin);
            That(material.Balance == balance && (earned == 0 ? material.RaidEarned == 0 : material.EarnedBy(role.Id, week) == earned) &&
                CurrencyWallet.Get(role.Inventory, CurrencyKind.Gold) == gold && Mission() == battlePass &&
                worldStored(songs) && MemorySongs(0) == songs && ReceiptCount() == 1 && SourceEvents() == (battlePass > 0 ? 1 : 0),
                name + "资格、回执、余额、战令和内存牌面一致");

            bool worldStored(int expected)
            {
                var eligibility = JsonSerializer.Deserialize<RaidPhaseEligibility>(Text("SELECT phase0_receipt FROM character_raid_runs"))!;
                var stored = JsonSerializer.Deserialize<PreparedRaidReward>(Text("SELECT plan_json FROM character_raid_reward_receipts"))!;
                return Songs(eligibility.Offers[0].Grants) == expected && GoldOf(eligibility.Offers[0].Grants) == 19 &&
                    Songs(stored.Grants) == expected && GoldOf(stored.Grants) == 19;
            }
        }

        public int MemorySongs(int phase) => Songs(Battle.RewardPolicy(Actor, phase)!.Offers[0].Grants);

        public int StoredSongs(int phase)
        {
            string column = phase == 0 ? "phase0_receipt" : "phase1_receipt";
            var eligibility = JsonSerializer.Deserialize<RaidPhaseEligibility>(Text($"SELECT {column} FROM character_raid_runs"))!;
            return Songs(eligibility.Offers[0].Grants);
        }

        public Character Reload() => Repository.LoadCharacter(Owner, 0);

        public AccountMaterialBalance Material() => Reload().Inventory.AccountWallet.Material(Song);

        public string PhaseReceipt(ushort character)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT phase0_receipt FROM character_raid_runs WHERE character_id=$id";
            command.Parameters.AddWithValue("$id", character);
            return (string)command.ExecuteScalar()!;
        }

        public long Mission() => Repository.ReadBattlePassMissions(Owner, Origin)!.Slots
            .Single(row => BattlePassUndefeatedSongSource.Matches(row.Instance.Key.Category, row.Instance.Definition)).Count;

        public long ReceiptCount() => Number("SELECT count(*) FROM character_raid_reward_receipts");

        public long SourceEvents() => Number("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='raid_undefeated_song_acquisition'");

        PreparedRaidReward Face(int phase, RaidPhaseEligibility row) => Face(Primary, phase, row);

        PreparedRaidReward Face(Seat seat, int phase, RaidPhaseEligibility row) =>
            new(seat.Battle.Instance, phase, 1, 0, seat.Actor.Character, Source, row.Offers[0].Grants, phase == 1, row.Week) { RaidType = RaidKind.Luke };

        public int Sql(string sql, params object[] values)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            for (int i = 0; i < values.Length; i++) command.Parameters.AddWithValue("$p" + i, values[i]);
            return command.ExecuteNonQuery();
        }

        long Number(string sql)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }

        string Text(string sql)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            return (string)command.ExecuteScalar()!;
        }

        SqliteConnection Open()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            return db;
        }

        public void Dispose() => Owner.Dispose();

        public sealed class Seat(GameSession session, RaidActor actor, AntonRaidBattle battle, ushort slot,
            Dictionary<int, AntonRaidBattle.LukePhaseSettlement> pending, Dictionary<int, Guid> runs)
        {
            public GameSession Session { get; } = session;
            public RaidActor Actor { get; } = actor;
            public AntonRaidBattle Battle { get; } = battle;
            public ushort Slot { get; } = slot;
            public Dictionary<int, AntonRaidBattle.LukePhaseSettlement> Pending { get; } = pending;
            public Dictionary<int, Guid> Runs { get; } = runs;
        }

        static BattlePassCatalog Catalog()
        {
            var source = new List<object> { "[ui path]", "UI/SongCapFixture.xui" };
            for (int id = 1; id <= 70; id++)
            {
                if (id == 48)
                {
                    source.AddRange(new object[] { "[mission]", 48, 300, 1000, 43, "获得不渝之咏", -1, "[/mission]" });
                    continue;
                }
                source.AddRange(new object[] { "[mission]", id, 1, 100, 777, "合成任务夹具", -1, "[/mission]" });
            }
            source.Add("[daily mission]");
            for (int day = 0; day < 7; day++) source.AddRange(new object[] { 1, 2, 3, 4 });
            source.AddRange(new object[] { "[/daily mission]", "[weekly mission]", 48, 26, 27, 28, "[/weekly mission]", "[season mission]" });
            source.AddRange(Enumerable.Range(51, 18).Cast<object>());
            source.Add("[/season mission]");
            for (int level = 1; level <= 80; level++) source.AddRange(new object[] { "[level reward]", level, 999001, 1, 0, "[/level reward]" });
            source.Add("[exp table]");
            for (int level = 1; level <= 80; level++) source.AddRange(new object[] { level, level == 80 ? 0 : 1300 });
            source.AddRange(new object[] { "[/exp table]", "[tier table]", 1, 80, "[/tier table]", "[postal]", 1, 2, 3, 4, "[/postal]",
                "[package index]", 10, "[/package index]", "[certificate index]", 11, "[/certificate index]",
                "[buy restrict code]", 12, "[/buy restrict code]", "[golden mission exp ratio]", 3, "[cera consumption exp percentage]", 50 });
            var strings = new List<string>();
            PvfToken Token(object value)
            {
                if (value is int number) return new(0, number);
                string text = (string)value;
                strings.Add(text);
                return new(text.StartsWith('[') ? (byte)3 : (byte)6, strings.Count - 1);
            }
            return BattlePassCatalog.Parse(source.Select(Token).ToArray(), index => strings[index]);
        }
    }

    readonly record struct ClaimSnapshot(Character Role, long Gold);
}
