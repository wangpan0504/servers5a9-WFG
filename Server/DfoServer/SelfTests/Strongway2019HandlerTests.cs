using System.Net;
using DfoServer.Game.Strongway2019;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using PvfLib;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class Strongway2019HandlerTests
{
    public static async Task RunAsync()
    {
        int before = Count;
        var activity = Strongway2019CatalogTests.CreateCatalog();
        foreach (int members in new[] { 1, 4 })
        {
            using var f = new Strongway2019MatchingTests.Fixture(activity: activity);
            var first = f.Team(count: members);
            var matching = new Strongway2019MatchingService(f.Parties, activity, f.Clock);
            var handler = new Strongway2019Handler(matching, new Strongway2019Projection());
            string directory = Path.Combine(Path.GetTempPath(), "strongway-handler-" + Guid.NewGuid().ToString("N"));
            using var log = new ServerLog(directory);
            using var stop = new CancellationTokenSource();
            using var output = new MemoryStream();
            var actor = first[0].CaptureSocialActor()!;
            await using var sender = new GameConnectionSender(actor.Owner, output, stop, 0, (_, _, _, _, _) => { });
            var context = new GameConnectionContext(new(1525, IPAddress.Loopback, output, stop), actor.Owner, first[0], sender);
            var commands = new GameCommandRegistry();
            new PvpMatchingHandler(null!, log, f.Clock, handler).Register(commands);
            handler.Register(commands); commands.Seal();
            That(await commands.DispatchAsync(context, new Packet(1, 520,
                Convert.FromHexString("010E000000FFFFFFFFFFFFFFFF00F0140000"))) == CommandResult.Handled,
                "原520唯一注册经精确18B首期形状分流到真实处理器");
            await sender.DrainAsync();
            That(first.All(session => ReferenceEquals(session.CurrentStrongway2019Ticket, first[0].CurrentStrongway2019Ticket)) &&
                first[0].CurrentStrongway2019Ticket is not null &&
                output.ToArray().SequenceEqual(GameFrame.Build(1, 520, 0, [1, 1, 14, 0, 0])),
                "真实Context与MemoryStream发送原520五字节ACK，同时原一人或四人票据已安装");
            output.SetLength(0); output.Position = 0;
            That(await commands.DispatchAsync(context, new Packet(1, 521, [1, 14, 0])) == CommandResult.Handled,
                "原521唯一注册分流首期取消");
            await sender.DrainAsync();
            That(first.All(session => session.CurrentStrongway2019Ticket is null) &&
                output.ToArray().SequenceEqual(GameFrame.Build(1, 521, 1, [1])),
                "取消先释放本队票据，真实发送精确单字节ACK");
        }
        Console.WriteLine($"PASS {Count - before} 首期单人及四人真实Handler/连接发送与原匹配分流");
    }

    public static async Task RunResourcesAsync(PvfArchive archive, TownCatalog towns, ItemResources resources)
    {
        int before = Count;
        var activity = new Strongway2019Catalog(archive);
        using var f = new Strongway2019MatchingTests.Fixture(towns, activity);
        var first = f.Team(); var second = f.Team(); var sessions = first.Concat(second).ToArray();
        var matching = new Strongway2019MatchingService(f.Parties, activity, f.Clock);
        using var log = new ServerLog(Path.Combine(Path.GetTempPath(), "strongway-handler-resources-" + Guid.NewGuid().ToString("N")));
        var profiles = new Strongway2019ProfileProjection(f.Repository, activity.DungeonStartDelay);
        var projection = new Strongway2019Projection(new Strongway2019RewardProjection(resources.Items, f.Repository, log, profiles), matching);
        var entries = new Strongway2019MatchingEntryService(matching, f.Parties, f.Repository, f.Repository, f.Clock);
        bool departed = false;
        var handler = new Strongway2019Handler(matching, projection, entries, DungeonCatalog.Load(archive),
            DungeonRoomCatalog.Load(archive), DungeonMonsterDropCatalog.Load(archive), resources.Items,
            resources.EpicPieces, activity, f.Clock, _ =>
            {
                That(second.All(session => MailCount(session) == 1) && first.All(session => MailCount(session) == 0),
                    "522调用原退场delegate前已提交获胜四人各一封真实奖励邮件，败方无奖励");
                departed = true; return Task.CompletedTask;
            });
        var commands = new GameCommandRegistry();
        new PvpMatchingHandler(null!, log, f.Clock, handler).Register(commands); handler.Register(commands); commands.Seal();
        var stops = sessions.Select(_ => new CancellationTokenSource()).ToArray();
        var outputs = sessions.Select(_ => new MemoryStream()).ToArray();
        var frames = sessions.Select(_ => new List<(byte Kind, ushort Opcode, byte[] Body)>()).ToArray();
        var senders = sessions.Select((session, i) => new GameConnectionSender(session.CaptureSocialActor()!.Owner,
            outputs[i], stops[i], 0, (kind, opcode, _, body, _) => frames[i].Add((kind, opcode, body.ToArray())))).ToArray();
        var contexts = sessions.Select((session, i) => new GameConnectionContext(new(i, IPAddress.Loopback, outputs[i], stops[i]),
            session.CaptureSocialActor()!.Owner, session, senders[i])).ToArray();
        int[] entryNotices = new int[8];
        bool[] waitingNotices = new bool[8];
        try
        {
            for (int i = 0; i < sessions.Length; i++)
            {
                int index = i;
                sessions[i].MailboxArrived = receipt => contexts[index].Sender.Post(0, (ushort)NotiPacketType.MAILBOX_ALARM,
                    Network.Builders.MailboxPackets.Alarm(), "mailbox_arrived", () => sessions[index].CanPublishMailboxAlarm(receipt));
                sessions[i].Strongway2019NoticeArrived = notice =>
                {
                    if (notice.Kind == Strongway2019NoticeKind.Waiting) waitingNotices[index] = true;
                    return projection.DeliverAsync(contexts[index], notice);
                };
                sessions[i].PartyDungeonNoticeArrived = notice =>
                {
                    That(waitingNotices[index], "各真实连接先发布1492才发布原Entry通知");
                    entryNotices[index]++; return Task.CompletedTask;
                };
            }
            byte[] queue = Convert.FromHexString("010E000000FFFFFFFFFFFFFFFF00F0140000");
            await commands.DispatchAsync(contexts[0], new Packet(1, 520, queue));
            first[3].CommandGate.Wait();
            try
            {
                await commands.DispatchAsync(contexts[4], new Packet(1, 520, queue));
                That(sessions.All(session => session.CurrentStrongway2019Ticket is not null && session.CurrentStrongway2019Match is null),
                    "队友原命令门忙时两支票据保留，不在520内等待或半队入场");
            }
            finally { first[3].CommandGate.Release(); }
            await handler.MaintainAsync(CancellationToken.None);
            await Task.WhenAll(senders.Select(sender => sender.DrainAsync()));
            var match = first[0].CurrentStrongway2019Match!;
            That(match is not null && sessions.All(session => ReferenceEquals(session.CurrentStrongway2019Match, match)) &&
                entryNotices.All(count => count == 1), "两队真实520完成八人正式入场并各投影一次原Entry");
            uint deadline = checked((uint)f.Clock.GetUtcNow().ToUnixTimeSeconds() + (uint)activity.DungeonStartDelay);
            for (int i = 0; i < sessions.Length; i++)
            {
                var expected = Network.Builders.Strongway2019Packets.Waiting(deadline, 0, (byte)(i / 4),
                    (i < 4 ? second : first).Select(session => session.Selected!.Id).ToArray());
                That(frames[i].Single(frame => frame.Opcode == 1492).Body.SequenceEqual(expected),
                    "真实1492发送Unix截止秒、本方side及对方四人名单");
            }
            foreach (var team in new[] { first, second })
                foreach (var session in team) team[0].PartyDungeon!.CompleteLoading(session);
            foreach (int phase in new[] { 3, 4 })
            {
                await Symbol(0, 0, 7, (uint)phase);
                await Symbol(1, 0, 7, (uint)phase);
                await Symbol(4, 1, 7, (uint)phase);
            }
            await Task.WhenAll(senders.Select(sender => sender.DrainAsync()));
            That(frames.All(rows => rows.Count(row => row.Opcode == 1525) == 4) &&
                match!.Competition.Snapshot.Phase == Strongway2019CompetitionPhase.Running,
                "两队3/4屏障各广播一次；同队不同成员重复不贡献第二来源");
            await Symbol(0, 0, 6, 37);
            await Task.WhenAll(senders.Select(sender => sender.DrainAsync()));
            That(frames.All(rows => rows.Count(row => row.Opcode == 1525) == 4),
                "开场map0没有配置目标，队长非零血条遥测不虚构活体HUD");
            await Symbol(1, 0, 6, 38);
            await Symbol(0, 1, 6, 39);
            await Symbol(0, 0, 6, 0);
            await Symbol(0, 0, 6, 101);
            await Task.WhenAll(senders.Select(sender => sender.DrainAsync()));
            That(frames.All(rows => rows.Count(row => row.Opcode == 1525) == 4),
                "队员、伪side、客户端0死亡及越界百分比不进入血条投影");
            await Symbol(0, 1, 0, uint.MaxValue);
            await Task.WhenAll(senders.Select(sender => sender.DrainAsync()));
            That(frames.All(rows => rows.Count(row => row.Opcode == 1525) == 4),
                "施技方不能伪造对方source side；非法输入不消费技能或广播");
            for (uint skill = 0; skill < 5; skill++)
            {
                f.Clock.Advance(TimeSpan.FromSeconds(100));
                await Symbol(0, 0, skill, uint.MaxValue);
                await Task.WhenAll(senders.Select(sender => sender.DrainAsync()));
                var effects = frames.Select(rows => rows.Last(row => row.Opcode == 1525).Body).ToArray();
                uint value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(effects[0].AsSpan(9));
                That(effects.All(body => body.SequenceEqual(Network.Builders.Strongway2019Packets.Symbol(0, skill, value))) &&
                    (skill == 4 ? activity.RandomMapIds.Contains(checked((int)value)) : value == 0),
                    "五技能向八连接广播同一source side与服务端效果，忽略C2S第三栈字段");
            }
            await commands.DispatchAsync(contexts[0], new Packet(1, 522, [1, 14, 0]));
            await Task.WhenAll(senders.Select(sender => sender.DrainAsync()));
            That(departed && match!.Competition.Snapshot.Terminal?.WinnerSide == 1 && frames.All(rows =>
                rows.Single(row => row.Opcode == 1494).Body.SequenceEqual(new byte[] { 1, 0 })),
                "1494首字节向真实双队全部连接发送同一个获胜side1，不按接收者写胜负bool");
            await projection.TerminalAsync(match!);
            await Task.WhenAll(senders.Select(sender => sender.DrainAsync()));
            That(frames.All(rows => rows.Count(row => row.Opcode == 1494) == 1 && rows.All(row => row.Opcode != 522)) &&
                first.All(session => MailCount(session) == 0) && second.All(session => MailCount(session) == 1) &&
                frames.Skip(4).All(rows => rows.Count(row => row.Opcode == (ushort)NotiPacketType.MAILBOX_ALARM) == 1),
                "重复终局只投影一次1494，522不添加未证ACK");
            That(first.All(session => Statistics(session) == new Strongway2019Statistics(0, 0, 0, 0)) &&
                second.All(session => Statistics(session) == new Strongway2019Statistics(1, 0, 0, 0)) &&
                frames.Take(4).All(rows => rows.All(row => row.Opcode != 1491)) &&
                frames.Skip(4).All(rows => rows.Count(row => row.Opcode == 1491) == 1 &&
                    rows.Single(row => row.Opcode == 1491).Body.SequenceEqual(Network.Builders.Strongway2019ProfilePackets.CharacterData(
                        new(1, 0, 0, 0), activity.DungeonStartDelay))),
                "真实匹配退出只记录固定胜队一胜且无虚构最佳耗时，败队历史空；赢家1491精确且重复终局不再发送");
            await projection.BeforeDepartureAsync(contexts[4]);
            That(second[0].CurrentStrongway2019Match is null && second.All(session => MailCount(session) == 1),
                "胜队成员选角/断线前先保留已提交四人奖励，再Detach本Session原比赛引用");
        }
        finally
        {
            foreach (var session in sessions) { session.Strongway2019NoticeArrived = null; session.PartyDungeonNoticeArrived = null; session.MailboxArrived = null; }
            foreach (var sender in senders) await sender.DisposeAsync();
            foreach (var output in outputs) output.Dispose();
            foreach (var stop in stops) stop.Dispose();
        }
        Console.WriteLine($"PASS {Count - before} 首期八连接匹配、等待、开场与终局原生投影");

        Task<CommandResult> Symbol(int connection, uint side, uint kind, uint value) =>
            commands.DispatchAsync(contexts[connection], new Packet(1, 1525,
                Network.Builders.Strongway2019Packets.Symbol(side, kind, value)[1..]));
        int MailCount(DfoServer.Game.Session.GameSession session) => f.Repository.LoadMailbox(
            session.CaptureSocialActor()!.Owner, session.Selected!.Id, f.Clock.GetUtcNow().ToUnixTimeSeconds()).Entries.Length;
        Strongway2019Statistics Statistics(DfoServer.Game.Session.GameSession session) => f.Repository.ReadStrongway2019Statistics(
            session.CaptureSocialActor()!.Owner, session.Selected!.Id);
    }

    public static Task RunDisconnectedResourcesAsync(PvfArchive archive, TownCatalog towns, ItemResources resources) =>
        RunDisconnectedResourcesCoreAsync(archive, towns, resources, winnerProfiles: false);

    public static Task RunDisconnectedWinnerProfileResourcesAsync(PvfArchive archive, TownCatalog towns, ItemResources resources) =>
        RunDisconnectedResourcesCoreAsync(archive, towns, resources, winnerProfiles: true);

    static async Task RunDisconnectedResourcesCoreAsync(PvfArchive archive, TownCatalog towns, ItemResources resources, bool winnerProfiles)
    {
        int before = Count;
        var activity = new Strongway2019Catalog(archive);
        using var f = new Strongway2019MatchingTests.Fixture(towns, activity);
        var first = f.Team(); var second = f.Team(); var sessions = first.Concat(second).ToArray();
        var matching = new Strongway2019MatchingService(f.Parties, activity, f.Clock);
        var ticket = matching.Queue(first[0], first.Select(session => session.PrepareStrongway2019Admission(5360, true)!).ToArray())!;
        matching.Queue(second[0], second.Select(session => session.PrepareStrongway2019Admission(5360, true)!).ToArray());
        var entry = new Strongway2019MatchingEntryService(matching, f.Parties, f.Repository, f.Repository, f.Clock)
            .Enter(matching.ReserveNext(ticket)!, DungeonCatalog.Load(archive), DungeonRoomCatalog.Load(archive),
                DungeonMonsterDropCatalog.Load(archive), resources.Items, resources.EpicPieces)!;
        foreach (var team in new[] { first, second })
            foreach (var session in team) team[0].PartyDungeon!.CompleteLoading(session);
        using var log = new ServerLog(Path.Combine(Path.GetTempPath(), "strongway-handler-disconnect-" + Guid.NewGuid().ToString("N")));
        var store = new FailingStore(f.Repository);
        var profiles = winnerProfiles ? new Strongway2019ProfileProjection(f.Repository, activity.DungeonStartDelay) : null;
        var projection = new Strongway2019Projection(new Strongway2019RewardProjection(resources.Items, store, log, profiles), matching);
        int closedConnection = winnerProfiles ? 4 : 0;
        var stops = sessions.Select(_ => new CancellationTokenSource()).ToArray();
        var outputs = sessions.Select(_ => new MemoryStream()).ToArray();
        var results = new int[8];
        var senders = sessions.Select((session, i) => new GameConnectionSender(session.CaptureSocialActor()!.Owner,
            outputs[i], stops[i], 0, (_, opcode, _, _, _) => { if (opcode == 1494) results[i]++; })).ToArray();
        var contexts = sessions.Select((session, i) => new GameConnectionContext(new(i, IPAddress.Loopback, outputs[i], stops[i]),
            session.CaptureSocialActor()!.Owner, session, senders[i])).ToArray();
        try
        {
            for (int i = 0; i < sessions.Length; i++)
            {
                int index = i;
                sessions[i].Strongway2019NoticeArrived = notice => projection.DeliverAsync(contexts[index], notice);
            }
            await senders[closedConnection].DisposeAsync();
            Exception? failure = null;
            try { await projection.BeforeDepartureAsync(contexts[1]); }
            catch (Exception error) { failure = error; }
            That(failure is IOException && ReferenceEquals(first[1].CurrentStrongway2019Match, entry.Match),
                "奖励存储抛IOException仍阻止释放原比赛，不被网络失败隔离吞掉");
            failure = null;
            try { await projection.BeforeDepartureAsync(contexts[1]); }
            catch (Exception error) { failure = error; }
            await Task.WhenAll(senders.Where((_, index) => index != closedConnection).Select(sender => sender.DrainAsync()));
            That(failure is null && first[1].CurrentStrongway2019Match is null &&
                results.Where((_, index) => index != closedConnection).All(count => count == 1) &&
                second.All(session => f.Repository.LoadMailbox(session.CaptureSocialActor()!.Owner, session.Selected!.Id,
                    f.Clock.GetUtcNow().ToUnixTimeSeconds()).Entries.Length == 1),
                "单收件sender已关闭（含获胜1491）仍发布其余七连接、完成获胜四邮件，重试后BeforeDeparture可Detach且不重复结果");
            if (winnerProfiles)
                That(second.All(session => f.Repository.ReadStrongway2019Statistics(session.CaptureSocialActor()!.Owner,
                    session.Selected!.Id) == new Strongway2019Statistics(1, 0, 0, 0)) &&
                    first.All(session => f.Repository.ReadStrongway2019Statistics(session.CaptureSocialActor()!.Owner,
                        session.Selected!.Id) == new Strongway2019Statistics(0, 0, 0, 0)),
                    "获胜1491发送断开不会丢失或重算四人胜场，奖励存储重试也不重复统计");
            await projection.SymbolAsync(entry.Match, 0, 5, 1);
            That(results.Where((_, index) => index != closedConnection).All(count => count == 1), "后续广播同样隔离已关闭的单连接发送失败");
        }
        finally
        {
            foreach (var session in sessions) session.Strongway2019NoticeArrived = null;
            foreach (var sender in senders) await sender.DisposeAsync();
            foreach (var output in outputs) output.Dispose();
            foreach (var stop in stops) stop.Dispose();
        }
        Console.WriteLine($"PASS {Count - before} 首期断sender隔离与持久化失败重试");
    }

    public static async Task RunStaleOwnerResourcesAsync(PvfArchive archive, TownCatalog towns, ItemResources resources)
    {
        int before = Count;
        using var f = new Strongway2019MatchingTests.Fixture(towns, new Strongway2019Catalog(archive));
        var session = f.Team()[0]; var actor = session.CaptureSocialActor()!;
        using var log = new ServerLog(Path.Combine(Path.GetTempPath(), "strongway-handler-stale-" + Guid.NewGuid().ToString("N")));
        var projection = new Strongway2019Projection(new Strongway2019RewardProjection(resources.Items, f.Repository, log));
        using var stop = new CancellationTokenSource(); using var output = new MemoryStream();
        await using var sender = new GameConnectionSender(actor.Owner, output, stop, 0, (_, _, _, _, _) => { });
        var context = new GameConnectionContext(new(1, IPAddress.Loopback, output, stop), actor.Owner, session, sender);
        var replacement = f.Takeover(session);
        Exception? failure = null;
        try { await projection.BeforeDepartureAsync(context); }
        catch (Exception error) { failure = error; }
        That(failure is null && !actor.Owner.IsCurrent && replacement.IsCurrent && output.Length == 0,
            "接管后的旧owner断线BeforeDeparture跳过无权奖励提交并允许后续清理，不触碰新owner或发包");
        Console.WriteLine($"PASS {Count - before} 首期旧owner断线收尾");
    }

    public static async Task RunLoadingExitResourcesAsync(PvfArchive archive, TownCatalog towns, ItemResources resources)
    {
        int before = Count;
        var activity = new Strongway2019Catalog(archive);
        using var f = new Strongway2019MatchingTests.Fixture(towns, activity);
        var first = f.Team(); var second = f.Team(); var sessions = first.Concat(second).ToArray();
        var matching = new Strongway2019MatchingService(f.Parties, activity, f.Clock);
        var ticket = matching.Queue(first[0], first.Select(session => session.PrepareStrongway2019Admission(5360, true)!).ToArray())!;
        matching.Queue(second[0], second.Select(session => session.PrepareStrongway2019Admission(5360, true)!).ToArray());
        var entry = new Strongway2019MatchingEntryService(matching, f.Parties, f.Repository, f.Repository, f.Clock)
            .Enter(matching.ReserveNext(ticket)!, DungeonCatalog.Load(archive), DungeonRoomCatalog.Load(archive),
                DungeonMonsterDropCatalog.Load(archive), resources.Items, resources.EpicPieces)!;
        using var log = new ServerLog(Path.Combine(Path.GetTempPath(), "strongway-handler-loading-" + Guid.NewGuid().ToString("N")));
        var projection = new Strongway2019Projection(new Strongway2019RewardProjection(resources.Items, f.Repository, log), matching);
        var stops = sessions.Select(_ => new CancellationTokenSource()).ToArray();
        var outputs = sessions.Select(_ => new MemoryStream()).ToArray();
        var senders = sessions.Select((session, i) => new GameConnectionSender(session.CaptureSocialActor()!.Owner,
            outputs[i], stops[i], 0, (_, _, _, _, _) => { })).ToArray();
        var contexts = sessions.Select((session, i) => new GameConnectionContext(new(i, IPAddress.Loopback, outputs[i], stops[i]),
            session.CaptureSocialActor()!.Owner, session, senders[i])).ToArray();
        try
        {
            for (int i = 0; i < sessions.Length; i++)
            {
                int index = i;
                sessions[i].Strongway2019NoticeArrived = notice => projection.DeliverAsync(contexts[index], notice);
            }
            That(sessions.All(session => session.Phase == DfoServer.Game.Session.SessionPhase.Loading),
                "真实八人入场尚未加载完成，不能把Dungeon状态冒成Loading退出回归");
            await projection.BeforeDepartureAsync(contexts[0]);
            That(entry.Match.Competition.Snapshot.Terminal is { Reason: Strongway2019FinishReason.Exit, WinnerSide: 1 } &&
                first[0].CurrentStrongway2019Match is null && first.All(session => MailCount(session) == 0) &&
                second.All(session => MailCount(session) == 1),
                "初始Loading退出仍按固定胜队给四名LoadingRun胜者各一封真实邮件，再释放退出者");
        }
        finally
        {
            foreach (var session in sessions) session.Strongway2019NoticeArrived = null;
            foreach (var sender in senders) await sender.DisposeAsync();
            foreach (var output in outputs) output.Dispose();
            foreach (var stop in stops) stop.Dispose();
        }
        Console.WriteLine($"PASS {Count - before} 首期真实Loading退出固定胜队奖励");
        int MailCount(DfoServer.Game.Session.GameSession session) => f.Repository.LoadMailbox(
            session.CaptureSocialActor()!.Owner, session.Selected!.Id, f.Clock.GetUtcNow().ToUnixTimeSeconds()).Entries.Length;
    }

    public static Task RunAutomaticReturnResourcesAsync(PvfArchive archive, TownCatalog towns, ItemResources resources) =>
        RunAutomaticReturnResourcesCoreAsync(archive, towns, resources, reconnect: false);

    public static Task RunReconnectAfterAutomaticReturnResourcesAsync(PvfArchive archive, TownCatalog towns, ItemResources resources) =>
        RunAutomaticReturnResourcesCoreAsync(archive, towns, resources, reconnect: true);

    static async Task RunAutomaticReturnResourcesCoreAsync(PvfArchive archive, TownCatalog towns, ItemResources resources, bool reconnect)
    {
        int before = Count;
        var activity = new Strongway2019Catalog(archive);
        using var f = new Strongway2019MatchingTests.Fixture(towns, activity);
        var first = f.Team(); var second = f.Team(); var sessions = first.Concat(second).ToArray();
        var matching = new Strongway2019MatchingService(f.Parties, activity, f.Clock);
        var ticket = matching.Queue(first[0], first.Select(session => session.PrepareStrongway2019Admission(5360, true)!).ToArray())!;
        matching.Queue(second[0], second.Select(session => session.PrepareStrongway2019Admission(5360, true)!).ToArray());
        var entry = new Strongway2019MatchingEntryService(matching, f.Parties, f.Repository, f.Repository, f.Clock)
            .Enter(matching.ReserveNext(ticket)!, DungeonCatalog.Load(archive), DungeonRoomCatalog.Load(archive),
                DungeonMonsterDropCatalog.Load(archive), resources.Items, resources.EpicPieces)!;
        using var log = new ServerLog(Path.Combine(Path.GetTempPath(), "strongway-handler-return-" + Guid.NewGuid().ToString("N")));
        var profiles = new Strongway2019ProfileProjection(f.Repository, activity.DungeonStartDelay);
        var projection = new Strongway2019Projection(new Strongway2019RewardProjection(resources.Items, f.Repository, log, profiles), matching, f.Clock);
        var handler = new Strongway2019Handler(matching, projection, catalog: activity, clock: f.Clock);
        var partyDungeons = new DfoServer.Game.Dungeons.PartyDungeonService(f.Parties, f.Repository);
        var stops = sessions.Select(_ => new CancellationTokenSource()).ToArray();
        var outputs = sessions.Select(_ => new MemoryStream()).ToArray();
        var results = new int[8]; var returned = new int[8]; var departureNotices = new int[8];
        var histories = sessions.Select(_ => new List<byte[]>()).ToArray();
        var senders = sessions.Select((session, i) => new GameConnectionSender(session.CaptureSocialActor()!.Owner,
            outputs[i], stops[i], 0, (_, opcode, _, body, _) =>
            { if (opcode == 1494) results[i]++; if (opcode == 1491) histories[i].Add(body.ToArray()); })).ToArray();
        var contexts = sessions.Select((session, i) => new GameConnectionContext(new(i, IPAddress.Loopback, outputs[i], stops[i]),
            session.CaptureSocialActor()!.Owner, session, senders[i])).ToArray();
        projection.Depart = async (context, holds) =>
        {
            int index = Array.IndexOf(sessions, context.Session);
            That(second.All(session => MailCount(session) == 1), "自动退场前已给原获胜四人各提交一封奖励");
            var party = context.Session.CurrentParty!.Invoke()!;
            var instance = context.Session.PartyDungeon!;
            That(holds(party.Commands) && party.Commands.CurrentCount == 0 &&
                instance.Members.All(member => holds(member.Actor.Session.CommandGate) && member.Actor.Session.CommandGate.CurrentCount == 0),
                "后台退场只携带实际持有的原队伍及全体连接命令门证明");
            await projection.BeforeDepartureAsync(context);
            using var peers = await PartyDungeonProjection.AcquirePeersAsync(context, instance.Members, holdsCommandGate: holds);
            That(await PartyDungeonProjection.DepartAsync(context, partyDungeons, f.Parties, () => Task.CompletedTask) == CommandResult.Handled,
                "自动退场复用原PartyDungeonService与原Departure通知");
            returned[index]++;
        };
        try
        {
            for (int i = 0; i < sessions.Length; i++)
            {
                int index = i;
                sessions[i].Strongway2019NoticeArrived = notice => projection.DeliverAsync(contexts[index], notice);
                sessions[i].PartyDungeonNoticeArrived = notice =>
                {
                    if (notice.Kind == DfoServer.Game.Dungeons.PartyDungeonNoticeKind.Departure) departureNotices[index]++;
                    return Task.CompletedTask;
                };
            }
            var run = entry.Match.Competition.Participants[0].Run;
            entry.Match.Competition.Exit(run, run.Scene.Identity);
            await handler.MaintainAsync(CancellationToken.None);
            await Task.WhenAll(senders.Select(sender => sender.DrainAsync()));
            That(results.All(count => count == 1) && returned.All(count => count == 0),
                "首次终局发布1494且保留原副本，不在结果展示同拍自动返城");
            f.Clock.Advance(TimeSpan.FromSeconds(activity.ReturnVillageDelay) - TimeSpan.FromMilliseconds(1));
            await handler.MaintainAsync(CancellationToken.None);
            That(returned.All(count => count == 0), "原资源返城延迟前一毫秒仍不释放任何原Run");
            f.Clock.Advance(TimeSpan.FromMilliseconds(1));
            first[3].CommandGate.Wait();
            try
            {
                await handler.MaintainAsync(CancellationToken.None);
                That(first.All(session => ReferenceEquals(session.CurrentStrongway2019Match, entry.Match)) &&
                    returned.Take(4).All(count => count == 0) && returned.Skip(4).All(count => count == 1) &&
                    second.All(session => session.Phase == DfoServer.Game.Session.SessionPhase.Town && session.PartyDungeon is null),
                    "到期忙队零等待保留原比赛，另一队仍经真实域退场返城而不阻塞维护");
            }
            finally { first[3].CommandGate.Release(); }
            await handler.MaintainAsync(CancellationToken.None);
            await handler.MaintainAsync(CancellationToken.None);
            await Task.WhenAll(senders.Select(sender => sender.DrainAsync()));
            That(returned.All(count => count == 1) && departureNotices.All(count => count == 1) &&
                sessions.All(session => session.Phase == DfoServer.Game.Session.SessionPhase.Town &&
                    session.PartyDungeon is null && session.CurrentStrongway2019Match is null) && matching.Snapshot().Matches.Length == 0 &&
                results.All(count => count == 1),
                "忙门释放后下一轮八人全部原退场仅一次，无残留match或重复1494");
            That(first.All(session => Statistics(session) == new Strongway2019Statistics(0, 0, 0, 0)) &&
                second.All(session => Statistics(session) == new Strongway2019Statistics(1, 0, 0, 0)) &&
                histories.Take(4).All(rows => rows.Count == 0) && histories.Skip(4).All(rows => rows.Count == 1 &&
                    rows[0].SequenceEqual(Network.Builders.Strongway2019ProfilePackets.CharacterData(new(1, 0, 0, 0), activity.DungeonStartDelay))),
                "自动返城前后胜队统计保留一次，败队空历史；重复维护与原退场不重复1491");
            if (reconnect)
            {
                var original = second[0].CaptureSocialActor()!;
                ushort slot = second[0].Selected!.Slot;
                var channel = second[0].ChannelEntry;
                second[0].ReturnToRoster();
                var replacement = f.Takeover(original.Owner);
                var reopened = new DfoServer.Persistence.CharacterRepository(f.DatabasePath);
                var restored = new DfoServer.Game.Session.GameSession(reopened, towns, replacement, entry: channel,
                    timeProvider: f.Clock, strongway2019Catalog: activity, strongway2019AntiEvil: _ => 5400);
                using var newStop = new CancellationTokenSource(); using var newOutput = new MemoryStream();
                var newFrames = new List<(ushort Opcode, byte[] Body)>();
                await using var newSender = new GameConnectionSender(replacement, newOutput, newStop, 0,
                    (_, opcode, _, body, _) => newFrames.Add((opcode, body.ToArray())));
                try
                {
                    restored.Select(slot, resumeTown: true); restored.MarkReady();
                    var newContext = new GameConnectionContext(new(100, IPAddress.Loopback, newOutput, newStop), replacement, restored, newSender);
                    var newProfiles = new Strongway2019ProfileProjection(reopened, activity.DungeonStartDelay);
                    await newProfiles.InformationAsync(newContext);
                    await newSender.DrainAsync();
                    That(restored.Selected!.Id == original.Character.Id && restored.CurrentStrongway2019Match is null &&
                        restored.PartyDungeon is null && restored.CurrentFormalDungeonScene() is null &&
                        reopened.ReadStrongway2019Statistics(replacement, original.Character.Id) == new Strongway2019Statistics(1, 0, 0, 0) &&
                        newFrames.Single(row => row.Opcode == 1491).Body.SequenceEqual(
                            Network.Builders.Strongway2019ProfilePackets.CharacterData(new(1, 0, 0, 0), activity.DungeonStartDelay)),
                        "匹配获胜后新Repository与新Session只恢复持久胜场，1491真实发送且不复活旧Run或Match");
                    var mail = reopened.LoadMailbox(replacement, original.Character.Id, f.Clock.GetUtcNow().ToUnixTimeSeconds());
                    That(mail.Entries is [{ Attachments: [{ Item.Core: { ItemId: 490012737, Count: 1 } }] }],
                        "新Session读取匹配唯一737完整附件，重建仓储不重复发奖或丢失收件身份");
                    await projection.BeforeDepartureAsync(contexts[4]);
                    await projection.TerminalAsync(entry.Match);
                    await projection.SymbolAsync(entry.Match, 1, 6, 99);
                    await newSender.DrainAsync();
                    That(restored.Phase == DfoServer.Game.Session.SessionPhase.Town && restored.CurrentStrongway2019Match is null &&
                        newFrames.Count == 1 && reopened.ReadStrongway2019Statistics(replacement, original.Character.Id).MatchingWins == 1 &&
                        reopened.LoadMailbox(replacement, original.Character.Id, f.Clock.GetUtcNow().ToUnixTimeSeconds()).Entries.Length == 1,
                        "旧owner/context终局、HUD及断线清理不能向新连接发包、重记胜场或重复737邮件");
                }
                finally { restored.End(); }
            }
        }
        finally
        {
            foreach (var session in sessions) { session.Strongway2019NoticeArrived = null; session.PartyDungeonNoticeArrived = null; }
            foreach (var sender in senders) await sender.DisposeAsync();
            foreach (var output in outputs) output.Dispose();
            foreach (var stop in stops) stop.Dispose();
        }
        Console.WriteLine($"PASS {Count - before} 首期真实八连接终局延迟自动返城与忙门重试");
        int MailCount(DfoServer.Game.Session.GameSession session) => f.Repository.LoadMailbox(
            session.CaptureSocialActor()!.Owner, session.Selected!.Id, f.Clock.GetUtcNow().ToUnixTimeSeconds()).Entries.Length;
        Strongway2019Statistics Statistics(DfoServer.Game.Session.GameSession session) => f.Repository.ReadStrongway2019Statistics(
            session.CaptureSocialActor()!.Owner, session.Selected!.Id);
    }

    sealed class FailingStore(IStrongway2019RewardStore inner) : IStrongway2019RewardStore
    {
        bool fail = true;
        public Strongway2019RewardState ReadStrongway2019RewardState(DfoServer.Game.Accounts.AccountOwners.Lease owner,
            Strongway2019RewardPeriod period, Strongway2019RewardBucket bucket) => inner.ReadStrongway2019RewardState(owner, period, bucket);
        public Strongway2019RewardCommit CommitStrongway2019Reward(DfoServer.Game.Accounts.AccountOwners.Lease owner,
            Strongway2019RewardPlan plan)
        {
            if (fail) { fail = false; throw new IOException("controlled reward store failure"); }
            return inner.CommitStrongway2019Reward(owner, plan);
        }
    }
    public static async Task RunTimeoutAutomaticReturnResourcesAsync(PvfArchive archive, TownCatalog towns, ItemResources resources)
    {
        int before = Count;
        var activity = new Strongway2019Catalog(archive);
        using var f = new Strongway2019MatchingTests.Fixture(towns, activity);
        var first = f.Team(); var second = f.Team(); var sessions = first.Concat(second).ToArray();
        var matching = new Strongway2019MatchingService(f.Parties, activity, f.Clock, serverEvents: f.Events);
        var ticket = matching.Queue(first[0], first.Select(session => session.PrepareStrongway2019Admission(5360, true)!).ToArray())!;
        matching.Queue(second[0], second.Select(session => session.PrepareStrongway2019Admission(5360, true)!).ToArray());
        var entry = new Strongway2019MatchingEntryService(matching, f.Parties, f.Repository, f.Repository, f.Clock)
            .Enter(matching.ReserveNext(ticket)!, DungeonCatalog.Load(archive), DungeonRoomCatalog.Load(archive),
                DungeonMonsterDropCatalog.Load(archive), resources.Items, resources.EpicPieces)!;
        using var log = new ServerLog(Path.Combine(Path.GetTempPath(), "strongway-handler-timeout-" + Guid.NewGuid().ToString("N")));
        var profiles = new Strongway2019ProfileProjection(f.Repository, activity.DungeonStartDelay);
        var projection = new Strongway2019Projection(new Strongway2019RewardProjection(resources.Items, f.Repository, log, profiles), matching, f.Clock);
        var handler = new Strongway2019Handler(matching, projection, catalog: activity, clock: f.Clock);
        var commands = new GameCommandRegistry(); handler.Register(commands); commands.Seal();
        var partyDungeons = new DfoServer.Game.Dungeons.PartyDungeonService(f.Parties, f.Repository);
        var stops = sessions.Select(_ => new CancellationTokenSource()).ToArray();
        var outputs = sessions.Select(_ => new MemoryStream()).ToArray();
        var frames = sessions.Select(_ => new List<(ushort Opcode, byte[] Body)>()).ToArray();
        var returned = new int[8]; var departureNotices = new int[8];
        var senders = sessions.Select((session, i) => new GameConnectionSender(session.CaptureSocialActor()!.Owner,
            outputs[i], stops[i], 0, (_, opcode, _, body, _) => frames[i].Add((opcode, body.ToArray())))).ToArray();
        var contexts = sessions.Select((session, i) => new GameConnectionContext(new(i, IPAddress.Loopback, outputs[i], stops[i]),
            session.CaptureSocialActor()!.Owner, session, senders[i])).ToArray();
        projection.Depart = async (context, holds) =>
        {
            int index = Array.IndexOf(sessions, context.Session);
            That(sessions.All(session => MailCount(session) == 0 && Statistics(session) == new Strongway2019Statistics(0, 0, 0, 0)),
                "超时自动退场前八人无737邮件、无虚构胜场或最佳用时");
            await projection.BeforeDepartureAsync(context);
            using var peers = await PartyDungeonProjection.AcquirePeersAsync(context, context.Session.PartyDungeon!.Members,
                holdsCommandGate: holds);
            That(await PartyDungeonProjection.DepartAsync(context, partyDungeons, f.Parties, () => Task.CompletedTask) == CommandResult.Handled,
                "超时返城复用原PartyDungeonService和原Departure路径");
            returned[index]++;
        };
        try
        {
            for (int i = 0; i < sessions.Length; i++)
            {
                int index = i;
                sessions[i].Strongway2019NoticeArrived = notice => projection.DeliverAsync(contexts[index], notice);
                sessions[i].PartyDungeonNoticeArrived = notice =>
                {
                    if (notice.Kind == DfoServer.Game.Dungeons.PartyDungeonNoticeKind.Departure) departureNotices[index]++;
                    return Task.CompletedTask;
                };
            }
            foreach (var team in new[] { first, second })
                foreach (var session in team) team[0].PartyDungeon!.CompleteLoading(session);
            foreach (uint phase in new uint[] { 3, 4 })
                foreach (int leader in new[] { 0, 4 })
                    await commands.DispatchAsync(contexts[leader], new Packet(1, 1525,
                        Network.Builders.Strongway2019Packets.Symbol(checked((uint)(leader / 4)), 7, phase)[1..]));
            That(entry.Match.Competition.Snapshot.Phase == Strongway2019CompetitionPhase.Running,
                "八个原Run真实加载并由原1525双队3/4握手启动600秒预算");
            f.Clock.Advance(TimeSpan.FromMilliseconds(599_999));
            await handler.MaintainAsync(CancellationToken.None);
            await Task.WhenAll(senders.Select(sender => sender.DrainAsync()));
            That(entry.Match.Competition.Snapshot.Terminal is null && frames.All(rows => rows.All(row => row.Opcode != 1494)) &&
                returned.All(count => count == 0), "真实维护在600秒前一毫秒不裁定或提前退场");
            f.Clock.Advance(TimeSpan.FromMilliseconds(1));
            await handler.MaintainAsync(CancellationToken.None);
            await Task.WhenAll(senders.Select(sender => sender.DrainAsync()));
            var terminal = entry.Match.Competition.Snapshot.Terminal;
            That(terminal is { Reason: Strongway2019FinishReason.Timeout, WinnerSide: null, ClearFact: null,
                    RewardParticipants.IsEmpty: true, CombatElapsedMilliseconds: 600_000 } &&
                frames.All(rows => rows.Single(row => row.Opcode == 1494).Body.SequenceEqual(new byte[] { 255, 0 })) &&
                sessions.All(session => ReferenceEquals(session.CurrentStrongway2019Match, entry.Match) && session.PartyDungeon is not null) &&
                returned.All(count => count == 0), "600秒维护向真实八连接发布同一FF00双败无奖结果，并保留结果展示期原副本");
            f.Clock.Advance(TimeSpan.FromSeconds(activity.ReturnVillageDelay) - TimeSpan.FromMilliseconds(1));
            await handler.MaintainAsync(CancellationToken.None);
            That(returned.All(count => count == 0), "超时结果的资源返城延迟前一毫秒保留全部原Run");
            f.Clock.Advance(TimeSpan.FromMilliseconds(1));
            await handler.MaintainAsync(CancellationToken.None);
            await handler.MaintainAsync(CancellationToken.None);
            await projection.TerminalAsync(entry.Match);
            await Task.WhenAll(senders.Select(sender => sender.DrainAsync()));
            That(returned.All(count => count == 1) && departureNotices.All(count => count == 1) &&
                sessions.All(session => session.Phase == DfoServer.Game.Session.SessionPhase.Town && session.PartyDungeon is null &&
                    session.CurrentStrongway2019Match is null && MailCount(session) == 0 && Statistics(session) == new Strongway2019Statistics(0, 0, 0, 0)) &&
                matching.Snapshot().Matches.Length == 0 &&
                frames.All(rows => rows.Count(row => row.Opcode == 1494) == 1 && rows.All(row => row.Opcode != 1491)) &&
                ReferenceEquals(entry.Match.Competition.Snapshot.Terminal, terminal),
                "超时后八连接各经原路径退场一次，无残留比赛、无重复1494、无奖励邮件或虚构1491，终局引用保持");
        }
        finally
        {
            foreach (var session in sessions) { session.Strongway2019NoticeArrived = null; session.PartyDungeonNoticeArrived = null; }
            foreach (var sender in senders) await sender.DisposeAsync();
            foreach (var output in outputs) output.Dispose();
            foreach (var stop in stops) stop.Dispose();
        }
        Console.WriteLine($"PASS {Count - before} 首期八连接600秒超时FF00无奖与资源延迟原路径返城");
        int MailCount(DfoServer.Game.Session.GameSession session) => f.Repository.LoadMailbox(
            session.CaptureSocialActor()!.Owner, session.Selected!.Id, f.Clock.GetUtcNow().ToUnixTimeSeconds()).Entries.Length;
        Strongway2019Statistics Statistics(DfoServer.Game.Session.GameSession session) => f.Repository.ReadStrongway2019Statistics(
            session.CaptureSocialActor()!.Owner, session.Selected!.Id);
    }

}
