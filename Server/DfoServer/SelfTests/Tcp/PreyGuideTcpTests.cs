using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Raids;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Raids;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class PreyGuideTcpTests
{
    internal static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var clock = new LukeRaidClockTests.Clock();
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "prey-guide-flow"), resources: resources, clock: clock);
        await fixture.StartAsync();
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var owner = owners.Acquire("preyflow", () => { }))
        {
            var point = new Location(38, 0, 629, 196);
            var candidate = CharacterCreation.Prepare("PreyFlow", resources.Characters.Get(0), point, point, resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                { Experience = resources.Growth.GetLevelStartExperience(95) }, resources.Learning);
            var role = repository.Create(owner, candidate with { Level = 95, AcceptedStoryLevel = 95,
                CompletedQuests = [8935], InitialLearning = learning });
            var items = resources.ItemResources;
            InventoryService.Commit(repository, owner, role,
                new InventoryService(items.Items, items.GoldLimits, items.EpicPieces).Grant(role, [new(10309342, 2)]));
        }
        var catalog = RaidCatalog.Load(resources.Archive).PrepareResources(resources.Archive,
            SelfTestCatalogs.Dungeons(resources.Archive), resources.ItemResources.Items).Find(3)!.Resources!.EntryCatalog!;
        using var peer = Open(fixture, "preyflow", gamePort: ChannelTcpTests.Endpoints(fixture)[42], channelNumber: 42);
        peer.Select();
        Read("selected");
        peer.Send(664, Build(writer => { writer.Write((byte)3); writer.Write(5u); writer.Write("guide"u8); writer.Write(new byte[] { 1, 0, 0, 0 }); }));
        var created = Read("created");
        That(created.Any(packet => packet is { Kind: 1, Opcode: 664, Body: [1, ..] }), "TCP真实664创建单人引导团");
        peer.Send(666);
        var started = Read("started");
        That(started.Any(packet => packet is { Kind: 1, Opcode: 666, Body: [1] }), "TCP真实666启动普雷引导");
        That(fixture.Query("SELECT raid_type,state FROM character_raid_runs WHERE character_id=$p0", peer.Id) is [[3L, 0L]],
            "TCP费用与类型3生命周期共同提交");
        peer.Send(666);
        That(Read("start replay").Any(packet => packet is { Kind: 1, Opcode: 666, Body: [1] }), "TCP重复666只回放成功");
        clock.Advance(25);
        peer.Send(1361, Build(writer => { writer.Write((byte)2); writer.Write(U32(created.Single(packet => packet is { Kind: 1, Opcode: 664 }).Body, 1)); }));
        var prepared = Read("prepared");
        var symbolPacket = prepared.LastOrDefault(packet => packet is { Kind: 0, Opcode: 583 }) ??
            started.Last(packet => packet is { Kind: 0, Opcode: 583 });
        var symbols = ReadSymbols(symbolPacket.Body);
        int branch = symbols[129];
        var report = new AntonTcpProbe.AntonProbeReport();
        foreach (int dungeon in branch == 0 ? new[] { 291100391, 291100393 } : new[] { 291100390, 291100392 })
            AntonTcpProbe.Enter(peer, catalog, dungeon, report, clear: true);
        Claim(0);
        AntonTcpProbe.Enter(peer, catalog, 291100394, report, clear: true);
        AntonTcpProbe.Enter(peer, catalog, 291100397, report, clear: true);
        Claim(1);
        That(fixture.Query("SELECT state,phase0_event IS NOT NULL,phase1_event IS NOT NULL FROM character_raid_runs WHERE character_id=$p0", peer.Id)
            is [[2L, 1L, 1L]], "TCP真实MAP死亡与退场生成两阶段最终通关记录");
        That(fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE character_id=$p0 AND raid_type=3", peer.Id)
            is [[4L]], "TCP两阶段两类奖励各提交一次");

        void Claim(int phase)
        {
            foreach (byte kind in new byte[] { 1, 0 })
            {
                peer.Send(660, [kind, 0]);
                var packets = Read("reward");
                int ack = packets.FindIndex(packet => packet is { Kind: 1, Opcode: 660, Body: [1] });
                var display = packets.LastOrDefault(packet => packet is { Kind: 0, Opcode: 600 } && packet.Body[0] == kind);
                That(ack >= 0 && display is not null && packets.IndexOf(display) < ack, "TCP600真实产物在660成功前发布：" + phase);
                var body = display!.Body;
                That(body[1] == 1 && U16(body, 2) == peer.Id && body[4] == 0 && body[5] > 0 && body.Length == 6 + 16 * body[5],
                    "TCP普雷通用600完整16B行且归本人");
                for (int index = 0; index < body[5]; index++)
                {
                    int at = 6 + index * 16;
                    That(body[at] == 0 && body[at + 7] == 0 && U32(body, at + 8) == 0 &&
                        U16(body, at + 5) > 0 && resources.ItemResources.Items.TryGet(checked((int)U32(body, at + 1)), out _),
                        "TCP600每项使用真实资源产物及完整显式字段");
                }
                if (kind == 1)
                {
                    var receipts = fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE character_id=$p0", peer.Id);
                    peer.Send(660, [kind, 0]);
                    That(Read("reward replay").Any(packet => packet is { Kind: 1, Opcode: 660, Body: [1] }) &&
                        fixture.Query("SELECT count(*) FROM character_raid_reward_receipts WHERE character_id=$p0", peer.Id)[0][0].Equals(receipts[0][0]),
                        "TCP重复660回放确认且不多写收据");
                }
            }
        }
        List<WirePacket> Read(string action) => AntonTcpProbe.Barrier(peer, "prey " + action);
    }

    static Dictionary<int, int> ReadSymbols(byte[] body)
    {
        var result = new Dictionary<int, int>();
        for (int index = 0; index < body[0]; index++) result.Add(checked((int)U32(body, 1 + index * 8)), unchecked((int)U32(body, 5 + index * 8)));
        return result;
    }
}
