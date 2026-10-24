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
        var growth = SelfTestCatalogs.Growth(resources.Archive, catalog);
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
        int[] completedAfterGrow = [];
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
            var learned = Role(0);
            That(Command("level GmTcp0 60").StartsWith("OK "), "在线GM改等级提交");
            var role = Role(0);
            var free60 = SkillPointLedger.FreeBaseline(0, 0, 60, catalog);
            That(role.Level == 60 && role.Learning.Experience == growth.GetLevelStartExperience(60) &&
                role.Learning.Trees.Zip(learned.Learning.Trees).All(pair =>
                    pair.First.Sp == catalog.TotalSp(60) + 137 - SkillPointLedger.Spent(pair.First.Skills, free60, 0, id => catalog.Get(0, id)) &&
                    pair.First.Tp == catalog.TotalTp(60) + 7 - SkillPointLedger.Spent(pair.First.Skills, free60, 0, id => catalog.Get(0, id), tp: true) &&
                    pair.Second.Skills.Where(skill => catalog.Get(0, skill.Id).FixedLevel is null).All(pair.First.Skills.Contains)) &&
                role.Learning.Trees[0].Skills.Single(skill => skill.Id == 5) is { Level: 2 } ghost && ghost.Command != "" &&
                role.Learning.Trees[0].Sp < role.Learning.Trees[1].Sp, "在线GM改等级保留已学技能与指令，SP/TP按新总额减已学费用重算");
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            Bytes(peer.SkillInfo, SkillPackets.Initialized(Role(0).Learning), "重登收到改等级后的技能和点数");
            That(Command("grow GmTcp0 1 2").StartsWith("OK ") && Role(0).GrowType == 33, "分支和二觉命令闭合");
            completedAfterGrow = Role(0).CompletedQuests;
            That(completedAfterGrow.Length > 0 && Role(0).AccountCompletedQuests.Length == 0,
                "GM转职觉醒已持久对应角色完成历史，重登应使用有历史的严格包序");
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
            peer.Send(508, [0, 1, 118, 0, 1, 46, 0, 0]); peer.Expect(1, 508, [1]);
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
            That(Role(0).CompletedQuests.SequenceEqual(completedAfterGrow), "服务重建前后转职觉醒完成账本不变");
            peer.Select(prepared: true);
            var history = peer.CompletedQuestHistory;
            var completed = completedAfterGrow.ToHashSet();
            That(history.Length == 30004 && U32(history) == 30000 && completed.All(id => id is > 0 and < 30000),
                "重启选角的完成历史使用完整30000项表，持久任务编号合法");
            That(Enumerable.Range(0, 30000).All(id => history[4 + id] == (id == 13700 || completed.Contains(id) ? 1 : 0)),
                "重启选角逐项恢复已提交转职觉醒历史及独立月轮山显示位");
            Bytes(peer.SkillInfo, SkillPackets.Initialized(Role(0).Learning), "服务重启保留职业、技能和点数");
        }
        using (var peer = Open(fixture))
        {
            var role = Role(1);
            That(role.CompletedQuests.Length == 0 && role.AccountCompletedQuests.Length == 0,
                "特殊职业的真实任务账本在重启后保持空集");
            peer.Select(1, combos: SkillPackets.Combos(role.Learning));
            That(peer.CompletedQuestHistory.Length == 30004 && U32(peer.CompletedQuestHistory) == 30000 &&
                Enumerable.Range(0,30000).All(id => peer.CompletedQuestHistory[4+id] == (id == 13700 ? 1 : 0)),
                "空账本重启选角严格只投影月轮山显示位，不伪造其他任务完成记录");
            Bytes(peer.SkillInfo, SkillPackets.Initialized(role.Learning), "特殊职业重启回放默认连招、觉醒与点数");
        }
        await fixture.StopAsync();
        Console.WriteLine("PASS C# TCP GM等级/职业重置、默认连招与重登/重启回放；非Windows实机");
    }
}
