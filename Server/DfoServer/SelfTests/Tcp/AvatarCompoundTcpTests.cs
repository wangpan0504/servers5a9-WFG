using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class AvatarCompoundTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        var selected = Select(resources.ItemResources);
        var items = resources.ItemResources.Items;
        var sourceTemplate = items.Get(selected.TargetItemId);
        var materialTemplate = items.Get(selected.MaterialItemId);
        var empty = InventorySnapshot.Empty;
        short materialSlot = ItemSlotBoundService.Range(materialTemplate.Kind, empty).Start;
        var source0 = InventoryRewardGrantService.CreateItem(sourceTemplate, new(InventoryListType.Avatar, 10),
            InventoryRewardGrantService.Create(sourceTemplate) with { Durability = selected.Ability });
        var source1 = InventoryRewardGrantService.CreateItem(sourceTemplate, new(InventoryListType.Avatar, 11),
            InventoryRewardGrantService.Create(sourceTemplate) with { Durability = selected.Ability });
        var material = InventoryRewardGrantService.CreateItem(materialTemplate,
            new(InventoryListType.Main, materialSlot), InventoryRewardGrantService.Create(materialTemplate, 2));
        var request = new AvatarCompoundRequest(materialSlot, 10, selected.TargetItemId, 11, selected.TargetItemId,
            selected.TargetItemId, selected.Ability, 0, 1);
        byte[] body = Body(request);

        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "avatar-compound"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repository.Create(owner, new("CompoundTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Job = 0,
                Level = 95,
                AcceptedStoryLevel = 1,
                Skills = resources.Characters.Get(0).Skills,
                Inventory = new([source0, source1, material])
            });
        string original = Snapshot();
        await fixture.StartAsync();

        using (var peer = Open(fixture))
        {
            peer.Send(99, body);
            peer.Expect(1, 99, AvatarCompoundPackets.Failure());
            That(original == Snapshot(), "未选角合成失败不消费资产或增加revision");
            peer.Select();
            peer.Barrier();
            string before = Snapshot();
            peer.Send(99, body[..^1]);
            peer.Expect(1, 99, AvatarCompoundPackets.Failure());
            peer.Barrier();
            That(before == Snapshot(), "畸形22字节请求拒绝且连接继续可用");

            fixture.Sql("CREATE TRIGGER fail_avatar_compound_late BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='avatar_compound' BEGIN SELECT RAISE(ABORT,'compound rollback'); END");
            peer.Send(99, body);
            peer.Closed();
            That(before == Snapshot(), "合成晚期审计失败回滚材料、双源、产出详情、revision及审计且不提前成功");
            fixture.Sql("DROP TRIGGER fail_avatar_compound_late");
        }

        int[] outputIds;
        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Barrier();
            string before = Snapshot();
            peer.Send(99, body);
            var acknowledgement = peer.Expect(1, 99).Body;
            That(acknowledgement.Length == AvatarCompoundPackets.SuccessSize && acknowledgement[0] == 1 &&
                acknowledgement[1] == 3 && acknowledgement[2] == 1 && I16(acknowledgement, 3) == 10 &&
                acknowledgement[9] == 1 && I16(acknowledgement, 10) == 11 &&
                acknowledgement[16] == 0 && I16(acknowledgement, 17) == materialSlot,
                "真实分派先返回固定131字节ACK，三条扣除记录与请求槽一致且无重复NOTI14");
            outputIds = new[] { 23, 77 }.Where(at => I16(acknowledgement, at) >= 0)
                .Select(at => I32(acknowledgement, at + 2)).ToArray();
            That(outputIds.Length is 1 or 2 && outputIds[0] == selected.TargetItemId &&
                U16(acknowledgement, 33) == selected.Ability && I32(acknowledgement, 35) == 30 &&
                I32(acknowledgement, 69) == 4, "普通目标必定产出，随机稀有仅作为固定第二项并携带完整详情");
            peer.Barrier();
            That(MaterialCount() == 1 && AvatarIds().Order().SequenceEqual(outputIds.Order()) &&
                AvatarDetails() == outputIds.Length && before != Snapshot(), "一次提交原子扣除材料与双源并持久化一至两个新时装详情");

            string committed = Snapshot();
            peer.Send(99, body);
            peer.Expect(1, 99, AvatarCompoundPackets.Failure());
            peer.Barrier();
            That(committed == Snapshot(), "重复旧请求因源槽已消费而失败，不重复扣材料或发产出");
        }

        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Barrier();
            byte[] avatarList = peer.SpecialContainers.Single(packet =>
                packet is { Kind: 0, Opcode: 13 } && packet.Body[0] == (byte)InventoryListType.Avatar).Body;
            int count = U16(avatarList, 3);
            var restored = Enumerable.Range(0, count).Select(index => I32(avatarList, 7 + index * 165)).Order().ToArray();
            That(restored.SequenceEqual(outputIds.Order()), "重连完整时装列表恢复合成产出，不依赖结果窗口临时对象");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] &&
            fixture.Query("PRAGMA foreign_key_check").Length == 0, "合成后无孤立核心、时装详情或外键损坏");

        int MaterialCount() => fixture.Query("""
            SELECT json_extract(i.item_core,'$.Value') FROM character_inventory_items i
            JOIN character_inventory_slots s USING(item_uid)
            WHERE s.list_type=0 AND s.slot_index=$p0
            """, materialSlot) is [[long count]] ? checked((int)count) : 0;
        int[] AvatarIds() => fixture.Query("""
            SELECT json_extract(i.item_core,'$.ItemId') FROM character_inventory_items i
            JOIN character_inventory_slots s USING(item_uid)
            WHERE s.list_type=1 ORDER BY s.slot_index
            """).Select(row => Convert.ToInt32(row[0], System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        int AvatarDetails() => Convert.ToInt32(fixture.Query("SELECT count(*) FROM inventory_avatar_details")[0][0],
            System.Globalization.CultureInfo.InvariantCulture);
        string Snapshot() => fixture.Snapshot("SELECT id,revision,gold FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_avatar_details ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM sqlite_sequence ORDER BY name");
    }

    static CompoundCase Select(ItemResources resources)
    {
        var config = resources.AvatarCompounds.ForCharacterJob(0);
        foreach (var pool in config.Parts.Values)
        {
            var rareAbilities = new HashSet<byte>?[pool.RarePool.Length];
            bool validRarePool = true;
            for (int index = 0; index < pool.RarePool.Length; index++)
            {
                try
                {
                    var template = resources.Items.Get(pool.RarePool[index].ItemId);
                    var definition = resources.AvatarPackages.GetAvatar(template.Id);
                    if (template.Kind != ItemKind.Avatar || AvatarCompoundCatalog.Part(template.EquipmentSlot) != pool.Part ||
                        (template.Jobs & 1) == 0 || definition.PendingRule is not null || definition.Abilities.IsEmpty)
                    {
                        validRarePool = false;
                        break;
                    }
                    rareAbilities[index] = definition.Abilities.ToHashSet();
                }
                catch (Exception error) when (error is InvalidDataException or KeyNotFoundException)
                {
                    validRarePool = false;
                    break;
                }
            }
            if (!validRarePool) continue;
            foreach (var target in pool.TargetPool)
            {
                try
                {
                    var template = resources.Items.Get(target.ItemId);
                    var definition = resources.AvatarPackages.GetAvatar(target.ItemId);
                    if (template.Kind != ItemKind.Avatar || AvatarCompoundCatalog.Part(template.EquipmentSlot) != pool.Part ||
                        template.Grade != config.Grade && template.Grade != config.UpperGrade ||
                        (template.Jobs & 1) == 0 || definition.PendingRule is not null)
                        continue;
                    var abilities = definition.Abilities.ToHashSet();
                    foreach (var values in rareAbilities)
                        abilities.IntersectWith(values!);
                    if (abilities.Count != 0)
                        return new(target.ItemId, config.MaterialItemId, abilities.Min());
                }
                catch (Exception error) when (error is InvalidDataException or KeyNotFoundException) { }
            }
        }
        throw new InvalidDataException("现场装扮合成目录没有职业0可完整验收的目标、稀有池及共用能力");
    }

    static byte[] Body(AvatarCompoundRequest request)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(request.MaterialSlot);
        writer.Write(request.SourceSlot0);
        writer.Write(request.SourceItemId0);
        writer.Write(request.SourceSlot1);
        writer.Write(request.SourceItemId1);
        writer.Write(request.TargetItemId);
        writer.Write(request.Ability);
        writer.Write(request.TargetIsRare);
        writer.Write(request.MaterialType);
        return stream.ToArray();
    }

    static short I16(byte[] body, int at) => BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(at));
    static ushort U16(byte[] body, int at) => BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(at));
    static int I32(byte[] body, int at) => BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(at));
    sealed record CompoundCase(int TargetItemId, int MaterialItemId, byte Ability);
}
