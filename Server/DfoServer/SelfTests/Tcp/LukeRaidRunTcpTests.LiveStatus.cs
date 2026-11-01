using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class LukeRaidRunTcpTests
{
    static DungeonPoint[] ProductionFacilityStops(RaidDungeonCatalog catalog, LukeRaidObjectCatalog objects, int dungeon, int ordinal)
    {
        var maze = catalog.Find(dungeon)!.Mazes.Single(row => row.SourceOrdinal == ordinal);
        return maze.MapSpecification!.Where(room => !maze.Bosses.Contains(room.Position) && objects.Find(new(dungeon, ordinal, room.Kind,
            room.Position, room.Id, room.Path)) is [{ Symbol: 117 or 118 or 119 }])
            .Select(room => room.Position).Distinct().ToArray();
    }

    // 仅消费正常生产路线实际经过的设施房，不手改Battle、符号或设施预算。
    static void ProductionFacility(TcpPeer[] peers, TcpPeer reporter, LukeRaidObjectCatalog objects,
        LukeSanctumObjectRules rules, int dungeon, int maze, DungeonMap room, Dictionary<int, int> counts,
        int completed, RaidScript script)
    {
        if (objects.Find(new(dungeon, maze, room.Kind, room.Position, room.Id, room.Path)) is not [var binding] ||
            binding.Symbol is not (117 or 118 or 119)) return;
        int symbol = binding.Symbol;
        counts[symbol] = Math.Min(counts[symbol] + 1, rules.Budget(dungeon)!.Cap);
        byte[] action = Build(writer => { writer.Write((uint)symbol); writer.Write(1u); writer.Write((byte)1); });
        for (int replay = 0; replay < 2; replay++)
        {
            reporter.Send(662, action);
            foreach (var peer in peers)
            {
                var frames = Drain(peer, true);
                That(frames.Count(packet => packet is { Kind: 1, Opcode: 662, Body: [1] }) == (peer == reporter ? 1 : 0),
                    "真实设施及同房重放只向报告者确认662，不伪造其他人的动作");
                var snapshot = frames.Single(packet => packet is { Kind: 0, Opcode: 583 }).Body;
                uint Value(int key) => Enumerable.Range(0, snapshot[0]).Where(index => U32(snapshot, 1 + 8 * index) == key)
                    .Select(index => U32(snapshot, 5 + 8 * index)).Single();
                That(counts.All(row => Value(row.Key) == row.Value),
                    "生产全团图内及城镇成员实时接收三圣所真实破坏数，同房重放不重复减少蓝格");
                var target = script.Phases[0].Dungeons.Single(row => row.Id == dungeon);
                That(Value(script.Symbols[target.HpSymbol]) == completed,
                    "设施破坏数与三段环通关数独立，破坏装置不能扣地下城次数");
                That(!frames.Any(packet => packet.Opcode is 261 or 601),
                    "普通设施事件不触发结果按钮或阶段结算");
            }
        }
    }

    static void ProductionLiveHp(TcpPeer[] peers, TcpPeer[] members, TcpPeer reporter)
    {
        foreach (var peer in peers) Drain(peer, true);
        foreach (var values in new uint[][] { [73, 41], [37, 12] })
        {
            reporter.Send(695, Build(writer =>
            {
                writer.Write(0x12345678u); writer.Write((byte)values.Length);
                foreach (uint value in values) writer.Write(value);
            }));
            byte[] expected = Build(writer =>
            {
                writer.Write((byte)1); writer.Write((ushort)1); writer.Write((byte)members.Length);
                foreach (var member in members) writer.Write(member.Id);
                writer.Write(0u); writer.Write(0x12345678u); writer.Write((byte)values.Length);
                foreach (uint value in values) writer.Write(value);
            });
            foreach (var peer in peers)
            {
                var frames = Drain(peer, true);
                Bytes(frames.Single(packet => packet is { Kind: 0, Opcode: 634 }).Body, expected,
                    "二阶段能源室连续695按原顺序向全团634更新双目标百分比，不要求重开面板");
                That(!frames.Any(packet => packet.Opcode is 261 or 596 or 601),
                    "能源HP刷新不重启进度计时、不打开结果入口、不重复结算");
            }
        }
    }
}
