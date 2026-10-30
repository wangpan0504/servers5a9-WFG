using System.Net;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers.Inventory;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class AvatarEmblemMalformedGuardTests
{
    public static async Task RunCoreAsync()
    {
        int checks = Count;
        string root = Path.Combine(Path.GetTempPath(), "s5a9-avatar-emblem-malformed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool passed = false;
        try
        {
            foreach (var (_, run) in Cases(root)) await run();
            passed = true;
            Console.WriteLine($"PASS {Count - checks} 装扮徽章malformed会话发布断言");
        }
        finally { if (passed) Directory.Delete(root, true); else Console.WriteLine("FAIL fixture: " + root); }
    }

    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string root)
    {
        yield return ("未选角malformed不发布", () => Unselected(Path.Combine(root, "unselected")));
        yield return ("当前选角malformed仅generic1", () => Current(Path.Combine(root, "current")));
        yield return ("旧epoch的malformed排队回包不发布", () => ChangedEpoch(Path.Combine(root, "epoch")));
        yield return ("旧owner保持既有Sender拒绝", () => ChangedOwner(Path.Combine(root, "owner")));
    }

    static async Task Unselected(string root)
    {
        using var fixture = new Fixture(root);
        fixture.Session.ReturnToRoster();
        string saved = fixture.Saved;
        await using var peer = new Peer(fixture);
        That(await peer.StartMalformed() == CommandResult.Handled, "malformed仍由实际C201注册承接");
        await peer.Drain();
        That(peer.Acks.Count == 0 && peer.Bytes == 0 && fixture.Saved == saved,
            "无选角捕获不得发布malformed结果，资产不变");
    }

    static async Task Current(string root)
    {
        using var fixture = new Fixture(root);
        string saved = fixture.Saved;
        await using var peer = new Peer(fixture);
        That(await peer.StartMalformed() == CommandResult.Handled, "当前选角malformed由真实handler承接");
        await peer.Drain();
        That(peer.Acks.Count == 1 && peer.Writes == 1 && peer.Acks[0].AsSpan().SequenceEqual(new byte[] { 0, 1, 0 }) &&
            peer.Bytes > 0 && fixture.Saved == saved,
            "当前选角只回success0和未命名generic1，不借已证无孔码4或修改资产");
    }

    static async Task ChangedEpoch(string root)
    {
        using var fixture = new Fixture(root);
        string saved = fixture.Saved;
        await using var peer = new Peer(fixture);
        var first = peer.BlockFirstWrite();
        await peer.WaitForFirstWrite();
        long pending = peer.PendingBytes;
        var malformed = peer.StartMalformed();
        That(!malformed.IsCompleted && peer.PendingBytes > pending,
            "在实际首帧写入门后确定性排入malformed回包，不靠sleep猜发送阶段");
        fixture.Session.ReturnToRoster(); fixture.Session.Select(0); fixture.Session.MarkReady();
        peer.ReleaseFirstWrite();
        await first;
        That(await malformed == CommandResult.Handled, "切角后命令完成且连接仍可排空");
        await peer.Drain();
        That(peer.Acks.Count == 0 && peer.Writes == 1 && peer.Bytes == peer.FirstFrameBytes && fixture.Saved == saved,
            "重新选角后仅已进入写入的前置帧落流，旧malformed ACK在Sender泵中被原捕获守卫抑制");
    }

    static async Task ChangedOwner(string root)
    {
        using var fixture = new Fixture(root);
        string saved = fixture.Saved;
        await using var peer = new Peer(fixture);
        var first = peer.BlockFirstWrite();
        await peer.WaitForFirstWrite();
        long pending = peer.PendingBytes;
        var malformed = peer.StartMalformed();
        That(!malformed.IsCompleted && peer.PendingBytes > pending, "owner接管前已确定性排入原请求应答");
        using var replacement = fixture.Owners.Acquire(fixture.Owner.Account, () => { });
        peer.ReleaseFirstWrite();
        bool firstRejected = await Stale(first);
        bool malformedRejected = await Stale(malformed);
        That(firstRejected && malformedRejected && !fixture.Owner.IsCurrent && replacement.IsCurrent &&
            peer.Acks.Count == 0 && peer.Writes == 1 && peer.Bytes == peer.FirstFrameBytes,
            "原Sender在旧generation失效时结束旧发送，不把malformed结果发新owner");
        That(JsonSerializer.Serialize(fixture.Repository.List(replacement).Single()) == saved,
            "新owner冷重读资产与原快照一致，测试不借旧lease读库");
    }

    static async Task<bool> Stale(Task task)
    {
        try { await task; }
        catch (InvalidOperationException error) when (error.Message == "Stale account generation") { return true; }
        return false;
    }

    sealed class Fixture : IDisposable
    {
        public CharacterRepository Repository { get; }
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease Owner { get; }
        public TownCatalog Towns { get; }
        public GameSession Session { get; }
        public string Root { get; }
        public string Saved => JsonSerializer.Serialize(Repository.List(Owner).Single());
        public Fixture(string root)
        {
            Root = root;
            Directory.CreateDirectory(root);
            Repository = new CharacterRepository(Path.Combine(root, "game.db"));
            Owner = Owners.Acquire("mr278-malformed-fixture", () => { });
            var point = new Location(1, 0, 10, 10);
            var map = new TownMap("map/c201-synthetic.map", [new(new(0, 0, 100, 100), [], [])], [], true, []);
            Towns = new(new() { [(1, 0)] = new(1, 0, 1, [], [], [], point, map, [], []) },
                point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var created = Repository.Create(Owner, new("C201Guard", point, point) { Level = 95 });
            Session = new(Repository, Towns, Owner);
            Session.Select(created.Slot); Session.MarkReady();
        }
        public void Dispose() => Owner.Dispose();
    }

    sealed class Peer : IAsyncDisposable
    {
        readonly CancellationTokenSource stop = new();
        readonly FirstWriteGate stream = new();
        readonly ServerLog log;
        readonly GameConnectionSender sender;
        readonly GameCommandRegistry commands = new();
        readonly GameConnectionContext context;
        public List<byte[]> Acks { get; } = [];
        public long Bytes => stream.Length;
        public int Writes => stream.Writes;
        public int FirstFrameBytes => stream.FirstFrameBytes;
        public long PendingBytes => sender.PendingBytes;
        public Peer(Fixture fixture)
        {
            log = new ServerLog(fixture.Root);
            sender = new(fixture.Owner, stream, stop, 0, (kind, opcode, _, body, _) =>
            {
                if (kind == 1 && opcode == 201) Acks.Add(body.ToArray());
            });
            context = new(new(278, IPAddress.Loopback, stream, stop), fixture.Owner, fixture.Session, sender);
            // malformed不会运行资源或投影；使用空只读目录，若误入资源路径应保持异常可见。
            var items = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
            var stats = new CharacterStatComputer(new CharacterCatalog(new()));
            var pieces = new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]");
            var projection = new InventoryProjection(stats, new TownProjection(fixture.Towns, stats, null, pieces), pieces);
            new AvatarEmblemHandler(new InventoryAvatarEmblemService(items), projection, log).Register(commands);
            commands.Seal();
        }
        public Task<CommandResult> StartMalformed() => commands.DispatchAsync(context, new Packet(1, 201, [1, 7, 0, 1, 2, 3, 4]));
        public Task BlockFirstWrite()
        {
            stream.Arm();
            return sender.Send(0, 2, [0], "mr278_guard_fixture_prefix");
        }
        public Task WaitForFirstWrite() => stream.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        public void ReleaseFirstWrite() => stream.Release();
        public Task Drain() => sender.DrainAsync();
        public async ValueTask DisposeAsync()
        {
            stream.Release();
            await sender.DisposeAsync();
            log.Dispose(); stop.Dispose(); stream.Dispose();
        }
    }

    // 只阻塞一次真实WriteAsync，用可观测信号控制排队与切角的先后，不替换Sender逻辑。
    sealed class FirstWriteGate : MemoryStream
    {
        readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool armed;
        public Task Entered => entered.Task;
        public int Writes { get; private set; }
        public int FirstFrameBytes { get; private set; }
        public void Arm() => armed = true;
        public void Release() => released.TrySetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (armed)
            {
                armed = false;
                FirstFrameBytes = buffer.Length;
                entered.TrySetResult();
                await released.Task.WaitAsync(cancellationToken);
            }
            await base.WriteAsync(buffer, cancellationToken);
            Writes++;
        }
    }
}
