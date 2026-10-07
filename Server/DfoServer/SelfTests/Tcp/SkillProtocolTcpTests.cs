using DfoServer.Game.Accounts;
using System.Security.Cryptography;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class SkillProtocolTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "skill-learning"));
        var pvf = resources.Archive;
        var characters = resources.Characters;
        var towns = resources.Towns;
        var catalog = SkillLearningCatalog.Load(pvf);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        var growth = CharacterGrowthCatalog.Load(pvf);
        ushort initialSp = checked((ushort)SkillCatalog.Load(pvf).GetLevelSpTotal(4));
        ulong initialExperience = growth.GetLevelStartExperience(4);
        // 预置仅属隔离协议fixture，不替代生产正常升级或Windows发点验收。
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var starting = SkillRepositoryTests.Fixture("TcpSkill", towns, characters, catalog);
            repository.Create(owner, starting with
            {
                Level = 4,
                InitialLearning = new(initialExperience, Array.AsReadOnly(starting.InitialLearning.Trees.Select(tree => tree with { Sp = initialSp }).ToArray())),
                AcceptedStoryLevel = 1,
                ActiveMainQuests = [new(0, 3145, 1)],
                Inventory = DfoServer.Game.Inventory.Services.InventoryRewardGrantService.CreateStarter(characters.Get(0))
            });
        }
        await fixture.StartAsync();
        byte ghostSlot;
        byte[] player;
        using (var peer = Open(fixture))
        {
            peer.Select();
            long selectedRevision = Convert.ToInt64(fixture.Query("SELECT revision FROM characters WHERE slot=0")[0][0]);
            var trees = SkillPacketsTests.DecodeInitialized(peer.SkillInfo);
            ghostSlot = trees[0].Skills.Single(skill => skill.Id == 5).Slot;
            That(trees.All(tree => tree.Sp == initialSp && tree.Tp == 0) && trees.All(tree => tree.Skills.Length == 6),
                "TCP完整NOTI19两树余额与六个真实绑定");
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
            That(Convert.ToInt64(fixture.Query("SELECT revision FROM characters WHERE slot=0")[0][0]) == selectedRevision + 2,
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
        }
        await fixture.StopAsync();
        await fixture.StartAsync("skill-restart");
        using (var peer = Open(fixture))
        {
            peer.Select();
            var trees = SkillPacketsTests.DecodeInitialized(peer.SkillInfo);
            That(trees[0].Skills.Single(skill => skill.Id == 46).Level == 2 && trees[1].Skills.Single(skill => skill.Id == 46).Level == 1,
                "重启恢复不同context等级，不把主树重复投影为两树");
            int cost = catalog.Get(0, 46).GetOrdinaryResourceSpCost(1, 0);
            That(trees[0].Sp == initialSp - 15 - cost && trees[1].Sp == initialSp - 15, "重启保留独立余额，不重新发点");
            peer.Send(29, [0, 1, 5, 0, 0, 1, 3]);
            peer.Closed();
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
        await fixture.StopAsync();
        Console.WriteLine("PASS C# TCP 技能：完整投影、两树扣点、失败包序、重登重启及长度拒绝；非生产成长验收");
    }

    private static void CheckDatabase(TcpTestFixture fixture, byte context, ushort sp, byte level, byte slot)
    {
        var facts = fixture.Query("""
            SELECT p.sp,b.slot,b.level FROM character_skill_points p JOIN character_skill_bindings b
            ON p.character_id=b.character_id AND p.context=b.context
            WHERE p.context=$p0 AND b.skill_id=5
            """, context);
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
