using DfoServer.Game.Accounts;
using System.Security.Cryptography;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Network.Management;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class SkillProtocolTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "skill-learning"), gm: true, resources: resources);
        var pvf = resources.Archive;
        var characters = resources.Characters;
        var towns = resources.Towns;
        var catalog = SkillLearningCatalog.Load(pvf);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        var growth = CharacterGrowthCatalog.Load(pvf);
        const byte characterLevel = 20;
        ushort initialSp = checked((ushort)SkillCatalog.Load(pvf).GetLevelSpTotal(characterLevel));
        ulong initialExperience = growth.GetLevelStartExperience(characterLevel);
        var ghostDefinition = catalog.Get(0, 5);
        ushort bulkSp = checked((ushort)Enumerable.Range(1, 3).Sum(level => ghostDefinition.GetOrdinaryResourceSpCost(level, 0)));
        ushort[] passiveIds = [12, 35, 177];
        int passiveCost = passiveIds.Sum(id => catalog.Get(0, id).GetOrdinaryResourceSpCost(0, 0));
        // 预置仅属隔离协议fixture，不替代生产正常升级或Windows发点验收。
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var starting = SkillRepositoryTests.Fixture("TcpSkill", towns, characters, catalog);
            repository.Create(owner, starting with
            {
                Level = characterLevel,
                InitialLearning = new(initialExperience, Array.AsReadOnly(starting.InitialLearning.Trees.Select(tree => tree with { Sp = initialSp }).ToArray())),
                AcceptedStoryLevel = 1,
                ActiveMainQuests = [new(0, 3145, 1)],
                Inventory = DfoServer.Game.Inventory.Services.InventoryRewardGrantService.CreateStarter(characters.Get(0))
            });
            var bulk = SkillRepositoryTests.Fixture("TcpBulk", towns, characters, catalog);
            repository.Create(owner, bulk with
            {
                Level = 9,
                AcceptedStoryLevel = 1,
                ActiveMainQuests = [new(0, 3145, 1)],
                InitialLearning = new(growth.GetLevelStartExperience(9), Array.AsReadOnly(bulk.InitialLearning.Trees
                    .Select(tree => tree with { Sp = bulkSp }).ToArray())),
                Inventory = DfoServer.Game.Inventory.Services.InventoryRewardGrantService.CreateStarter(characters.Get(0))
            });
        }
        await fixture.StartAsync();
        byte ghostSlot;
        byte quickStandingSlot;
        byte[] player;
        using (var peer = Open(fixture))
        {
            peer.Select();
            long selectedRevision = Convert.ToInt64(fixture.Query("SELECT revision FROM characters WHERE slot=0")[0][0]);
            var trees = SkillPacketsTests.DecodeInitialized(peer.SkillInfo);
            ghostSlot = trees[0].Skills.Single(skill => skill.Id == 5).Slot;
            That(trees.All(tree => tree.Sp == initialSp && tree.Tp == 0) && trees.All(tree => tree.Skills.Length == 6),
                "TCP完整NOTI19两树余额与六个真实绑定");
            That(trees.All(tree => tree.Skills.Select(skill => skill.Slot).SequenceEqual(new byte[] { 0, 1, 2, 102, 103, 104 })),
                "TCP初始主动和被动分离，NOTI19两树不把被动绑定到快捷槽");
            peer.Send(28, WithChecksum([0, 0, 6, 255, 255, 255, 255, 0]));
            peer.Expect(1, 28, [1, 0, 0, 6]);
            CheckDatabase(fixture, 0, initialSp, 1, 6);
            peer.Send(28, [1, 0, 198, 255, 255, 255, 255, 0]);
            peer.Expect(1, 28, [1, 1, 0, 198]);
            CheckDatabase(fixture, 1, initialSp, 1, 198);
            peer.Send(28, WithChecksum([1, 198, 6, 255, 255, 255, 255, 0]));
            peer.Expect(1, 28, [1, 1, 198, 6]);
            ghostSlot = 6;
            for (int repeat = 0; repeat < 2; repeat++)
            {
                peer.Send(28, WithChecksum([0, 1, 2, 255, 255, 255, 255, 0]));
                peer.Expect(1, 28, [1, 0, 1, 2]);
                CheckDatabase(fixture, 0, initialSp, 1, (byte)(repeat == 0 ? 2 : 1), 46);
            }
            peer.Send(28, [0, 6, 6, 255, 255, 255, 255, 0]);
            peer.Expect(1, 28, [1, 0, 6, 6]);
            That(Convert.ToInt64(fixture.Query("SELECT revision FROM characters WHERE slot=0")[0][0]) == selectedRevision + 5,
                "空槽、扩展槽和已占槽交换均提交，同槽确认不递增revision");
            peer.Send(29, WithChecksum([0, 1, 5, 0, 0, 1, 0]));
            var first = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
            That(first.Context == 0 && first.Mode == 0 && first.Sp == initialSp - 15 && first.Tp == 0 &&
                first.Skills.Single() == new LearnedSkill(ghostSlot, 5, 2), "TCP学习已提交后的完整13B ACK29");
            CheckDatabase(fixture, 0, checked((ushort)(initialSp - 15)), 2, ghostSlot);
            peer.Send(29, [1, 1, 5, 0, 0, 1, 1]);
            var second = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
            That(second.Context == 1 && second.Mode == 1 && second.Sp == initialSp - 15 && second.Skills.Single().Slot == ghostSlot,
                "TCP另一context独立学习且slot稳定");
            CheckDatabase(fixture, 1, checked((ushort)(initialSp - 15)), 2, ghostSlot);
            peer.Send(29, WithChecksum([0, 1, 255, 255, 0, 1, 0]));
            peer.Expect(1, 29, [0, 0x15, 0]);
            peer.Barrier();
            That(Convert.ToInt64(fixture.Query("SELECT revision FROM characters WHERE slot=0")[0][0]) == selectedRevision + 7,
                "已知业务失败后连接可继续且revision不变");
        }
        using (var peer = Open(fixture))
        {
            var packets = peer.Select();
            player = packets[3].Body;
            var trees = SkillPacketsTests.DecodeInitialized(peer.SkillInfo);
            That(trees.All(tree => tree.Sp == initialSp - 15 && tree.Skills.Single(skill => skill.Id == 5) == new LearnedSkill(ghostSlot, 5, 2)),
                "TCP重登恢复两树同slot等级与余额");
            That(U32(player, 24) == initialExperience && U32(player, 28) == 0, "Player保留完整u64经验fixture");
            // 另一个真实技能的单项学习，费用查询匹配资源，不硬编码测试费用。
            peer.Send(29, WithChecksum([0, 1, 46, 0, 0, 1, 0]));
            var next = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
            int cost = catalog.Get(0, 46).GetOrdinaryResourceSpCost(1, 0);
            That(next.Sp == initialSp - 15 - cost && next.Skills.Single().Id == 46, "TCP另一真实技能按PVF费用扣SP");
            peer.Send(29, WithChecksum([0, 1, 190, 0, 0, 1, 0]));
            var quickStanding = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
            quickStandingSlot = quickStanding.Skills.Single().Slot;
            That(quickStanding.Sp == initialSp - 15 - cost - 10 && quickStanding.Skills.Single().Id == 190 &&
                quickStanding.Skills.Single().Level == 1, "TCP真实11B受身蹲伏首学准确扣10SP并返回新绑定");
            peer.Send(29, WithChecksum([1, 1, 190, 0, 0, 1, 1]));
            var alternateQuickStanding = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
            That(alternateQuickStanding.Sp == initialSp - 15 - 10 && alternateQuickStanding.Skills.Single() ==
                new LearnedSkill(quickStandingSlot, 190, 1), "另一树独立首学190保持实际slot与对应余额");
            peer.Send(29, WithChecksum([0, 3, 12, 0, 0, 1, 35, 0, 0, 1, 177, 0, 0, 1, 0]));
            var passive = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
            That(passive.Context == 0 && passive.Sp == initialSp - 15 - cost - 10 - passiveCost && passive.Tp == 0 &&
                passive.Skills.SequenceEqual(new LearnedSkill[] { new(12, 12, 1), new(54, 35, 1), new(105, 177, 1) }),
                "TCP真实新学被动批次按共通、转职和基础组分配，ACK保留请求顺序并按资源扣点");
            foreach (var skill in passive.Skills)
                CheckDatabase(fixture, 0, passive.Sp, skill.Level, skill.Slot, skill.Id);
        }
        using (var peer = Open(fixture))
        {
            peer.Select(1);
            var beforeTrees = SkillPacketsTests.DecodeInitialized(peer.SkillInfo);
            byte slot = beforeTrees[0].Skills.Single(skill => skill.Id == 5).Slot;
            long beforeRevision = Convert.ToInt64(fixture.Query("SELECT revision FROM characters WHERE slot=1")[0][0]);
            peer.Send(29, WithChecksum([0, 1, 5, 0, 0, 3, 0]));
            var main = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
            That(main.Sp == 0 && main.Skills.Single() == new LearnedSkill(slot, 5, 4) &&
                Convert.ToInt64(fixture.Query("SELECT revision FROM characters WHERE slot=1")[0][0]) == beforeRevision + 1,
                "TCP真实11B多级请求逐阶扣45SP，唯一13B ACK及一次revision推进到最终4级");
            peer.Send(29, [1, 1, 5, 0, 0, 2, 1]);
            var alternate = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
            int secondTreeCost = ghostDefinition.GetOrdinaryResourceSpCost(1, 0) + ghostDefinition.GetOrdinaryResourceSpCost(2, 0);
            That(alternate.Context == 1 && alternate.Mode == 1 && alternate.Sp == bulkSp - secondTreeCost &&
                alternate.Skills.Single() == new LearnedSkill(slot, 5, 3), "TCP直接7B第二树多级，只ACK最终3级和实际余额");
            string snapshot = fixture.Snapshot("SELECT context,sp,tp FROM character_skill_points ORDER BY character_id,context") +
                fixture.Snapshot("SELECT context,slot,skill_id,level FROM character_skill_bindings ORDER BY character_id,context,slot") +
                fixture.Snapshot("SELECT revision FROM characters WHERE slot=1");
            peer.Send(29, WithChecksum([1, 1, 5, 0, 0, 2, 0]));
            peer.Expect(1, 29, [0, 0x16, 0]);
            peer.Barrier();
            That(fixture.Snapshot("SELECT context,sp,tp FROM character_skill_points ORDER BY character_id,context") +
                fixture.Snapshot("SELECT context,slot,skill_id,level FROM character_skill_bindings ORDER BY character_id,context,slot") +
                fixture.Snapshot("SELECT revision FROM characters WHERE slot=1") == snapshot,
                "TCP多级末阶SP不足不保存临时4级或部分费用，拒绝后同连接继续");
            peer.Send(29, [1, 1, 5, 0, 0, 1, 0]);
            var continuation = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
            That(continuation.Sp == 0 && continuation.Skills.Single() == new LearnedSkill(slot, 5, 4),
                "多级拒绝后按原3级一次合法增量仍可学习，不使用已回滚临时等级");
            peer.Send(29, WithChecksum([0, 1, 190, 0, 0, 2, 0]));
            peer.Expect(1, 29, [0, 0xD9, 0]);
            peer.Barrier();
            That(Convert.ToInt64(fixture.Query("SELECT count(*) FROM character_skill_bindings WHERE character_id=(SELECT id FROM characters WHERE slot=1) AND skill_id=190")[0][0]) == 0,
                "TCP未学190请求两级拒绝D9，无临时一级绑定且不伪称原已精通12");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("skill-restart", isolatedProcess: true);
        using (var peer = Open(fixture))
        {
            peer.Select();
            var trees = SkillPacketsTests.DecodeInitialized(peer.SkillInfo);
            That(trees[0].Skills.Single(skill => skill.Id == 46).Level == 2 && trees[1].Skills.Single(skill => skill.Id == 46).Level == 1,
                "重启恢复不同context等级，不把主树重复投影为两树");
            int cost = catalog.Get(0, 46).GetOrdinaryResourceSpCost(1, 0);
            That(trees[0].Sp == initialSp - 15 - cost - 10 - passiveCost && trees[1].Sp == initialSp - 15 - 10 &&
                trees.All(tree => tree.Skills.Single(skill => skill.Id == 190) == new LearnedSkill(quickStandingSlot, 190, 1)),
                "重启保留190及独立余额、slot，不重新发点");
            That(trees[0].Skills.Where(skill => passiveIds.Contains(skill.Id)).SequenceEqual(new LearnedSkill[]
                { new(12, 12, 1), new(54, 35, 1), new(105, 177, 1) }) &&
                trees[1].Skills.All(skill => !passiveIds.Contains(skill.Id)),
                "重启后NOTI19恢复新被动的三个分组槽位，另一树不增加技能");
            That(trees.All(tree => tree.Skills.Single(skill => skill.Id == 5) == new LearnedSkill(6, 5, 2)),
                "重启恢复换槽后的两树绑定，随后学习沿新槽位升级");
            string beforeDuplicate = fixture.Snapshot("SELECT context,sp,tp FROM character_skill_points ORDER BY context") +
                fixture.Snapshot("SELECT context,slot,skill_id,level FROM character_skill_bindings ORDER BY context,slot") +
                fixture.Snapshot("SELECT revision FROM characters WHERE slot=0");
            foreach (byte context in new byte[] { 0, 1 })
            {
                peer.Send(29, WithChecksum([context, 1, 190, 0, 0, 1, 0]));
                peer.Expect(1, 29, [0, 0x12, 0]);
                peer.Barrier();
            }
            That(fixture.Snapshot("SELECT context,sp,tp FROM character_skill_points ORDER BY context") +
                fixture.Snapshot("SELECT context,slot,skill_id,level FROM character_skill_bindings ORDER BY context,slot") +
                fixture.Snapshot("SELECT revision FROM characters WHERE slot=0") == beforeDuplicate,
                "重启后重复190目标2精通12拒绝保持连接，两树/slot/等级/revision不变");
            peer.Send(29, [0, 1, 5, 0, 0, 1, 3]);
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            peer.Select(1);
            var restored = SkillPacketsTests.DecodeInitialized(peer.SkillInfo);
            That(restored.All(tree => tree.Sp == 0 && tree.Skills.Single(skill => skill.Id == 5).Level == 4) &&
                restored[0].Skills.Single(skill => skill.Id == 5).Slot == restored[1].Skills.Single(skill => skill.Id == 5).Slot,
                "TCP重启恢复多级最终结果及独立两树余额，没有中间等级或失败费用");
        }
        foreach (byte[] invalid in new byte[][] { [0, 1, 5, 0, 0, 1], [0, 1, 5, 0, 0, 1, 0, 0] })
        {
            using var peer = Open(fixture);
            peer.Select();
            string before = fixture.Snapshot("SELECT context,sp,tp FROM character_skill_points ORDER BY context");
            peer.Send(29, invalid);
            peer.Closed();
            That(fixture.Snapshot("SELECT context,sp,tp FROM character_skill_points ORDER BY context") == before,
                "TCP短长请求关闭且余额不变");
        }
        foreach (byte[] invalid in new byte[][]
        {
            [0, 102, 0, 255, 255, 255, 255, 0], [0, 6, 138, 255, 255, 255, 255, 0],
            [0, 6, 0], [0, 6, 0, 255, 255, 255, 255, 0, 0]
        })
        {
            using var peer = Open(fixture);
            peer.Select();
            string before = fixture.Snapshot("SELECT context,slot,skill_id,level FROM character_skill_bindings ORDER BY context,slot") +
                fixture.Snapshot("SELECT revision FROM characters WHERE slot=0");
            peer.Send(28, invalid);
            if (invalid.Length == 8) peer.Barrier();
            else peer.Closed();
            That(fixture.Snapshot("SELECT context,slot,skill_id,level FROM character_skill_bindings ORDER BY context,slot") +
                fixture.Snapshot("SELECT revision FROM characters WHERE slot=0") == before, "非法换槽不写库或发布成功");
        }
        using (var peer = Open(fixture))
        {
            peer.Create("SkillNew", job: 14);
            peer.Select(2);
            var trees = SkillPacketsTests.DecodeInitialized(peer.SkillInfo);
            That(trees.All(tree => tree.Sp == 0 && tree.Tp == 0 && tree.Skills.OrderBy(skill => skill.Id)
                .Select(skill => new CharacterSkill(skill.Id, skill.Level)).SequenceEqual(characters.Get(14).Skills)),
                "TCP实际新建女圣职者保留完整初始技能且不赠点");
            That(trees.All(tree => tree.Skills.Where(skill => catalog.Get(14, skill.Id).Type == SkillType.Passive)
                .Select(skill => skill.Slot).SequenceEqual(new byte[] { 102, 103, 104 })),
                "TCP创建入口和首次登录使用正确初始被动槽位");
        }
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "level SkillNew 6", out string slotReply) && slotReply.StartsWith("OK "),
            "隔离fixture设置女圣职者学习等级，重登沿正常来源同步");
        using (var peer = Open(fixture))
        {
            peer.Select(2);
            ushort priestSp = checked((ushort)catalog.TotalSp(6));
            peer.Send(29, WithChecksum([0, 1, 3, 0, 0, 1, 0]));
            var hammer = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
            int hammerCost = catalog.Get(14, 3).GetOrdinaryResourceSpCost(0, 0);
            That(hammer.Skills.Single() == new LearnedSkill(3, 3, 1) && hammer.Sp == priestSp - hammerCost,
                "真实女圣职者空中锤击新学填主快捷栏首空位3");
            peer.Send(28, WithChecksum([0, 0, 198, 255, 255, 255, 255, 0]));
            peer.Expect(1, 28, [1, 0, 0, 198]);
            peer.Send(29, WithChecksum([0, 1, 2, 0, 0, 1, 0]));
            var gap = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
            That(gap.Skills.Single() == new LearnedSkill(0, 2, 1) && gap.Sp == priestSp - hammerCost -
                catalog.Get(14, 2).GetOrdinaryResourceSpCost(0, 0), "主快捷栏中间空位优先复用，不继续排到扩展栏");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(2);
            var trees = SkillPacketsTests.DecodeInitialized(peer.SkillInfo);
            That(trees[0].Skills.Single(skill => skill.Id == 3) == new LearnedSkill(3, 3, 1) &&
                trees[0].Skills.Single(skill => skill.Id == 2) == new LearnedSkill(0, 2, 1) &&
                trees[0].Skills.Single(skill => skill.Id == 1).Slot == 198 &&
                trees[1].Skills.All(skill => skill.Id is not (2 or 3)), "重登保留新技能补空位、主动手动扩展位置和独立另一树");
        }
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "sp TcpSkill 137", out string bonusReply) && bonusReply.StartsWith("OK "),
            "GM设置额外来源而非覆盖学习余额");
        using (var peer = Open(fixture))
        {
            peer.Select();
            var initial = SkillPacketsTests.DecodeInitialized(peer.SkillInfo);
            That(initial[0].Sp == initialSp + 137 - 15 - 20 - 10 - passiveCost, "重登按费用扣除派生余额，额外137保留");
            peer.Send(29, [0, 1, 5, 0, 0, 2, 0]);
            var increase = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
            That(increase.Skills.Single().Level == 4 && increase.Sp == initial[0].Sp - 30, "TCP一次加两级逐阶扣点");
            peer.Send(29, [0, 1, 5, 0, 1, 2, 0]);
            var decrease = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
            That(decrease.Skills.Single().Level == 2 && decrease.Sp == initial[0].Sp, "TCP降两级按费用减少退款");
            peer.Send(29, [0, 1, 12, 0, 1, 1, 0]);
            That(SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body).Skills.Single().Level == 0, "TCP零级删除包含完整ACK变更");
            peer.Send(491, [0, 0]); peer.Expect(1, 491, [1, 0, 1]);
            var reset = SkillPacketsTests.DecodeInitialized(peer.Expect(0, 19).Body);
            That(reset[0].Sp == initialSp + 137 && reset[1].Sp == initial[1].Sp &&
                reset[0].Skills.Single(skill => skill.Id == 5).Slot == 6, "整树重置保留额外来源、另一树和免费技能已有槽位");
            peer.Barrier();
        }
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "contract TcpSkill 60", out string contractReply) && contractReply.StartsWith("OK "),
            "账号达人状态由可信来源设置");
        using (var peer = Open(fixture))
        {
            peer.Select(expertContract: true);
            That(peer.ExpertContract.Length == 11 && U16(peer.ExpertContract, 0) == 2 && peer.ExpertContract[2] == 27 &&
                System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(peer.ExpertContract.AsSpan(3)) is > 0 and <= 60,
                "契约登录通知包含type2、effect27及64位剩余秒数");
            peer.Send(29, [0, 1, 5, 0, 0, 12, 0]);
            That(SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body).Skills.Single().Level == 13, "TCP达人允许加五等级范围购买");
        }
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "contract TcpSkill 0", out string expiredReply) && expiredReply.StartsWith("OK "),
            "隔离fixture设置契约到期");
        using (var peer = Open(fixture))
        {
            peer.Select(expertContract: true);
            var trees = SkillPacketsTests.DecodeInitialized(peer.SkillInfo);
            That(trees[0].Skills.Single(skill => skill.Id == 5).Level == 10 && trees[0].Sp == initialSp + 137 - 135 &&
                System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(peer.ExpertContract.AsSpan(3)) == 0,
                "到期重登普通等级回落、退费、客户端状态清除及额外来源保留");
            peer.Barrier();
        }
        await fixture.StopAsync();
        Console.WriteLine("PASS C# TCP 技能：两树扣除、额外SP、降级/重置、达人到期、换槽与重登重启；非Windows验收");
    }

    private static void CheckDatabase(TcpTestFixture fixture, byte context, ushort sp, byte level, byte slot, ushort id = 5)
    {
        var facts = fixture.Query("""
            SELECT p.sp,b.slot,b.level FROM character_skill_points p JOIN character_skill_bindings b
            ON p.character_id=b.character_id AND p.context=b.context
            WHERE p.context=$p0 AND b.skill_id=$p1 AND p.character_id=(SELECT id FROM characters WHERE slot=0)
            """, context, id);
        That(facts.Length == 1 && Convert.ToUInt16(facts[0][0]) == sp && Convert.ToByte(facts[0][1]) == slot &&
            Convert.ToByte(facts[0][2]) == level, "ACK后的SQL余额、slot及等级对应同一提交");
    }

    // 独立TCP编码真实发送前检查尾，不调用服务端Parser/Builder计算。
    static byte[] WithChecksum(byte[] body)
    {
        byte[] digest = MD5.HashData(body);
        byte[] result = new byte[body.Length + 4];
        body.CopyTo(result, 0);
        result[body.Length] = (byte)(digest[0] ^ digest[13] ^ digest[8] ^ digest[10] ^ 0x81);
        result[body.Length + 1] = (byte)(digest[1] ^ digest[9] ^ digest[5] ^ digest[12] ^ 0x78);
        result[body.Length + 2] = (byte)(digest[2] ^ digest[6] ^ digest[14] ^ digest[4] ^ 0x1A);
        result[body.Length + 3] = (byte)(digest[3] ^ digest[15] ^ digest[11] ^ digest[7] ^ 0xBF);
        return result;
    }
}
