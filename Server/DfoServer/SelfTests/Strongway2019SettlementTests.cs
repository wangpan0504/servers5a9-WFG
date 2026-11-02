using DfoServer.Game.Characters;
using DfoServer.Persistence;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Network.Handlers;
using DfoServer.Network;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.Game.Strongway2019;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;
using System.Text.Json;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class Strongway2019SettlementTests
{
    public static void RunResources(PvfArchive pvf, TownCatalog towns, ItemResources items)
    {
        RunSimpleResources(pvf, towns, items);
        RunHeroResources(pvf, towns, items);
    }

    public static void RunSimpleResources(PvfArchive pvf, TownCatalog towns, ItemResources items) =>
        Run(pvf, towns, items, 5359);

    public static void RunHeroResources(PvfArchive pvf, TownCatalog towns, ItemResources items) =>
        Run(pvf, towns, items, 5363);

    static void Run(PvfArchive pvf, TownCatalog towns, ItemResources items, uint dungeonId)
    {
        int before = Count;
        var growth = CharacterGrowthCatalog.Load(pvf);
        var dungeons = DungeonCatalog.Load(pvf);
        var rooms = DungeonRoomCatalog.Load(pvf);
        var drops = DungeonMonsterDropCatalog.Load(pvf);
        var kills = DungeonKillExperienceCatalog.Load(pvf);
        var clear = DungeonClearExperienceCatalog.Load(pvf);
        int draws = 0;
        var cards = new DungeonCardRewardPolicy(Testing.SelfTestCatalogs.DropRules(pvf), items.DungeonDropItems,
            items.Items, items.GoldLimits, items.EpicPieces, _ => { draws++; return 0; },
            prices: DungeonCardPriceCatalog.Load(pvf));
        using var f = new Strongway2019MatchingTests.Fixture(towns, new(pvf), growth);
        var team = f.Team();
        var service = new PartyDungeonService(f.Parties, f.Repository);
        That(service.BeginSelection(team[0]).Length == 4, "首期普通结算使用真实四人选图");
        var entered = service.Enter(team[0], dungeonId, 2, dungeons, rooms, drops, items.Items, items.EpicPieces, null);
        That(entered.Length == 4 && entered.All(notice => notice.Scene is
            { Entry.DisableMonsterAndClearExperience: true, Entry.DisableClearReward: true }),
            "首期普通结算保留真实DGN禁用经验和普通奖励标记：" + dungeonId);
        CompleteRoute(team, growth, kills);
        var actors = team.Select(session => session.Selected!.Id).ToArray();
        foreach (var session in team)
        {
            var pending = session.PrepareStrongway2019Reward();
            That(pending is { Fact: not null } &&
                session.CommitStrongway2019Reward(pending, items.Items, f.Repository) is { Commit.Granted: true },
                "首期真实终点先提交本模式活动邮件：" + dungeonId);
            var previous = session.Selected!;
            byte[] body = [99, 99];
            var result = session.SettleFormalDungeon(previous.Id, 99, 99, body, dungeons, clear, growth, cards,
                reportMembers: actors);
            That(result.Outcome == DungeonRunOutcome.Accepted && result.Receipt is not null,
                "首期真实终点接受普通结果报告，资源禁用经验不能阻断结算：" + dungeonId);
            bool inventoryEqual = JsonSerializer.Serialize(session.Selected!.Inventory) == JsonSerializer.Serialize(previous.Inventory);
            bool learningEqual = JsonSerializer.Serialize(session.Selected.Learning) == JsonSerializer.Serialize(previous.Learning);
            That(result.Reward is { TotalExperience: 0 } && result.GainedExperience == 0 && result.ProficiencyGained == 0,
                "首期结果不发放普通经验及熟练度：" + dungeonId);
            That(result.Card is null && result.Options is null && result.BlackDiamond is null && draws == 0,
                "首期结果不抽取普通金币、装备及会员牌：" + dungeonId);
            That(result.Inventory is null, "首期结果没有普通奖励库存回执：" + dungeonId);
            That(inventoryEqual && learningEqual, "首期结果完整库存和学习内容保持相同：" + dungeonId);
            That(session.SettleFormalDungeon(previous.Id, 99, 99, body, dungeons, clear, growth, cards,
                    reportMembers: actors).Outcome == DungeonRunOutcome.Duplicate && draws == 0 &&
                session.FormalDungeonResultFocus() is not null,
                "重复首期结果只读原回执且原结算菜单可用：" + dungeonId);
        }
        using (var db = new SqliteConnection("Data Source=" + f.DatabasePath + ";Pooling=False"))
        {
            db.Open();
            using var query = db.CreateCommand();
            query.CommandText = "SELECT (SELECT count(*) FROM character_dungeon_card_rewards) + " +
                "(SELECT count(*) FROM character_dungeon_card_options)";
            That(Convert.ToInt64(query.ExecuteScalar()) == 0, "首期终点不持久保存普通翻牌资格：" + dungeonId);
        }
        That(team[0].PartyDungeon!.ResultReturn(team[0]).Length == 4 && team.All(session => session.Phase == SessionPhase.Town),
            "首期普通结果后原四人返城事务可完成：" + dungeonId);
        Console.WriteLine($"PASS {Count - before} 首期{dungeonId}真实终点零普通奖励与结果返城");
    }


    public static async Task RunRewardRetryResourcesAsync(PvfArchive pvf, TownCatalog towns, ItemResources items)
    {
        int before = Count;
        var growth = CharacterGrowthCatalog.Load(pvf);
        var dungeons = DungeonCatalog.Load(pvf);
        var rooms = DungeonRoomCatalog.Load(pvf);
        var drops = DungeonMonsterDropCatalog.Load(pvf);
        var kills = DungeonKillExperienceCatalog.Load(pvf);
        var clear = DungeonClearExperienceCatalog.Load(pvf);
        var stats = new CharacterStatComputer(CharacterCatalog.Load(pvf, items.Items));
        foreach (uint dungeonId in new uint[] { 5359, 5363 })
        {
            using var f = new Strongway2019MatchingTests.Fixture(towns, new(pvf), growth);
            var team = f.Team();
            var service = new PartyDungeonService(f.Parties, f.Repository);
            That(service.BeginSelection(team[0]).Length == 4 &&
                service.Enter(team[0], dungeonId, 2, dungeons, rooms, drops, items.Items, items.EpicPieces, null).Length == 4,
                "奖励重试回归先沿正常四人入口安装简单或英雄实例：" + dungeonId);
            CompleteRoute(team, growth, kills);
            var session = team[0];
            var actor = session.CaptureSocialActor()!;
            using var log = new ServerLog(Path.Combine(Path.GetDirectoryName(f.DatabasePath)!, "logs"));
            using var stop = new CancellationTokenSource();
            using var output = new MemoryStream();
            await using var sender = new GameConnectionSender(actor.Owner, output, stop, 0, (_, _, _, _, _) => { });
            var context = new GameConnectionContext(new(1598, System.Net.IPAddress.Loopback, output, stop), actor.Owner, session, sender);
            var store = new RewardBusyOnce(f.Repository);
            var rewards = new Strongway2019RewardProjection(items.Items, store, log,
                new Strongway2019ProfileProjection(f.Repository, 5, f.Repository));
            context.Strongway2019 = new Strongway2019Projection(rewards, clock: f.Clock);
            var selectedBefore = session.Selected!;
            Reject<SqliteException>(() => rewards.Commit(context),
                "真实终点后的首份活动奖励遇SQLITE_BUSY，尚未提交且保留原准备");
            var frozen = store.Last!;
            That(f.Repository.LoadMailbox(actor.Owner, actor.Character.Id, frozen.Created).Entries.IsEmpty &&
                session.PrepareStrongway2019Reward() is not null,
                "奖励忙锁失败无邮件，原Run仍可重试");
            var commands = new GameCommandRegistry();
            new DungeonSettlementHandler(log, dungeons, clear, growth, new(towns, stats, null, items.EpicPieces),
                rooms, drops, items.Items, new(stats), pieces: items.EpicPieces).Register(commands);
            commands.Seal();
            var body = DungeonSettlementProtocolTests.Report(team.Select(member => (member.Selected!.Id, (byte)99)).ToArray());
            That(await commands.DispatchAsync(context, new Packet(1, (ushort)CmdPacketType.SET_PLAY_RESULT, body)) == CommandResult.Handled,
                "忙锁结束后的正常SET_PLAY_RESULT仍完成本角色结算");
            var mails = f.Repository.LoadMailbox(actor.Owner, actor.Character.Id, frozen.Created).Entries;
            Console.WriteLine($"strongway_reward_retry dungeon={dungeonId} calls={store.Calls} selectedChanged={!ReferenceEquals(session.Selected, selectedBefore)} mails={mails.Length}");
            That(store.Calls == 2 && ReferenceEquals(store.Last, frozen) && mails.Length == 1 &&
                mails[0].Attachments is [{ Item.Core.Count: 1 }] && mails[0].Attachments[0].Item.Core.ItemId == (dungeonId == 5359 ? 490012735 : 490012736),
                "正常结算替换Selected之前必须重试原冻结奖励，保留唯一完整735/736邮件");
            That(!ReferenceEquals(session.Selected, selectedBefore) && session.FormalDungeonResultFocus() is not null,
                "成功重试不阻断原结算快照更新及正常结果菜单");
            var restored = new CharacterRepository(f.DatabasePath);
            var replay = restored.CommitStrongway2019Reward(actor.Owner, frozen);
            That(replay is { Granted: true, Duplicate: true } && replay.MailId == mails[0].Id &&
                restored.ReadStrongway2019RewardState(actor.Owner, Strongway2019RewardPeriod.At(DateTimeOffset.FromUnixTimeSeconds(frozen.Created)),
                    Strongway2019RewardBucket.Challenge) == new Strongway2019RewardState(1, 1),
                "新Repository恢复原实例唯一奖励及账号日周额度");
            That(await commands.DispatchAsync(context, new Packet(1, (ushort)CmdPacketType.SET_PLAY_RESULT, body)) == CommandResult.Handled &&
                store.Calls == 2 && restored.LoadMailbox(actor.Owner, actor.Character.Id, frozen.Created).Entries.Length == 1,
                "重复正常结果报告不重抽、不重入奖励存储、不重复发邮件");
        }
        Console.WriteLine($"PASS {Count - before} 首期奖励忙锁后正常结算重试与恢复");
    }

    sealed class RewardBusyOnce(IStrongway2019RewardStore inner) : IStrongway2019RewardStore
    {
        public int Calls { get; private set; }
        public Strongway2019RewardPlan? Last { get; private set; }
        public Strongway2019RewardState ReadStrongway2019RewardState(DfoServer.Game.Accounts.AccountOwners.Lease owner,
            Strongway2019RewardPeriod period, Strongway2019RewardBucket bucket) => inner.ReadStrongway2019RewardState(owner, period, bucket);
        public Strongway2019RewardCommit CommitStrongway2019Reward(DfoServer.Game.Accounts.AccountOwners.Lease owner, Strongway2019RewardPlan plan)
        {
            Last = plan;
            if (++Calls == 1) throw new SqliteException("controlled strongway reward busy", 5);
            return inner.CommitStrongway2019Reward(owner, plan);
        }
    }

    static void CompleteRoute(GameSession[] team, CharacterGrowthCatalog growth, DungeonKillExperienceCatalog kills)
    {
        var instance = team[0].PartyDungeon!;
        foreach (var session in team) instance.CompleteLoading(session);
        var current = team[0].CurrentFormalDungeonScene()!;
        var target = current.Entry.BossMap.Position;
        var from = new Dictionary<DungeonPoint, DungeonPoint?> { [current.Entry.StartMap.Position] = null };
        var queue = new Queue<DungeonPoint>();
        queue.Enqueue(current.Entry.StartMap.Position);
        while (queue.TryDequeue(out var point) && !from.ContainsKey(target))
        foreach (var link in current.Entry.Maze.StaticTopology.Links!)
        {
            DungeonPoint? next = link.First == point ? link.Second : link.Second == point ? link.First : null;
            if (next is not { } adjacent || from.ContainsKey(adjacent)) continue;
            from.Add(adjacent, point);
            queue.Enqueue(adjacent);
        }
        That(from.ContainsKey(target), "首期结算回归沿完整真实路线抵达终点");
        var path = new Stack<DungeonPoint>();
        for (var point = target; from[point] is { } parent; point = parent) path.Push(point);
        while (true)
        {
            foreach (var monster in current.Monsters.Monsters.Where(row => row.IsBlocking).OrderBy(row => row.SpawnType == 3).ToArray())
            {
                if (!current.Snapshot.LivingMonsters.Contains(monster.ObjectKey)) continue;
                var observation = FormalDungeonSessionTests.Observation(current, monster);
                if (monster.SpawnType == 3) observation = observation with { RewriteFlag = 1, Unknown18 = 9000, Unknown26 = 30 };
                var notices = instance.ObserveDeath(team[0], monster.ObjectKey,
                    session => session.ObserveFormalDungeonDeath(observation, kills, growth));
                That(notices.Count(notice => notice.Kind == PartyDungeonNoticeKind.Death &&
                    notice.Death?.Outcome == DungeonRunOutcome.Accepted) == 4,
                    "首期结算回归的四个原Run提交真实死亡：" + monster.MonsterId);
                current = team[0].CurrentFormalDungeonScene()!;
            }
            if (!path.TryPop(out var next)) break;
            That(instance.Move(team[0], checked((byte)next.X), checked((byte)next.Y), 0).Length == 4,
                "首期结算回归沿原四人移动推进");
            foreach (var session in team) instance.CompleteLoading(session);
            current = team[0].CurrentFormalDungeonScene()!;
        }
        That(team.All(session => session.Phase == SessionPhase.Settlement), "首期结算回归由真实终点进入结果阶段");
    }
}
