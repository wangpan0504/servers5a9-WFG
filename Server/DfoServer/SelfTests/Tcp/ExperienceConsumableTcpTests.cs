using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Skills;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class ExperienceConsumableTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "experience-consumable"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
        var created = new List<Character>();
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("Capsule", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get);
            foreach (var (name, level, item) in new (string, byte, int)[]
                { ("Capsule", 49, ExperienceConsumableTests.PercentItem), ("CapsuleRoll", 60, ExperienceConsumableTests.FixedItem),
                  ("CapsuleMap", 16, ExperienceConsumableTests.PercentItem), ("OldBook", 1, 1034),
                  ("MillionBook", 60, 1231), ("TenMilBook", 60, 1232), ("CappedBook", 94, 1232),
                  ("TownCapsule", 60, 10005340), ("GuideTown", 14, 1034), ("GuideMap", 14, 1034),
                  ("EmoteLevel", 19, ExperienceConsumableTests.PercentItem) })
            {
                var inventory = candidate.Inventory.Items.Add(new(0, new(InventoryListType.Main, 65),
                    new(item, GameWorld.Items.ItemKind.Consumable, 2)));
                if (name == "CapsuleMap")
                    inventory = inventory.Add(new(0, new(InventoryListType.Main, 66),
                        new(490010192, GameWorld.Items.ItemKind.Consumable, 1)));
                created.Add(repository.Create(owner, candidate with
                {
                    Name = name, Level = level, InitialLearning = SkillPointLedger.Synchronize(0, 0, level, candidate.InitialLearning with
                    { Experience = growth.GetCumulativeExperienceBoundary(level) - 1, BonusSp = 17, BonusTp = 5 }, resources.Learning),
                    Inventory = candidate.Inventory with { Items = inventory }
                }));
            }
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Send(30, [65, 0]); peer.Expect(1, 30, [0, 19, 0]);
            peer.Select(created[0].Slot);
            string before = fixture.Snapshot();
            foreach (byte[] body in new byte[][] { [255, 255], [67, 0] })
            {
                peer.Send(30, body); peer.Expect(1, 30, [0, 19, 0]); peer.Barrier();
                That(fixture.Snapshot() == before, "非法或空源槽失败收尾后连接继续，资产不变");
            }
            var first = Use(peer, created[0], growth.GetLevelExperienceSpan(49) * 10 / 100, 50);
            That(first.All(packet => packet.Opcode != 14) && first.Single(packet => packet.Opcode == 37).Body.Length == 91 &&
                first.Count(packet => packet.Opcode == 21) == 2 && first.Count(packet => packet.Opcode == 574) == 1,
                "先增量ACK及升级候选，再由下一请求分配新等级每日并刷新候选，不重复删除源");
            using var readOwner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            var after = repository.LoadCharacter(readOwner, created[0].Slot);
            That(after.ActiveMainQuests.Select(row => (int)row.QuestId).Order().SequenceEqual(new[] {13033,13036}),
                "49升50保留旧等级每日并新增当前等级每日，不提前删除尚未完成任务");
            Bytes(first.Single(packet => packet.Opcode == 574).Body,
                DfoServer.Network.Builders.QuestPackets.ActiveList(after.ActiveMainQuests),"每日分配通知精确对应已提交两项任务");
            Use(peer, after, growth.GetLevelExperienceSpan(50) * 10 / 100, 50);
            before = fixture.Snapshot(); peer.Send(30, [65, 0]); peer.Expect(1, 30, [0, 19, 0]); peer.Barrier();
            That(fixture.Snapshot() == before, "同槽连续消费到空后，不重用上次增量响应");
        }
        foreach (int index in new[] { 8, 9 })
        {
            using var peer = Open(fixture);
            var before = created[index];
            peer.Select(before.Slot);
            That(peer.SkillGuideInfo.Length > 0 && peer.SkillGuideInfo.All(body =>
                Enumerable.Range(0, body[0]).All(row => body[3 + row * 3] == 0)), "14级选角完整恢复技能引导");
            if (index == 9) { FormalDungeonTcpTests.EntryTown(peer); FormalDungeonTcpTests.Enter(peer); FormalDungeonTcpTests.Load(peer); }
            var packets = Use(peer, before, 100, 15);
            var ids = resources.Learning.SkillIds(before.Job);
            var guides = packets.Where(packet => packet is { Kind: 0, Opcode: 1497 }).ToArray();
            That(guides.Length == (ids.Count + 254) / 255, "城镇及副本14升15均完整刷新所有职业技能引导");
            foreach (var pair in guides.Zip(ids.Chunk(255)))
                Bytes(pair.First.Body, Build(writer =>
                {
                    writer.Write(checked((byte)pair.Second.Length));
                    foreach (ushort id in pair.Second) { writer.Write(id); writer.Write((byte)5); }
                }), "跨15级通知逐项为真实完整u16技能编号及停止次数5");
        }
        fixture.Sql($"CREATE TRIGGER capsule_tcp_abort AFTER UPDATE OF level ON characters WHEN NEW.id={created[1].Id} BEGIN SELECT RAISE(ABORT,'capsule tcp rollback'); END");
        string failed;
        using (var peer = Open(fixture))
        {
            peer.Select(created[1].Slot); failed = fixture.Snapshot(); peer.Send(30, [65, 0]); peer.Closed();
        }
        That(fixture.Snapshot() == failed, "成长阶段SQL失败不发送成功ACK，回滚经验及源库存");
        fixture.Sql("DROP TRIGGER capsule_tcp_abort");
        using (var peer = Open(fixture))
        {
            peer.Select(created[1].Slot); Use(peer, created[1], 500000, 61);
        }
        using (var peer = Open(fixture))
        {
            peer.Select(created[2].Slot); FormalDungeonTcpTests.EntryTown(peer); FormalDungeonTcpTests.Enter(peer);
            string before = fixture.Snapshot(); peer.Send(30, [65, 0]); peer.Expect(1, 30, [0, 19, 0]); peer.Barrier();
            That(fixture.Snapshot() == before, "副本Loading阶段不能消费胶囊");
            FormalDungeonTcpTests.Load(peer);
            before = fixture.Snapshot();
            peer.Send(30, [66, 0]); peer.Expect(1, 30, [0, 19, 0]); peer.Barrier();
            That(fixture.Snapshot() == before, "城镇与赛丽亚并集胶囊在已加载副本拒绝，不借保存的房间坐标放行");
            var packets = Use(peer, created[2], growth.GetLevelExperienceSpan(16) * 10 / 100, 17);
            That(packets.Count(packet => packet.Opcode == 2) == 1 && packets.Single(packet => packet.Opcode == 2).Body[0] == 1,
                "副本升级只刷新Player，不重建Actor或城镇上下文");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(created[3].Slot);
            var packets = Use(peer, created[3], 100, 2);
            That(packets.All(packet => packet.Opcode != 14), "旧经验书同样由ACK30扣源，不重复发送源删除");
        }
        for (int index = 4; index < 7; index++)
        {
            using var peer = Open(fixture);
            var before = created[index];
            peer.Select(before.Slot);
            ulong gain = index == 4 ? 1000000UL : index == 5 ? 10000000UL : 1UL;
            byte target = before.Level;
            while (before.Learning.Experience + gain >= growth.GetCumulativeExperienceBoundary(target)) target++;
            var packets = Use(peer, before, gain, target);
            That(packets.All(packet => packet.Opcode != 14), "大额经验书由ACK30扣源，不重复删除或截断增量");
            using var readOwner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            var after = repository.LoadCharacter(readOwner, before.Slot);
            That(after.Inventory.Find(new(InventoryListType.Main, 65))!.Core.Count == 1 &&
                after.Learning.Experience == before.Learning.Experience + gain,
                "百万和千万经验书的源数量及累计经验持久化");
            if (index == 6)
            {
                string full = fixture.Snapshot();
                peer.Send(30, [65, 0]); peer.Expect(1, 30, [0, 19, 0]); peer.Barrier();
                That(fixture.Snapshot() == full, "满级零到账拒绝且不吞剩余经验书，连接继续");
            }
        }
        using (var peer = Open(fixture))
        {
            peer.Select(created[7].Slot);
            Use(peer, created[7], 100000, 61);
            FormalDungeonTcpTests.EntryTown(peer);
            using var readOwner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            var after = repository.LoadCharacter(readOwner, created[7].Slot);
            Use(peer, after, 100000, 61);
            That(repository.LoadCharacter(readOwner, created[7].Slot).Inventory.Find(new(InventoryListType.Main, 65)) is null,
                "城镇与赛丽亚并集胶囊在房间和公开城镇均可经CMD30消费，不重复扣源");

        }
        using (var peer = Open(fixture))
        {
            peer.Select(created[10].Slot);
            var packets = Use(peer, created[10], growth.GetLevelExperienceSpan(19) * 10 / 100, 20);
            var expected = fixture.ExpectedChatEmoticons(peer.Id);
            if (expected is not null && resources.ChatEmoticons.Eligible(19).Length != resources.ChatEmoticons.Eligible(20).Length)
                Bytes(packets.Single(packet => packet is { Kind: 0, Opcode: 411 }).Body, expected,
                    "升级跨过PVF等级门槛后刷新账号聊天表情配置");
        }
        await fixture.StopAsync(); await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(created[0].Slot); peer.Barrier();
            That(fixture.Query("SELECT level,experience FROM characters WHERE id=$p0", created[0].Id) is [[50L, var xp]] &&
                Convert.ToUInt64(xp) == created[0].Learning.Experience + growth.GetLevelExperienceSpan(49) * 10 / 100 + growth.GetLevelExperienceSpan(50) * 10 / 100,
                "服务实例重建后装载已提交绝对经验，不重新赠送胶囊经验");
        }

        List<WirePacket> Use(TcpPeer peer, Character before, ulong gain, byte target)
        {
            peer.Send(30, [65, 0]);
            peer.Expect(1, 30, Build(w =>
            {
                w.Write((byte)1); w.Write((ushort)65); w.Write((byte)1); w.Write((uint)gain);
                w.Write((ushort)growth.GetSpDelta(before.Level, target)); w.Write((ushort)growth.GetTpDelta(before.Level, target));
            }));
            peer.SendBarrier();
            var packets = new List<WirePacket>();
            while (packets.Count < 20)
            {
                var packet = peer.Read();
                if (packet is { Kind: 1, Opcode: 1347 })
                {
                    Bytes(packet.Body, [1], "消费后请求屏障成功");
                    var experience = packets.Single(item => item.Opcode == 37).Body;
                    That(experience[0] == target && BinaryPrimitives.ReadUInt64LittleEndian(experience.AsSpan(1)) == before.Learning.Experience + gain,
                        "NOTI37反映同一笔已提交的绝对经验，不再叠加ACK差额");
                    if (target > before.Level)
                    {
                        int player = packets.FindLastIndex(item => item is { Kind: 0, Opcode: 2 } && item.Body[0] == 1);
                        bool dungeon = packets.Count(item => item is { Kind: 0, Opcode: 2 }) == 1;
                        int skills = packets.FindIndex(item => dungeon ? item is { Kind: 1, Opcode: 29 } : item is { Kind: 0, Opcode: 19 });
                        That(player >= 0 && skills > player && (!dungeon || packets.All(item => item is not { Kind: 0, Opcode: 19 })),
                            "胶囊升级在Player后同步真实技能；图内采用差量而非清理运行对象的完整19");
                    }
                    return packets;
                }
                packets.Add(packet);
            }
            throw new InvalidDataException("经验胶囊响应未收齐");
        }
    }
}
