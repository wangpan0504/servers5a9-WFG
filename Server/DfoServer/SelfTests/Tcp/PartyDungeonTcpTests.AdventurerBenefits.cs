using DfoServer.Network.Management;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class PartyDungeonTcpTests
{
    public static async Task RunAdventurerBenefitsAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var clock = new FrameClock();
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "adventurer-benefits"), gm: true, resources: resources, clock: clock);
        await fixture.StartAsync();
        using var group = new Coop(fixture, resources, "advent", new(38, 2, 1085, 249), [3145], 50, 2, memberJobs: [0, 1]);
        var hero = group.Peers[0]; var fighter = group.Peers[1];
        string name = Convert.ToString(fixture.Query("SELECT name FROM characters WHERE id=$p0", hero.Id)[0][0])!;
        string account = Convert.ToString(fixture.Query("SELECT account FROM characters WHERE id=$p0", hero.Id)[0][0])!;
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "account " + account + " returning-adventurer " + name, out string response) && response.StartsWith("OK "),
            "真实GM命令持久化所选账号回归资格");
        That(File.Exists(Path.Combine(fixture.Options.DataDirectory, "adventurer-benefits.json")), "回归GM成功前已落盘");
        group.All();
        var identity = group.All()[0].Single(packet => packet is { Kind: 0, Opcode: 1761 });
        That(identity.Body.Length == 16 && U32(identity.Body) == 1 && U32(identity.Body, 12) > clock.Now.ToUnixTimeSeconds(),
            "真实账号回归身份按原生16B通知同步到客户端");
        group.Join(); group.Enter(3, 0);
        var entered = group.LastPackets;
        bool ReturningActor(WirePacket packet, ushort id, ushort type) => packet is { Kind: 0, Opcode: 2 } &&
            packet.Body.Length > 158 && packet.Body[0] == 0 && U16(packet.Body, 121) == id &&
            U16(packet.Body, packet.Body.Length - 37) == type;
        That(entered.All(rows => rows.Any(packet => ReturningActor(packet, hero.Id, 1))),
            "组队进图各客户端Actor携带新回归头像身份");
        group.Load();
        var loaded = group.LastPackets;
        bool Buff(WirePacket packet, uint id) => packet is { Kind: 0, Opcode: 487 } &&
            Enumerable.Range(0, packet.Body[0]).Any(index => U32(packet.Body, 1 + index * 16) == id);
        That(loaded[0].Any(packet => Buff(packet, 1087)) && loaded.All(rows => rows.Any(packet => Buff(packet, 1273))),
            "正式加载屏障后职业队友和回归队友收到资源Buff");
        That(!loaded[1].Any(packet => Buff(packet, 1087)), "职业自身空表不授予队友专属Buff");
        ushort key = Enumerable.Range(0, group.Map[18]).Select(index => 19 + index * 22)
            .Where(at => group.Map[at + 21] == 0 && group.Map[at + 13] != 9).Select(at => U16(group.Map, at + 6)).First();
        int monsterAt = Enumerable.Range(0, group.Map[18]).Select(index => 19 + index * 22).Single(at => U16(group.Map, at + 6) == key);
        hero.Send(39, FormalDungeonTcpTests.Death(key, hero.Id, group.Map[monsterAt + 12]));
        var killed = group.All();
        var exp = killed[0].FirstOrDefault(packet => packet is { Kind: 0, Opcode: 37 });
        That(exp is not null, "活动角色沿真实击杀链取得经验通知");
        int buffOffset = 51 + exp!.Body[50] * 8;
        uint displayed = U32(exp.Body, buffOffset);
        long heroExperience = Convert.ToInt64(fixture.Query("SELECT experience FROM characters WHERE id=$p0", hero.Id)[0][0]);
        long fighterExperience = Convert.ToInt64(fixture.Query("SELECT experience FROM characters WHERE id=$p0", fighter.Id)[0][0]);
        That(displayed > 0 && displayed == U32(exp.Body, buffOffset + 4) && heroExperience - fighterExperience == displayed,
            "同级同怪的真实经验差额等于Buff显示与基础扣除分项，无重复展示或只发图标");
        // 不把图标当成经验到账：核对持久化角色经验与显示分项使用同一奖励链。
        That(Convert.ToInt64(fixture.Query("SELECT experience FROM characters WHERE id=$p0", hero.Id)[0][0]) >
            (long)SelfTestCatalogs.Growth(resources.Archive, resources.Learning).GetLevelStartExperience(50), "职业活动击杀经验真实落库");
        clock.Now = clock.Now.AddDays(29);
        var expired = group.All();
        // 1347的ACK早于自动刷新；下一请求的ACK才能证明前一次刷新已完成。
        var expiredTail = group.All();
        for (int at = 0; at < expired.Length; at++) expired[at] = [.. expired[at], .. expiredTail[at]];
        That(expired.All(rows => rows.Any(packet => ReturningActor(packet, hero.Id, 0))),
            "到期对本人及队友刷新原生头像身份且不重建战斗Player");
        That(expired[0].Any(packet => packet is { Kind: 0, Opcode: 488 } &&
            Enumerable.Range(0, packet.Body[0]).Any(index => U32(packet.Body, 1 + index * 4) == 1273)) &&
            !expired[0].Any(packet => Buff(packet, 1273)), "到期刷新删除回归Buff且不重新安装");
        fighter.Send(42);
        var left = group.All(fighter);
        That(left[0].Any(packet => packet is { Kind: 0, Opcode: 488 }) && !left[0].Any(packet => Buff(packet, 1087)),
            "活动职业队友离队后幸存角色清除原生职业Buff");
    }
}
