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
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class FiendwarBattlefieldTests
{
    internal static async Task RunBoardProjectionAsync(PvfArchive archive)
    {
        int before = Count;
        string path = RaidCatalog.Load(archive).Find(2)!.Path;
        var tokens = archive.ReadTokens(path);
        var symbols = archive.ReadTokens(path + ".symbol");
        var team = FiendwarScript.Parse(path, tokens, symbols, archive.ResolveString);
        var guide = FiendwarScript.ParseGuide(path, tokens, symbols, archive.ResolveString);
        BoardKinds(team);
        await BoardConnectionAsync(BoardScript(team), 0);
        await BoardConnectionAsync(guide, 0);
        await BoardConnectionAsync(guide, 1);
        await BoardGuardAsync(BoardScript(team));
        foreach (ushort opcode in new ushort[] { 1526, 1527 })
        {
            await BoardActiveWriteAsync(BoardScript(team), opcode, fail: false);
            await BoardActiveWriteAsync(BoardScript(team), opcode, fail: true);
        }
        Console.WriteLine($"PASS {Count - before} 漩涡动态头像与发送水位断言");
    }

    static void BoardKinds(FiendwarScript team)
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(1793410200);
        var engine = new FiendwarBattlefield(team, new());
        var initial = engine.Create(Guid.NewGuid(), start, 9527).State;
        var areas = initial.Areas.ToImmutableDictionary(row => row.Key, _ => new FiendwarAreaState());
        for (int kind = 1; kind <= 4; kind++) areas = areas.SetItem(5 + kind, new(Enemy: new(kind, kind)));
        for (int kind = 1; kind <= 2; kind++) areas = areas.SetItem(9 + kind, new(Monster: new(4 + kind, kind)));
        var field = initial with { Areas = areas, NextUnit = 6 };
        var view = new AntonRaidBattleView(field.Instance, 0, 0, AntonRaidBattleState.Fighting,
            field.Symbols, ImmutableDictionary<int, RaidDungeonState>.Empty, ImmutableDictionary<RaidTimer, int>.Empty)
            { Fiendwar = field };
        foreach (bool enemy in new[] { false, true })
        {
            var hidden = RaidCombatProjection.FiendwarUnitSnapshot(view, enemy, guide: false);
            That(hidden.Count == (enemy ? 4 : 2) && hidden.All(row => row.Value.Kind == 0),
                "雷达未完成时四类敌军和两类中立怪均为各自原生问号");
            That(hidden.All(row => row.Value.Serial == (enemy ? field.Areas[row.Key].Enemy : field.Areas[row.Key].Monster)!.Value.Serial),
                "遮罩只隐藏头像，不改变区域或单位serial");
        }
        // 使用真实领域通关入口，不仅靠显示快照中的测试符号切换证明雷达行为。
        var completed = engine.CompleteArea(field, 15, start.AddSeconds(1)).State;
        That(completed.Symbols["[radar state]"] == 1 && completed.Dungeons[team.Areas[15].DungeonId] == "clear",
            "真实完成雷达产生已提交状态1并清除雷达副本");
        var revealed = view with { Symbols = completed.Symbols, Fiendwar = completed };
        foreach (bool enemy in new[] { false, true })
        {
            var shown = RaidCombatProjection.FiendwarUnitSnapshot(revealed, enemy, guide: false);
            That(shown.Values.Select(unit => unit.Kind).Order().SequenceEqual(Enumerable.Range(1, enemy ? 4 : 2)) &&
                shown.All(row => row.Value == (enemy ? field.Areas[row.Key].Enemy : field.Areas[row.Key].Monster)),
                "真实雷达通关恢复原头像，领域单位Kind及serial未被显示遮罩污染");
        }
        That(field.RandomState == completed.RandomState && field.NextUnit == completed.NextUnit &&
            field.Areas.Values.Where(area => area.Enemy is not null).All(area => area.Enemy!.Value.Kind != 0),
            "雷达与展示转换不重新抽怪或修改原单位身份");
    }

    static FiendwarScript BoardScript(FiendwarScript source) => source with { Rules = [
        new([], []) { Conditions = [new(FiendwarOp.SymbolEquals, 0, Key: "[fiendwar init]")],
            Actions = [new(FiendwarOp.SetSymbol, 1, Key: "[fiendwar init]"),
                new(FiendwarOp.CreateEnemy, 14), new(FiendwarOp.CreateEnemy, 16), new(FiendwarOp.CreateEnemy, 18),
                new(FiendwarOp.CreateMonsters)] }
    ] };

    static AntonRaidBattle BoardBattle(FiendwarScript script, int route = 0) =>
        new(script.Script, kind: RaidKind.Fiendwar, fiendwar: script, fiendwarRoute: route);

    static void BoardRadar(AntonRaidBattle battle, int value)
    {
        var view = battle.View;
        That(battle.SetMechanism(battle.Instance, view.Revision, "[radar state]", value) is not null,
            "服务端机制快照切换雷达状态并推进原Battle revision");
    }

    static GameConnectionContext BoardContext(AccountOwners.Lease owner, GameConnectionSender sender,
        Stream stream, CancellationTokenSource stop) =>
        new(new(1, IPAddress.Loopback, stream, stop), owner, new GameSession(null!, null!, owner), sender);

    static async Task BoardConnectionAsync(FiendwarScript script, int route)
    {
        var battle = BoardBattle(script, route);
        var field = battle.View.Fiendwar!;
        using var owner = new AccountOwners().Acquire("fiendwar-board-" + script.Guide + route, () => { });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var stream = new MemoryStream();
        var frames = new List<(ushort Opcode, byte[] Body)>();
        await using var sender = new GameConnectionSender(owner, stream, stop, 0,
            (_, opcode, _, body, _) => frames.Add((opcode, body.ToArray())));
        var context = BoardContext(owner, sender, stream, stop);
        await Post();
        Check(reveal: script.Guide, changed: false);
        frames.Clear();
        BoardRadar(battle, 1 - battle.View.Symbols["[radar state]"]);
        await Post();
        Check(reveal: true, changed: !script.Guide);
        frames.Clear();
        BoardRadar(battle, 1 - battle.View.Symbols["[radar state]"]);
        await Post();
        Check(reveal: script.Guide, changed: !script.Guide);
        frames.Clear();
        await Post();
        Check(reveal: script.Guide, changed: false);
        That(ReferenceEquals(field, battle.View.Fiendwar) && sender.PendingBytes == 0,
            "展示与重复快照保留领域单位、随机及迷宫状态并归还发送预算");

        async Task Post()
        {
            RaidCombatProjection.PostFiendwarSnapshot(context, battle, battle.View, () => true);
            await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        void Check(bool reveal, bool changed)
        {
            foreach (bool enemy in new[] { false, true })
            {
                ushort opcode = enemy ? (ushort)1527 : (ushort)1526;
                var packet = frames.Where(frame => frame.Opcode == opcode).ToArray();
                That(packet.Length == 1, "一次有效战况仍分别发送1526/1527完整快照");
                var rows = BoardRows(packet[0].Body, enemy);
                var actual = field.Areas.Where(row => (enemy ? row.Value.Enemy : row.Value.Monster) is not null).ToArray();
                That(rows.Count(row => row.Operation == 1) == actual.Length &&
                    rows.Count(row => row.Operation == 2) == (changed ? actual.Length : 0),
                    "同serial头像变化先删旧显示再完整添加；重复快照仍包含现存单位");
                That(rows.Select(row => row.Operation).SequenceEqual(rows.Select(row => row.Operation).OrderDescending()),
                    "完整删除行始终位于添加行之前");
                foreach (var area in actual)
                {
                    var unit = (enemy ? area.Value.Enemy : area.Value.Monster)!.Value;
                    var add = rows.Single(row => row.Area == area.Key && row.Operation == 1);
                    That(add.Serial == unit.Serial && add.Kind == (reveal ? unit.Kind : 0),
                        "团队雷达显示转换及引导两路线保留真实区域和单位身份");
                    if (changed)
                        That(rows.Single(row => row.Area == area.Key && row.Operation == 2).Kind == (reveal ? 0 : unit.Kind),
                            "删除使用客户端之前实际收到的头像，不使用领域真实Kind代替");
                }
                var cache = enemy ? context.FiendwarEnemies : context.FiendwarMonsters;
                That(cache.Count == actual.Length && rows.Where(row => row.Operation == 1)
                    .All(row => cache[row.Area] == new FiendwarUnit(row.Serial, row.Kind)),
                    "缓存只保存实际成功写出的显示快照");
            }
        }
    }

    static async Task BoardGuardAsync(FiendwarScript script)
    {
        var battle = BoardBattle(script);
        using var owner = new AccountOwners().Acquire("fiendwar-board-guard", () => { });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var stream = new GameSenderBudgetTests.HeldStream();
        var frames = new List<(ushort Opcode, byte[] Body)>();
        await using var sender = new GameConnectionSender(owner, stream, stop, 0,
            (_, opcode, _, body, _) => frames.Add((opcode, body.ToArray())));
        var context = BoardContext(owner, sender, stream, stop);
        var held = sender.Send(0, 2, [1], "fiendwar_board_guard_hold");
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bool current = true;
        RaidCombatProjection.PostFiendwarSnapshot(context, battle, battle.View, () => true);
        BoardRadar(battle, 1);
        RaidCombatProjection.PostFiendwarSnapshot(context, battle, battle.View, () => current);
        RaidCombatProjection.PostFiendwarSnapshot(context, battle, BoardBattle(script).View, () => true);
        That(context.FiendwarEnemies.IsEmpty && context.FiendwarMonsters.IsEmpty,
            "入队但未写出不能提前消耗头像水位");
        current = false;
        stream.Release.TrySetResult();
        await held.WaitAsync(TimeSpan.FromSeconds(5));
        await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        That(frames.All(frame => frame.Opcode is not (1526 or 1527)) &&
            context.FiendwarEnemies.IsEmpty && context.FiendwarMonsters.IsEmpty,
            "旧revision、失效actor守卫和另一实例均不发布单位或改变显示缓存");
        RaidCombatProjection.PostFiendwarSnapshot(context, battle, battle.View, () => true);
        await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        That(context.FiendwarEnemies.Count == 3 && context.FiendwarMonsters.Count == 3 && sender.PendingBytes == 0,
            "跳过旧快照后空缓存连接仍完整恢复当前战况，不吞掉必要添加");
        frames.Clear();
        var nextScript = script with { Rules = [
            new([], []) { Conditions = [new(FiendwarOp.SymbolEquals, 0, Key: "[fiendwar init]")],
                Actions = [new(FiendwarOp.SetSymbol, 1, Key: "[fiendwar init]"),
                    new(FiendwarOp.CreateEnemy, 9), new(FiendwarOp.CreateEnemy, 14)] }
        ] };
        var next = BoardBattle(nextScript);
        RaidCombatProjection.PostFiendwarSnapshot(context, next, next.View, () => true);
        await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        That(context.FiendwarEnemies.Keys.Order().SequenceEqual(new[] { 9, 14 }) &&
            context.FiendwarEnemies[9] == new FiendwarUnit(1, 0) &&
            context.FiendwarEnemies[14] == new FiendwarUnit(2, 0) && context.FiendwarMonsters.IsEmpty,
            "同连接新实例完整恢复单位迁区、同区serial替换及消失单位，不沿用旧头像");
        var enemyRows = BoardRows(frames.Single(frame => frame.Opcode == 1527).Body, enemy: true);
        var monsterRows = BoardRows(frames.Single(frame => frame.Opcode == 1526).Body, enemy: false);
        That(enemyRows.Count(row => row.Operation == 2 && row.Kind > 0) == 3 &&
            enemyRows.Count(row => row.Operation == 1 && row.Kind == 0) == 2 &&
            monsterRows.Length == 3 && monsterRows.All(row => row.Operation == 2 && row.Kind > 0),
            "迁区、替换和删除在真实完整单位包中删除旧已写Kind，不仅替换本地缓存");
    }

    static async Task BoardActiveWriteAsync(FiendwarScript script, ushort opcode, bool fail)
    {
        var battle = BoardBattle(script);
        using var owner = new AccountOwners().Acquire("fiendwar-board-active-" + opcode + fail, () => { });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var stream = new BoardHeldStream(opcode, fail);
        var frames = new List<byte[]>();
        await using var sender = new GameConnectionSender(owner, stream, stop, 0,
            (_, sent, _, body, _) => { if (sent == opcode) frames.Add(body.ToArray()); });
        var context = BoardContext(owner, sender, stream, stop);
        RaidCombatProjection.PostFiendwarSnapshot(context, battle, battle.View, () => true);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bool enemy = opcode == 1527;
        That((enemy ? context.FiendwarEnemies : context.FiendwarMonsters).IsEmpty,
            "实际单位帧写入尚未完成时，缓存保持上次真实水位");
        BoardRadar(battle, 1);
        RaidCombatProjection.PostFiendwarSnapshot(context, battle, battle.View, () => true);
        stream.Release.TrySetResult();
        if (fail)
        {
            await stream.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await sender.DisposeAsync();
            That(stop.IsCancellationRequested && frames.Count == 0 && sender.PendingBytes == 0 &&
                (enemy ? context.FiendwarEnemies : context.FiendwarMonsters).IsEmpty,
                "单位socket写失败不能记录任何问号或真实显示水位，完整回收队列");
        }
        else
        {
            await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            That(frames.Count == 2 && BoardRows(frames[0], enemy).All(row => row is { Kind: 0, Operation: 1 }),
                "帧开始写后revision改变，已送达旧问号帧仍完整记录");
            var rows = BoardRows(frames[1], enemy);
            That(rows.Count(row => row is { Kind: 0, Operation: 2 }) == 3 &&
                rows.Count(row => row.Kind > 0 && row.Operation == 1) == 3 &&
                (enemy ? context.FiendwarEnemies : context.FiendwarMonsters).Values.All(unit => unit.Kind > 0) &&
                sender.PendingBytes == 0,
                "后续有效快照从刚刚实际写出的旧显示求删除和添加，不遗漏中途写帧水位");
        }
    }

    static (byte Area, uint Serial, byte Kind, uint Operation)[] BoardRows(byte[] body, bool enemy)
    {
        int width = enemy ? 14 : 10;
        That(body.Length == 1 + body[0] * width, "单位包保留完整10/14字节行");
        return Enumerable.Range(0, body[0]).Select(index =>
        {
            int at = 1 + index * width;
            if (enemy) That(BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at + 10)) == 2,
                "雷达遮罩不改变1527额外u32原值");
            return (body[at], BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at + 1)), body[at + 5],
                BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at + 6)));
        }).ToArray();
    }

    // 只暂停第一份目标单位帧；其余协议沿真实发送泵正常写出，不用固定睡眠制造竞态。
    sealed class BoardHeldStream(ushort opcode, bool fail) : MemoryStream
    {
        int held;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(buffer.Span[1..]) == opcode && Interlocked.Exchange(ref held, 1) == 0)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                if (fail)
                {
                    Failed.TrySetResult();
                    throw new IOException("synthetic fiendwar board write failure");
                }
            }
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}
