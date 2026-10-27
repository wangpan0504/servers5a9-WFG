using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Net;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Network;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class NativeGameplayRecordsTests
{
    public static async Task RunAsync()
    {
        int before = Count;
        // 独立按原码LCG、旋转和CRC计算的固定向量，不调用生产Builder生成请求。
        byte[] wire = Convert.FromHexString("010700000013000001028C009A3805A88DCDCDCD33178632CDCDCDCD");
        var rows = NativeGameplayRecords.Read(wire);
        var row = rows.Single();
        That(row is { Kind: 1, Sequence: 7, SourceSlot: 0, Appearance.NpcId: 2, Appearance.Colors: 0 } &&
            row.Appearance.NameColor == unchecked((int)0xFF5AD6F7), "原生可靠记录、UDP140校验及解码使用独立固定向量");
        Bytes(NativeGameplayRecords.WithAppearance(row, row.Appearance!), wire, "未变更选择保持完整原生传输记录");
        var replaced = NativeGameplayRecords.WithAppearance(row, new(17, unchecked((int)0xFF5AD6F7), 0x01020304));
        That(NativeGameplayRecords.Read(replaced).Single().Appearance is { NpcId: 17, Colors: 0x01020304 } &&
            replaced.AsSpan(0, 12).SequenceEqual(wire.AsSpan(0, 12)), "冻结结果只重建UDP正文及CRC，保留可靠序号与槽位");
        byte[] ack = [0, 0, 7, 0, 0, 0, 0, 0];
        That(NativeGameplayRecords.Read([..wire, ..ack]).Length == 2, "合并的数据与确认记录按边界逐条读取");
        foreach (var invalid in new byte[][] { wire[..^1], [3], [0, 0], [..wire[..7], 8, ..wire[8..]],
            [..wire[..12], 0, 0, 0, 0, ..wire[16..]], [1, 0, 0, 0, 0, 3, 0, 0, 0, 2, 140, 0], new byte[NativeGameplayRecords.MaximumLength + 1] })
            Reject<InvalidDataException>(() => NativeGameplayRecords.Read(invalid), "坏记录、校验、槽位及伪装UDP140不能绕过资格验证");
        var frame = NativeFrame.Build(0x12345678, 0x90ABCDEF, wire);
        That(frame.Length == wire.Length + 13 && frame[0] == 1 && BitConverter.ToUInt32(frame, 8) == 0x90ABCDEF,
            "原生回送保留源与目标关联，不套普通游戏帧");
        frame[0] = 0;
        That(NativeFrame.Parse(frame) is { Kind: 0, Association: 0x12345678, Route: 0x90ABCDEF }, "接纳原码中的kind0中继发送分支");
        NativeObjectStateTests.RunCore();
        Domain();
        using var lifetime = new CancellationTokenSource();
        using var block = new BlockedStream();
        var connection = new ConnectionContext(1, IPAddress.Loopback, block, lifetime);
        await using (var sender = new NativeConnectionSender(connection, CancellationToken.None, connection.Disconnect))
        {
            That(sender.Post(frame, () => true), "第一条原生发送进入独立写泵");
            await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (int index = 0; index < 32; index++) That(sender.Post(frame, () => true), "慢连接队列保留有界容量");
            That(!sender.Post(frame, () => true) && lifetime.IsCancellationRequested, "超限仅关闭本条原生连接，不无界积压");
        }
        Console.WriteLine($"PASS {Count - before} Native中继、UDP140、资格与发送队列断言");
    }

    static void Domain()
    {
        var target = new NpcFavorTarget(2, 40000) { DungeonExceptions = [70], Shapes = [(6356, 175)] };
        var rules = new NpcFavorRules(20, 100, 5, ImmutableDictionary<int, NpcFavorGift>.Empty, [], [0]);
        var catalog = new NpcFavorCatalog(id => id == 2 ? target : null, rules);
        var service = new NpcFavorAppearanceService(catalog);
        var role = new Character(1, 0, "FavorNative", 0, new(1, 0, 10, 10), null)
            { Level = 20, Inventory = new([]) { NpcFavor = [new(2, 30000)] } };
        var map = new DungeonMap("boss", new(0, 0), 1, "synthetic.map");
        var maze = new DungeonMaze(0, 1, 1, "[g]", [map.Position], [map.Position], [map], []);
        var entry = new DungeonEntryPlan(1, 0, 3, 0, 0, 0, maze, map, map);
        var context = new NpcFavorDungeonCapture(role, 1, 1, entry,
            new(new(1, 0, 1, Guid.NewGuid(), 1), SinglePlayerDungeonPhase.Settlement, map, []), null);
        var appearance = new NpcFavorAppearance(2, unchecked((int)0xFF5AD6F7), 0);
        That(service.Validate(context, appearance), "实际保存的二级好感允许客户端选择出场");
        foreach (var invalid in new[] { appearance with { NpcId = 999 }, appearance with { Colors = 1 }, appearance with { NameColor = 0 } })
            That(!service.Validate(context, invalid), "未知NPC和伪造外观不能变成受信出场结果");
        That(!service.Validate(context with { Entry = entry with { DungeonId = 70 } }, appearance), "按NPC资源排除指定副本");
        That(!service.Validate(context with { Character = role with { Level = 19 } }, appearance), "低等级不能伪造已解锁的NPC");
        That(!service.Validate(context, appearance with { NpcId = 175 }) &&
            service.Validate(context with { Character = role with { CompletedQuests = [6356] } }, appearance with { NpcId = 175 }),
            "任务变体外观只按完成事实映射，不凭任意子NPC编号放行");
        That(service.Validate(context with { Character = role with { Inventory = InventorySnapshot.Empty } },
            appearance with { NpcId = 0 }), "没有候选时仍保留原生默认商人回退");
    }

    sealed class BlockedStream : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}
