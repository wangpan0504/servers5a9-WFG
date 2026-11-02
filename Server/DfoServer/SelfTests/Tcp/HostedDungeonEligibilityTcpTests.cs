using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using DfoServer.Game.Dungeons;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class HostedDungeonEligibilityTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        HostedDungeonEligibilitySessionTests.Run(resources.Towns, resources.Archive, resources.Characters);
        await OffUnchangedAsync(source, root, resources);
        await OrdinaryMissingProfileAsync(source, root, resources);
        await QuestRouteRejectedAsync(source, root, resources);
    }

    static async Task OffUnchangedAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "hosted-eligibility-off"), resources: resources);
        await fixture.StartAsync();
        using var peer = TcpPeer.Open(fixture);
        peer.Create("HostedOff");
        CompleteStoryRoute(fixture, peer.Id);
        peer.Select();
        FormalDungeonTcpTests.EntryTown(peer);
        LoadOrdinary(peer);
        That(!fixture.Events().Any(EventIsEligibility), "默认关闭不写资格探测日志");
    }

    static async Task OrdinaryMissingProfileAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source with { EnableHostedDungeonEligibility = true },
            Path.Combine(root, "hosted-eligibility-on"), resources: resources);
        await fixture.StartAsync();
        using var peer = TcpPeer.Open(fixture);
        peer.Create("HostedOn");
        CompleteStoryRoute(fixture, peer.Id);
        peer.Select();
        FormalDungeonTcpTests.EntryTown(peer);
        LoadOrdinary(peer);
        string afterLoad = fixture.Snapshot();
        var detail = await WaitEligibilityAsync(fixture);
        That(detail.GetProperty("CanFight").GetBoolean() == false &&
            detail.GetProperty("Reason").GetString() == HostedDungeonEligibilityRules.ReasonMissingCombatProfile &&
            detail.GetProperty("Applied").GetBoolean(),
            "普通房间探测缺少 CombatProfile 时明确拒绝");
        That(fixture.Snapshot() == afterLoad, "探测不改已完成入场后的角色资产");
    }

    static async Task QuestRouteRejectedAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source with { EnableHostedDungeonEligibility = true },
            Path.Combine(root, "hosted-eligibility-quest"), resources: resources);
        await fixture.StartAsync();
        using var peer = TcpPeer.Open(fixture);
        peer.Create("HostedQuest");
        peer.Select();
        FormalDungeonTcpTests.EntryTown(peer);
        FormalDungeonTcpTests.Enter(peer);
        FormalDungeonTcpTests.Load(peer);
        var detail = await WaitEligibilityAsync(fixture);
        That(detail.GetProperty("CanFight").GetBoolean() == false &&
            detail.GetProperty("Reason").GetString() == HostedDungeonEligibilityRules.ReasonNotOrdinary,
            "任务路线按特殊请求拒绝");
    }

    static void CompleteStoryRoute(TcpTestFixture fixture, ushort characterId)
    {
        fixture.Sql("DELETE FROM character_active_main_quests WHERE character_id=$p0", characterId);
        fixture.Sql("INSERT INTO character_completed_quests(character_id,quest_id) VALUES($p0,3145)", characterId);
    }

    static void LoadOrdinary(TcpPeer peer)
    {
        peer.Send(15, new byte[4]);
        peer.Expect(1, 15, [1]);
        peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 1]);
        peer.Expect(0, 27, peer.SelectionNoticeBody());
        byte[] selection = Convert.FromHexString("030000000000000000FFFF0000000000000000000000000000");
        BinaryPrimitives.WriteUInt32LittleEndian(selection, 3);
        peer.Send(16, selection);
        That(peer.Expect(0, 28).Body.Length == 36, "普通资格探测入场使用完整 NOTI28");
        _ = peer.Expect(0, 29).Body;
        FormalDungeonTcpTests.Load(peer);
    }

    static bool EventIsEligibility(JsonElement line) =>
        line.GetProperty("Kind").GetString() is "hosted_dungeon_eligibility" or "hosted_dungeon_eligibility_failed";

    static async Task<JsonElement> WaitEligibilityAsync(TcpTestFixture fixture)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(3))
        {
            var match = fixture.Events().LastOrDefault(line => line.GetProperty("Kind").GetString() == "hosted_dungeon_eligibility");
            if (match.ValueKind == JsonValueKind.Object)
                return match.GetProperty("Detail");
            await Task.Delay(25);
        }
        throw new InvalidOperationException("缺少 hosted_dungeon_eligibility 日志");
    }
}
