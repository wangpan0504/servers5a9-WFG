using DfoServer.Game.Accounts;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class GrowUpChangeTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        int checks = Count;
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "growup-change"), resources: resources);
        var repository = new CharacterRepository(fixture.Database, resources.Quests);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repository.Create(owner, GrowUpChangeTests.CharacterFixture(resources));
        await fixture.StartAsync();
        static byte[] Body(byte target, byte confirm)
        {
            var body = new byte[15];
            Random.Shared.NextBytes(body.AsSpan(0, 13));
            body[13] = target; body[14] = confirm;
            return body;
        }
        using (var peer = Open(fixture))
        {
            var entry = peer.Select(); peer.Barrier();
            Bytes(entry.Single(packet => packet is { Kind: 0, Opcode: 1153 }).Body, [0, 30, 0, 0, 0], "入城下发0次与等级30");
            string before = Snapshot();
            foreach (var (body, error) in new (byte[], ushort)[] { (Body(1, 0), 3), (Body(0, 0), 1), (Body(6, 0), 1), (Body(2, 1), 23) })
            {
                peer.Send(785, body); peer.Expect(1, 785, [0, (byte)error, (byte)(error >> 8)]); peer.Barrier();
                That(Snapshot() == before, "原分支、无效分支、未报价确认均拒绝且不改存档，连接可继续");
            }
            peer.Send(785, Body(2, 0)); ReadCommitted(peer, 0x02, 1);
            That(fixture.Query("SELECT grow_type,gold FROM characters WHERE name='GrowHero'") is [[2L, var gold]] &&
                (long)gold == GrowUpChangeTests.StartGold && fixture.Query("SELECT change_count FROM character_growup_change") is [[1L]],
                "0次免费菜单变更：直接提交分支且不扣金币");
            string paid = Snapshot();
            peer.Send(785, Body(3, 0)); peer.Expect(1, 785, [1, 133, 0, 0, 0, 1]); peer.Barrier();
            That(Snapshot() == paid, "第二次首包只返回133询价，不扣费");
            peer.Send(785, Body(3, 1)); ReadCommitted(peer, 0x03, 2);
            That(fixture.Query("SELECT grow_type,gold FROM characters WHERE name='GrowHero'") is [[3L, var charged]] &&
                (long)charged == GrowUpChangeTests.StartGold - 100113 && fixture.Query("SELECT change_count FROM character_growup_change") is [[2L]],
                "确认后扣100113金币并提交第二次变更");
            string once = Snapshot();
            peer.Send(785, Body(4, 1)); peer.Expect(1, 785, [0, 23, 0]); peer.Barrier();
            That(Snapshot() == once, "已用报价的重复确认不再扣费");
        }
        using (var peer = Open(fixture))
        {
            var entry = peer.Select(prepared: true); peer.Barrier();
            Bytes(entry.Single(packet => packet is { Kind: 0, Opcode: 1153 }).Body, [2, 30, 0, 0, 0], "重连回放已提交次数");
            string before = Snapshot();
            var tailed = Body(4, 0);
            tailed = [.. tailed, .. BitConverter.GetBytes(ClientBodyChecksum.Compute(tailed) ^ 1)];
            peer.Send(785, tailed); peer.Closed();
            That(Snapshot() == before, "检查尾不匹配的785不进入事务");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(prepared: true); peer.Barrier();
            string before = Snapshot();
            peer.Send(785, new byte[14]); peer.Closed();
            That(Snapshot() == before, "畸形785不返回通用成功");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "职业变更、次数和金币的文件库完整");
        Console.WriteLine($"PASS {Count - checks} 技能窗职业变更785、1153入城、报价确认与重连TCP断言");

        void ReadCommitted(TcpPeer peer, byte grow, byte count)
        {
            byte[] actor = peer.Expect(0, 2).Body;
            int nameLength = checked((int)U32(actor, 123));
            That(actor[0] == 0 && U16(actor, 121) == peer.Id && actor[128 + nameLength] == grow, "ACK785前先以真实Actor安装新分支");
            var trees = SkillPacketsTests.DecodeInitialized(peer.Expect(0, 19).Body);
            That(trees.Length == 2 && trees.All(tree => tree.Skills.All(skill => skill.Command.Length == 0)), "完整19同步两树");
            That(peer.Expect(0, 2).Body[0] == 1, "随后同步Player");
            WirePacket packet;
            int guard = 0;
            while ((packet = peer.Read()) is not { Kind: 1, Opcode: 785 })
                That(++guard <= 8 && packet.Kind == 0, "ACK785前只允许有界的库存/任务通知");
            Bytes(packet.Body, [1, 0, 0, 0, 0, count], "ACK785 result0携带已提交次数");
            peer.Barrier();
        }

        string Snapshot() => fixture.Snapshot("SELECT * FROM characters") +
            fixture.Snapshot("SELECT * FROM character_growup_change") +
            fixture.Snapshot("SELECT * FROM character_skill_bindings ORDER BY character_id,context,slot") +
            fixture.Snapshot("SELECT * FROM character_skill_points ORDER BY character_id,context") +
            fixture.Snapshot("SELECT * FROM character_completed_quests ORDER BY character_id,quest_id");
    }
}
