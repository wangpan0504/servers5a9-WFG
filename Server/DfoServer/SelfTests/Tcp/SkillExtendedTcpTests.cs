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
            peer.Barrier(); // 原生ACK应用指令，不能再用19重建正在编辑的图标。
            peer.Send(7, []); peer.Expect(1, 7, [1]); peer.Expect(0, 2);
            peer.Select();
            customized = peer.SceneSkillInfo;
            var commands = SkillPacketsTests.DecodeInitialized(customized);
            That(commands[0].Skills.Single(skill => skill.Id == 5).Command == "028004" &&
                commands[1].Skills.All(skill => skill.Command == catalog.Get(0, skill.Id).DefaultCommand) &&
                commands[0].Sp == before[0].Sp && commands[0].Tp == before[0].Tp, "TCP指令整数回推、主树隔离及点数不变");
            long revision = Role(0).Revision;
            peer.Send(331, [1, 5, 0, 3, 2, 128, 4]); peer.Expect(1, 331, [1]); peer.Barrier();
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
            peer.Barrier();
            That(Role(0).Learning.Trees[0].Skills.Single(skill => skill.Id == 5).Command == "04",
                "实收恢复默认也通过331保存默认指令");
            peer.Send(332, []); peer.Expect(1, 332, [1]);
            peer.Barrier();
            That(Role(0).Learning.Trees.All(tree => tree.Skills.All(skill => skill.Command.Length == 0)),
                "332清除自定义存档，由原生回调恢复默认，保持当前图标");
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
            // 新学主动只进下排排列区150–197：取当前树的首个空位，绝不占上排快捷栏0–11。
            int expected = Enumerable.Range(150, 48).First(slot => initial[0].Skills.All(skill => skill.Slot != slot));
            That(bought.Skills.Single().Slot == expected, "黑武新学主动进入排列区首个空位，不占上排快捷栏");
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
            var beforeSelection = Role(1);
            peer.Send(510, [0, 0]);
            peer.Select(1, combos: SkillPackets.Combos(beforeSelection.Learning));
            That(SkillRepositoryTests.Equal(Role(1), beforeSelection),
                "尚未选角的510不写技能或角色，连接可继续选角且没有成功回包");
            var beforeLateRequests = Role(1);
            peer.Send(7);
            peer.Expect(1, 7, [1]);
            peer.Expect(0, 2);
            // 返回选角后客户端会补发迟到的自动组合、连招保存与槽位变更：服务端只拒绝，不写库，也不关闭连接（#428）。
            peer.Send(510, [0, 0]);
            peer.Send(508, [0, 2, 118, 0, 2, 46, 0, 77, 0, 119, 0, 1, 77, 0, 0]);
            peer.Send(28, [0, 0, 198, 255, 255, 255, 255, 0]);
            peer.Select(1, combos: SkillPackets.Combos(beforeLateRequests.Learning));
            That(SkillRepositoryTests.Equal(Role(1), beforeLateRequests),
                "返回选角后的迟到510/508/28被拒绝：技能与revision不变且连接可继续使用");
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
        var avengerFree = catalog.FreeSkills(4, 0x24);
        var avengerState = SkillLearning.Initialize(4, avengerFree, catalog).State! with
            { BonusSp = 0, BonusTp = 0, Experience = growth.GetLevelStartExperience(95) };
        avengerState = SkillPointLedger.Synchronize(4, 0x24, 95, avengerState, catalog);
        // 保留旧双树遗漏的绑定，让正常选角入口负责补齐并持久化。
        avengerState = avengerState with { Trees = Array.AsReadOnly(avengerState.Trees.Select(tree => tree with
        {
            Skills = Array.AsReadOnly(tree.Skills.Where(skill => skill.Id is not (120 or 135)).ToArray())
        }).ToArray()) };
        var avengerRole = repository.Create(owner, new NewCharacter("AwakenGrant", resources.Towns.Room, resources.Towns.InitialReturn)
        { Job = 4, GrowType = 0x24, Level = 95, AcceptedStoryLevel = 1, Skills = avengerFree.ToArray(), InitialLearning = avengerState,
            Inventory = DfoServer.Game.Inventory.Services.InventoryRewardGrantService.CreateStarter(resources.Characters.Get(4)) });
        byte[] avengerProjection;
        using (var peer = Open(fixture))
        {
            peer.Select(avengerRole.Slot);
            avengerProjection = peer.SkillInfo;
            var initialized = SkillPacketsTests.DecodeInitialized(peer.SkillInfo);
            var saved = Role(avengerRole.Slot).Learning;
            That(initialized.Zip(saved.Trees).All(pair => pair.First.Sp == pair.Second.Sp && pair.First.Tp == pair.Second.Tp &&
                pair.First.Skills.Select(skill => (skill.Slot, skill.Id, skill.Level))
                    .SequenceEqual(pair.Second.Skills.Select(skill => (skill.Slot, skill.Id, skill.Level)))) &&
                saved.Trees.Zip(avengerState.Trees).All(pair => pair.First.Skills.Count == pair.Second.Skills.Count + 2 &&
                    pair.Second.Skills.All(skill => pair.First.Skills.Single(value => value.Id == skill.Id) == skill) &&
                    pair.First.Skills.Any(skill => skill.Id == 120 && skill.Level == 1) &&
                    pair.First.Skills.Any(skill => skill.Id == 135 && skill.Level == 1) &&
                    pair.First.Skills.Single(skill => skill.Id == 119).Level == 10 &&
                    pair.First.Sp == pair.Second.Sp && pair.First.Tp == pair.Second.Tp),
                "旧双树选角持久补齐审判、恶魔屏障并完整投影，保留其余等级、绑定与余额");
            peer.Barrier();
        }
        byte doomSlot;
        using (var peer = Open(fixture))
        {
            peer.Select(avengerRole.Slot);
            var before = Role(avengerRole.Slot);
            That(before.Learning.Trees.All(tree => tree.Sp == 13320 && tree.Tp == 46 && tree.Skills.All(skill => skill.Id != 253)),
                "复仇者缺失类型夹具使用正常95级来源且未预先赠送毁灭强击");
            peer.Send(29, [0, 2, 253, 0, 0, 8, 255, 255, 0, 1, 0]);
            peer.Expect(1, 29, [0, 0x15, 0]);
            peer.Barrier();
            var rejected = Role(avengerRole.Slot);
            That(rejected.Revision == before.Revision && rejected.Learning.Trees[0].Sp == 13320 &&
                rejected.Learning.Trees[0].Skills.All(skill => skill.Id != 253),
                "合法毁灭强击后接不存在资源仍整批拒绝，无前序扣点或等级提交");
            peer.Send(29, Purchase(253, 8));
            var single = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
            doomSlot = single.Skills.Single().Slot;
            That(single.Sp == 12600 && single.Tp == 46 && single.Skills.Single() == new LearnedSkill(doomSlot, 253, 8) &&
                (doomSlot < 12 || doomSlot is >= 150 and < 198), "缺失类型技能正常CMD29返回八级主动槽位并仅扣720SP");
            var saved = Role(avengerRole.Slot);
            That(saved.Revision == before.Revision + 1 && saved.Learning.Trees[1].Sp == 13320 &&
                saved.Learning.Trees[1].Skills.All(skill => skill.Id != 253), "单项学习提交一次且备用树保持原样");
            peer.Send(29, [1, 2, 251, 0, 0, 7, 253, 0, 0, 8, 0]);
            var mixed = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
            That(mixed.Context == 1 && mixed.Sp == 12040 && mixed.Tp == 46 &&
                mixed.Skills.Select(skill => (skill.Id, skill.Level)).SequenceEqual(new[] { ((ushort)251, (byte)7), ((ushort)253, (byte)8) }) &&
                Role(avengerRole.Slot).Learning.Trees[0].Sp == 12600, "备用树混合批次接受缺省类型主动并独立扣点");
            peer.Barrier();
        }
        var avengerSaved = Role(avengerRole.Slot);
        using (var peer = Open(fixture))
        {
            peer.Select(avengerRole.Slot);
            avengerProjection = peer.SkillInfo;
            peer.Barrier();
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
        var awakenedState = SkillLearning.Initialize(0, catalog.FreeSkills(0, 5), catalog).State! with
            { Experience = growth.GetLevelStartExperience(51) };
        awakenedState = SkillPointLedger.Synchronize(0, 0x15, 51, awakenedState, catalog);
        var awakenedRole = repository.Create(owner, new NewCharacter("GhostNight", resources.Towns.Room, resources.Towns.InitialReturn)
        { Job = 0, GrowType = 0x15, Level = 51, AcceptedStoryLevel = 1, Skills = awakenedFree.ToArray(),
            InitialLearning = awakenedState,
            Inventory = DfoServer.Game.Inventory.Services.InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0)) });
        await fixture.StartAsync("extended-restart");
        using (var peer = Open(fixture))
        {
            peer.Select(avengerRole.Slot);
            Bytes(peer.SkillInfo, avengerProjection, "服务重建后完整回放已补齐的觉醒双树");
            var replay = Role(avengerRole.Slot);
            That(replay.Revision == avengerSaved.Revision && replay.Learning.Trees.Zip(avengerSaved.Learning.Trees)
                .All(pair => pair.First.Sp == pair.Second.Sp && pair.First.Tp == pair.Second.Tp && pair.First.Skills.SequenceEqual(pair.Second.Skills)),
                "重复选角不再次补发技能、改变余额或增加revision");
            var learnedReplay = SkillPacketsTests.DecodeInitialized(peer.SceneSkillInfo);
            That(learnedReplay[0].Sp == 12600 && learnedReplay[1].Sp == 12040 && learnedReplay.All(tree => tree.Tp == 46 &&
                tree.Skills.Single(skill => skill.Id == 253).Level == 8 && tree.Skills.Single(skill => skill.Id == 253).Command == "01030304") &&
                learnedReplay[0].Skills.Single(skill => skill.Id == 253).Slot == doomSlot &&
                learnedReplay[1].Skills.Single(skill => skill.Id == 253).Slot == avengerSaved.Learning.Trees[1].Skills.Single(skill => skill.Id == 253).Slot,
                "缺省类型技能重登及夹具重建恢复真实双树余额、等级、槽位和默认指令");
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            peer.Select(awakenedRole.Slot);
            var before = SkillPacketsTests.DecodeInitialized(peer.SkillInfo);
            That(before.All(tree => tree.Skills.Single(skill => skill.Id == 296) is { Level: 1, Slot: >= 54 and < 102 }),
                "剑影觉醒按CHR赠送鬼夜一级并进入转职被动组");
            foreach (byte context in new byte[] { 0, 1 })
            {
                byte expectedSlot = before[context].Skills.Single(skill => skill.Id == 296).Slot;
                peer.Send(29, [context, 1, 0x28, 1, 0, 1, 0]);
                var learned = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
                That(learned.Context == context && learned.Sp == before[context].Sp - 45 && learned.Tp == before[context].Tp &&
                    learned.Skills.Single() == new LearnedSkill(expectedSlot, 296, 2),
                    "鬼夜免费一级之上正常付费学习二级，保留被动槽位并扣除45SP " + context);
                var saved = Role(awakenedRole.Slot).Learning;
                That(saved.Trees[context].Skills.Single(skill => skill.Id == 296) == learned.Skills.Single() &&
                    saved.Trees[context].Sp == learned.Sp && saved.Trees[1 - context].Sp == before[1 - context].Sp - (context == 1 ? 45 : 0),
                    "鬼夜学习提交并保持双树独立 " + context);
            }
            var current = Role(awakenedRole.Slot);
            peer.Send(29, Purchase(296, 1)); peer.Expect(1, 29, [0, 0xD9, 0]); peer.Barrier();
            That(Role(awakenedRole.Slot).Revision == current.Revision, "Lv51不能再学鬼夜三级，失败不提交");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(awakenedRole.Slot);
            var replay = SkillPacketsTests.DecodeInitialized(peer.SkillInfo);
            That(replay.Zip(awakenedState.Trees).All(pair => pair.First.Skills.Single(skill => skill.Id == 296).Level == 2 &&
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
            peer.Send(331, [1, 5, 0, 2, 2, 4]); peer.Expect(1, 331, [1]); peer.Barrier();
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
        Console.WriteLine("PASS C# TCP TP、觉醒赠技、指令默认/保存/重登、特殊职业创建/学习及连招编排；非Windows验收");
    }

    private static byte[] Purchase(ushort id, byte levels, bool decrease = false) =>
        [0, 1, (byte)id, (byte)(id >> 8), (byte)(decrease ? 1 : 0), levels, 0];
}
