using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class FiendwarFailureTcpTests
{
    // 复用引导组的只读资源、监听和文件库，正式666扣费后推进服务端原时钟。
    internal static void Run(TcpTestFixture fixture, TcpTestResources resources, LukeRaidClockTests.Clock clock)
    {
        var repository = new CharacterRepository(fixture.Database);
        foreach (var scenario in new (bool Guide, bool Leave, bool Fault)[] { (true, false, false), (false, false, false),
            (true, true, false), (true, false, true) })
        {
            string account = "fwrefund" + (scenario.Guide ? "g" : "t") + (scenario.Leave ? "l" : "s") + (scenario.Fault ? "f" : "n");
            using (var owner = new AccountOwners().Acquire(account, () => { }))
            {
                var point = new Location(38, 0, 629, 196);
                var prepared = CharacterCreation.Prepare("Fw" + account[8..], resources.Characters.Get(0), point, point, resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0, 0, 95, prepared.InitialLearning with
                    { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning);
                var role = repository.Create(owner, prepared with { Level = 95, AcceptedStoryLevel = 95,
                    CompletedQuests = [8672], InitialLearning = learning });
                var items = resources.ItemResources;
                InventoryService.Commit(repository, owner, role, InventoryRewardGrantService.TryGrantBatch(role,
                    [new(scenario.Guide ? 10309054 : 10306760, 2)], items.Items, items.GoldLimits, items.EpicPieces, clock.GetUtcNow()));
            }
            using var peer = Open(fixture, account, gamePort: ChannelTcpTests.Endpoints(fixture)[39], channelNumber: 39);
            bool failureShown = false, failureClosed = false, expectFailure = false;
            peer.Select(); Read();
            peer.Send(1426); Read();
            peer.Send(664, Build(writer => { writer.Write((byte)2); writer.Write(Name(account));
                writer.Write(new byte[] { scenario.Guide ? (byte)1 : (byte)0, 0, 0, 7 }); }));
            That(Read().Any(row => row is { Kind: 1, Opcode: 664, Body: [1, ..] }), "退票回归真实建团");
            byte area = scenario.Guide ? (byte)33 : (byte)2;
            var arrival = resources.Towns.Entrance(new(57, 1, 0, 0), new(57, area, 0, 0), new HashSet<int> { 8672 })!;
            peer.Send(36, Area(57, area, checked((ushort)arrival.X), checked((ushort)arrival.Y), 57, 1)); Read();
            peer.Send(666);
            That(Read().Any(row => row is { Kind: 1, Opcode: 666, Body: [1] }), "退票回归真实开战扣费");
            That(fixture.Query("SELECT state FROM character_raid_runs WHERE character_id=$p0", peer.Id) is [[0L]], "开战只建立原入场记录");
            if (scenario is { Guide: true, Leave: false, Fault: false })
            {
                // 34是途中区域，5350在35；沿真实两道门进入怪物房，不能在非战斗区假定已有入场。
                foreach (byte target in new byte[] { 34, 35 })
                {
                    ushort origin = checked((ushort)(target - 1));
                    var encounter = resources.Towns.Entrance(new(57, checked((byte)origin), 0, 0),
                        new(57, target, 0, 0), new HashSet<int> { 8672 })!;
                    peer.Send(36, Area(57, target, checked((ushort)encounter.X), checked((ushort)encounter.Y), 57, origin));
                    Read();
                }
                var entered = AntonTcpProbe.TakeFiendwarEntry(peer, 5350);
                That(entered.Any(row => row is { Kind: 0, Opcode: 28 }) && entered.Any(row => row is { Kind: 0, Opcode: 29 }),
                    "失败回归先真实进入引导怪物房，覆盖图内退场推进战况版本");
                peer.Send(37, new byte[8]); Read();
            }
            if (scenario.Leave) { peer.Send(665, BitConverter.GetBytes(peer.Id)); Read(); }
            if (scenario.Fault)
                fixture.Sql($"CREATE TRIGGER fiendwar_refund_rollback BEFORE UPDATE OF state ON character_raid_runs WHEN OLD.character_id={peer.Id} AND NEW.state=1 BEGIN SELECT RAISE(ABORT,'fiendwar refund rollback'); END");
            expectFailure = !scenario.Leave && !scenario.Fault;
            clock.Advance(scenario.Guide ? 1800 : 3600);
            Read();
            string mailQuery = "SELECT count(*) FROM mailbox_messages WHERE recipient_id=$p0 AND delivery_key LIKE 'fiendwar-refund:%'";
            That(fixture.Query(mailQuery, peer.Id) is [[var mailCount]] && Convert.ToInt64(mailCount) == (scenario.Leave || scenario.Fault ? 0 : 1),
                "系统失败退一票，主动离团或事务回滚不产生邮件");
            if (!scenario.Leave)
                That(fixture.Query("SELECT state FROM character_raid_runs WHERE character_id=$p0", peer.Id) is [[var state]] &&
                    Convert.ToInt64(state) == (scenario.Fault ? 0 : 1), "退票与原入场终态同事务提交或回滚");
            if (!scenario.Leave && !scenario.Fault)
            {
                That(fixture.Query("SELECT delivery_content FROM mailbox_messages WHERE recipient_id=$p0 AND delivery_key LIKE 'fiendwar-refund:%'", peer.Id)
                    is [[string content]] && content.Contains((scenario.Guide ? 10309054 : 10306760).ToString(), StringComparison.Ordinal),
                    "团队与引导分别退回真实支付票种");
                for (int budget = 0; budget < 256 && !failureClosed; budget++) Observe(peer.Read());
                That(failureShown && failureClosed, "系统失败先显示本团1523状态5，再关闭原攻坚队窗口");
                peer.Send(664, Build(writer => { writer.Write((byte)2); writer.Write(Name(account));
                    writer.Write(new byte[] { scenario.Guide ? (byte)1 : (byte)0, 0, 0, 7 }); }));
                That(Read().Any(row => row is { Kind: 1, Opcode: 664, Body: [1, ..] }), "失败后同一角色无需重登即可重建攻坚队");
                peer.Send(36, Area(57, area, checked((ushort)arrival.X), checked((ushort)arrival.Y), 57, 1)); Read();
                peer.Send(666);
                That(Read().Any(row => row is { Kind: 1, Opcode: 666, Body: [1] }), "失败后新攻坚队进入按钮仍可实际开战");
            }
            clock.FireQueued(); Read();
            That(fixture.Query(mailQuery, peer.Id) is [[var repeated]] && Convert.ToInt64(repeated) == (scenario.Leave || scenario.Fault ? 0 : 1),
                "重复时钟不二次退款");
            That(fixture.Query("SELECT count(*) FROM character_raid_runs WHERE character_id=$p0 AND (state=2 OR clear_week IS NOT NULL)", peer.Id) is [[0L]],
                "失败退票不伪造通关或重置日入场历史");
            if (scenario.Fault) fixture.Sql("DROP TRIGGER fiendwar_refund_rollback");
            peer.Send(665, BitConverter.GetBytes(peer.Id)); Read();
            List<WirePacket> Read()
            {
                var rows = AntonTcpProbe.Barrier(peer, "fiendwar refund");
                foreach (var row in rows) Observe(row);
                return rows;
            }
            void Observe(WirePacket row)
            {
                if (row is { Kind: 0, Opcode: 1523, Body: [5, 0, 0] }) failureShown = true;
                if (expectFailure && row is { Kind: 1, Opcode: 665, Body: [1, 1] })
                {
                    That(failureShown, "失败提醒必须先于原团强制离队回包");
                    failureClosed = true;
                }
            }
        }
    }
}
