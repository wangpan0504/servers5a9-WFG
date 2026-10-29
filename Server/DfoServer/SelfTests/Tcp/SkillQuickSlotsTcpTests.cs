using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class SkillQuickSlotsTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "skill-slots"), resources: resources);
        var catalog = resources.Learning;
        var growth = SelfTestCatalogs.Growth(resources.Archive, catalog);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { });
        var profile = resources.Characters.Get(13);
        var role = repository.Create(owner, new NewCharacter("QuickTwelve", resources.Towns.Room, resources.Towns.InitialReturn)
        { Job = 13, AcceptedStoryLevel = 1, Skills = profile.Skills, InitialLearning = SkillLearning.Initialize(13, profile.Skills, catalog).State!,
            Inventory = DfoServer.Game.Inventory.Services.InventoryRewardGrantService.CreateStarter(profile) });
        var reset = new CharacterProgressReset(resources.Characters, catalog, growth);
        role = repository.ResetProgress(owner, reset.Prepare(role, new(CharacterProgressField.Level, owner.Account, role.Name, 95, 0)));
        role = repository.ResetProgress(owner, reset.Prepare(role, new(CharacterProgressField.Grow, owner.Account, role.Name, 35, 0)));
        Character Role() => repository.LoadCharacter(owner, role.Slot);
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            var before = Role();
            var granted = before.Learning.Trees[0].Skills.Single(skill => skill.Id == 68);
            That(granted is { Level: 1, Slot: >= 102 and < 138 }, "重建角色已获得CHR觉醒被动首级，保留初始被动绑定");
            // 原实收批次在赠技补齐后会把68从一级加到十七级，超过Lv95门槛，仍应整批拒绝。
            peer.Send(29, Convert.FromHexString("000B6E0000036D0000085500000D6C00000B6B00000754000012440000105300001A5200001C4F00001F5000001F00"));
            peer.Expect(1, 29, [0, 0xD9, 0]); peer.Barrier();
            That(Role().Revision == before.Revision, "免费首级已存在时仍校验批次目标等级，后项失败不提交前序技能");
            // 保持原目标等级16，仅将68的增量改为15；其它十项及槽位覆盖保留。
            peer.Send(29, Convert.FromHexString("000B6E0000036D0000085500000D6C00000B6B000007540000124400000F5300001A5200001C4F00001F5000001F00"));
            var bought = SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body);
            That(bought.Skills.Select(skill => skill.Slot).SequenceEqual(new byte[] { 5, 6, 7, 8, 54, 9, granted.Slot, 10, 11, 150, 151 }),
                "实收混合批次填满0–11，主动溢出150区，被动不占扩展栏");
            That(Role().Learning.Trees[0].Skills.Where(skill => skill.Slot < 12).Select(skill => skill.Slot)
                .SequenceEqual(Enumerable.Range(0, 12).Select(slot => (byte)slot)), "实际SQL完整占用12格可见快捷栏");
            peer.Send(28, [0, 6, 152, 255, 255, 255, 255, 0]); peer.Expect(1, 28, [1, 0, 6, 152]);
            peer.Send(29, [0, 1, 78, 0, 0, 1, 0]);
            That(SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body).Skills.Single() == new LearnedSkill(6, 78, 1),
                "新学技能复用扩展栏中间空位6");
            peer.Send(29, [0, 1, 78, 0, 0, 1, 0]);
            That(SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body).Skills.Single() == new LearnedSkill(6, 78, 2),
                "升级保持实际扩展槽位");
            peer.Send(29, [0, 1, 78, 0, 1, 2, 0]); peer.Expect(1, 29);
            peer.Send(29, [0, 1, 72, 0, 0, 1, 0]);
            That(SkillPacketsTests.DecodeSuccess(peer.Expect(1, 29).Body).Skills.Single() == new LearnedSkill(6, 72, 1),
                "退款到0后的扩展空位供下一新技能复用");
            peer.Send(28, [0, 54, 6, 255, 255, 255, 255, 0]); peer.Barrier();
            That(Role().Learning.Trees[0].Skills.Single(skill => skill.Id == 72).Slot == 6 &&
                Role().Learning.Trees[0].Skills.Single(skill => skill.Id == 107).Slot == 54, "被动与扩展快捷栏交换拒绝且连接可继续");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); Bytes(peer.SkillInfo, SkillPackets.Initialized(Role().Learning), "重登回放12格的真实持久绑定和余额");
            That(SkillPacketsTests.DecodeInitialized(peer.SkillInfo)[1].Skills.All(skill => skill.Id is not (110 or 109 or 78 or 72)),
                "另一棵技能树不混入新学绑定");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("skill-slots-restart");
        using (var peer = Open(fixture))
        {
            peer.Select(); Bytes(peer.SkillInfo, SkillPackets.Initialized(Role().Learning), "服务重启回放完整12格布局");
        }
        await fixture.StopAsync();
        Console.WriteLine("PASS C# TCP 实收学习批次、12格填充、复用与重登/重启；非Windows实机");
    }
}
