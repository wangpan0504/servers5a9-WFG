using System.Net;
using System.Buffers.Binary;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Strongway2019;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Protocol;
using DfoServer.Network.Projection;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class Strongway2019DungeonHandlerTests
{
    public static async Task RunResourcesAsync(PvfArchive pvf, TownCatalog towns, ItemResources items)
    {
        await RunEntryFeedbackResourcesAsync(pvf, towns, items);
        int before = Count;
        var activity = new Strongway2019Catalog(pvf);
        using var f = new Strongway2019MatchingTests.Fixture(towns, activity);
        var first = f.Team(); var second = f.Team();
        var matching = new Strongway2019MatchingService(f.Parties, activity, f.Clock);
        var ticket = matching.Queue(first[0], first.Select(session => session.PrepareStrongway2019Admission(5360, true)!).ToArray())!;
        matching.Queue(second[0], second.Select(session => session.PrepareStrongway2019Admission(5360, true)!).ToArray());
        var pair = matching.ReserveNext(ticket)!;
        var dungeons = DungeonCatalog.Load(pvf); var rooms = DungeonRoomCatalog.Load(pvf);
        var entered = new Strongway2019MatchingEntryService(matching, f.Parties, f.Repository, f.Repository, f.Clock)
            .Enter(pair, dungeons, rooms, DungeonMonsterDropCatalog.Load(pvf), items.Items, items.EpicPieces)!;
        That(entered is not null, "真实MOVE处理器回归先安装八人原Run");
        foreach (var team in new[] { first, second })
            foreach (var session in team) team[0].PartyDungeon!.CompleteLoading(session);
        var competition = entered!.Match.Competition;
        foreach (int phase in new[] { 3, 4 })
        foreach (var session in new[] { first[0], second[0] })
        {
            var run = session.CaptureStrongway2019Run(session.PartyDungeon!)!;
            competition.ConfirmOpening(run, run.Scene.Identity, phase);
        }
        using var log = new ServerLog(Path.Combine(Path.GetTempPath(), "strongway-dungeon-handler-" + Guid.NewGuid().ToString("N")));
        var stats = new CharacterStatComputer(CharacterCatalog.Load(pvf, items.Items));
        var town = new TownProjection(towns, stats, null, items.EpicPieces);
        var handler = new DungeonHandler(log, dungeons, rooms, CharacterGrowthCatalog.Load(pvf),
            DungeonKillExperienceCatalog.Load(pvf), town, new CharacterLevelProjection(stats),
            strongwayRoute: new Strongway2019RouteService(activity, dungeons, rooms));
        var registry = new GameCommandRegistry(); handler.Register(registry); registry.Seal();
        using var stop = new CancellationTokenSource();
        using var output = new MemoryStream();
        var actor = first[0].CaptureSocialActor()!;
        await using var sender = new GameConnectionSender(actor.Owner, output, stop, 0, (_, _, _, _, _) => { });
        var context = new GameConnectionContext(new(1526, IPAddress.Loopback, output, stop), actor.Owner, first[0], sender);
        var body = new byte[100]; body[0] = 1;
        That(await registry.DispatchAsync(context, new Packet(1, (ushort)CmdPacketType.MOVE_MAP, body)) == CommandResult.Handled &&
            first.All(session => session.Phase == DfoServer.Game.Session.SessionPhase.Loading &&
                session.CurrentFormalDungeonScene()!.Snapshot.CurrentMap.Position == new DungeonPoint(1, 0)),
            "真实Dispatcher持本队门与本人命令门时MOVE仍取得其余原连接门，不能重复取已由Handler占住的peer门");
        Console.WriteLine($"PASS {Count - before} 首期真实DungeonHandler与分派门下路线推进");
    }

    public static async Task RunProgressResourcesAsync(PvfArchive pvf, TownCatalog towns, ItemResources items)
    {
        int before = Count;
        foreach (int members in new[] { 1, 4 })
            await RunProgressTeamAsync(pvf, towns, items, members);
        Console.WriteLine($"PASS {Count - before} 首期一人及四人真实Loaded进度、配置目标CMD39血条与完整终点");
    }

    static async Task RunProgressTeamAsync(PvfArchive pvf, TownCatalog towns, ItemResources items, int members)
    {
        var activity = new Strongway2019Catalog(pvf);
        var growth = CharacterGrowthCatalog.Load(pvf);
        using var f = new Strongway2019MatchingTests.Fixture(towns, activity, growth);
        var first = f.Team(count: members); var second = f.Team(count: members); var sessions = first.Concat(second).ToArray();
        var matching = new Strongway2019MatchingService(f.Parties, activity, f.Clock);
        var ticket = matching.Queue(first[0], first.Select(session => session.PrepareStrongway2019Admission(5360, true)!).ToArray())!;
        matching.Queue(second[0], second.Select(session => session.PrepareStrongway2019Admission(5360, true)!).ToArray());
        var dungeons = DungeonCatalog.Load(pvf); var rooms = DungeonRoomCatalog.Load(pvf);
        var drops = DungeonMonsterDropCatalog.Load(pvf);
        var entered = new Strongway2019MatchingEntryService(matching, f.Parties, f.Repository, f.Repository, f.Clock)
            .Enter(matching.ReserveNext(ticket)!, dungeons, rooms, drops, items.Items, items.EpicPieces)!;
        using var log = new ServerLog(Path.Combine(Path.GetTempPath(), "strongway-progress-handler-" + Guid.NewGuid().ToString("N")));
        var stats = new CharacterStatComputer(CharacterCatalog.Load(pvf, items.Items));
        var town = new TownProjection(towns, stats, null, items.EpicPieces);
        var profiles = new Strongway2019ProfileProjection(f.Repository, activity.DungeonStartDelay);
        var rewardProjection = new Strongway2019RewardProjection(items.Items, f.Repository, log, profiles);
        var projection = new Strongway2019Projection(rewardProjection, matching, f.Clock, activity);
        var handler = new DungeonHandler(log, dungeons, rooms, growth, DungeonKillExperienceCatalog.Load(pvf),
            town, new CharacterLevelProjection(stats), drops, items.Items, items.EpicPieces,
            partyDungeons: new(f.Parties, f.Repository), partyStats: stats, parties: f.Parties,
            strongwayRoute: new(activity, dungeons, rooms), strongwayMatching: matching,
            strongwayProjection: projection, strongwayRewards: rewardProjection);
        var registry = new GameCommandRegistry(); handler.Register(registry);
        new Strongway2019Handler(matching, projection, catalog: activity, clock: f.Clock).Register(registry); registry.Seal();
        var stops = sessions.Select(_ => new CancellationTokenSource()).ToArray();
        var outputs = sessions.Select(_ => new MemoryStream()).ToArray();
        var frames = sessions.Select(_ => new List<(byte Kind, ushort Opcode, byte[] Body)>()).ToArray();
        var senders = sessions.Select((session, i) => new GameConnectionSender(session.CaptureSocialActor()!.Owner,
            outputs[i], stops[i], 0, (kind, opcode, _, body, _) => frames[i].Add((kind, opcode, body.ToArray())))).ToArray();
        var contexts = sessions.Select((session, i) => new GameConnectionContext(new(i, IPAddress.Loopback, outputs[i], stops[i]),
            session.CaptureSocialActor()!.Owner, session, senders[i])).ToArray();
        try
        {
            for (int i = 0; i < sessions.Length; i++)
            {
                int index = i;
                handler.PreparePartyContext(contexts[i]);
                sessions[i].Strongway2019NoticeArrived = notice => projection.DeliverAsync(contexts[index], notice);
            }
            await PartyDungeonProjection.PublishAsync(entered.Notices);
            await Drain();
            That(frames.All(rows => rows.Single(row => row.Kind == 0 && row.Opcode == 28).Body is { Length: 36 } body &&
                BinaryPrimitives.ReadUInt32LittleEndian(body) == 5360 && body[4] == 2),
                "两支原队全部连接NOTI28安装匹配5360与指定难度2，不能沿普通/英雄目标");
            var mapSequence = first[0].CurrentFormalDungeonScene()!.Entry.Maze.MapSpecification!.ToArray();
            That(mapSequence.Length == 9 && mapSequence[0].Position == new DungeonPoint(0, 0) &&
                mapSequence[^1].Position == first[0].CurrentFormalDungeonScene()!.Entry.BossMap.Position,
                "匹配资源明确为开场0加八段战斗，末段8为真终点");
            for (int i = 0; i < members - 1; i++) await Load(i);
            await Drain();
            That(frames.All(rows => Progress(rows, 0, 5).Length == 0), "原队伍加载屏障未齐不发布current");
            await Load(members - 1); await Drain();
            That(frames.All(rows => Progress(rows, 0, 5).SequenceEqual(new uint[] { 0 })),
                "真实Loaded向比赛全部连接发布source0 current0，使用map-spec索引而非maze序号");
            await Load(members - 1); await Drain();
            That(frames.All(rows => Progress(rows, 0, 5).Length == 1), "重复FINISH_LOADING不重复发布开场current");
            for (int i = members; i < sessions.Length; i++) await Load(i);
            foreach (int phase in new[] { 3, 4 })
                foreach (int source in new[] { 0, members })
                    await registry.DispatchAsync(contexts[source], new Packet(1, 1525,
                        Network.Builders.Strongway2019Packets.Symbol((uint)(source / members), 7, (uint)phase)[1..]));
            await Drain();
            That(frames.All(rows => Progress(rows, 1, 5).SequenceEqual(new uint[] { 0 })),
                "另一原队加载只发布source1 current0，不借前一队进度");
            int targetDeaths = 0;
            for (int ordinal = 0; ordinal < mapSequence.Length; ordinal++)
            {
                var current = first[0].CurrentFormalDungeonScene()!;
                if (ordinal > 0)
                {
                    int beforeLoad = Progress(frames[0], 0, 5).Length;
                    var move = new byte[100]; move[0] = checked((byte)mapSequence[ordinal].Position.X);
                    move[1] = checked((byte)mapSequence[ordinal].Position.Y);
                    await registry.DispatchAsync(contexts[0], new Packet(1, (ushort)CmdPacketType.MOVE_MAP, move));
                    for (int i = 0; i < members - 1; i++) await Load(i);
                    await Drain();
                    That(frames.All(rows => Progress(rows, 0, 5).Length == beforeLoad), "过房请求和未齐加载不提前增加current");
                    await Load(members - 1); await Load(members - 1); await Drain();
                    current = first[0].CurrentFormalDungeonScene()!;
                    That(frames.All(rows => Progress(rows, 0, 5).SequenceEqual(Enumerable.Range(0, ordinal + 1).Select(value => (uint)value))),
                        "真实过房Loaded只增至map-spec序列索引：" + ordinal);
                }
                bool configured = activity.MapMonsters.TryGetValue(current.Snapshot.CurrentMap.Id, out int target);
                if (configured)
                {
                    That(frames.All(rows => Progress(rows, 0, 6).Last() == 100), "配置目标真实装载后血条初始化100：" + ordinal);
                    int hpBefore = Progress(frames[0], 0, 6).Length;
                    await Symbol(0, 0, 37); await Drain();
                    That(frames.All(rows => Progress(rows, 0, 6).Length == hpBefore + 1 && Progress(rows, 0, 6).Last() == 37),
                        "配置目标在原队全部Run存活时，队长37合法遥测向比赛全员投影");
                    await Symbol(members == 1 ? members : 1, 0, 38);
                    await Symbol(0, 1, 39); await Symbol(0, 0, 0); await Symbol(0, 0, 101); await Drain();
                    That(frames.All(rows => Progress(rows, 0, 6).Length == hpBefore + 1),
                        "目标存活也拒绝非本队队长、假side、客户端死亡零及越界百分比");
                }
                foreach (var monster in current.Monsters.Monsters.Where(row => row.IsBlocking).OrderBy(row => row.SpawnType == 3).ToArray())
                {
                    current = first[0].CurrentFormalDungeonScene()!;
                    if (!current.Snapshot.LivingMonsters.Contains(monster.ObjectKey)) continue;
                    var body = DfoServer.Testing.FormalDungeonTcpTests.Death(monster.ObjectKey, current.Character.Id, checked((byte)monster.CandidateLevel));
                    if (monster.SpawnType == 3)
                    {
                        body[55] = 1; BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(18), 9000);
                        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(26), 30);
                        DfoServer.Testing.FormalDungeonTcpTests.SealDeath(body);
                    }
                    int zeroBefore = Progress(frames[0], 0, 6).Count(value => value == 0);
                    var invalid = body.ToArray(); invalid[^1] ^= 1;
                    await registry.DispatchAsync(contexts[0], new Packet(1, (ushort)CmdPacketType.DIE_MONSTER, invalid));
                    await Drain();
                    That(frames.All(rows => Progress(rows, 0, 6).Count(value => value == 0) == zeroBefore),
                        "错误校验死亡不能把配置目标HUD置零");
                    await registry.DispatchAsync(contexts[0], new Packet(1, (ushort)CmdPacketType.DIE_MONSTER, body));
                    await registry.DispatchAsync(contexts[0], new Packet(1, (ushort)CmdPacketType.DIE_MONSTER, body));
                    await Drain();
                    bool isTarget = configured && monster.MonsterId == target;
                    if (isTarget) targetDeaths++;
                    That(frames.All(rows => Progress(rows, 0, 6).Count(value => value == 0) == zeroBefore + (isTarget ? 1 : 0)) &&
                        first.All(session => !session.CurrentFormalDungeonScene()!.Snapshot.LivingMonsters.Contains(monster.ObjectKey)),
                        "只有配置目标的真实CMD39确认死亡发一次零，重报或其它怪不增加：" + monster.MonsterId);
                    if (isTarget)
                    {
                        int afterDeath = Progress(frames[0], 0, 6).Length;
                        await Symbol(0, 0, 37); await Drain();
                        That(frames.All(rows => Progress(rows, 0, 6).Length == afterDeath && Progress(rows, 0, 6).Last() == 0),
                            "配置目标真实死亡后队长不能用非零遥测抬回HUD");
                    }
                }
            }
            That(targetDeaths == 8 && first.All(session => session.Phase == DfoServer.Game.Session.SessionPhase.Settlement) &&
                entered.Match.Competition.Snapshot.Terminal is { WinnerSide: 0, Reason: Strongway2019FinishReason.Clear } &&
                frames.All(rows => rows.Single(row => row.Opcode == 1494).Body.SequenceEqual(new byte[] { 0, 0 })) &&
                first.All(session => MailCount(session) == 1) && second.All(session => MailCount(session) == 0),
                "八段配置目标经真实CMD39/过房完成，原队全部Run真终点奖励与全场同winnerSide结果闭合");
        }
        finally
        {
            foreach (var session in sessions) { session.Strongway2019NoticeArrived = null; session.PartyDungeonNoticeArrived = null; }
            foreach (var sender in senders) await sender.DisposeAsync();
            foreach (var output in outputs) output.Dispose();
            foreach (var stop in stops) stop.Dispose();
        }
        Task<CommandResult> Load(int index) => registry.DispatchAsync(contexts[index],
            new Packet(1, (ushort)CmdPacketType.FINISH_LOADING, new byte[8]));
        Task Drain() => Task.WhenAll(senders.Select(sender => sender.DrainAsync()));
        Task<CommandResult> Symbol(int index, uint side, uint value) => registry.DispatchAsync(contexts[index],
            new Packet(1, 1525, Network.Builders.Strongway2019Packets.Symbol(side, 6, value)[1..]));
        int MailCount(DfoServer.Game.Session.GameSession session) => f.Repository.LoadMailbox(
            session.CaptureSocialActor()!.Owner, session.Selected!.Id, f.Clock.GetUtcNow().ToUnixTimeSeconds()).Entries.Length;
    }

    static uint[] Progress(List<(byte Kind, ushort Opcode, byte[] Body)> frames, uint side, uint kind) => frames
        .Where(row => row.Kind == 1 && row.Opcode == 1525 && row.Body.Length == 13 && row.Body[0] == 1 &&
            BinaryPrimitives.ReadUInt32LittleEndian(row.Body.AsSpan(1)) == side && BinaryPrimitives.ReadUInt32LittleEndian(row.Body.AsSpan(5)) == kind)
        .Select(row => BinaryPrimitives.ReadUInt32LittleEndian(row.Body.AsSpan(9))).ToArray();
}
