using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using DfoServer.Network;
using DfoServer.Network.Parsers;
using DfoServer.Network.Management;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class LegacyStatusConsumableTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "legacy-status"), gm: true, resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
        Character role, failed;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("Legacy", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0, 0, 60, candidate.InitialLearning with
                { Experience = growth.GetLevelStartExperience(60), BonusSp = 17, BonusTp = 5 }, resources.Learning);
            foreach (byte context in new byte[] { 0, 1 })
                learning = SkillLearning.Plan(0, 0, 60, learning, new(context, 0, [new(5, false, 2)]), resources.Learning).State!;
            candidate = candidate with { Level = 60, InitialLearning = learning, Inventory = candidate.Inventory with
            {
                ReviveCoins = 3, Items = candidate.Inventory.Items.AddRange(new[]
                {
                    new InventoryItem(0, new(InventoryListType.Main, 65), new(7180, ItemKind.Consumable, 2)),
                    new InventoryItem(0, new(InventoryListType.Main, 66), new(7181, ItemKind.Consumable, 1)),
                    new InventoryItem(0, new(InventoryListType.Main, 67), new(28, ItemKind.Consumable, 1)),
                    new InventoryItem(0, FatigueConsumableTests.Slot, new(490001834, ItemKind.Consumable, 1)),
                    new InventoryItem(0, FatigueConsumableTests.Second, new(490001834, ItemKind.Consumable, 1))
                })
            } };
            role = repository.Create(owner, candidate);
            failed = repository.Create(owner, candidate with { Name = "LegacyFail" });
        }
        fixture.Sql("UPDATE character_fatigue SET used=50,remaining=maximum-50");
        await fixture.StartAsync();
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "infinite-fatigue off", out _), "疲劳恢复实扣用例关闭默认无限显示");
        using (var peer = Open(fixture))
        {
            peer.Select(role.Slot);
            foreach (var (slot, gain, total) in new[] { ((ushort)65, 1u, 4), ((ushort)66, 5u, 9) })
            {
                peer.Send(30, Build(w => w.Write(slot)));
                peer.Expect(1, 30, Build(w => { w.Write((byte)1); w.Write(slot); w.Write((byte)13); w.Write(gain); w.Write(0u); }));
                var delta = peer.Expect(0, 14).Body;
                That(delta[0] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(delta.AsSpan(1)) == 1 &&
                    BinaryPrimitives.ReadInt16LittleEndian(delta.AsSpan(3)) == 1 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(delta.AsSpan(9)) == total &&
                    Convert.ToInt32(fixture.Query("SELECT revive_coins FROM characters WHERE id=$p0", role.Id)[0][0]) == total,
                    "币券ACK后只通知真实复活币钱包格，不重复删除源或发送获得播报");
                peer.Barrier();
            }
            peer.Send(30, [67, 0]);
            peer.Expect(1, 30, [0, 19, 0]);
            peer.Barrier();
            using var reader = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            var current = repository.LoadCharacter(reader, role.Slot);
            That(current.Learning.Trees.All(tree => tree.Sp == role.Learning.Trees.Single(old => old.Context == tree.Context).Sp &&
                    tree.Tp == role.Learning.Trees.Single(old => old.Context == tree.Context).Tp) &&
                current.Inventory.Find(new(InventoryListType.Main, 67))?.Core.ItemId == 28,
                "不支持的28只返回拒绝，不回放技能树或消费源道具");
            foreach (uint claimed in new uint[] { 64, 199 })
            {
                string snapshot = fixture.Snapshot();
                peer.Send(515, ActionBody(new(68, 0, 0, claimed, 0, 0)));
                peer.Expect(1, 515, [0, 0, 0, 68, 0, 0, (byte)claimed]); peer.Barrier();
                That(fixture.Snapshot() == snapshot, "统一动作入口拒绝以疲劳药冒充宠物转换或入库令牌，不扣源或写资产");
            }
            var action = new StackableActionUseRequest(68, 0, 0, FatigueConsumableDefinition.Action, 0, 0);
            peer.Send(515, ActionBody(action));
            peer.Expect(1, 515, [1, 68, 0, 0, 58, 0, 0, 0]);
            var fatigue = repository.LoadCharacter(reader, role.Slot).Fatigue!;
            peer.Expect(0, 36, Build(w => { w.Write((ushort)49); w.Write((ushort)156); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); }));
            peer.Barrier();
            That(fatigue.Used == 49 && fatigue.Remaining == 107, "重复类型疲劳药先消费回执，再同步同次提交的1点疲劳绝对余额");
            var cooling = action with { Slot = 69 };
            string untouched = fixture.Snapshot();
            peer.Send(515, ActionBody(cooling));
            peer.Expect(1, 515, [0, 19, 0, 69, 0, 0, 58]); peer.Barrier();
            That(fixture.Snapshot() == untouched, "持久冷却拒绝后连接继续、源及疲劳不变");
        }
        fixture.Sql($"CREATE TRIGGER legacy_tcp_abort AFTER UPDATE OF revive_coins ON characters WHEN NEW.id={failed.Id} AND NEW.revive_coins<>OLD.revive_coins BEGIN SELECT RAISE(ABORT,'legacy rollback'); END");
        string before;
        using (var peer = Open(fixture))
        {
            peer.Select(failed.Slot); before = fixture.Snapshot(); peer.Send(30, [65, 0]); peer.Closed();
        }
        That(fixture.Snapshot() == before, "SQL失败不发成功ACK、不增加币也不扣源");
        fixture.Sql("DROP TRIGGER legacy_tcp_abort");
        await fixture.StopAsync(); await fixture.StartAsync();
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "infinite-fatigue off", out _), "重启后保持用例普通疲劳显示");
        using (var peer = Open(fixture))
        {
            peer.Select(role.Slot);
            using var reader = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            var current = repository.LoadCharacter(reader, role.Slot);
            That(current.Inventory.ReviveCoins == 9 && current.Learning.BonusSp == 17 && current.Learning.BonusTp == 5 &&
                current.Learning.Trees.All(tree => tree.Sp == role.Learning.Trees.Single(old => old.Context == tree.Context).Sp),
                "重启及重选保留币余额、原技能树与额外点数来源");
            var action = new StackableActionUseRequest(69, 0, 0, FatigueConsumableDefinition.Action, 0, 0);
            peer.Send(515, ActionBody(action)); peer.Expect(1, 515, [0, 19, 0, 69, 0, 0, 58]); peer.Barrier();
            That(repository.LoadCharacter(reader, role.Slot).Fatigue!.Used == 49, "重启不能绕过疲劳恢复冷却");
        }
    }

    static byte[] ActionBody(StackableActionUseRequest request) => Build(w =>
    {
        w.Write(request.Slot); w.Write(request.List); w.Write(request.Reserved); w.Write(request.Action);
        w.Write(request.FirstArgument); w.Write(request.SecondArgument);
    });
}
