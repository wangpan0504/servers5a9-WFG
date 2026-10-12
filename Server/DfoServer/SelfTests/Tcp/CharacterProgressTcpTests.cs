using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.Network.Builders;
using DfoServer.Network.Management;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class CharacterProgressTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "gm-progress"), gm: true, resources: resources);
        var catalog = resources.Learning;
        var growth = CharacterGrowthCatalog.Load(resources.Archive, catalog);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { });
        foreach (byte job in new byte[] { 0, 9 })
        {
            var profile = resources.Characters.Get(job);
            var learning = SkillLearning.Initialize(job, profile.Skills, catalog).State! with
            { BonusSp = 137, BonusTp = 7, Experience = growth.GetLevelStartExperience(95) };
            learning = SkillPointLedger.Synchronize(job, 0, 95, learning, catalog);
            repository.Create(owner, new NewCharacter("GmTcp" + job, resources.Towns.Room, resources.Towns.InitialReturn)
            { Job = job, Level = 95, AcceptedStoryLevel = 1, Skills = profile.Skills, InitialLearning = learning,
                Inventory = DfoServer.Game.Inventory.Services.InventoryRewardGrantService.CreateStarter(profile) });
        }
        Character Role(ushort slot) => repository.LoadCharacter(owner, slot);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Options.PvfPath)!);
        File.Copy(source.PvfPath, fixture.Options.PvfPath);
        var offline = await fixture.CliAsync(null, "--gm", "level", "GmTcp0", "50");
        That(offline.StartsWith("OK ") && Role(0).Learning.Trees.All(tree => tree.Sp == catalog.TotalSp(50) + 137 && tree.Tp == catalog.TotalTp(50) + 7),
            "离线GM命令也使用匹配资源重置双树与点数");
        await fixture.StartAsync();
        string Command(string line)
        {
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, line, out string text), "GM端口返回 " + line);
            return text;
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Send(29, [0, 1, 5, 0, 0, 1, 0]); peer.Expect(1, 29);
            peer.Send(331, [1, 5, 0, 3, 2, 3, 4]); peer.Expect(1, 331); peer.Expect(0, 19);
            That(Command("level GmTcp0 60").StartsWith("OK "), "在线GM改等级提交");
            var role = Role(0);
            That(role.Level == 60 && role.Learning.Experience == growth.GetLevelStartExperience(60) &&
                role.Learning.Trees.All(tree => tree.Sp == catalog.TotalSp(60) + 137 && tree.Tp == catalog.TotalTp(60) + 7 &&
                    tree.Skills.All(skill => skill.Command == "")), "在线GM重新发满点并清除两树指令");
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            Bytes(peer.SkillInfo, SkillPackets.Initialized(Role(0).Learning), "重登收到重置后的技能和点数");
            That(Command("grow GmTcp0 1 2").StartsWith("OK ") && Role(0).GrowType == 33, "分支和二觉命令闭合");
            long revision = Role(0).Revision;
            That(Command("grow GmTcp0 0 1").StartsWith("ERR ") && Role(0).Revision == revision, "非法未转职觉醒不写库");
            using var options = JsonDocument.Parse(Command("grow-options GmTcp0")[3..]);
            That(options.RootElement.GetProperty("first").GetInt32() == 1 && options.RootElement.GetProperty("second").GetInt32() == 2,
                "真实GM端口返回当前选项供网页使用");
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            peer.Select(1, combos: SkillPackets.Combos(Role(1).Learning));
            peer.Send(508, [0, 1, 118, 0, 1, 46, 0, 0]); peer.Expect(0, 447);
            That(Command("grow GmTcp9 0 2").StartsWith("OK "), "特殊职业first0二觉重置");
            var role = Role(1);
            That(role.GrowType == 32 && role.Learning.Trees.All(tree => tree.Skills.All(skill => skill.Command == "") &&
                tree.Sp == catalog.TotalSp(95) + 137 && tree.Tp == catalog.TotalTp(95) + 7), "特殊职业重置指令和点数");
            peer.Barrier();
        }
        await fixture.StopAsync();
        await fixture.StartAsync("gm-progress-restart");
        using (var peer = Open(fixture))
        {
            peer.Select(); Bytes(peer.SkillInfo, SkillPackets.Initialized(Role(0).Learning), "服务重启保留职业、技能和点数");
        }
        using (var peer = Open(fixture))
        {
            var role = Role(1);
            peer.Select(1, combos: SkillPackets.Combos(role.Learning));
            Bytes(peer.SkillInfo, SkillPackets.Initialized(role.Learning), "特殊职业重启回放默认连招、觉醒与点数");
        }
        await fixture.StopAsync();
        Console.WriteLine("PASS C# TCP GM等级/职业重置、默认连招与重登/重启回放；非Windows实机");
    }
}
