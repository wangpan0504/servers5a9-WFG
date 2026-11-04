using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;
using DfoServer.Network.Parsers;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AntonConstructorTests
{
    internal static void RunCore()
    {
        int before = Count;
        foreach (var row in AntonConstructorDefinition.All)
        {
            That(AntonConstructorDefinition.Find((uint)row.Dungeon, row.Map) == row &&
                AntonConstructorDefinition.Find(218, row.Map) is null &&
                AntonConstructorDefinition.Find((uint)row.Dungeon, row.Map + 10000) is null,
                "Constructor许可必须同时匹配正式DGN与MAP");
            That(RaidRequests.AntonConstructorLast(Packet(row.Signal, 1, 0)) == new AntonConstructorLast(AntonConstructorEffect.VolcanoSignal, row.Dungeon),
                "四个火山信号精确绑定各自孵化场");
        }
        That(RaidRequests.AntonConstructorLast(Packet(110, 1000, 2)) == new AntonConstructorLast(AntonConstructorEffect.Energy) &&
            RaidRequests.AntonConstructorLast(Packet(110, 500, 2)) == new AntonConstructorLast(AntonConstructorEffect.InfectionEnergy),
            "1000与500是减量operand，线上减法为2");
        foreach (var bad in new[] { Array.Empty<byte>(), Packet(110, 1000, 2)[..8], new byte[10],
            Packet(110, 1000, 0), Packet(110, 500, 3), Packet(110, 100, 2), Packet(110, 3000, 2),
            Packet(20000, 0, 0), Packet(20000, 1, 2), Packet(19999, 1, 0), Packet(20004, 1, 0) })
            That(RaidRequests.AntonConstructorLast(bad) is null, "Constructor解析拒绝错误宽度、符号、操作与operand");

        var clock = new ConstructorClock();
        var resource = AntonConstructorDefinition.All[0];
        var scope = new AntonDungeonObjectScope(Guid.NewGuid(), Guid.NewGuid(), 3, 7, 10, 11);
        var death = new AntonConstructorDeath(scope.Battle, scope.Run, scope.Generation, scope.RoomEpoch, resource, 77, true);
        var effects = new[] { new AntonConstructorLast(AntonConstructorEffect.Energy),
            new AntonConstructorLast(AntonConstructorEffect.InfectionEnergy), new AntonConstructorLast(AntonConstructorEffect.VolcanoSignal, 221) };
        var ledger = new AntonConstructorLedger(clock); ledger.Open(scope, resource);
        int commits = 0; var ids = new HashSet<long>();
        bool Commit(AntonConstructorEffectFact fact) { commits++; ids.Add(fact.EventId); return true; }
        foreach (var effect in effects) That(ledger.Report(scope, effect), "Last早到可按三种独立效果有界等待");
        That(ledger.PendingCount == 3 && ledger.Apply(scope, Commit) == 0 && commits == 0 &&
            effects.All(effect => !ledger.Report(scope, effect)), "没有正式死亡时三个Last不能单独改变团本");
        That(!ledger.Report(scope, new(AntonConstructorEffect.VolcanoSignal, 222)) &&
            !ledger.Record(death with { MonsterKey = 0 }) && !ledger.Record(death with { RoomEpoch = 8 }) &&
            !ledger.Record(death with { Definition = AntonConstructorDefinition.All[1] }), "错图信号和错误死亡身份拒绝");
        That(ledger.Record(death) && ledger.Record(death) && ledger.Apply(scope, _ => false) == 0 && ledger.PendingCount == 3,
            "业务拒绝不消费原三项配对资格");
        That(ledger.Apply(scope, Commit) == 3 && commits == 3 && ids.Count == 3 && ledger.PendingCount == 0 &&
            ledger.Apply(scope, Commit) == 0 && effects.All(effect => !ledger.Report(scope, effect)),
            "真实死亡与各自Last配对后分别一次，稳定EventId不会互相吞并");
        var host = scope with { HostEpoch = 12, HostCharacter = 20 };
        ledger.Open(host, resource);
        That(effects.All(effect => !ledger.Report(host, effect)) && ledger.Apply(host, Commit) == 0,
            "同房换host保留死亡和已消费状态，不能借新host重扣");

        var normal = new AntonConstructorLedger(clock); normal.Open(scope, resource);
        That(normal.Record(death with { Infected = false }) && normal.Apply(scope, Commit) == 0 &&
            !normal.Report(scope, effects[1]) && normal.Report(scope, effects[0]) && normal.Report(scope, effects[2]) &&
            normal.Apply(scope, Commit) == 2, "普通死亡不会自动执行Last或获得感染500，但仍有1000与火山信号");
        var early = new AntonConstructorLedger(clock); early.Open(scope, resource);
        early.Report(scope, effects[1]); early.Record(death with { Infected = false });
        That(early.Apply(scope, Commit) == 0 && early.PendingCount == 0, "死亡前伪报感染Last在死亡事实到达时丢弃");
        var expired = new AntonConstructorLedger(clock); expired.Open(scope, resource); expired.Report(scope, effects[0]);
        clock.Advance(AntonConstructorLedger.PairWindow + TimeSpan.FromSeconds(1)); expired.Record(death);
        That(expired.Apply(scope, Commit) == 0 && expired.PendingCount == 0 && expired.Report(scope, effects[0]),
            "过期Last不能借后续死亡执行，新的合法声明仍可重试");
        expired.Open(host, resource);
        That(expired.PendingCount == 0 && expired.Report(host, effects[0]) && expired.Apply(host, Commit) == 1,
            "换host清等待但保留该房已批准死亡");
        var next = host with { Generation = 4, RoomEpoch = 8 };
        expired.Open(next, resource);
        That(!expired.Report(host, effects[0]) && !expired.Record(death) && expired.Report(next, effects[0]) &&
            expired.Apply(next, Commit) == 0, "换房或换目标代清死亡，旧scope及旧死亡不能作用新代");
        Console.WriteLine($"PASS {Count - before} 安徒恩Constructor死亡与三类Last断言");
    }

    internal static void RunResources(TcpTestResources resources, RaidDungeonCatalog catalog, RaidScript script)
    {
        var archive = resources.Archive;
        var rooms = SelfTestCatalogs.Rooms(archive);
        That(script.Symbols["[infection dungeon index]"] == 8 && script.Symbols["[is exist infection dungeon]"] == 7 &&
            AntonConstructorDefinition.All.All(row => !script.Symbols.Values.Contains(row.Signal)),
            "真实符号表数字位于名称之前：感染身份8、开闭7，四个火山信号单独保存");
        foreach (var row in AntonConstructorDefinition.All)
        {
            var map = catalog.Find(row.Dungeon)!.Mazes.SelectMany(maze => maze.MapSpecification!).Single(roomMap => roomMap.Id == row.Map);
            That(rooms.Get(map).Monsters.Any(monster => monster.MonsterId == row.Monster &&
                monster.Path.Replace('\\', '/').Equals(row.MonsterPath, StringComparison.OrdinalIgnoreCase)),
                "候选PVF正式孵化场MAP通过monster.lst绑定对应Constructor");
            string mob = archive.ReadText(row.MonsterPath).Replace('\\', '/').ToLowerInvariant();
            string action = row.LastPath[(row.LastPath.LastIndexOf("/action/", StringComparison.Ordinal) + 1)..];
            That(mob.Contains("[last action]", StringComparison.Ordinal) && mob.Contains(action, StringComparison.Ordinal),
                "四个Constructor各自mob声明对应Last路径");
            string last = string.Join(' ', archive.ReadText(row.LastPath).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            That(last.Contains("[RAID SET SYMBOL] 110 [-] 1000", StringComparison.Ordinal) &&
                last.Contains($"[CHECK RAID SYMBOL] 8 [=] {row.Dungeon}", StringComparison.Ordinal) &&
                last.Contains("[RAID SET SYMBOL] 110 [-] 500", StringComparison.Ordinal) &&
                last.Contains($"[RAID SET SYMBOL] {row.Signal} [=] 1", StringComparison.Ordinal),
                "真实Last包含1000、按内部symbol8判定的500及独立火山信号");
        }
    }

    static byte[] Packet(int symbol, int value, byte operation)
    {
        byte[] body = new byte[9]; BinaryPrimitives.WriteInt32LittleEndian(body, symbol);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), value); body[8] = operation; return body;
    }
    sealed class ConstructorClock : TimeProvider
    {
        long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        internal void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }
}
