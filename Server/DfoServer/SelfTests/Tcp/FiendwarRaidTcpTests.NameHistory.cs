using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FiendwarRaidTcpTests
{
    static void VerifyNameHistory(TcpTestFixture fixture, TcpTestResources resources, CharacterRepository repository,
        TcpPeer requester, byte server)
    {
        const string account = "fiendname";
        using (var owner = new AccountOwners().Acquire(account, () => { }))
        {
            var role = repository.Create(owner, CharacterCreation.Prepare("FiendOld", resources.Characters.Get(0),
                resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get));
            repository.Rename(owner, role, "FiendNew");
        }
        using var target = Open(fixture, account);
        target.Select(); target.Barrier();
        string before = fixture.Snapshot("SELECT * FROM character_name_history WHERE character_id=$p0", target.Id);
        for (int repeat = 0; repeat < 2; repeat++)
        {
            requester.Send(821, Request(server, target.Id, 1, 10));
            var frames = AntonTcpProbe.Barrier(requester, "fiendwar cross-channel former name");
            var body = frames.Single(row => row is { Kind: 0, Opcode: 761 }).Body;
            int width = checked((int)U32(body, 5));
            That(body[0] == 10 && body[1] == server && U16(body, 2) == target.Id && body[4] == 1 &&
                body.Length == 14 + width && DecodeName(body, 9, width) == "FiendOld" && body[9 + width] == server &&
                U32(body, 10 + width) > 0 && !frames.Any(row => row is { Kind: 1, Opcode: 821 } or { Kind: 0, Opcode: 736 }),
                "漩涡频道实际查询普通10频道在线角色，返回原生761及真实历史，不造ACK或错用736");
        }
        requester.Send(821, Request(server, requester.Id, 1, 39));
        Bytes(AntonTcpProbe.Barrier(requester, "fiendwar empty former name").Single(row => row is { Kind: 0, Opcode: 761 }).Body,
            [39, server, (byte)requester.Id, (byte)(requester.Id >> 8), 0], "没有历史的角色发送完整零行表");
        foreach (var body in new[]
        {
            Request(unchecked((byte)(server + 1)), target.Id, 1, 10), Request(server, target.Id, 1, 39),
            Request(server, ushort.MaxValue, 1, 10), Request(server, target.Id, 0, 10),
            Request(server, target.Id, 1, 10).Concat(new byte[] { 0 }).ToArray()
        })
        {
            requester.Send(821, body);
            That(!AntonTcpProbe.Barrier(requester, "fiendwar invalid former name target").Any(row => row is { Kind: 0, Opcode: 761 }),
                "错服、错频道、离线目标、其它模式或多余尾字节不返回虚假历史");
        }
        That(fixture.Snapshot("SELECT * FROM character_name_history WHERE character_id=$p0", target.Id) == before,
            "重复和拒绝的跨频道查询不修改改名账本");

        static byte[] Request(byte server, ushort character, byte mode, byte channel) =>
            [server, (byte)character, (byte)(character >> 8), mode, channel];
    }
}
