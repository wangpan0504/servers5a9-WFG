using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class SoloPartyTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "solo-party"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repository.Create(owner, CharacterCreation.Prepare("SoloParty", resources.Characters.Get(0), resources.Towns.Room, resources.Towns.InitialReturn, null));
        await fixture.StartAsync();
        using var peer = Open(fixture);
        byte[] request = Convert.FromHexString("000304000000000500000000000007");
        peer.Send(12, request);
        peer.Select(); peer.Barrier();
        string before = fixture.Snapshot();
        peer.Send(12, request);
        var created = OwnInformation();
        That(created.Length == 56 && U16(created, 0) == 1 && U16(created, 2) == 9999 &&
            created[6] == 2 && created[7] == 10 && created[9] == 0 && created[12] == 3 && created[14] == 4 &&
            created[36] == 7 && created[37] == 1 && created[38] == 0 && U16(created, 39) == peer.Id,
            "真实12使用统一队伍名册，唯一队长槽字段完整");
        peer.Barrier();
        foreach (byte title in new byte[] { 4, 5 })
        {
            request[1] = title;
            peer.Send(12, request);
            var updated = OwnInformation();
            That(updated.Length == 56 && updated[9] == 0 && updated[12] == title && U16(updated, 4) == U16(created, 4),
                "重复创建修改同一队伍，仍发送本版完整设置与成员名册");
            peer.Barrier();
        }
        peer.Send(13); peer.Expect(1, 13, [1]);
        var clear = peer.Expect(0, 9).Body;
        That(clear.Length == 11 && clear[9] == 3 && U16(clear, 4) == U16(created, 4), "退队删除同一队伍模型");
        peer.Barrier();
        That(before == fixture.Snapshot(), "创建、修改及退队不写数据库、资产或revision");
        peer.Send(12, request); OwnInformation(); peer.Barrier();
        peer.Send(7); peer.Batch((0, 9), (1, 7), (0, 2));
        peer.Select(); peer.Barrier();
        peer.Send(13); peer.Expect(1, 13, [0, 19, 0]); peer.Barrier();
        peer.Send(12, request);
        That(OwnInformation()[9] == 0, "重选重新创建，不能继承旧队伍");
        peer.Barrier();

        byte[] OwnInformation()
        {
            peer.Batch((0, 2), (0, 153), (0, 11));
            var body = peer.Expect(0, 9).Body;
            peer.Batch((0, 11), (0, 153), (0, 26));
            return body;
        }
    }
}
