using System.Buffers.Binary;
using DfoServer.Development.Dungeons;
using DfoServer.Network.Management;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunMonsterAiAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source with { DevelopmentDungeonEntryProbe = DungeonEntryProbeMode.Disabled },
            Path.Combine(root, "monster-ai"), gm: true, resources: resources);
        await fixture.StartAsync();
        bool created = false;
        string path = Path.Combine(fixture.Options.DataDirectory, "monster-ai.json");
        // 默认关闭：读到原生29本身就证明29之前没有私有包，之后再确认不会补发。
        Capture();
        Write("{\"Enabled\":true,\"Rules\":[{\"DungeonId\":3,\"Warlike\":{\"Value\":90}}]}");
        Reload(true);
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "monster-ai status", out var status) &&
            status.StartsWith("OK ", StringComparison.Ordinal), "GM状态查询返回开启状态:" + status);
        var enabled = CaptureAi(out var profile);
        That(profile.Length == 56 && U32(profile, 0) == 0x3149414D && U32(profile, 4) == 1 && U32(profile, 8) == 3 &&
            U32(profile, 12) == U32(enabled, 14) && U32(profile, 16) == U32(enabled, 3) &&
            profile[20] == enabled[0] && profile[21] == enabled[1] && profile[22] == 0 && profile[23] == 0,
            "私有包序号从1开始，副本/地图/种子/坐标与同次真实29逐字段一致且排在29之前");
        That(U32(profile, 24) == 1 && BinaryPrimitives.ReadSingleLittleEndian(profile.AsSpan(28)) == 90f &&
            U32(profile, 32) == 0 && U32(profile, 40) == 0 && U32(profile, 48) == 0 &&
            profile.AsSpan(36, 4).SequenceEqual(new byte[4]) && profile.AsSpan(44, 4).SequenceEqual(new byte[4]) &&
            profile.AsSpan(52, 4).SequenceEqual(new byte[4]),
            "命中规则的好战性固定90，未配置的三项显式原生且不带数字");
        Write("{\"Enabled\":true,\"Rules\":[{\"DungeonId\":3,\"Warlike\":{\"Value\":70}}]}");
        Reload(true);
        CaptureAi(out var reloaded);
        That(BinaryPrimitives.ReadSingleLittleEndian(reloaded.AsSpan(28)) == 70f && U32(reloaded, 4) == 1,
            "GM重载后新连接使用新参数，私有包序号按连接重新从1开始");
        Write("{\"Enabled\":true,\"Rules\":[{\"DungeonId\":0,\"HitPoints\":{\"Multiplier\":3},\"Defense\":{\"Multiplier\":2}}]}");
        Reload(true);
        var expandedMap = CaptureAi(out var expanded);
        That(expanded.Length == 72 && U32(expanded, 0) == 0x3249414D && U32(expanded, 8) == 3 &&
            U32(expanded, 12) == U32(expandedMap, 14) && U32(expanded, 56) == 2 &&
            BinaryPrimitives.ReadSingleLittleEndian(expanded.AsSpan(60)) == 3 && U32(expanded, 64) == 2 &&
            BinaryPrimitives.ReadSingleLittleEndian(expanded.AsSpan(68)) == 2,
            "全图规则在真实入场生成MAI2，血防值与正式房间身份一致");
        Write("{\"Enabled\":true,\"Rules\":[{\"DungeonId\":3,\"Warlike\":{\"Value\":70}}]}");
        Reload(true);
        Write("{\"Enabled\":true,\"Rules\":[{\"DungeonId\":3,\"Warlike\":{\"Value\":70}}],\"Typo\":1}");
        Reload(false);
        CaptureAi(out var kept);
        That(BinaryPrimitives.ReadSingleLittleEndian(kept.AsSpan(28)) == 70f,
            "非法配置整批拒绝，线上继续使用上一份合法快照");
        Write("{\"Enabled\":true,\"Rules\":[{\"DungeonId\":4,\"Warlike\":{\"Value\":90}}]}");
        Reload(true);
        var neutral = CaptureAi(out var unmatched);
        That(U32(unmatched, 0) == 0x3149414D && U32(unmatched, 8) == 3 && U32(unmatched, 12) == U32(neutral, 14) &&
            U32(unmatched, 24) == 0 && U32(unmatched, 32) == 0 && U32(unmatched, 40) == 0 && U32(unmatched, 48) == 0 &&
            unmatched.AsSpan(28).ToArray().All(value => value == 0),
            "开启但未命中副本时仍发四项全mode0的中性包，不带任何数字");
        Write("{\"Enabled\":false,\"Rules\":[{\"DungeonId\":3,\"Warlike\":{\"Value\":90}}]}");
        Reload(true);
        Capture();
        File.Delete(path);
        Reload(true);
        Capture();
        Console.WriteLine("PASS monster ai production TCP config/reload/29 ordering/private packet (synthetic client)");

        // 关闭状态：Enter按顺序读到28、29，已证明29前无私有包；NoFrame再排除29之后补发。
        byte[] Capture()
        {
            using var peer = Open(fixture);
            if (!created) { peer.Create("MonsterAiTcp"); created = true; }
            peer.Select();
            EntryTown(peer);
            byte[] map = Enter(peer);
            peer.NoFrame(150, "怪物AI关闭时整次进房不得出现私有包", (0, 32752));
            return map;
        }
        byte[] CaptureAi(out byte[] body)
        {
            using var peer = Open(fixture);
            if (!created) { peer.Create("MonsterAiTcp"); created = true; }
            peer.Select();
            EntryTown(peer);
            byte[]? captured = null;
            byte[] map = Enter(peer, beforeMap: () => captured = peer.Expect(0, 32752).Body);
            body = captured!;
            return map;
        }
        void Write(string json) => File.WriteAllText(path, json);
        void Reload(bool success)
        {
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "monster-ai reload", out var answer) &&
                answer.StartsWith(success ? "OK " : "ERR ", StringComparison.Ordinal), "真实GM重载返回预期结果:" + answer);
        }
    }
}
