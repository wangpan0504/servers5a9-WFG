using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

// ACK LOGIN第3字节安装已确认的频道类型：团本准入与普通频道冒险团标签共用该上下文。
internal static class RaidChannelTypeTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "raid-channel-type"), resources: resources);
        await fixture.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        byte normal = endpoints.Keys.Min();
        foreach (var (channel, type) in new[] { ((byte)19, (byte)0x17), ((byte)20, (byte)0x20), ((byte)39, (byte)0x2D),
            ((byte)41, (byte)0x36), ((byte)42, (byte)0x32), (normal, (byte)22), ((byte)32, (byte)0), ((byte)43, (byte)0) })
        {
            using var peer = Open(fixture, "chtype" + channel, gamePort: endpoints[channel], channelNumber: channel);
            That(peer.LoginEndpoint.Length > 3 && peer.LoginEndpoint[3] == type,
                $"频道 {channel} ACK LOGIN 第 3 字节 = 0x{type:X2}（实际 0x{(peer.LoginEndpoint.Length > 3 ? peer.LoginEndpoint[3] : -1):X2}）");
        }
        // 在名册发布后改库，证明权威门禁不信任旧资格位；同时覆盖直接C2S4与先C2S8的入口。
        foreach (var (channel, label, level, entryQuest, prerequisite) in new[]
            { ((byte)19, "anton", 85, 12829, 3779), ((byte)20, "luke", 90, 2903, 4872) })
        foreach (int scenario in Enumerable.Range(0, 3))
        {
            string account = label + "admission" + scenario;
            var point = new DfoServer.GameWorld.Towns.Location(38, 0, 629, 196);
            DfoServer.Game.Characters.Character role;
            using (var owner = new DfoServer.Game.Accounts.AccountOwners().Acquire(account, () => { }))
                role = new DfoServer.Persistence.CharacterRepository(fixture.Database).Create(owner,
                    new(label + "Gate" + scenario, point, point) { Level = (byte)level, CompletedQuests = [entryQuest, prerequisite] });
            using var peer = Open(fixture, account, gamePort: endpoints[channel], channelNumber: channel);
            if (scenario >= 2) peer.Send(8, [255, 255, 2]);
            peer.Send(1278, []); peer.Expect(1, 1278, [1]);
            if (scenario < 2)
                fixture.Sql("DELETE FROM character_completed_quests WHERE character_id=$p0 AND quest_id=$p1",
                    role.Id, scenario == 0 ? entryQuest : prerequisite);
            else
                fixture.Sql("UPDATE characters SET level=$p1 WHERE id=$p0", role.Id, level - 1);
            string before = fixture.Snapshot("SELECT town,area,x,y,return_town,return_area,revision FROM characters WHERE id=$p0", role.Id);
            peer.Send(4, [0, 0]);
            peer.Expect(1, 4, [0, 96, 0]);
            peer.Send(1278, []); peer.Expect(1, 1278, [1]);
            That(fixture.Snapshot("SELECT town,area,x,y,return_town,return_area,revision FROM characters WHERE id=$p0", role.Id) == before,
                label + "拒绝过期名册、缺前置或等级越界，不保存落点/返回地区且连接仍可处理后续请求");
        }
    }
}
