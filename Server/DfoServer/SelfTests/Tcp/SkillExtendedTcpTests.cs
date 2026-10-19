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
        var catalog = resources.Learning;
        var growth = SelfTestCatalogs.Growth(resources.Archive, catalog);
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
        var atPriest = resources.Characters.Get(14);
        var atPriestState = SkillLearning.Initialize(14, catalog.FreeSkills(14, 4), catalog).State! with
            { Experience = growth.GetLevelStartExperience(25) };
        atPriestState = SkillPointLedger.Synchronize(14, 4, 25, atPriestState, catalog);
        repository.Create(owner, new NewCharacter("DualSlash", resources.Towns.Room, resources.Towns.InitialReturn)
        { Job = 14, GrowType = 4, Level = 25, AcceptedStoryLevel = 1, Skills = catalog.FreeSkills(14, 4).ToArray(),
            InitialLearning = atPriestState, Inventory = DfoServer.Game.Inventory.Services.InventoryRewardGrantService.CreateStarter(atPriest) });
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
            That(commands[0].Skills.Single(skill => skill.Id == 5).Command == "028004" &&
                commands[1].Skills.All(skill => skill.Command == catalog.Get(0, skill.Id).DefaultCommand) &&
                commands[0].Sp == before[0].Sp && commands[0].Tp == before[0].Tp, "TCP指令整数回推、主树隔离及点数不变");
            long revision = Role(0).Revision;
            peer.Send(331, [1, 5, 0, 3, 2, 128, 4]); peer.Expect(1, 331, [1]); peer.Expect(0, 19, customized);
            That(Role(0).Revision == revision, "相同指令免写入和revision");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(sceneSkills: customized);
            Bytes(peer.SceneSkillInfo, customized, "重登场景后回放保存指令及资源默认");
            peer.Send(29, [0, 1, 5, 0, 0, 1, 0]);
            var ack = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
            That(ack.Skills.Single().Command == "028004" && ack.Skills.Single().Level == 2, "升级ACK保留自定义指令");
            peer.Send(331, [1, 5, 0, 1, 4]); peer.Expect(1, 331, [1]);
            That(SkillPacketsTests.DecodeInitialized(peer.Expect(0, 19).Body)[0].Skills.Single(skill => skill.Id == 5).Command == "04",
                "实收恢复默认也通过331保存默认指令");
            peer.Send(332, []); peer.Expect(1, 332, [1]);
            That(SkillPacketsTests.DecodeInitialized(peer.Expect(0, 19).Body).All(tree =>
                tree.Skills.All(skill => skill.Command == catalog.Get(0, skill.Id).DefaultCommand)), "332清除自定义存档并显式恢复资源默认指令");
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
            // 普通学习只回ACK29；若额外推NOTI447，下一条28回执会先读到447而失败。
            That(bought.Skills.Single().Slot == 7, "黑武新学主动补可见扩展空位7");
            // 自动组合的排列由客户端本地生成，510只确认开始；服务端不生成也不落库任何排列。
            var tree = Role(1).Learning.Trees[0];
            ushort from46 = tree.Skills.Single(skill => skill.Id == 46).Slot;
            ushort from8 = tree.Skills.Single(skill => skill.Id == 8).Slot;
            ushort from169 = tree.Skills.Single(skill => skill.Id == 169).Slot;
            peer.Send(28, [0, (byte)from46, 9, 255, 255, 255, 255, 0]); peer.Expect(1, 28, [1, 0, (byte)from46, 9]);
            peer.Send(28, [0, (byte)from8, 11, 255, 255, 255, 255, 0]); peer.Expect(1, 28, [1, 0, (byte)from8, 11]);
            peer.Send(510, [0, 0]);
            // 510阶段先把会被收进连招列的上排技能移出快捷栏，回放完整树后才确认；
            // 客户端收到回执后才本地生成排列，重建必须发生在排列显示之前。
            var preclean = SkillPacketsTests.DecodeInitialized(peer.Expect(0, 19).Body)[0];
            peer.Expect(1, 510, [1]);
            That(preclean.Skills.Single(skill => skill.Id == 46).Slot is >= 150 and < 198 &&
                preclean.Skills.Single(skill => skill.Id == 8).Slot is >= 150 and < 198 &&
                preclean.Skills.Single(skill => skill.Id == 77).Slot is >= 150 and < 198 &&
                preclean.Skills.Single(skill => skill.Id == 169).Slot == from169,
                "自动组合开始前收走声明池内的上排技能，未声明的保持原位");
            // 客户端随后提交自己的排列：118:[46,8]、119:[77]，其余列空。
            peer.Send(508, [0, 6, 118, 0, 2, 46, 0, 8, 0, 119, 0, 1, 77, 0, 120, 0, 0, 121, 0, 0, 122, 0, 0, 123, 0, 0, 0]);
            peer.Expect(1, 508, [1]);
            // 保存只回ACK，不再回放技能树；屏障确认后面没有19/447覆盖客户端刚显示的排列。
            peer.Barrier();
            var saved = Role(1).Learning.Trees[0];
            That(saved.Combos.Single(combo => combo.Root == 118).Children.SequenceEqual(new ushort[] { 46, 8 }) &&
                saved.Combos.Single(combo => combo.Root == 119).Children.SequenceEqual(new ushort[] { 77 }) &&
                saved.Combos.Where(combo => combo.Root is not (118 or 119)).All(combo => combo.Children.Count == 0) &&
                saved.Skills.Count(skill => skill.Slot < 6) == 6,
                "自动组合按客户端提交的排列与子技能顺序原样落库");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(1, combos: SkillPackets.Combos(Role(1).Learning));
            long revision = Role(1).Revision;
            peer.Send(7);
            peer.Expect(1, 7, [1]);
            peer.Expect(0, 2);
            // 返回选角后客户端会补发迟到的连招保存与槽位变更：服务端只拒绝，不写库，也不关闭连接（#428）。
            peer.Send(508, [0, 2, 118, 0, 2, 46, 0, 77, 0, 119, 0, 1, 77, 0, 0]);
            peer.Send(28, [0, 0, 198, 255, 255, 255, 255, 0]);
            peer.Select(1, combos: SkillPackets.Combos(Role(1).Learning));
            That(Role(1).Revision == revision, "返回选角后的迟到508/28被拒绝：不写库且连接可继续使用");
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
        using (var peer = Open(fixture))
        {
            peer.Select(3);
            var before = Role(3);
            peer.Send(29, Purchase(68, 7));
            peer.Expect(1, 29, [0, 0xD9, 0]);
            That(Role(3).Revision == before.Revision && Role(3).Learning.Trees[0].Sp == before.Learning.Trees[0].Sp,
                "双重切割等级不足正常拒绝、不扣点且连接继续");
            // zz实收的单级学习正文及4B检查尾，独立编码保留原故障形状。
            byte[] request = [0, 1, 68, 0, 0, 1, 0, 0x0B, 0xD2, 0xFE, 0x7E];
            for (byte level = 1; level <= 2; level++)
            {
                peer.Send(29, request);
                var learned = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
                That(learned.Skills.Single().Id == 68 && learned.Skills.Single().Level == level &&
                    learned.Sp == before.Learning.Trees[0].Sp - 20 * level && learned.Tp == 0,
                    "双重切割完整ACK、逐级扣20SP且同连接继续 " + level);
                var saved = Role(3);
                That(saved.Learning.Trees[0].Skills.Single(skill => skill.Id == 68).Level == level &&
                    saved.Learning.Trees[0].Sp == learned.Sp && saved.Learning.Trees[1].Sp == before.Learning.Trees[1].Sp &&
                    saved.Learning.Trees[1].Skills.All(skill => skill.Id != 68), "学习提交与双树隔离 " + level);
            }
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            peer.Select(3);
            var trees = SkillPacketsTests.DecodeInitialized(peer.SkillInfo);
            That(trees[0].Skills.Single(skill => skill.Id == 68).Level == 2 &&
                trees[0].Sp == atPriestState.Trees[0].Sp - 40 && trees[1].Skills.All(skill => skill.Id != 68),
                "双重切割重登回放等级、余额并保持副树");
            peer.Barrier();
        }
        for (byte job = 9; job <= 10; job++)
        {
            using var peer = Open(fixture);
            peer.Create("Fresh" + job, job);
            ushort slot = (ushort)(job - 5);
            peer.Select(slot, combos: job == 9 ? SkillPackets.Combos(Role(slot).Learning) : null);
            var trees = SkillPacketsTests.DecodeInitialized(peer.SkillInfo);
            That(trees.All(tree => tree.Sp == 0 && tree.Tp == 0 && tree.Skills.Length == resources.Characters.Get(job).Skills.Length),
                "TCP正常创建特殊职业完整双树且不补发点数 " + job);
            That(trees.All(tree => tree.Skills.Where(skill => catalog.Get(job, skill.Id).Type == SkillType.Passive)
                .All(skill => skill.Slot is >= 102 and < 138)), "特殊职业新角色被动不进入快捷栏 " + job);
        }
        await fixture.StopAsync();
        // 复现dd的默认三段斩：主树Lv18、副树Lv1，没有任何自定义指令存档。
        var free = catalog.FreeSkills(0, 5);
        var defaultState = SkillLearning.Initialize(0, free, catalog).State! with
            { Experience = growth.GetLevelStartExperience(49) };
        defaultState = SkillPointLedger.Synchronize(0, 5, 49, defaultState, catalog);
        var defaultLearning = SkillLearning.Plan(0, 5, 49, defaultState, new(0, 0, [new(8, false, 17)]), catalog);
        That(defaultLearning.Success, "dd三段斩Lv18默认指令夹具按真实资源学习");
        var defaultRole = repository.Create(owner, new NewCharacter("DefaultSlash", resources.Towns.Room, resources.Towns.InitialReturn)
        { Job = 0, GrowType = 5, Level = 49, AcceptedStoryLevel = 1, Skills = free.ToArray(),
            InitialLearning = defaultLearning.State!,
            Inventory = DfoServer.Game.Inventory.Services.InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0)) });
        var awakenedFree = catalog.FreeSkills(0, 0x15);
        var awakenedState = SkillLearning.Initialize(0, awakenedFree, catalog).State! with
            { Experience = growth.GetLevelStartExperience(48) };
        awakenedState = SkillPointLedger.Synchronize(0, 0x15, 48, awakenedState, catalog);
        var awakenedRole = repository.Create(owner, new NewCharacter("GhostNight", resources.Towns.Room, resources.Towns.InitialReturn)
        { Job = 0, GrowType = 0x15, Level = 48, AcceptedStoryLevel = 1, Skills = awakenedFree.ToArray(),
            InitialLearning = awakenedState,
            Inventory = DfoServer.Game.Inventory.Services.InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0)) });
        await fixture.StartAsync("extended-restart");
        using (var peer = Open(fixture))
        {
            peer.Select(awakenedRole.Slot);
            var before = SkillPacketsTests.DecodeInitialized(peer.SkillInfo);
            That(before.All(tree => tree.Skills.All(skill => skill.Id != 296)), "剑影觉醒夹具不提前赠送鬼夜");
            foreach (byte context in new byte[] { 0, 1 })
            {
                byte expectedSlot = Enumerable.Range(54, 48).Select(value => (byte)value)
                    .First(slot => before[context].Skills.All(skill => skill.Slot != slot));
                peer.Send(29, [context, 1, 0x28, 1, 0, 1, 0]);
                var learned = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
                That(learned.Context == context && learned.Sp == before[context].Sp - 45 && learned.Tp == before[context].Tp &&
                    learned.Skills.Single() == new LearnedSkill(expectedSlot, 296, 1),
                    "鬼夜首次学习返回真实转职被动槽位和扣除45SP后的ACK " + context);
                var saved = Role(awakenedRole.Slot).Learning;
                That(saved.Trees[context].Skills.Single(skill => skill.Id == 296) == learned.Skills.Single() &&
                    saved.Trees[context].Sp == learned.Sp && saved.Trees[1 - context].Sp == before[1 - context].Sp - (context == 1 ? 45 : 0),
                    "鬼夜学习提交并保持双树独立 " + context);
            }
            var current = Role(awakenedRole.Slot);
            peer.Send(29, Purchase(296, 1)); peer.Expect(1, 29, [0, 0xD9, 0]); peer.Barrier();
            That(Role(awakenedRole.Slot).Revision == current.Revision, "Lv48不能再学鬼夜二级，失败不提交");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(awakenedRole.Slot);
            var replay = SkillPacketsTests.DecodeInitialized(peer.SkillInfo);
            That(replay.Zip(awakenedState.Trees).All(pair => pair.First.Skills.Single(skill => skill.Id == 296).Level == 1 &&
                pair.First.Skills.Single(skill => skill.Id == 296).Slot is >= 54 and < 102 && pair.First.Sp == pair.Second.Sp - 45),
                "鬼夜断线重连恢复两树等级、被动槽位及余额");
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            var expected = Role(1).Learning;
            peer.Select(1, combos: SkillPackets.Combos(expected));
            That(expected.Trees[0].Combos.Single(combo => combo.Root == 119).Children.Single() == 77 &&
                expected.Trees[1].Combos.Single(combo => combo.Root == 119).Children.All(id => id != 77), "重启回放两树独立连招");
            peer.Barrier();
        }
        byte[] defaultReplay;
        using (var peer = Open(fixture))
        {
            peer.Select(defaultRole.Slot);
            long revision = Role(defaultRole.Slot).Revision;
            peer.Send(29, Purchase(296, 1)); peer.Expect(1, 29, [0, 0x12, 0]);
            That(Role(defaultRole.Slot).Revision == revision, "未觉醒剑影仍受鬼夜零上限限制，不因缺class放行");
            defaultReplay = peer.SceneSkillInfo;
            var trees = SkillPacketsTests.DecodeInitialized(defaultReplay);
            That(trees[0].Skills.Single(skill => skill.Id == 8).Level == 18 &&
                trees[1].Skills.Single(skill => skill.Id == 8).Level == 1 &&
                trees.All(tree => tree.Skills.Single(skill => skill.Id == 8).Command == "0C04"),
                "默认三段斩两树显式回放按住方向Z而非空字段，真实等级保持");
            peer.Send(7, []); peer.Expect(1, 7, [1]); peer.Expect(0, 2);
            peer.Select(defaultRole.Slot);
            Bytes(peer.SceneSkillInfo, defaultReplay, "同连接重选默认角色恢复完整指令绑定");
            peer.Send(7, []); peer.Expect(1, 7, [1]); peer.Expect(0, 2);
            peer.Select(0);
            peer.Send(331, [1, 5, 0, 2, 2, 4]); peer.Expect(1, 331, [1]); peer.Expect(0, 19);
            peer.Send(7, []); peer.Expect(1, 7, [1]); peer.Expect(0, 2);
            peer.Select(defaultRole.Slot);
            Bytes(peer.SceneSkillInfo, defaultReplay, "自定义角色切回默认角色不混入上一角色指令");
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            peer.Select(defaultRole.Slot);
            Bytes(peer.SceneSkillInfo, defaultReplay, "断线重登默认指令仍在场景后恢复");
            That(fixture.Query("SELECT skill_id FROM character_skill_commands WHERE character_id=" + defaultRole.Id).Length == 0,
                "默认指令回放不写技能指令表");
            peer.Barrier();
        }
        await fixture.StopAsync();
        Console.WriteLine("PASS C# TCP TP、指令默认/保存/重登、特殊职业创建/学习及连招编排；非Windows验收");
    }

    private static byte[] Purchase(ushort id, byte levels, bool decrease = false) =>
        [0, 1, (byte)id, (byte)(id >> 8), (byte)(decrease ? 1 : 0), levels, 0];
}
