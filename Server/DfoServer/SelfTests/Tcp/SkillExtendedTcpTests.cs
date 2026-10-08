using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class SkillExtendedTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "skill-extended"), resources: resources);
        var catalog = SkillLearningCatalog.Load(resources.Archive);
        var growth = CharacterGrowthCatalog.Load(resources.Archive, catalog);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { });
        foreach (byte job in new byte[] { 0, 9, 10 })
        {
            var profile = resources.Characters.Get(job);
            var initial = SkillLearning.Initialize(job, profile.Skills, catalog).State! with { BonusSp = 137, BonusTp = 7,
                Experience = growth.GetLevelStartExperience(95) };
            var state = SkillPointLedger.Synchronize(job, 0, 95, initial, catalog);
            repository.Create(owner, new NewCharacter("Ext" + job, resources.Towns.Room, resources.Towns.InitialReturn)
            { Job = job, Level = 95, AcceptedStoryLevel = 1, Skills = profile.Skills, InitialLearning = state,
                Inventory = DfoServer.Game.Inventory.Services.InventoryRewardGrantService.CreateStarter(profile) });
        }
        Character Role(ushort slot) => repository.List(owner).Single(role => role.Slot == slot);
        await fixture.StartAsync();
        var tp = PvfScript.List(resources.Archive.ReadText("skill/swordmanskill.lst")).Keys.Select(id => catalog.Get(0, checked((ushort)id)))
            .First(skill => skill.SupportsTpLearning && skill.GetMaximum(0) >= 2 && skill.Prerequisites!.Count == 0);
        int tpCost = tp.GetPointCost(0, 0) + tp.GetPointCost(1, 0);
        byte[] customized;
        using (var peer = Open(fixture))
        {
            peer.Select();
            var before = SkillPacketsTests.DecodeInitialized(peer.SkillInfo);
            peer.Send(331, [1, 5, 0, 3, 2, 128, 4]); peer.Expect(1, 331, [1]);
            customized = peer.Expect(0, 19).Body;
            var commands = SkillPacketsTests.DecodeInitialized(customized);
            That(commands[0].Skills.Single(skill => skill.Id == 5).Command == "028004" && commands[1].Skills.All(skill => skill.Command == "") &&
                commands[0].Sp == before[0].Sp && commands[0].Tp == before[0].Tp, "TCP指令整数回推、主树隔离及点数不变");
            long revision = Role(0).Revision;
            peer.Send(331, [1, 5, 0, 3, 2, 128, 4]); peer.Expect(1, 331, [1]); peer.Expect(0, 19, customized);
            That(Role(0).Revision == revision, "相同指令免写入和revision");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(sceneSkills: customized);
            Bytes(peer.SkillInfo, customized, "重登场景绑定前后均回放保存指令");
            peer.Send(29, [0, 1, 5, 0, 0, 1, 0]);
            var ack = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
            That(ack.Skills.Single().Command == "028004" && ack.Skills.Single().Level == 2, "升级ACK保留自定义指令");
            peer.Send(331, [1, 5, 0, 1, 4]); peer.Expect(1, 331, [1]);
            That(SkillPacketsTests.DecodeInitialized(peer.Expect(0, 19).Body)[0].Skills.Single(skill => skill.Id == 5).Command == "04",
                "实收恢复默认也通过331保存默认指令");
            peer.Send(332, []); peer.Expect(1, 332, [1]);
            That(SkillPacketsTests.DecodeInitialized(peer.Expect(0, 19).Body).All(tree => tree.Skills.All(skill => skill.Command == "")), "332清除自定义指令");
            ushort sp = ack.Sp;
            peer.Send(29, Purchase(tp.SkillId, 2));
            var bought = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
            That(bought.Tp == catalog.TotalTp(95) + 7 - tpCost && bought.Sp == sp && bought.Skills.Single().Level == 2, "TCP TP逐级扣除，额外7与SP保留");
            peer.Send(29, Purchase(tp.SkillId, 2, decrease: true));
            var refund = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
            That(refund.Tp == catalog.TotalTp(95) + 7 && refund.Sp == sp && refund.Skills.Single().Level == 0, "TCP TP退款删除绑定并保留独立来源");
            peer.Send(29, Purchase(tp.SkillId, 2)); peer.Expect(1, 29);
            peer.Send(491, [0, 0]); peer.Expect(1, 491, [1, 0, 1]);
            var reset = SkillPacketsTests.DecodeInitialized(peer.Expect(0, 19).Body);
            That(reset[0].Tp == catalog.TotalTp(95) + 7 && reset[0].Sp == catalog.TotalSp(95) + 137 && reset[1].Tp == reset[0].Tp,
                "TCP重置恢复SP/TP来源，不重置额外点数");
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            peer.Select(1, combos: SkillPackets.Combos(Role(1).Learning));
            var initial = SkillPacketsTests.DecodeInitialized(peer.SkillInfo);
            That(initial.All(tree => tree.Skills.Count(skill => skill.Slot < 6) == 6), "黑暗武士默认六个根占主快捷槽");
            long revision = Role(1).Revision;
            peer.Send(28, [0, 0, 198, 255, 255, 255, 255, 0]); peer.Barrier();
            That(Role(1).Revision == revision, "连招根移出主六槽拒绝，连接仍可继续");
            peer.Send(29, Purchase(77, 1));
            var bought = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
            peer.Expect(0, 447);
            That(bought.Skills.Single().Slot == 7, "黑武新学主动补可见扩展空位7");
            peer.Send(510, [0, 0]);
            var cleaned = SkillPacketsTests.DecodeInitialized(peer.Expect(0, 19).Body);
            peer.Expect(1, 510, [1]);
            That(cleaned[0].Skills.Single(skill => skill.Id == 77).Slot is >= 150 and < 198 && cleaned[0].Sp == bought.Sp,
                "自动编排前清理候选快捷绑定，等级和费用保持");
            peer.Send(508, [0, 2, 118, 0, 2, 46, 0, 77, 0, 119, 0, 1, 77, 0, 0]);
            var notification = peer.Expect(0, 447).Body;
            Bytes(notification, SkillPackets.Combos(Role(1).Learning, refresh: true), "连招通知匹配实际SQL提交状态");
            var combos = Role(1).Learning.Trees[0].Combos;
            That(combos.Single(combo => combo.Root == 118).Children.SequenceEqual(new ushort[] { 46 }) &&
                combos.Single(combo => combo.Root == 119).Children.SequenceEqual(new ushort[] { 77 }), "连招跨组重复归最后位置并原子存档");
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            peer.Select(2);
            peer.Send(29, Purchase(131, 1));
            var learned = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
            That(learned.Skills.Single().Slot is >= 102 and < 138 && learned.Sp < catalog.TotalSp(95) + 137,
                "缔造者被动按资源扣费并保持被动分组");
        }
        for (byte job = 9; job <= 10; job++)
        {
            using var peer = Open(fixture);
            peer.Create("Fresh" + job, job);
            ushort slot = (ushort)(job - 6);
            peer.Select(slot, combos: job == 9 ? SkillPackets.Combos(Role(slot).Learning) : null);
            var trees = SkillPacketsTests.DecodeInitialized(peer.SkillInfo);
            That(trees.All(tree => tree.Sp == 0 && tree.Tp == 0 && tree.Skills.Length == resources.Characters.Get(job).Skills.Length),
                "TCP正常创建特殊职业完整双树且不补发点数 " + job);
            That(trees.All(tree => tree.Skills.Where(skill => catalog.Get(job, skill.Id).Type == SkillType.Passive)
                .All(skill => skill.Slot is >= 102 and < 138)), "特殊职业新角色被动不进入快捷栏 " + job);
        }
        await fixture.StopAsync();
        await fixture.StartAsync("extended-restart");
        using (var peer = Open(fixture))
        {
            var expected = Role(1).Learning;
            peer.Select(1, combos: SkillPackets.Combos(expected));
            That(expected.Trees[0].Combos.Single(combo => combo.Root == 119).Children.Single() == 77 &&
                expected.Trees[1].Combos.Single(combo => combo.Root == 119).Children.All(id => id != 77), "重启回放两树独立连招");
            peer.Barrier();
        }
        await fixture.StopAsync();
        Console.WriteLine("PASS C# TCP TP、指令默认/保存/重登、特殊职业创建/学习及连招编排；非Windows验收");
    }

    private static byte[] Purchase(ushort id, byte levels, bool decrease = false) =>
        [0, 1, (byte)id, (byte)(id >> 8), (byte)(decrease ? 1 : 0), levels, 0];
}
