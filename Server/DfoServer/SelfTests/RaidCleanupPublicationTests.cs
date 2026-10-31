using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class RaidCleanupPublicationTests
{
    public static void RunCore() => RunAsync().GetAwaiter().GetResult();

    static async Task RunAsync()
    {
        foreach (string mode in new[] { "end", "roster", "town", "stale", "concurrent", "publication-fault", "combined-fault", "record-fault", "record-publication-fault" })
        {
            await using var fixture = new Fixture(mode);
            var actors = fixture.Actors;
            var departing = actors[1];
            long epoch = departing.Session.SelectionEpoch;
            var original = fixture.Registry.Find(actors[0])!;
            That(original.Started is not null && fixture.Battle.View.State == AntonRaidBattleState.Fighting &&
                fixture.Battle.RosterClears()![actors[0].Character] == 1,
                "清理夹具真实开战并记录一次权威清图，不用未开战早退路径冒充跨账号投影");
            if (mode == "end") departing.Session.End();
            else if (mode == "roster") departing.Session.ReturnToRoster();
            else if (mode == "town")
            {
                await fixture.Town.ReturnToSelectionAsync(fixture.Contexts[1], () => false);
                That(departing.IsCurrent && ReferenceEquals(fixture.Registry.Find(actors[0]), original),
                    "返回选角资格拒绝仍在最外owner中复验，不清名册或推进epoch");
                await fixture.Town.ReturnToSelectionAsync(fixture.Contexts[1], () => true);
                That(fixture.Contexts[1].RosterRequested && fixture.Contexts[1].SelectionRosterPushed,
                    "真实TownProjection外层owner退出后完成收尾发布及原选角名册流程");
            }
            else if (mode == "stale")
            {
                var replacement = fixture.Replace(1);
                var newParty = fixture.Registry.Create(replacement, 0, "new", "new"u8.ToArray(), 20)!;
                departing.Session.End();
                That(replacement.IsCurrent && ReferenceEquals(fixture.Registry.Find(replacement), newParty) &&
                    replacement.Session.Ready && replacement.Session.Selected is not null,
                    "旧generation收尾只移除旧Session/epoch，不能失效接管者及其新团");
            }
            else if (mode == "concurrent")
            {
                using var ready = new CountdownEvent(2);
                using var go = new ManualResetEventSlim();
                Task[] ends = actors.Skip(1).Select(actor => Task.Run(() =>
                { ready.Signal(); That(go.Wait(TimeSpan.FromSeconds(5)), "并发断开释放屏障"); actor.Session.End(); })).ToArray();
                try { That(ready.Wait(TimeSpan.FromSeconds(5)), "两个账号就绪后同时断开"); }
                finally { go.Set(); }
                await Task.WhenAll(ends).WaitAsync(TimeSpan.FromSeconds(5));
                That(actors[2].Session.Selected is null && !actors[2].Session.Ready,
                    "并发断开各自完成本地失效，不互相持锁等待或遗留选角");
            }
            else if (mode is "record-fault" or "record-publication-fault")
            {
                var recordError = new IOException("raid-cleanup-record-fault");
                fixture.InjectDisconnectRecordFailure(recordError);
                var originalNotice = actors[0].Session.RaidNoticeArrived;
                var publishError = new IOException("raid-cleanup-record-publication-fault");
                if (mode == "record-publication-fault") actors[0].Session.RaidNoticeArrived = (_, _) => throw publishError;
                try
                {
                    try { departing.Session.End(); throw new Exception("expected disconnect record failure"); }
                    catch (IOException error) when (mode == "record-fault")
                    { That(ReferenceEquals(error, recordError), "真实CommitAntonDisconnect记录异常仍原样传播"); }
                    catch (AggregateException error) when (mode == "record-publication-fault")
                    {
                        That(error.InnerExceptions.Count == 2 && ReferenceEquals(error.InnerExceptions[0], recordError) &&
                            ReferenceEquals(error.InnerExceptions[1], publishError), "真实记录异常及随后发布异常都保留，不能覆盖首因");
                    }
                }
                finally { actors[0].Session.RaidNoticeArrived = originalNotice; }
                That(fixture.RecordFailureCalls == 1, "已提交后记录只调用一次故障时钟，不重试或补写候选");
            }
            else
            {
                var originalNotice = actors[0].Session.RaidNoticeArrived;
                var publishError = new IOException("raid-cleanup-publication-fault");
                actors[0].Session.RaidNoticeArrived = (_, _) => throw publishError;
                try
                {
                    if (mode == "publication-fault")
                    {
                        try { departing.Session.End(); throw new Exception("expected publication failure"); }
                        catch (IOException error) { That(ReferenceEquals(error, publishError), "发布异常原样传播而非吞掉"); }
                    }
                    else
                    {
                        var resetError = new IOException("raid-cleanup-activity-fault");
                        var context = fixture.Context(1, new FailingActivity(resetError));
                        try { await fixture.Town.ReturnToSelectionAsync(context); throw new Exception("expected combined failure"); }
                        catch (AggregateException error)
                        {
                            That(error.InnerExceptions.Count == 2 && ReferenceEquals(error.InnerExceptions[0], resetError) &&
                                ReferenceEquals(error.InnerExceptions[1], publishError), "本地后续活动异常和锁外发布异常均保留，不覆盖首因");
                        }
                    }
                }
                finally { actors[0].Session.RaidNoticeArrived = originalNotice; }
            }
            That(departing.Session.Selected is null && !departing.Session.Ready &&
                departing.Session.Phase == SessionPhase.Unselected && departing.Session.SelectionEpoch == epoch + 1 &&
                departing.Session.StartedRaid is null &&
                (!departing.Owner.IsCurrent || departing.Session.RaidCombatView() is null) && fixture.Registry.Find(departing) is null,
                "正常、接管和发布异常路径均完整失效本地选角/战斗/名册，不能停在半清理状态");
            var after = fixture.Registry.Find(actors[0])!;
            RaidActor[] survivors = mode == "concurrent" ? [actors[0]] : [actors[0], actors[2]];
            That(after.Members.Select(m => m.Actor).SequenceEqual(survivors) &&
                after.Members.Select(m => m.Slot).SequenceEqual(mode == "concurrent" ? new byte[] { 0 } : [0, 2]) &&
                ReferenceEquals(actors[0].Session.CurrentRaidBattle(actors[0]), fixture.Battle),
                "幸存名册保持原Session/epoch/槽位及原战斗，不以重建团或替换身份规避清理");
            That(after.Revision > original.Revision && after.Key == original.Key &&
                after.Members.Length == (mode == "concurrent" ? 1 : 2) && !fixture.Registry.IsCurrent(original),
                "移除回执保留真实名册key并推进revision，旧快照立即失效");
            if (mode is not ("publication-fault" or "combined-fault" or "record-publication-fault"))
                await fixture.VerifyNotices(after);
            else
                await fixture.VerifyInformation(after);
        }
    }

    sealed class FailingActivity(Exception error) : IGameConnectionActivity
    {
        public bool BlocksTownActions => false;
        public void Reset() => throw error;
    }

    sealed class FaultClock : TimeProvider
    {
        public Exception? Failure { get; set; }
        public Action? BeforeFailure { get; set; }
        public int FailureCalls { get; private set; }
        public override long GetTimestamp()
        {
            if (Failure is not { } error) return 0;
            FailureCalls++; BeforeFailure?.Invoke(); throw error;
        }
    }

    sealed class Fixture : IAsyncDisposable
    {
        readonly string root;
        readonly AccountOwners owners = new();
        readonly CharacterRepository repository;
        readonly TownCatalog towns;
        readonly List<AccountOwners.Lease> leases = [];
        readonly List<GameSession> sessions = [];
        readonly MemoryStream[] streams = [new(), new(), new()];
        readonly CancellationTokenSource[] stops = [new(), new(), new()];
        readonly GameConnectionSender[] senders;
        readonly ServerLog log;
        readonly GameCommandRegistry commands = new();
        FaultClock? recordClock;
        public RaidRegistry Registry { get; } = new();
        public RaidActor[] Actors { get; }
        public GameConnectionContext[] Contexts { get; }
        public TownProjection Town { get; }
        public AntonRaidBattle Battle { get; }

        public Fixture(string mode)
        {
            root = Path.Combine(Path.GetTempPath(), "s5a9-raid-cleanup-" + mode + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var origin = new Location(1, 0, 10, 10);
            var map = new TownMap("cleanup.map", [new(new(0, 0, 30, 30), [], [])], [], false, []);
            towns = new(new() { [(1, 0)] = new(1, 0, 1, [], [], [], origin, map, [], []),
                [(19, 2)] = new(19, 2, 1, [], [], [], null,
                    map with { Links = [new(new(10, 6, 4, 10), 1, 0, [], [])] }, [], []) },
                origin, origin, new(0, [], []), [], new Dictionary<uint, uint[]>());
            repository = new(Path.Combine(root, "game.db"));
            var pieces = new EpicPieceCatalog("[piece list]\n9001 9002\n[/piece list]");
            var parties = new PartyService(repository);
            Town = new(towns, new CharacterStatComputer(new CharacterCatalog(new())), null, pieces, parties: parties);
            Actors = Enumerable.Range(0, 3).Select(Make).ToArray();
            var leader = Actors[0];
            Registry.Create(leader, 0, "cleanup", "cleanup"u8.ToArray(), 20);
            foreach (var actor in Actors.Skip(1))
            {
                That(Registry.Invite(leader, actor, 0) is not null && Registry.Respond(actor, leader.Character, true).Accepted,
                    "清理夹具通过真实邀请加入");
            }
            foreach (var actor in Actors) That(Registry.Assign(leader, actor.Character, 1) is not null, "清理夹具真实分队");
            var script = new RaidScript("synthetic/cleanup.etc", new(19, 2), new(100, 1), null,
                ImmutableDictionary<string, int>.Empty.Add("[hp]", 1),
                [new("first", [new(210, RaidDungeonState.Open, "[hp]", 4, 5)], [], [])], [], []);
            var definition = new RaidDefinition(0, script.Path, 20, 1, 1) { Resources = new(script, [], [], [], [], []) };
            That(new AntonRaidStart(Registry, parties, repository, towns, pieces).Start(leader, Registry.Find(leader)!, definition)
                is { Error: 0, Receipt: not null }, "清理夹具真实事务扣票开战");
            Battle = leader.Session.CurrentRaidBattle(leader)!;
            var identity = new DungeonRunIdentity(leader.Character, 0, 1, Guid.NewGuid(), 1);
            That(Battle.BindDungeon(identity, 210) && Battle.CompleteDungeon(new(identity, 210, [Guid.NewGuid()])
                { ClearMap = new("boss", new(0, 0), 1, "cleanup.map") }) is not null,
                "通过实际绑定及清图事实产生非零成员计数，不手改展示账本");
            senders = Actors.Select((actor, i) => new GameConnectionSender(actor.Owner, streams[i], stops[i], 0, (_, _, _, _, _) => { })).ToArray();
            Contexts = Enumerable.Range(0, 3).Select(i => Context(i)).ToArray();
            for (int i = 0; i < 3; i++)
            {
                var context = Contexts[i];
                Actors[i].Session.RaidNoticeArrived = (change, actor) => RaidProjection.Notice(context, change, actor);
            }
            log = new(Path.Combine(root, "logs"));
            new RaidHandler(log, new RaidCatalog(ImmutableDictionary<byte, RaidDefinition>.Empty.Add(0, definition)), Registry).Register(commands);
            commands.Seal();
        }

        RaidActor Make(int index)
        {
            var lease = owners.Acquire("cleanup-" + index, () => { }); leases.Add(lease);
            if (repository.ListRoster(lease).Length == 0)
                repository.Create(lease, new("Clean" + index, towns.InitialReturn, towns.InitialReturn)
                { Level = 85, CompletedQuests = [12829, 3779], Inventory = new([new InventoryItem(0,
                    new(InventoryListType.Main, 121), new(100, ItemKind.Material, 2))]) });
            var session = new GameSession(repository, towns, lease, entry: new(new(2, 19), 23, "[raid]"));
            sessions.Add(session); session.Select(0); session.MarkReady();
            return session.CaptureRaidActor(Registry)!;
        }
        public int RecordFailureCalls => recordClock?.FailureCalls ?? 0;

        public void InjectDisconnectRecordFailure(Exception error)
        {
            recordClock = new FaultClock();
            var candidates = new AntonRaidRejoin(true, clock: recordClock);
            Registry.BindRejoin(candidates);
            var actor = Actors[0];
            var seed = Registry.CaptureAntonDisconnect(actor, Battle, null, false)!;
            // 仅故障注入：沿已有记录API播种到期扫描项，使随后真实Record在注入时钟处失败。
            // 不把这个合成deadline条目当作卢克重返资格，也不注入生产回调。
            That(seed is not null && candidates.Record(seed with { LukeDeadlineAnchor = 0 }), "记录故障夹具确实安装原实例候选");
            var departing = Actors[1]; long epoch = departing.Epoch;
            recordClock.BeforeFailure = () => That(Registry.Find(departing) is null &&
                departing.Session.Selected is null && !departing.Session.Ready &&
                departing.Session.Phase == SessionPhase.Unselected && departing.Session.SelectionEpoch == epoch + 1,
                "故障只在名册移除和本地失效完成后的记录扫描触发，不是Capture或其它前置失败");
            recordClock.Failure = error;
        }
        public RaidActor Replace(int index) => Make(index);
        public GameConnectionContext Context(int index, IGameConnectionActivity? activity = null) => new(
            new(index + 1, IPAddress.Loopback, streams[index], stops[index]), Actors[index].Owner, Actors[index].Session, senders[index], activity);

        public async Task VerifyNotices(RaidParty after)
        {
            await senders[0].DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var updates = Frames(streams[0]).Where(p => p.Kind == 0 && p.Opcode == 591 && p.Body.Length >= 8 &&
                BinaryPrimitives.ReadUInt32LittleEndian(p.Body.AsSpan(4)) == 3).ToArray();
            That(updates.Length > 0, "退出后真实Notice发出591成员完整更新");
            Bytes(updates[^1].Body, Expected(after, information: false), "591保留幸存成员完整字段、分队和非零通关数，不降为零计数");
            await VerifyInformation(after);
        }
        public async Task VerifyInformation(RaidParty after)
        {
            byte[] request = new byte[5]; request[0] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), after.Key);
            That(await commands.DispatchAsync(Contexts[0], new Packet(1, 1361, request)) == CommandResult.Handled,
                "退出后1361沿真实Handler查询当前名册");
            await senders[0].DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var reply = Frames(streams[0]).Last(p => p.Kind == 1 && p.Opcode == 1361);
            Bytes(reply.Body, Expected(after, information: true), "1361完整成员字段及非零通关数与已提交名册一致");
        }
        byte[] Expected(RaidParty after, bool information)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            if (information) { writer.Write((byte)1); writer.Write((byte)1); writer.Write(after.Key); }
            else { writer.Write(after.Key); writer.Write(3u); }
            writer.Write(checked((byte)after.Members.Length));
            foreach (var member in after.Members.OrderBy(m => m.Slot))
            {
                byte[] name = CharacterName.Encoding.GetBytes(member.Actor.Name);
                writer.Write(member.Actor.Character); writer.Write((byte)0); writer.Write((uint)name.Length); writer.Write(name);
                writer.Write(new byte[] { 0, 0, 1, 0 }); writer.Write(0u); writer.Write((ushort)0);
                writer.Write(member.Actor.Character == Actors[0].Character ? (byte)1 : (byte)0);
                writer.Write(new byte[35]);
            }
            return stream.ToArray();
        }
        static List<Packet> Frames(MemoryStream stream)
        {
            byte[] bytes = stream.ToArray(); List<Packet> result = [];
            for (int at = 0; at < bytes.Length;)
            {
                int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 3));
                That(length >= 16 && at + length <= bytes.Length, "内存发送泵保留完整帧边界");
                result.Add(new(bytes[at], BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at + 1)), bytes[(at + 16)..(at + length)]));
                at += length;
            }
            return result;
        }
        public async ValueTask DisposeAsync()
        {
            if (recordClock is not null) recordClock.Failure = null;
            foreach (var session in sessions) session.RaidNoticeArrived = null;
            foreach (var session in sessions) session.End();
            foreach (var sender in senders) await sender.DisposeAsync();
            foreach (var stop in stops) stop.Dispose();
            foreach (var stream in streams) stream.Dispose();
            foreach (var lease in leases) lease.Dispose();
            log.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }
}
