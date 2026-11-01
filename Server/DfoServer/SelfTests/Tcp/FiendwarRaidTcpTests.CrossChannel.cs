using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FiendwarRaidTcpTests
{
    static void VerifyCrossChannel(TcpTestFixture fixture, TcpTestResources resources, CharacterRepository repository,
        TcpPeer leader, byte server, uint raidKey)
    {
        const string account = "fiendcross";
        using (var owner = new AccountOwners().Acquire(account, () => { }))
        {
            var role = repository.Create(owner, CharacterCreation.Prepare("FwCross", resources.Characters.Get(0),
                resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get) with
                { Level = 95, AcceptedStoryLevel = 95, CompletedQuests = [8672] });
            repository.CommitInventory(owner, role, new(role.Inventory, role.Inventory with { ReviveCoins = 17 }, "fixture_cross_coins"));
        }
        for (int direction = 0; direction < 2; direction++)
        {
            ushort entrant;
            using (var original = Open(fixture, account))
            {
                original.Select(); original.Barrier();
                entrant = original.Id;
                var source = direction == 0 ? original : leader;
                var target = direction == 0 ? leader : original;
                source.Send(822, Build(writer =>
                {
                    writer.Write((byte)1); writer.Write(server); writer.Write(target.Id);
                    writer.Write(999u); writer.Write((ushort)1234); writer.Write((byte)(direction == 0 ? 39 : 10)); writer.Write((byte)0);
                }));
                Bytes(AntonTcpProbe.Barrier(source, "cross request ack").Single(row => row is { Kind: 1, Opcode: 822 }).Body,
                    [1], "822只在真实邀请已投递后回成功，不附猜测ACK正文");
                var seeded = target.ExpectOtherPlayerActor(source.Id, direction == 0 ? (byte)10 : (byte)39);
                var notices = AntonTcpProbe.Barrier(target, "cross request summary");
                notices.Insert(0, seeded);
                var summary = notices.Single(row => row is { Kind: 0, Opcode: 758 }).Body;
                int actor = notices.FindIndex(row => row is { Kind: 0, Opcode: 2 } && row.Body.Length >= 123 && U16(row.Body, 121) == source.Id);
                int summaryAt = notices.FindIndex(row => row is { Kind: 0, Opcode: 758 });
                That(actor >= 0 && actor < summaryAt && summary.Length == 59 && U16(summary) == source.Id &&
                    summary[4] == server && summary[5] == (direction == 0 ? 10 : 39) &&
                    U32(summary, 6) == (direction == 0 ? 17u : 20u) && U16(summary, 14) == 1234,
                    "758先建立目标Actor，摘要使用真实复活币，客户端999不能伪造资产显示");
                if (direction == 0)
                {
                    original.Send(11, AntonRaidTcpTests.Peer(leader.Id, 0, false));
                    Bytes(AntonTcpProbe.Barrier(original, "cross before approval").Single(row => row is { Kind: 1, Opcode: 11 }).Body,
                        [0, 3, 0, 10], "未获队长接受的申请不能直接11入团");
                    leader.Send(823, [server, (byte)entrant, (byte)(entrant >> 8), 10]);
                    Bytes(AntonTcpProbe.Barrier(leader, "cross approval ack").Single(row => row is { Kind: 1, Opcode: 823 }).Body,
                        [1], "队长823真实授权切频道，尚未加团");
                    var leaderActor = original.ExpectOtherPlayerActor(leader.Id, 39);
                    var transfer = AntonTcpProbe.Barrier(original, "cross approved transfer");
                    transfer.Insert(0, leaderActor);
                    int responseAt = transfer.FindIndex(row => row is { Kind: 0, Opcode: 759 });
                    That(responseAt >= 0 && transfer.Take(responseAt).Any(row => row is { Kind: 0, Opcode: 2 } && U16(row.Body, 121) == leader.Id) &&
                        transfer.Take(responseAt).Any(row => row is { Kind: 0, Opcode: 590 } && U32(row.Body, 4) == raidKey),
                        "759前安装目标队长及其真实团目录，不能裸发换频道通知");
                    Bytes(transfer[responseAt].Body, [server, (byte)leader.Id, (byte)(leader.Id >> 8), 0, 0, 39],
                        "759定位队长所在漩涡频道");
                }
                else
                    That(notices.Take(summaryAt).Any(row => row is { Kind: 0, Opcode: 590 } && U32(row.Body, 4) == raidKey),
                        "主动邀请摘要之前发布真实团目录，散人可直接按原生路径换频道");
            }
            using var renewed = Open(fixture, account, gamePort: ChannelTcpTests.Endpoints(fixture)[39], channelNumber: 39);
            renewed.Select(); renewed.Barrier();
            That(renewed.Id == entrant, "真实重登录保留角色号，由新owner继续原授权");
            renewed.Send(11, AntonRaidTcpTests.Peer(leader.Id, 0, false));
            var joined = AntonTcpProbe.Barrier(renewed, "cross final response");
            That(joined.Any(row => row is { Kind: 1, Opcode: 11, Body: [1, ..] }) &&
                joined.Any(row => row is { Kind: 0, Opcode: 591 }),
                "两个邀请方向均经新连接的原11正式加入，收到真实名册");
            AntonTcpProbe.Barrier(leader, "cross leader joined");
            renewed.Send(11, AntonRaidTcpTests.Peer(leader.Id, 0, false));
            That(AntonTcpProbe.Barrier(renewed, "cross duplicate final").Any(row => row is { Kind: 1, Opcode: 11, Body: [0, ..] }),
                "消费后的凭证不重复加入或重放成功");
            renewed.Send(665, BitConverter.GetBytes(renewed.Id));
            That(AntonTcpProbe.Barrier(renewed, "cross leave").Any(row => row is { Kind: 1, Opcode: 665 }),
                "跨频道加入后沿原665正常离团");
            AntonTcpProbe.Barrier(leader, "cross leader leave");
        }
    }
}
