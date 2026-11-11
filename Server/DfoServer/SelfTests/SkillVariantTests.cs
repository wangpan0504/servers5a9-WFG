using DfoServer.Game.Characters;
using DfoServer.Network.Builders;
using DfoServer.Game.Accounts;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class SkillVariantTests
{
    public static void Run()
    {
        Character Role(byte job, byte grow, ushort skill, int tree = 0) => new(1, 0, "Variant", 0, new(0, 0, 0, 0), null)
        {
            Job = job,
            GrowType = grow,
            Learning = new(0, [new(0, 0, 0, [new(0, skill, 1)]), new(1, 0, 0, [])]) { SelectedTree = tree }
        };
        var steel = Role(0, 0x21, 259);
        var iron = steel with { Learning = steel.Learning with { SelectedTree = 1 } };
        That(CharacterSkillVariant.Read(steel) == 1 && CharacterSkillVariant.Read(iron) == 0,
            "剑魂当前树学习斩钢显示斩钢，切到未学习树恢复斩铁");
        That(CharacterSkillVariant.Read(Role(4, 1, 258)) == 1 && CharacterSkillVariant.Read(Role(4, 0x21, 258, 1)) == 0,
            "男圣骑圣灵之槌区分战斗与Buff，未觉醒也适用");
        foreach (byte job in Enumerable.Range(0, 16).Select(value => (byte)value))
            if (job != 0) That(CharacterSkillVariant.Read(Role(job, 0x21, 259)) == 0, "其它职业同编号不是斩钢");
        That(CharacterSkillVariant.Read(Role(0, 1, 259)) == 0 && CharacterSkillVariant.Read(Role(0, 0x22, 259)) == 0 &&
            CharacterSkillVariant.Read(Role(14, 0x21, 258)) == 0, "未觉醒、其它转职及女圣骑不冒分支");
        var original = TownPackets.Actor(iron, 0);
        var changed = TownPackets.Actor(steel, 0);
        That(original.Length == changed.Length && changed[^2] == 1 && original[^2] == 0 &&
            Enumerable.Range(0, original.Length).Where(at => original[at] != changed[at]).SequenceEqual([original.Length - 9, original.Length - 2]),
            "双树切换只改变原有当前树与分支字节，不增包长或覆盖其它显示字段");
        foreach (var role in new[] { steel, Role(4, 0x21, 258) })
        {
            var actor = TownPackets.Actor(role, 0, new(1, 19));
            var cross = RaidCrossChannelPackets.Request(role, new(1, 19), 100, 100, 0, actor);
            That(cross[54] == 1 && cross.Length == 59, "跨频道招募摘要沿用同一分支字节");
        }
        Publication(steel);
        Console.WriteLine("PASS SkillVariantTests");
    }

    private static void Publication(Character steel)
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-skill-variant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var point = new Location(38, 1, 450, 234);
            var towns = new TownCatalog(new(), point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var owners = new AccountOwners();
            using var owner = owners.Acquire("variant-source", () => { });
            using var peerOwner = owners.Acquire("variant-peer", () => { });
            var role = repository.Create(owner, new("VariantSrc", point, point)
            { GrowType = steel.GrowType, Level = 95, InitialLearning = steel.Learning, Skills = [new(259, 1)],
                CompletedQuests = [12829, 3779, 2903, 4872, CharacterRosterEntry.FiendWarEntryQuest] });
            repository.Create(peerOwner, new("VariantPeer", point, point) { Level = 95,
                CompletedQuests = [12829, 3779, 2903, 4872, CharacterRosterEntry.FiendWarEntryQuest] });
            var session = new GameSession(repository, towns, owner, entry: new(new(1, 1), 0, "[normal]"));
            var peer = new GameSession(repository, towns, peerOwner, entry: new(new(1, 1), 0, "[normal]"));
            session.Select(role.Slot); session.MarkReady(); peer.Select(0); peer.MarkReady();
            var sourceActor = session.CaptureSocialActor()!;
            var peerActor = peer.CaptureSocialActor()!;
            PartySnapshot? party = new(1, [new(sourceActor, session.PublicCharacter(role.Id)!, 0),
                new(peerActor, peer.PublicCharacter(peer.Selected!.Id)!, 1)], 0, 1);
            session.CurrentParty = () => party; peer.CurrentParty = () => party;
            var sourceNotices = new List<(PublicCharacterSnapshot Snapshot, Func<bool> Guard)>();
            var peerNotices = new List<(PublicCharacterSnapshot Snapshot, Func<bool> Guard)>();
            session.SkillVariantChanged = (snapshot, guard) => sourceNotices.Add((snapshot, guard));
            peer.SkillVariantChanged = (snapshot, guard) => peerNotices.Add((snapshot, guard));
            var changed = session.ChangeSkillTree(0).Receipt!;
            session.PublishSkillVariant(changed, 1);
            That(sourceNotices.Count == 1 && peerNotices.Count == 1 && peerNotices[0].Guard() &&
                CharacterSkillVariant.Read(peerNotices[0].Snapshot.SkillCharacter) == 0,
                "切换加点后本人和同队成员收到已提交的斩铁快照");
            session.PublishSkillVariant(changed, 0);
            That(peerNotices.Count == 1, "职业分支没变时不重复广播");
            peer.AdvancePartyMembership();
            That(!peerNotices[0].Guard(), "退队再入队使旧排队刷新失效");
            var restored = session.ChangeSkillTree(1).Receipt!;
            session.PublishSkillVariant(restored, 0);
            That(!peerNotices[0].Snapshot.IsUnchanged() && CharacterSkillVariant.Read(sourceNotices[^1].Snapshot.SkillCharacter) == 1,
                "紧接着恢复斩钢后旧斩铁快照不能回灌");
            party = null;
            foreach (byte type in new byte[] { 0, 1, 2, 3 })
            {
                session.End(); peer.End();
                int channelType = new[] { 0x19, 0x20, 0x2D, 0x32 }[type];
                session = new(repository, towns, owner, entry: new(new(1, 1), channelType, "[raid]"));
                peer = new(repository, towns, peerOwner, entry: new(new(1, 1), channelType, "[raid]"));
                session.Select(0); session.MarkReady(); peer.Select(0); peer.MarkReady();
                session.SkillVariantChanged = (snapshot, guard) => sourceNotices.Add((snapshot, guard));
                peer.SkillVariantChanged = (snapshot, guard) => peerNotices.Add((snapshot, guard));
                restored = new(session.Selected!, owner.Generation, session.SelectionEpoch);
                var registry = new RaidRegistry();
                var raidSource = session.CaptureRaidActor(registry)!;
                var raidPeer = peer.CaptureRaidActor(registry)!;
                That(registry.Create(raidSource, type, "Variant", "Variant"u8.ToArray(), 20) is not null,
                    "建立职业变体测试攻坚队");
                registry.Invite(raidSource, raidPeer, 0);
                That(registry.Respond(raidPeer, raidSource.Character, true).Accepted, "队友加入攻坚队");
                int count = peerNotices.Count;
                session.PublishSkillVariant(restored, 0, includeSelf: false);
                That(peerNotices.Count == count + 1 && peerNotices[^1].Guard(), "四类攻坚队共用职业刷新");
                registry.Leave(raidPeer, raidPeer.Character);
                That(!peerNotices[^1].Guard(), "退团后不发送旧团职业刷新");
                registry.Leave(raidSource, raidSource.Character);
            }
            session.End();
            int staleCount = peerNotices.Count;
            session.PublishSkillVariant(restored, 0);
            That(peerNotices.Count == staleCount && !sourceNotices[^1].Snapshot.IsCurrent(), "退出选角后旧学习回执不能发布");
            peer.End();
        }
        finally { TestFiles.DeleteDirectory(root, true); }
    }
}
