using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class SkillQuickSlotsTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        SkillLayoutTests.Run();
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
        var creatorProfile = resources.Characters.Get(10);
        var creatorLearning = SkillLearning.Initialize(10, creatorProfile.Skills, catalog).State!;
        var creatorSkill = creatorLearning.Trees[0].Skills.First(skill => catalog.Get(10, skill.Id).Type == SkillType.Active);
        ushort creatorActive = creatorSkill.Id;
        // 构造原生切栏故障现场：上排全空，下排仅一项，其余初始绑定保留在普通非快捷区。
        creatorLearning = creatorLearning with { Trees = Array.AsReadOnly(creatorLearning.Trees.Select(tree => tree with
        {
            Skills = Array.AsReadOnly(tree.Skills.Select(skill => skill.Id == creatorActive ? skill with { Slot = 6 } :
                skill.Slot < 12 ? skill with { Slot = (byte)(150 + skill.Slot) } : skill).OrderBy(skill => skill.Slot).ToArray())
        }).ToArray()) };
        var creator = repository.Create(owner, new NewCharacter("CreatorRows", resources.Towns.Room, resources.Towns.InitialReturn)
        { Job = 10, AcceptedStoryLevel = 1, Skills = creatorProfile.Skills, InitialLearning = creatorLearning });
        Character Role() => repository.LoadCharacter(owner, role.Slot);
        Character Creator() => repository.LoadCharacter(owner, creator.Slot);
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
            long unchanged = Role().Revision;
            byte empty = (byte)Enumerable.Range(150, 48).First(slot => Role().Learning.Trees[0].Skills.All(skill => skill.Slot != slot));
            peer.Send(28, [0, empty, 0, 255, 255, 255, 255, 0]); peer.Barrier();
            That(Role().Revision == unchanged, "普通职业空源仍拒绝，不能套用缔造者切栏例外");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); Bytes(peer.SkillInfo, SkillPackets.Initialized(Role().Learning), "重登回放12格的真实持久绑定和余额");
            That(SkillPacketsTests.DecodeInitialized(peer.SkillInfo)[1].Skills.All(skill => skill.Id is not (110 or 109 or 78 or 72)),
                "另一棵技能树不混入新学绑定");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(creator.Slot);
            var before = Creator();
            That(before.Learning.Trees.All(tree => tree.Skills.Where(skill => skill.Slot < 12)
                .SequenceEqual(new LearnedSkill[] { creatorSkill with { Slot = 6 } })), "缔造者切栏fixture真实双树均为上排全空、下排单项");
            foreach (byte context in new byte[] { 0, 1 })
            {
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    long revision = Creator().Revision;
                    // 匹配客户端一次连续发六组，再消费六个ACK；双空槽不能留下永久等待。
                    for (byte slot = 0; slot < 6; slot++)
                        peer.Send(28, [context, slot, (byte)(slot + 6), 255, 255, 255, 255, 0]);
                    for (byte slot = 0; slot < 6; slot++)
                        peer.Expect(1, 28, [1, context, slot, (byte)(slot + 6)]);
                    var after = Creator();
                    byte expected = (byte)(repeat == 0 ? 0 : 6);
                    That(after.Revision == revision + 1 && after.Learning.Trees[context].Skills.Single(skill => skill.Id == creatorActive)
                        == (creatorSkill with { Slot = expected }), "缔造者六组全ACK，只有非空交换递增revision且再次切栏可恢复");
                    That(after.Learning.Trees[1 - context].Skills.SequenceEqual(before.Learning.Trees[1 - context].Skills) &&
                        after.Learning.Experience == before.Learning.Experience &&
                        after.Learning.Trees[context].Sp == before.Learning.Trees[context].Sp &&
                        after.Learning.Trees[context].Tp == before.Learning.Trees[context].Tp,
                        "缔造者切栏保持另一树、经验与技能点");
                }
            }
            byte destination = (byte)Enumerable.Range(150, 48).First(slot => Creator().Learning.Trees[0].Skills.All(skill => skill.Slot != slot));
            peer.Send(28, [0, 6, destination, 255, 255, 255, 255, 0]);
            peer.Expect(1, 28, [1, 0, 6, destination]);
            That(Creator().Learning.Trees[0].Skills.Single(skill => skill.Id == creatorActive).Slot == destination,
                "缔造者切栏后仍可把真实技能拖出快捷栏并持久保存");
            long unchanged = Creator().Revision;
            peer.Send(28, [0, 2, 9, 255, 255, 255, 255, 0]); peer.Barrier();
            That(Creator().Revision == unchanged, "缔造者非对应空源交换仍拒绝，不泛化放宽");
            using var external = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = fixture.Database, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            external.Open();
            using (var writer = external.BeginTransaction(deferred: false))
            {
                peer.Send(28, [0, 2, 8, 255, 255, 255, 255, 0]);
                peer.Expect(1, 28, [1, 0, 2, 8]);
                That(Creator().Revision == unchanged, "外部writer锁下缔造者双空仍立即成功ACK，不申请writer或递增revision");
            }
        }
        await fixture.StopAsync();
        await fixture.StartAsync("skill-slots-restart");
        using (var peer = Open(fixture))
        {
            peer.Select(); Bytes(peer.SkillInfo, SkillPackets.Initialized(Role().Learning), "服务重启回放完整12格布局");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(creator.Slot); Bytes(peer.SkillInfo, SkillPackets.Initialized(Creator().Learning), "服务重启回放缔造者切栏和拖出后的真实双树");
        }
        await fixture.StopAsync();
        Console.WriteLine("PASS C# TCP 实收学习批次、12格填充、复用与重登/重启；非Windows实机");
    }
}
