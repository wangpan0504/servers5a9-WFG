using System.Buffers.Binary;
using System.Text;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class SinglePlayerDungeonRunTests
{
    static void VerifyHelperRotation()
    {
        var first = new DungeonMap("map", new(0, 0), 1, "map/fixture-start.map");
        var pillar = new DungeonMap("map", new(1, 0), 2, "map/fixture-hell.map");
        var boss = new DungeonMap("boss", new(2, 0), 3, "map/fixture-boss.map");
        var maze = new DungeonMaze(0, 3, 1, "helper-fixture", [first.Position], [boss.Position], [first, pillar, boss], [])
        { HellParty = new(2, pillar.Position) };
        var entry = new DungeonEntryPlan(99, 17, 3, 0, byte.MaxValue, 4, maze, first, boss)
        { HellParty = maze.HellParty, HellPartyMode = 1, BasisLevel = 93, MonsterBasisLevel = 93 };
        var rotation = new HelperHellRotation(1, 2, 93, 93, 4, 1, new(3, 2, 5, 110));
        var target = rotation.Apply(entry with { DungeonId = 4, BasisLevel = 95, MonsterBasisLevel = 95 });
        That(target?.StartMap == pillar && target.BasisLevel == 93 && target.MonsterBasisLevel == 93 &&
            target.Difficulty == 4 && target.HelperRotation == rotation && target.Maze == maze,
            "轮换直入真实柱子房且保留首次等级、难度和完整目标迷宫");
        That(rotation.Apply(entry with { IsPractice = true }) is null && rotation.Apply(entry with { Difficulty = 3 }) is null &&
            rotation.Apply(entry with { HellParty = null }) is null && rotation.Apply(entry with { IsHellLegend = true }) is null,
            "练习、不同难度、无柱子及传奇模式不能借用普通柱子轮换");
        That(HelperHellRotation.Next([3, 4, 5], 3) == 4 && HelperHellRotation.Next([3, 4, 5], 5) == 3 &&
            HelperHellRotation.Next([3], 3) is null && HelperHellRotation.Next([3, 4], 5) is null,
            "地区主列表循环轮转，无其他候选或当前图不在地区时拒绝");
        var authority = new FixtureCombatAuthority();
        var route = new DungeonFixedRoute([Plan(first, 10), Plan(pillar, 11), BossPlan(boss, 12)],
            [new(first.Position, pillar.Position), new(pillar.Position, boss.Position)]);
        That(SinglePlayerDungeonRun.TryCreate(entry, 1, 100, route, authority, out var run, out _), "建立普通深渊门资格夹具");
        run!.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        var before = run.Snapshot;
        That(!run.CanMove(before.Identity, pillar.Position), "尚有阻挡怪时轮换门资格与普通过门一致");
        var death = run.PrepareConfirmedDeath(authority.Death(before.Identity, 10, 1));
        That(!run.CanMove(run.Snapshot.Identity, pillar.Position), "死亡未提交时不能轮换");
        run.ConfirmCommittedDeath(death.Pending!);
        var cleared = run.Snapshot;
        That(run.CanMove(cleared.Identity, pillar.Position) && run.CanMove(cleared.Identity, pillar.Position) &&
            !run.CanMove(cleared.Identity with { Epoch = cleared.Identity.Epoch - 1 }, pillar.Position) && !run.CanMove(cleared.Identity, boss.Position) &&
            run.Snapshot.Identity == cleared.Identity && run.Snapshot.CurrentMap == cleared.CurrentMap,
            "门资格查询无副作用，旧身份与未连接目标保持拒绝");
        That(run.Move(cleared.Identity, Guid.NewGuid(), pillar.Position).Outcome == DungeonRunOutcome.Accepted &&
            !run.CanMove(run.Snapshot.Identity, first.Position), "准备后的正常Move仍生效，加载期间不可再次过门");

        string nonce = new('a', 32);
        byte[] Packet(string text, byte channel = 3)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            var body = new byte[11 + bytes.Length]; body[0] = channel;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7), (uint)bytes.Length);
            bytes.CopyTo(body, 11); return body;
        }
        var valid = Packet($"#95-helper/1 {nonce} 99 hell 1");
        That(HelperRequests.Parse(valid) == new HelperRequest(nonce, 99, true), "助手命令精确解析角色、nonce及明确开关值");
        That(HelperRequests.Parse(Packet($"#95-helper/1 {nonce} 99 cargo 1")) == new HelperRequest(nonce, 99, false, 1) &&
            HelperRequests.Parse(Packet($"#95-helper/1 {nonce} 99 cargo 2")) == new HelperRequest(nonce, 99, false, 2) &&
            HelperRequests.Parse(Packet($"#95-helper/1 {nonce} 99 cargo 0")) is null &&
            HelperRequests.Parse(Packet($"#95-helper/1 {nonce} 99 cargo 3")) is null,
            "双金库请求只能选择明确个人或账号容器");
        That(HelperRequests.ParseCapability(Encoding.ASCII.GetBytes($"95_HELPER_CAPS/1 {nonce} 99")) == new HelperRequest(nonce, 99, false) &&
            HelperRequests.ParseCapability(Encoding.ASCII.GetBytes($"95_HELPER_CAPS/1 {nonce} 099")) is null &&
            HelperRequests.ParseCapability(Encoding.ASCII.GetBytes($"95_HELPER_CAPS/2 {nonce} 99")) ==
                new HelperRequest(nonce, 99, false, CapabilityVersion: 2) &&
            HelperRequests.ParseCapability(Encoding.ASCII.GetBytes($"95_HELPER_CAPS/3 {nonce} 99")) is null,
            "静默库存同步扩展严格识别版本、nonce和规范角色号");
        foreach (string text in new[] { $"#95-helper/2 {nonce} 99 hell 1", $"#95-helper/1 {nonce} 099 hell 1",
            $"#95-helper/1 {nonce} 0 hell 1", $"#95-helper/1 {nonce} 99 hell 2", "#95-helper/1 " + new string('0', 32) + " 99 hell 1" })
            That(HelperRequests.Reserved(Packet(text)) && HelperRequests.Parse(Packet(text)) is null,
                "畸形或未知版助手机器命令被隔离且不执行业务");
        foreach (byte channel in new byte[] { 2, 6, 11, 13 })
            That(HelperRequests.Reserved(Packet($"#95-helper/1 {nonce} 99 hell 1", channel)) &&
                HelperRequests.Parse(Packet($"#95-helper/1 {nonce} 99 hell 1", channel)) is null,
                "机器文本跨聊天通道也隔离且不执行");
        valid[7]--;
        That(HelperRequests.Reserved(valid) && HelperRequests.Parse(valid) is null && !HelperRequests.Reserved(Packet("#95-helperful")),
            "助手长度畸形拒绝，近似普通文字不被吞掉");
    }
}
