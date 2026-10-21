using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class PermanentGrowthTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        PermanentGrowthTests.Run(resources.Towns, resources.Archive, resources.Characters, resources.ItemResources, resources.Learning);
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "permanent-growth"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
        var stats = new CharacterStatComputer(resources.Characters);
        Character role, failedRole;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("Permanent", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get);
            candidate = candidate with
            {
                Level = 60, InitialLearning = SkillPointLedger.Synchronize(0, 0, 60, candidate.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(60), BonusSp = 17, BonusTp = 5 }, resources.Learning),
                Inventory = candidate.Inventory with { Items = candidate.Inventory.Items.AddRange(PermanentGrowthTests.Cases.Select((row, index) =>
                    new InventoryItem(0, new(InventoryListType.Main, checked((short)(65 + index))), new(row.Id, ItemKind.Consumable, 2)))) }
            };
            role = repository.Create(owner, candidate);
            failedRole = repository.Create(owner, candidate with { Name = "PermRoll" });
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(role.Slot);
            for (int index = 0; index < PermanentGrowthTests.Cases.Length; index++)
            {
                var row = PermanentGrowthTests.Cases[index];
                peer.Send(30, Build(w => w.Write((ushort)(65 + index))));
                peer.Expect(1, 30, Build(w => { w.Write((byte)1); w.Write((ushort)(65 + index)); w.Write((byte)row.Kind); w.Write((uint)row.Amount); w.Write(0u); }));
                peer.SendBarrier();
                var packets = new List<WirePacket>();
                WirePacket packet;
                while ((packet = peer.Read()) is not { Kind: 1, Opcode: 1347 }) packets.Add(packet);
                using var reader = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
                var current = repository.LoadCharacter(reader, role.Slot);
                That(current.Inventory.Find(new(InventoryListType.Main, checked((short)(65 + index))))!.Core.Count == 1 &&
                    packets.All(value => value.Opcode is not (14 or 19)), "成功ACK先消费一次，后续不重复删除源或重建技能");
                if (row.Kind is PermanentGrowthKind.Sp or PermanentGrowthKind.Tp)
                {
                    var exp = packets.Single(value => value.Opcode == 37).Body;
                    That(BinaryPrimitives.ReadUInt64LittleEndian(exp.AsSpan(1)) == role.Learning.Experience &&
                        BinaryPrimitives.ReadUInt16LittleEndian(exp.AsSpan(13)) == current.Learning.Trees.Single(tree => tree.Context == 0).Sp &&
                        BinaryPrimitives.ReadUInt16LittleEndian(exp.AsSpan(19)) == current.Learning.Trees.Single(tree => tree.Context == 1).Tp,
                        "书的37同步两树绝对余额，没有升级或增加经验");
                }
                else
                {
                    var player = packets.Single(value => value is { Kind: 0, Opcode: 2 } && value.Body[0] == 1);
                    var basis = stats.Compute(current);
                    That(BinaryPrimitives.ReadUInt32LittleEndian(player.Body.AsSpan(36)) == basis.MaxHp &&
                        BinaryPrimitives.ReadInt16LittleEndian(player.Body.AsSpan(44)) == basis.PhysicalAttack &&
                        BinaryPrimitives.ReadInt16LittleEndian(player.Body.AsSpan(52)) == basis.FireResistance &&
                        BinaryPrimitives.ReadUInt32LittleEndian(player.Body.AsSpan(104)) == basis.MoveSpeed &&
                        packets.All(value => value.Opcode != 37), "属性石以同次提交的永久来源投影Player，不借经验通知或城镇Actor切状态");
                }
            }
        }
        fixture.Sql($"CREATE TRIGGER permanent_tcp_abort AFTER INSERT ON character_permanent_stats WHEN NEW.character_id={failedRole.Id} BEGIN SELECT RAISE(ABORT,'permanent TCP rollback'); END");
        string failed;
        using (var peer = Open(fixture))
        {
            peer.Select(failedRole.Slot); failed = fixture.Snapshot();
            peer.Send(30, [67, 0]); peer.Closed();
        }
        That(fixture.Snapshot() == failed, "属性来源SQL失败不发成功ACK，并回滚库存和revision");
        fixture.Sql("DROP TRIGGER permanent_tcp_abort");
        await fixture.StopAsync();
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(role.Slot);
            using var reader = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            var after = repository.LoadCharacter(reader, role.Slot);
            That(after.PermanentStats == new PermanentStatBonuses(250, 250, 50, 50, 50, 50, 10, 10) &&
                after.Learning.BonusSp == role.Learning.BonusSp + 25 && after.Learning.BonusTp == role.Learning.BonusTp + 6,
                "服务重启和重选后保持全部永久来源，不重复授予或纠正到原余额");
        }
    }
}
