using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class PartyDungeonTcpTests
{
    public static async Task RunFavorNpcAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "npc-favor-dungeon"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("FavorDungeon", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0, 0, 20, candidate.InitialLearning with
                { Experience = resources.Growth.GetLevelStartExperience(20) }, resources.Learning);
            var role = repository.Create(owner, candidate with { Level = 20, CompletedQuests = [3145], InitialLearning = learning });
            InventoryService.Commit(repository, owner, role, new(true, 0,
                new(role.Inventory, role.Inventory with { NpcFavor = [new(2, 30000)] }, "test_favor_dungeon")));
        }
        await fixture.StartAsync();
        using var peer = Open(fixture);
        using var other = Open(fixture, "native-favor-other");
        uint token = U32(peer.LoginEndpoint, 5);
        int port = checked((int)U32(peer.LoginEndpoint, 13 + checked((int)U32(peer.LoginEndpoint, 9))));
        var rules = new NpcFavorCatalog(resources.Archive).Rules!;
        byte[] proposal = Record(2, rules.NameColor(30000, 40000), 7);
        peer.Select();
        await Rejected(proposal);
        FormalDungeonTcpTests.EntryTown(peer);
        byte[] information = [];
        var map = FormalDungeonTcpTests.Enter(peer, information: body => information = body, requestQuestId: 0);
        FormalDungeonTcpTests.Load(peer); Drain(peer);
        await Rejected(proposal);
        var maze = SelfTestCatalogs.Dungeons(resources.Archive).Get(3).Mazes[information[7]];
        var current = new DungeonPoint(map[0], map[1]);
        var goal = new DungeonPoint(information[8], information[9]);
        bool finished = false;
        for (int room = 0; room < 32 && !finished; room++)
        {
            foreach (int at in Enumerable.Range(0, map[18]).Select(index => 19 + 22 * index)
                .Where(at => map[at + 13] <= 3 && map[at + 21] == 0).OrderBy(at => map[at + 13] == 3))
            {
                peer.Send(39, FormalDungeonTcpTests.Death(U16(map, at + 6), peer.Id, map[at + 12]));
                var notices = Drain(peer);
                finished |= notices.Any(packet => packet is { Kind: 0, Opcode: 31, Body.Length: 0 });
            }
            if (finished) break;
            var parents = new Dictionary<DungeonPoint, DungeonPoint?> { [current] = null };
            var queue = new Queue<DungeonPoint>(); queue.Enqueue(current);
            while (queue.TryDequeue(out var point))
            foreach (var link in maze.StaticTopology.Links!)
            {
                DungeonPoint? next = link.First == point ? link.Second : link.Second == point ? link.First : null;
                if (next is { } adjacent && parents.TryAdd(adjacent, point)) queue.Enqueue(adjacent);
            }
            That(parents.ContainsKey(goal) && current != goal, "NPC出场集成沿真实普通迷宫清房到最终Boss");
            var target = goal;
            while (parents[target] is { } parent && parent != current) target = parent;
            var move = new byte[100]; move[0] = checked((byte)target.X); move[1] = checked((byte)target.Y);
            peer.Send(45, move); peer.Expect(1, 45, [1]); map = peer.Expect(0, 29).Body;
            current = target; FormalDungeonTcpTests.Load(peer); Drain(peer);
        }
        That(finished, "只有真实提交的终末击杀才授权好感NPC原生出场");
        string committed = fixture.Snapshot();
        await Rejected(Record(999999, rules.NameColor(30000, 40000), 8));
        using (var foreign = new TcpClient { NoDelay = true })
        {
            await foreign.ConnectAsync(IPAddress.Loopback, port);
            await foreign.GetStream().WriteAsync(Outer(token, U32(other.LoginEndpoint, 5), proposal));
            await foreign.GetStream().WriteAsync(Outer(0, token, proposal));
            That(await ReadOne(foreign) == 0, "不能把出场消息路由给其它账号或城镇连接");
        }
        using (var native = new TcpClient { NoDelay = true })
        {
            await native.ConnectAsync(IPAddress.Loopback, port);
            var request = Outer(token, token, proposal); request[0] = 0;
            await native.GetStream().WriteAsync(request);
            Bytes(await ReadFrame(native), Outer(token, token, proposal), "真实Native kind0请求按完整原生记录回送，不发游戏ACK");
            var repeated = Record(0, unchecked((int)0xFF5AD6F7), 9);
            await native.GetStream().WriteAsync(Outer(token, token, repeated));
            Bytes(await ReadFrame(native), Outer(token, token, Record(2, rules.NameColor(30000, 40000), 9)),
                "同局重复商人创建回放首次选择，不再次抽取或退回其它NPC");
            peer.Barrier();
            That(fixture.Snapshot() == committed, "NPC出现与重放不修改好感、钱包、物品、任务或数据库版本");
            peer.Send(42); Drain(peer);
            await native.GetStream().WriteAsync(Outer(token, token, proposal));
            await native.GetStream().WriteAsync(Outer(0, token, proposal));
            That(await ReadOne(native) == 0, "离开副本后旧实例出场消息不能发布到城镇");
        }
        other.Send(692, Name("NativeAlive")); other.Expect(1, 692, [1]);

        async Task Rejected(byte[] body)
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, port);
            await client.GetStream().WriteAsync(Outer(token, token, body));
            // 同一Native流的非法关联作为顺序屏障，不靠睡眠猜测前一帧是否处理完成。
            await client.GetStream().WriteAsync(Outer(0, token, body));
            That(await ReadOne(client) == 0, "非清图上下文或伪造NPC不回送，主连接不受影响");
        }
    }

    static async Task VerifyFavorNpcNativeAsync(TcpPeer alpha, TcpPeer beta, TcpTestFixture fixture)
    {
        int port = checked((int)U32(alpha.LoginEndpoint, 13 + checked((int)U32(alpha.LoginEndpoint, 9))));
        uint leader = U32(alpha.LoginEndpoint, 5), member = U32(beta.LoginEndpoint, 5);
        using var first = new TcpClient { NoDelay = true };
        using var second = new TcpClient { NoDelay = true };
        await first.ConnectAsync(IPAddress.Loopback, port);
        await second.ConnectAsync(IPAddress.Loopback, port);
        await first.GetStream().WriteAsync(Outer(leader, leader, [0xff]));
        await second.GetStream().WriteAsync(Outer(member, member, [0xff]));
        await NativeChannelTcpTests.Seen(fixture, "native_associated", 2);
        string committed = fixture.Snapshot();
        var proposal = Record(0, unchecked((int)0xFF5AD6F7), 7);
        // 前置可靠记录必须整批转发，否则后续NPC记录会被客户端顺序队列阻塞。
        byte[] preceding = [1, 6, 0, 0, 0, 1, 0, 0, 0, 0xff];
        byte[] body = [.. preceding, .. proposal];
        await first.GetStream().WriteAsync(Outer(leader, member, body));
        Bytes(await ReadFrame(second), Outer(leader, member, body), "同一已清房实例按原始序列整批中继给真实成员");
        proposal[7] = 1;
        await second.GetStream().WriteAsync(Outer(member, leader, proposal));
        await second.GetStream().WriteAsync(Outer(0, member, proposal));
        That(await ReadOne(second) == 0 && first.Available == 0, "非队长不能替换当前NPC，拒绝不发布给队长");
        alpha.Barrier(); beta.Barrier();
        That(fixture.Snapshot() == committed, "原生中继不产生角色保存或奖励回执");
    }

    static async Task<int> ReadOne(TcpClient client)
    {
        try { return await client.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (IOException error) when (error.InnerException is SocketException { SocketErrorCode:
            SocketError.ConnectionReset or SocketError.ConnectionAborted }) { return 0; }
    }
    static async Task<byte[]> ReadFrame(TcpClient client)
    {
        var head = new byte[13];
        await client.GetStream().ReadExactlyAsync(head).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(2));
        That(count is >= 13 and <= 8192, "原生响应长度位于客户端缓冲边界");
        var result = new byte[count]; head.CopyTo(result, 0);
        await client.GetStream().ReadExactlyAsync(result.AsMemory(13)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        return result;
    }
    static byte[] Outer(uint from, uint to, byte[] body) => Build(w =>
    { w.Write((ushort)1); w.Write((ushort)(13 + body.Length)); w.Write(from); w.Write(to); w.Write((byte)0); w.Write(body); });

    // 独立的原码CRC及固定解码密钥向量；不给测试请求借用生产Parser/Builder。
    static byte[] Record(int npc, int color, uint sequence)
    {
        byte[] plain = Build(w => { w.Write(npc); w.Write(color); w.Write(0u); });
        uint crc = uint.MaxValue;
        foreach (byte value in plain)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = crc >> 1 ^ ((crc & 1) != 0 ? 0x4DB89129u : 0);
        }
        var encrypted = plain.Select(value => { int x = value ^ 0x6E; return (byte)(x >> 3 | x << 5); }).ToArray();
        return Build(w => { w.Write((byte)2); w.Write(sequence); w.Write((ushort)19); w.Write((byte)0); w.Write((byte)0);
            w.Write((byte)2); w.Write((ushort)140); w.Write(~crc); w.Write(encrypted); });
    }
}
