using System.Collections.Immutable;
using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.Network;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AntonSymbolProjectionTests
{
    internal static async Task RunAsync()
    {
        int before = Count;
        var script = AntonRaidBattleTests.Script(210) with
        {
            Symbols = ImmutableDictionary<string, int>.Empty.Add("[hp]", 50)
                .Add("[is exist infection dungeon]", 7).Add("[infection dungeon index]", 8).Add("[movie smoke in]", 121)
                .Add("[kill all character]", 102)
        };
        var battle = new AntonRaidBattle(script);
        using var owner = new AccountOwners().Acquire("anton-symbol-test", () => { });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var stream = new MemoryStream();
        var frames = new List<(ushort Opcode, byte[] Body)>();
        await using var sender = new GameConnectionSender(owner, stream, stop, 0,
            (_, opcode, _, body, _) => frames.Add((opcode, body.ToArray())));
        var session = new GameSession(null!, null!, owner);
        var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), owner, session, sender);
        async Task Post(bool restore = false, bool valid = true)
        {
            RaidCombatProjection.PostAntonSnapshot(context, battle, battle.View, () => valid, restore);
            await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        await Post(valid: false);
        That(frames.Count == 0 && context.AntonSymbols.Key is null, "失效发布不确认符号基线");
        RaidCombatProjection.PostAntonSnapshot(context, battle, battle.View, () => true);
        await Post();
        That(frames.Count(row => row.Opcode == 583) == 1 && frames.Single(row => row.Opcode == 583).Body[0] == 3,
            "首次接收完整普通符号");
        frames.Clear();
        await Post(); await Post();
        That(frames.All(row => row.Opcode is not (583 or 585)), "无变化不发583，也不发会重置感染标志的585");
        battle.SetMechanism(battle.Instance, battle.View.Revision, "[hp]", 0);
        await Post();
        Bytes(frames.Single(row => row.Opcode == 583).Body, [1, 50, 0, 0, 0, 0, 0, 0, 0], "单房间HP变化只发一个符号，保留u8数量和i32字段");
        frames.Clear();
        await Post(restore: true);
        That(frames.Single(row => row.Opcode == 583).Body[0] == 3 && frames.Any(row => row.Opcode == 585), "选图恢复显式全量");
        frames.Clear();
        await sender.ClearScene(); await Post();
        That(frames.Single(row => row.Opcode == 583).Body[0] == 3, "换场景epoch后全量恢复");
        frames.Clear();
        battle.SetMechanism(battle.Instance, battle.View.Revision, "[infection dungeon index]", 221);
        await Post();
        That(frames.Any(row => row.Opcode == 585) && frames.Single(row => row.Opcode == 583).Body[0] == 2,
            "感染身份变化发585，并恢复被客户端隐式置1的symbol7及新身份符号");
        frames.Clear(); await Post();
        That(frames.All(row => row.Opcode is not (583 or 585)), "感染身份非零且不变也不重发");
        // 第二个接收者拥有独立的连接记账，不能继承第一人的已发送状态。
        using var stream2 = new MemoryStream();
        var frames2 = new List<byte[]>();
        await using var sender2 = new GameConnectionSender(owner, stream2, stop, 0,
            (_, opcode, _, body, _) => { if (opcode == 583) frames2.Add(body.ToArray()); });
        var context2 = new GameConnectionContext(new(2, IPAddress.Loopback, stream2, stop), owner, session, sender2);
        RaidCombatProjection.PostAntonSnapshot(context2, battle, battle.View, () => true);
        await sender2.DrainAsync();
        That(frames2.Single()[0] == 3, "新成员或重连独立全量");
        frames.Clear();
        RaidCombatProjection.PostAntonSceneSymbols(context, battle, battle.View, () => true);
        await sender.DrainAsync();
        That(frames.Single(row => row.Opcode == 583).Body[0] == 3 && frames.Count == 1,
            "30后恢复普通状态，即使值未变化也补发，排除影片事件与585");
        var key = context.AntonSymbols.Key!.Value;
        That(context.AntonSymbols.Changed(key with { Phase = key.Phase + 1 }, 50, 0) &&
            context.AntonSymbols.Changed(key with { Instance = Guid.NewGuid() }, 50, 0) &&
            context.AntonSymbols.Changed(key with { SelectionEpoch = key.SelectionEpoch + 1 }, 50, 0),
            "换阶段、换团及换角色均不继承旧符号基线");
        frames.Clear();
        battle.SetMechanism(battle.Instance, battle.View.Revision, "[kill all character]", 1);
        await Post(valid: false);
        That(frames.Count == 0, "失效炸团事件不投递或消费");
        await Post(); await Post();
        That(Kills(frames.Select(row => (row.Opcode, row.Body))) == 1, "同场景炸团只执行一次，不丢失正常失败效果");
        frames.Clear();
        await Post(restore: true);
        await sender.ClearScene(); await Post();
        RaidCombatProjection.PostAntonSceneSymbols(context, battle, battle.View, () => true);
        await sender.DrainAsync();
        That(Kills(frames.Select(row => (row.Opcode, row.Body))) == 0 &&
            battle.SymbolSnapshot(battle.View).All(row => row.Id != 102), "进图、恢复和完整状态快照不重播旧杀全员");
        frames2.Clear();
        await sender2.ClearScene();
        RaidCombatProjection.PostAntonSnapshot(context2, battle, battle.View, () => true);
        await sender2.DrainAsync();
        That(Kills(frames2.Select(body => ((ushort)583, body))) == 0, "重连接收者不继承旧炸团执行事件");
        frames.Clear();
        long previousEvent = battle.View.AntonKillAllEvent;
        battle.SetMechanism(battle.Instance, battle.View.Revision, "[kill all character]", 1);
        await Post(); await Post();
        That(battle.View.AntonKillAllEvent > previousEvent && Kills(frames.Select(row => (row.Opcode, row.Body))) == 1,
            "再次炸团即使仍SET1也执行新事件一次");
        Console.WriteLine($"PASS 安徒恩符号增量投影：{Count - before}项断言");

        static int Kills(IEnumerable<(ushort Opcode, byte[] Body)> packets) => packets.Count(row =>
            row.Opcode == 583 && Enumerable.Range(0, row.Body[0]).Any(index =>
                System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(row.Body.AsSpan(1 + index * 8)) == 102));
    }
}
