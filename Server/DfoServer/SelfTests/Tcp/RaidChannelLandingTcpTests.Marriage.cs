using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Marriage;
using DfoServer.GameWorld.Marriage;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class RaidChannelLandingTcpTests
{
    public static async Task RunMarriageAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int checks = Count;
        MarriageLifecycleTests.RunCore();
        MarriageCoupleTests.RunCore();
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "marriage-channel"), resources: resources);
        await fixture.StartAsync();
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        var character = CreateRaider(fixture, resources, "MarryCh");
        using (var peer = Open(fixture, "marriagepartner")) peer.Create("MarryPeer", 1);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var first = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        using (var second = owners.Acquire("marriagepartner", () => { }))
        {
            var partner = repository.LoadCharacter(second, 0);
            var catalog = MarriageCatalog.Load(resources.Archive);
            var furniture = catalog.Furniture[10002693];
            var record = MarriageWeddingRules.CreateEngagement(Guid.NewGuid(), 9501, character.Id, partner.Id,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()) with
            {
                State = 4, RingLevel = 1, MarriedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Furniture = ImmutableDictionary<ushort, MarriageFurnitureStack>.Empty.Add(
                    checked((ushort)furniture.Placements[0].PositionId), new(10002693, 1))
            };
            character = repository.CommitMarriageTogether(first, second, character, partner,
                new(character.Inventory, character.Inventory, "marriage_channel_fixture"),
                new(partner.Inventory, partner.Inventory, "marriage_channel_fixture"), null, record).First;
        }
        // 10002693的真实材料贡献200；预期常数独立于生产计分器。
        var change = Switcher(fixture, resources, endpoints, character, marriageScore: 200);
        byte normal = endpoints.Keys.Min();
        foreach (byte channel in new byte[] { 19, 20, 39, 42 })
        {
            byte town = channel == 19 ? (byte)19 : (byte)38;
            byte area = channel == 19 ? (byte)0 : (byte)1;
            change(channel, true, town, "进入团队频道保留婚房属性", area);
            change(channel, false, null, "团队频道内重连保留婚房属性");
            change(normal, true, 38, "离开团队频道保留婚房属性", 1);
            change(normal, false, null, "普通频道内重连保留婚房属性");
        }
        That(repository.FindMarriage(character.Id) is { State: 4, RingLevel: 1, Furniture.Count: 1 },
            "多次频道切换不改关系、戒指或家具存档");
        Console.WriteLine($"PASS {Count - checks} marriage channel TCP assertions");
    }
}
