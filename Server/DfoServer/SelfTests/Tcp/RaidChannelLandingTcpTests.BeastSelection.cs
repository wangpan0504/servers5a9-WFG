using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class RaidChannelLandingTcpTests
{
    public static async Task RunBeastSelectionAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var clock = new DailyBoosterTestClock(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.FromHours(8)));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "beast-selection-wire"), resources: resources, clock: clock);
        await fixture.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        var area = resources.Towns.Areas[(31, 2)];
        var bounds = area.Map(RaidQuests.ToHashSet()).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(RaidQuests.Contains)).Bounds;
        var position = new Location(31, 2, (ushort)(bounds.X + bounds.Width / 2), (ushort)(bounds.Y + bounds.Height / 2));
        var character = CreateRaider(fixture, resources, "BeastSelTCP", position);
        var pair = BeastDungeonRotation.Pair(BeastDungeonRotation.ServiceDay(clock.GetUtcNow()));
        foreach (byte number in new byte[] { 10, 11 })
        {
            using var peer = Open(fixture, gamePort: endpoints[number], channelNumber: number);
            That(peer.LoginEndpoint[3] == 22, "两个普通频道都使用真实LOGIN type22");
            peer.Select(character.Slot, prepared: true, resumeTown: true, room: false);
            peer.Barrier();
            peer.Send(15, new byte[4]);
            peer.Expect(1, 15, [1]);
            peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]);
            peer.Expect(0, 27, peer.SelectionNoticeBody(selectionCandidates: [pair.First, pair.Second]));
            That(!Enumerable.Range(310, 5).Any(peer.DungeonEnterCounts.ContainsKey),
                "原五图不收到冒充Beast额度的550普通次数通知");
            peer.Barrier();
            peer.Send(3, [1]); peer.Expect(1, 3, [1]); peer.Closed();
        }
        That(fixture.Query("SELECT count(*) FROM character_beast_dungeon_entries") is [[0L]],
            "查看每日两图及换普通频道均不写入或消耗Beast额度");
        await RunBeastNativeDeadlineAsync(fixture, resources, clock, endpoints[10], character.Slot, pair.First);
    }
}
