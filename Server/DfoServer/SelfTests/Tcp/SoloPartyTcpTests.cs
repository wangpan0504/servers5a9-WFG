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
        peer.Send(12, request); peer.Expect(1, 12, [0, 19, 0]);
        peer.Select(); peer.Barrier();
        string before = fixture.Snapshot();
        peer.Send(12, request); peer.Expect(1, 12, [1]);
        var created = peer.Expect(0, 9).Body;
        That(created.Length == 56 && U16(created, 0) == 1 && U16(created, 2) == 9999 && U16(created, 4) == peer.Id &&
            created[6] == 2 && created[7] == 10 && created[9] == 0 && created[12] == 3 && created[14] == 4 &&
            created[36] == 7 && created[37] == 1 && created[38] == 0 && U16(created, 39) == peer.Id && created[42] == 100,
            "真实12成功后发送本版完整9及唯一队长槽，不靠空ACK伪造队伍");
        peer.Barrier();
        foreach (byte title in new byte[] { 4, 5 })
        {
            request[1] = title;
            peer.Send(12, request); peer.Expect(1, 12, [1]);
            var updated = peer.Expect(0, 9).Body;
            That(updated.Length == 38 && updated[9] == 1 && updated[12] == title && U16(updated, 4) == peer.Id,
                "重复创建窗口请求只更新同一队伍标题，不增加成员或替换队长");
            peer.Barrier();
        }
        peer.Send(13);
        var clear = peer.Expect(0, 9).Body;
        That(clear.Length == 11 && clear[9] == 3 && U16(clear, 4) == peer.Id, "单人退出按kind3删除同一个队伍模型");
        peer.Barrier();
        That(before == fixture.Snapshot(), "创建、修改和退队不修改数据库、位置、资产或revision");
        peer.Send(12, request); peer.Expect(1, 12, [1]); peer.Expect(0, 9); peer.Barrier();
        peer.Send(7); peer.Expect(0, 9); peer.Expect(1, 7, [1]); peer.Expect(0, 2);
        peer.Select(); peer.Barrier();
        peer.Send(13); peer.Expect(1, 13, [0, 19, 0]); peer.Barrier();
        peer.Send(12, request); peer.Expect(1, 12, [1]);
        That(peer.Expect(0, 9).Body[9] == 0, "重新选角后重新创建，不沿用旧会话的单人队伍");
        peer.Barrier();
    }
}
