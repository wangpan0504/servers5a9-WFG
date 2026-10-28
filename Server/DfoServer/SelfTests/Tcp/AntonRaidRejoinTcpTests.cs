using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class AntonRaidRejoinTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await RunCaseAsync(source, root, resources, enabled: false);
        await RunCaseAsync(source, root, resources, enabled: true);
    }

    static async Task RunCaseAsync(ServerOptions source, string root, TcpTestResources resources, bool enabled)
    {
        string name = enabled ? "anton-raid-rejoin" : "anton-raid-rejoin-off";
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, name), resources: resources);
        Directory.CreateDirectory(fixture.Options.DataDirectory);
        string json = enabled
            ? """{"StartMinimumOverride":1,"AntonRejoinEnabled":true}"""
            : """{"StartMinimumOverride":1}""";
        File.WriteAllText(Path.Combine(fixture.Options.DataDirectory, "raid-start.json"), json);
        await fixture.StartAsync();
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        var endpoints = ChannelTcpTests.Endpoints(fixture);
        for (int index = 0; index < 2; index++)
        {
            using var owner = owners.Acquire("antonrejoin" + index, () => { });
            var point = new Location(38, 0, 629, 196);
            var candidate = CharacterCreation.Prepare("Rej" + index, resources.Characters.Get(0), point, point, resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0, 0, 85, candidate.InitialLearning with
                { Experience = resources.Growth.GetLevelStartExperience(85) }, resources.Learning);
            var created = repository.Create(owner, candidate with
                { Level = 85, AcceptedStoryLevel = 85, CompletedQuests = [12829, 3779], InitialLearning = learning });
            var items = resources.ItemResources;
            InventoryService.Commit(repository, owner, created,
                new InventoryService(items.Items, items.GoldLimits, items.EpicPieces).Grant(created, [new(10096296, 2)]));
        }
        using var leader = Open(fixture, "antonrejoin0", gamePort: endpoints[19], channelNumber: 19);
        using var member = Open(fixture, "antonrejoin1", gamePort: endpoints[19], channelNumber: 19);
        leader.Select();
        member.Select();
        AntonRaidTcpTests.Drain(leader);
        AntonRaidTcpTests.Drain(member);
        leader.Send(664, Build(w => { w.Write((byte)0); w.Write(Name("Rejoin")); w.Write(new byte[4]); }));
        var createdList = AntonRaidTcpTests.Until(leader, 0, 590).Body;
        uint key = U32(createdList, 4);
        AntonRaidTcpTests.Until(leader, 0, 591);
        AntonRaidTcpTests.Until(leader, 1, 664);
        member.Send(10, AntonRaidTcpTests.Peer(leader.Id, 7, true));
        AntonRaidTcpTests.Until(member, 1, 10);
        var invite = AntonRaidTcpTests.Until(leader, 0, 7).Body;
        That(U16(invite) == member.Id && invite[2] == 10, "队长收到断线夹具的入队申请");
        leader.Send(11, AntonRaidTcpTests.Peer(member.Id, 7, false));
        AntonRaidTcpTests.Until(leader, 1, 11);
        AntonRaidTcpTests.Drain(leader);
        AntonRaidTcpTests.Drain(member);
        leader.Send(669, Build(w => { w.Write(0u); w.Write((uint)leader.Id); w.Write(1u); }));
        AntonRaidTcpTests.Until(leader, 1, 669);
        leader.Send(669, Build(w => { w.Write(0u); w.Write((uint)member.Id); w.Write(1u); }));
        AntonRaidTcpTests.Until(leader, 1, 669);
        AntonRaidTcpTests.Drain(leader);
        AntonRaidTcpTests.Drain(member);
        leader.Send(666);
        Bytes(ReadUntil(leader, 1, 666).Body, [1], "两人覆盖完成开战");
        QuietDrain(leader);
        QuietDrain(member);
        ushort disconnected = member.Id;
        member.Dispose();
        bool left = false;
        for (int at = 0; at < 64 && !left; at++)
        {
            var packet = leader.Read();
            left = packet is { Kind: 0, Opcode: 591 } && U32(packet.Body, 4) == 3;
        }
        QuietDrain(leader);
        That(left && AntonRaidTcpTests.Information(leader, key, 1).Members.All(row => row.Character != disconnected),
            "断线后幸存者名册不再包含该成员");
        using var again = Open(fixture, "antonrejoin1", gamePort: endpoints[19], channelNumber: 19);
        again.Select();
        string logs = fixture.Options.LogDirectory;
        if (!enabled)
        {
            AntonRaidTcpTests.Drain(again);
            again.Send(668, Build(w => w.Write(key)));
            Bytes(AntonRaidTcpTests.Until(again, 1, 668).Body, [0, 19, 0], "关闭时668仍是原拒绝");
            await fixture.StopAsync();
            var events = CompleteEvents(logs);
            That(!events.Any(row => row.GetProperty("Kind").GetString() == "raid_rejoin_notice"), "开关关闭不在重登后下发835");
            That(LastReason(events, "raid_start_request") == "formal-raid-rejoin-unavailable", "关闭时日志保持原拒绝原因");
            return;
        }
        var notice = ReadNotice(again);
        That(notice.Length == 7 && U32(notice) == key && notice[4] == 0 && notice[5] == 0 && notice[6] == 0,
            "重登后835携带原团key和未知零尾");
        again.Send(668, Build(w => w.Write(key)));
        Bytes(ReadAck(again, 668), [0, 19, 0], "校验通过仍回已证错误19");
        again.Send(668, Build(w => w.Write(key)));
        Bytes(ReadAck(again, 668), [0, 19, 0], "重复668拒绝");
        again.Send(668, Build(w => w.Write(key + 9)));
        Bytes(ReadAck(again, 668), [0, 19, 0], "错误key拒绝");
        var members = AntonRaidTcpTests.Information(leader, key, 1).Members;
        That(members.All(row => row.Character != again.Id), "拒绝后原成员没有回到当前名册");
        await fixture.StopAsync();
        var saved = CompleteEvents(logs);
        That(LastReason(saved, "raid_rejoin_notice") == "835-tail-semantics-unknown", "835尾部在日志中标为unknown");
        That(saved.Any(row => row.GetProperty("Kind").GetString() == "raid_rejoin_rejected" &&
            row.GetProperty("Detail").GetProperty("Reason").GetString() == "recovery-sequence-unknown"), "成功恢复包序unknown，不替换owner");
        That(saved.Any(row => row.GetProperty("Kind").GetString() == "raid_rejoin_rejected" &&
            row.GetProperty("Detail").GetProperty("Reason").GetString() == "duplicate-rejoin"), "重复重入有独立原因");
    }

    static string? LastReason(JsonElement[] events, string kind) => events.Last(row => row.GetProperty("Kind").GetString() == kind)
        .GetProperty("Detail").GetProperty("Reason").GetString();

    static JsonElement[] CompleteEvents(string directory)
    {
        var events = new List<JsonElement>();
        foreach (var line in File.ReadLines(Path.Combine(directory, "server.jsonl")))
        {
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                events.Add(doc.RootElement.Clone());
            }
            catch (JsonException) { }
        }
        return events.ToArray();
    }

    static void QuietDrain(TcpPeer peer)
    {
        peer.Send(1278);
        ReadUntil(peer, 1, 1278);
    }

    static WirePacket ReadUntil(TcpPeer peer, byte kind, ushort opcode)
    {
        for (int at = 0; at < 256; at++)
        {
            var packet = peer.Read();
            if (packet.Kind == kind && packet.Opcode == opcode) return packet;
        }
        throw new InvalidOperationException($"没有 {kind}/{opcode}");
    }

    static byte[] ReadNotice(TcpPeer peer)
    {
        for (int at = 0; at < 24; at++)
        {
            var packet = peer.Read();
            if (packet is { Kind: 0, Opcode: 835 }) return packet.Body;
        }
        throw new InvalidOperationException("重登后没有835");
    }

    static byte[] ReadAck(TcpPeer peer, ushort opcode)
    {
        for (int at = 0; at < 24; at++)
        {
            var packet = peer.Read();
            if (packet.Kind == 1 && packet.Opcode == opcode) return packet.Body;
        }
        throw new InvalidOperationException("没有668应答");
    }
}
