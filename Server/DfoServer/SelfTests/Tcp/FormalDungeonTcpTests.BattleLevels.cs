using System.Text.Json;
using DfoServer.Development.Dungeons;
using DfoServer.Game.Dungeons;
using DfoServer.Network.Management;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunBattleLevelsAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source with { DevelopmentDungeonEntryProbe = DungeonEntryProbeMode.Disabled },
            Path.Combine(root, "monster-battle-level"), gm: true, resources: resources);
        await fixture.StartAsync();
        bool created = false;
        var baseline = Capture();
        int map = checked((int)U32(baseline, 14));
        Write([new(3, null, 5, 100)]);
        Reload(true);
        var increased = Capture();
        Compare(increased, 5);
        Write([new(3, null, 5, 100), new(3, map, 0, null)]);
        Reload(true);
        Compare(Capture(), 0);
        File.WriteAllText(Path.Combine(fixture.Options.DataDirectory, "monster-battle-level.json"), "{\"Rules\":[],\"Typo\":1}");
        Reload(false);
        Compare(Capture(), 0);
        Write([new(3, null, -2, 100)]);
        Reload(true);
        Compare(Capture(), -2);
        TestFiles.DeleteFile(Path.Combine(fixture.Options.DataDirectory, "monster-battle-level.json"));
        Reload(true);
        Compare(Capture(), 0);
        Console.WriteLine("PASS monster battle level production TCP config/reload/session/29/default restoration (synthetic client)");

        byte[] Capture()
        {
            using var peer = Open(fixture);
            if (!created) { peer.Create("BattleTcp"); created = true; }
            peer.Select();
            EntryTown(peer);
            return Enter(peer);
        }
        void Write(DungeonBattleLevelRule[] rules) => File.WriteAllText(
            Path.Combine(fixture.Options.DataDirectory, "monster-battle-level.json"), JsonSerializer.Serialize(new { Rules = rules }));
        void Reload(bool success)
        {
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "monster-battle-level reload", out var answer) &&
                answer.StartsWith(success ? "OK " : "ERR ", StringComparison.Ordinal), "真实GM重载返回预期结果:" + answer);
        }
        void Compare(byte[] packet, int offset)
        {
            That(U32(packet, 14) == map && packet[18] == baseline[18] && baseline[18] > 0,
                "对照使用同一正式任务房间和全部演员");
            int affected = 0;
            for (int i = 0; i < baseline[18]; i++)
            {
                int at = 19+i*22;
                That(U32(packet, at+8) == U32(baseline, at+8), "配置不替换怪物模板或演员顺序");
                bool monster = baseline[at+13] <= 3;
                int expected = monster && offset != 0 ? Math.Clamp(baseline[at+12]+offset, 1, 100) : baseline[at+12];
                That(packet[at+12] == expected, "真实Runtime/Session回执和29构包必须共同发送战斗等级");
                if (monster) affected++;
            }
            That(affected > 0, "对照夹具必须覆盖实际怪物，不能空列表自证");
        }
    }
}
