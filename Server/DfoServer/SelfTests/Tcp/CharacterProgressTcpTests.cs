using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
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
        CharacterProgressResetTests.RunProfessions(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources);
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
        var baseProfile = resources.Characters.Get(0);
        repository.Create(owner, new NewCharacter("GmBase", resources.Towns.Room, resources.Towns.InitialReturn)
        { Job = 0, AcceptedStoryLevel = 1, Skills = baseProfile.Skills, InitialLearning = SkillLearning.Initialize(0, baseProfile.Skills, catalog).State! });
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
        using (var professions = JsonDocument.Parse(Command("profession-options")[3..]))
            That(professions.RootElement.GetProperty("baseJobChange").GetBoolean() &&
                professions.RootElement.GetProperty("jobs").GetArrayLength() == resources.Characters.Jobs.Count(),
                "真实GM端口返回完整资源职业目录与切换能力");
        That(Command("profession GmBase 1 0 0").StartsWith("OK ") && Role(2).Job == 1 && Role(2).GrowType == 0,
            "真实GM端口离线切换基础职业并持久化");
        using (var peer = Open(fixture))
        {
            peer.Select(2);
            var changed = Role(2);
            Bytes(peer.SkillInfo, SkillPackets.Initialized(changed.Learning), "基础职业切换后重新选角下发目标职业技能");
            That(Command("profession GmBase 2 0 0").StartsWith("ERR ") && Role(2).Revision == changed.Revision && Role(2).Job == 1,
                "真实在线账号不能切换基础职业且存档保持不变");
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Send(29, [0, 1, 5, 0, 0, 1, 0]); peer.Expect(1, 29);
            peer.Send(331, [1, 5, 0, 3, 2, 3, 4]); peer.Expect(1, 331, [1]);
            peer.Barrier(); // 原生ACK应用指令，不用完整19重建仍在编辑的技能面板。
            var learned = Role(0);
            That(learned.Learning.Trees[0].Skills.Single(skill => skill.Id == 5).Command == "020304",
                "自定义指令成功ACK后已按原始键序持久化");
            That(Command("level GmTcp0 60").StartsWith("OK "), "在线GM改等级提交");
            var role = Role(0);
            var free60 = SkillPointLedger.FreeBaseline(0, 0, 60, catalog);
            That(role.Level == 60 && role.Learning.Experience == growth.GetLevelStartExperience(60) &&
                role.Learning.Trees.Zip(learned.Learning.Trees).All(pair =>
                    pair.First.Sp == catalog.TotalSp(60) + 137 - SkillPointLedger.Spent(pair.First.Skills, free60, 0, id => catalog.Get(0, id)) &&
                    pair.First.Tp == catalog.TotalTp(60) + 7 - SkillPointLedger.Spent(pair.First.Skills, free60, 0, id => catalog.Get(0, id), tp: true) &&
                    pair.Second.Skills.Where(skill => catalog.Get(0, skill.Id).FixedLevel is null).All(pair.First.Skills.Contains)) &&
                role.Learning.Trees[0].Skills.Single(skill => skill.Id == 5) is { Level: 2 } ghost && ghost.Command == "020304" &&
                role.Learning.Trees[0].Sp < role.Learning.Trees[1].Sp, "在线GM改等级保留已学技能与指令，SP/TP按新总额减已学费用重算");
            peer.SendBarrier();
            var assigned = peer.Expect(0,574).Body;
            var refreshed = Role(0);
            That(refreshed.ActiveMainQuests.Select(row=>(int)row.QuestId).Order().SequenceEqual(new[]{13036,13040}) &&
                learned.ActiveMainQuests.All(row=>refreshed.ActiveMainQuests.Contains(row)),
                "GM50升60后保留原每日身份，下一请求只新增60级地区每日");
            Bytes(assigned,QuestPackets.ActiveList(refreshed.ActiveMainQuests),"GM等级变化后的每日通知对应真实活动槽");
            peer.Expect(0,356,TownPackets.CompletedQuests(refreshed));
            peer.Expect(0,21,QuestPackets.AvailableList(refreshed.Level,DfoServer.Game.Quests.QuestEligibility.Candidates(refreshed,resources.Quests)));
            // 21重建任务界面后按公共投影恢复同一完整完成历史，不吞掉额外通知。
            peer.Expect(0,356,TownPackets.CompletedQuests(refreshed));
            peer.Expect(1,1347,[1]);
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
            That(Enumerable.Range(0, 30000).All(id => history[4 + id] == (id == 13700 || InfernoQuestUnlock.Satisfied(id, completed) ? 1 : 0)),
                "重启选角逐项恢复已提交转职觉醒历史、月轮山及时空裂缝外传显示位");
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
        using (var peer = Open(fixture))
        {
            peer.Select(2);
            That(Role(2).Job == 1 && Role(2).GrowType == 0, "服务重建后基础职业持久化保持");
            Bytes(peer.SkillInfo, SkillPackets.Initialized(Role(2).Learning), "服务重建后目标基础职业双技能树回放");
        }
        await fixture.StopAsync();
        Console.WriteLine("PASS C# TCP GM等级/基础职业重置、默认连招与重登/重启回放；非Windows实机");
    }
}
