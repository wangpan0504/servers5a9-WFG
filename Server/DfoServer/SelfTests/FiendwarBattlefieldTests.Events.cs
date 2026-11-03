using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Raids;
using DfoServer.Network;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class FiendwarBattlefieldTests
{
    internal static async Task RunEventProjectionAsync(FiendwarScript team, FiendwarScript guide)
    {
        int before = Count;
        foreach (var source in new[] { team, guide })
        {
            var clock = new LukeRaidClockTests.Clock(DateTimeOffset.FromUnixTimeSeconds(1793410200));
            var engine = new FiendwarBattlefield(source, new());
            var initial = engine.Create(Guid.NewGuid(), clock.GetUtcNow(), 9527).State;
            That(initial.MovieEvents.IsEmpty && initial.WarningEvents.IsEmpty,
                "团队及引导初始零状态不是原生影片或预警事件");
            var script = EventScript(source);
            int area = script.Areas.Keys.Min();
            var eventEngine = new FiendwarBattlefield(script, new());
            var field = eventEngine.Create(Guid.NewGuid(), clock.GetUtcNow(), 9527).State;
            That(field.MovieEvents.Count == 5 && field.WarningEvents.Count == 1,
                "真实规则SET 0仍登记五种执行事件，不从状态值猜触发");
            foreach (var symbol in script.Script.Symbols.Where(row => row.Value is >= 121 and <= 125))
            {
                var next = eventEngine.SetSymbol(field, symbol.Key, 0, clock.GetUtcNow()).State;
                That(next.Symbols[symbol.Key] == 0 && next.MovieEvents[symbol.Value] > field.MovieEvents[symbol.Value] &&
                    next.Revision > field.Revision && field.EventSequence < next.EventSequence,
                    "影片同值再次SET签发新代，原不可变快照保持原代");
            }
            var repeated = eventEngine.Apply(field, new(FiendwarOp.SymbolChanged, Key: "[radar state]"), clock.GetUtcNow()).State;
            That(repeated.Areas[area].Warning && repeated.WarningEvents[area] > field.WarningEvents[area] &&
                field.MovieEvents.All(row => repeated.MovieEvents[row.Key] > row.Value),
                "真实规则再次预警及同值影片均生成新代，不能只比较bool或value");
            var cleared = eventEngine.Apply(repeated, new(FiendwarOp.SymbolChanged, Key: "[battery state]"), clock.GetUtcNow()).State;
            That(!cleared.Areas[area].Warning && cleared.WarningEvents[area] == repeated.WarningEvents[area],
                "清除区域预警不制造可重启计时器的1524零事件");

            var battle = new AntonRaidBattle(script.Script, kind: RaidKind.Fiendwar, clock: clock, fiendwar: script);
            using var owner = new AccountOwners().Acquire("fiendwar-events-" + source.Guide, () => { });
            var session = new GameSession(null!, null!, owner);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var stream = new GameSenderBudgetTests.HeldStream();
            var frames = new List<(ushort Opcode, byte[] Body)>();
            await using var sender = new GameConnectionSender(owner, stream, stop, 0,
                (_, opcode, _, body, _) => frames.Add((opcode, body.ToArray())));
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), owner, session, sender);
            var held = sender.Send(0, 2, [1], "fiendwar_event_fixture_hold");
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            bool current = true;
            try
            {
                RaidCombatProjection.PostFiendwarSnapshot(context, battle, battle.View, () => current);
                That(context.FiendwarMovieDisplays.Count == 0 && context.FiendwarWarningDisplays.Count == 0,
                    "仅排队不能提前消耗影片和预警事件代");
                current = false;
            }
            finally { stream.Release.TrySetResult(); }
            await held.WaitAsync(TimeSpan.FromSeconds(5));
            await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            That(frames.All(row => row.Opcode is not (1524 or 1530)) && context.FiendwarEventDisplayInstance == Guid.Empty &&
                context.FiendwarMovieDisplays.Count == 0 && context.FiendwarWarningDisplays.Count == 0,
                "写前原actor守卫失效跳过旧帧，不确认展示、不吞后续真实事件");
            frames.Clear();
            RaidCombatProjection.PostFiendwarSnapshot(context, battle, battle.View, () => true);
            RaidCombatProjection.PostFiendwarSnapshot(context, battle, battle.View, () => true);
            await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            That(frames.Where(row => row.Opcode == 1524).Select(row => row.Body).ToArray() is [var warning] &&
                warning.AsSpan().SequenceEqual(new byte[] { 1, checked((byte)area), 1 }),
                "同一连接重复快照只对真实区域预警发一次1524，不发零值清除行");
            var symbols = frames.Where(row => row.Opcode == 1530).SelectMany(row => EventSymbolRows(row.Body)).ToArray();
            That(symbols.Where(row => row.Id is >= 121 and <= 125).Select(row => row.Id).Order()
                    .SequenceEqual(new uint[] { 121, 122, 123, 124, 125 }) &&
                symbols.Where(row => row.Id is >= 121 and <= 125).All(row => row.Value == 0),
                "真实初始影片只送一次，原生122/124关闭事件的零值保留");
            uint radar = checked((uint)script.Script.Symbols["[radar state]"]);
            That(symbols.Any(row => row.Id == radar && row.Value == 0) &&
                context.FiendwarMovieDisplays.Count == 5 && context.FiendwarWarningDisplays.Count == 1,
                "普通状态零值仍投影，实际写帧才记录当前实例有界事件水位");
            frames.Clear();
            // 现有连接进入下一张区域地图不会重建Context；正常重放保留事件展示水位。
            context.FiendwarTimers = context.FiendwarTimers.Clear();
            RaidCombatProjection.PostFiendwarSnapshot(context, battle, battle.View, () => true);
            await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            That(frames.All(row => row.Opcode != 1524) && frames.Where(row => row.Opcode == 1530)
                    .SelectMany(row => EventSymbolRows(row.Body)).All(row => row.Id is < 121 or > 125),
                "区域重入和计时器恢复不重播已写影片或重启遭遇预警");
            frames.Clear();
            var nextBattle = new AntonRaidBattle(script.Script, kind: RaidKind.Fiendwar, clock: clock, fiendwar: script);
            RaidCombatProjection.PostFiendwarSnapshot(context, nextBattle, nextBattle.View, () => true);
            await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            That(frames.Count(row => row.Opcode == 1524) == 1 && frames.Where(row => row.Opcode == 1530)
                    .SelectMany(row => EventSymbolRows(row.Body)).Count(row => row.Id is >= 121 and <= 125) == 5 &&
                context.FiendwarEventDisplayInstance == nextBattle.Instance && sender.PendingBytes == 0,
                "新实例相同计数的真实事件可再发布，不沿用旧实例水位且发送预算完整归还");
        }
        await EventWriteFailureAsync(team);
        Console.WriteLine($"PASS {Count - before} 漩涡原生执行事件投影断言");
    }

    static FiendwarScript EventScript(FiendwarScript source)
    {
        int area = source.Areas.Keys.Min();
        var movies = source.Script.Symbols.Where(row => row.Value is >= 121 and <= 125).OrderBy(row => row.Value)
            .Select(row => new FiendwarInstruction(FiendwarOp.SetSymbol, 0, Key: row.Key)).ToImmutableArray();
        return source with { Rules = [
            new([], []) { Conditions = [new(FiendwarOp.SymbolEquals, 0, Key: "[fiendwar init]")],
                Actions = [new(FiendwarOp.SetSymbol, 1, Key: "[fiendwar init]"), .. movies, new(FiendwarOp.WarningEnemy, area)] },
            new([], []) { Conditions = [new(FiendwarOp.SymbolChanged, Key: "[radar state]")],
                Actions = [.. movies, new(FiendwarOp.WarningEnemy, area)] },
            new([], []) { Conditions = [new(FiendwarOp.SymbolChanged, Key: "[battery state]")],
                Actions = [new(FiendwarOp.DeleteAllEnemy)] }
        ] };
    }

    static (uint Id, uint Value)[] EventSymbolRows(byte[] body)
    {
        That(body.Length == 1 + body[0] * 8, "1530保留原生完整u32符号和值行");
        return Enumerable.Range(0, body[0]).Select(index =>
            (BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(1 + index * 8)),
             BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(5 + index * 8)))).ToArray();
    }

    static async Task EventWriteFailureAsync(FiendwarScript source)
    {
        var script = EventScript(source);
        var battle = new AntonRaidBattle(script.Script, kind: RaidKind.Fiendwar, fiendwar: script);
        using var owner = new AccountOwners().Acquire("fiendwar-event-write-failure", () => { });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var stream = new GameSenderBudgetTests.HeldStream { FailWrite = true };
        await using var sender = new GameConnectionSender(owner, stream, stop, 0, (_, _, _, _, _) => { });
        var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), owner,
            new GameSession(null!, null!, owner), sender);
        var held = sender.Send(0, 2, [1], "fiendwar_event_write_failure_hold");
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        RaidCombatProjection.PostFiendwarSnapshot(context, battle, battle.View, () => true);
        stream.Release.TrySetResult();
        bool failed = false;
        try { await held.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (IOException) { failed = true; }
        await sender.DisposeAsync();
        That(failed && stop.IsCancellationRequested && sender.PendingBytes == 0 &&
            context.FiendwarMovieDisplays.Count == 0 && context.FiendwarWarningDisplays.Count == 0,
            "实际socket写失败不能确认排队的事件代，连接终止完整回收预算");
    }
}
