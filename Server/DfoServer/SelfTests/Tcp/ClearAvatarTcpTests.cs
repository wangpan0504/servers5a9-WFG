using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class ClearAvatarTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        ClearAvatarTests.RunResources(resources.Characters, resources.ItemResources.Items);
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "clear-avatar"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        int originalCoat = resources.Characters.Get(0).DefaultAvatarEquipment[3];
        var cases = new List<(string Name, short Slot, int Clone, int Original, byte Grow)>
        { ("ClearCoat", 3, 101500878, originalCoat, 0),
            ("ClearEmpty", 3, 101500878, 0, 0),
            ("ClearAura", 9, 113590006, 101590068, 0),
            ("ClearAwaken", 3, 101500878, 0, 0x11),
            ("ClearSecond", 3, 101500878, 0, 0x21) };
        if (resources.ItemResources.Items.ContainsEquipment(401600057) &&
            resources.ItemResources.Items.Get(401600057) is { CloneAvatar: true, BorrowAvatarAppearance: true, EquipmentSlot: 10 })
            cases.Add(("ClearWeapon", 10, 401600057, 0, 0));
        else
            Console.WriteLine("SKIP 空武器装扮TCP：当前PVF未声明测试克隆武器装扮；全槽规则由核心自测覆盖");
        var roles = new List<Character>();
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            foreach (var row in cases)
            {
                var items = new List<InventoryItem> { Item(0, new(InventoryListType.Avatar, 0), row.Clone) };
                if (row.Original != 0) items.Add(Item(0, new(InventoryListType.Equipment, row.Slot), row.Original));
                if (row.Slot == 10)
                {
                    var weapon = resources.Characters.Get(0).Equipment.Single(item => item.Slot == 12).Template;
                    items.Add(InventoryRewardGrantService.CreateItem(weapon, new(InventoryListType.Equipment, 12),
                        InventoryRewardGrantService.Create(weapon) with { SealFlag = 0 }));
                }
                roles.Add(repository.Create(owner, new(row.Name, resources.Towns.Room, resources.Towns.InitialReturn)
                    { Level = 95, GrowType = row.Grow, AcceptedStoryLevel = 1, Inventory = new([.. items]) }));
            }
        await fixture.StartAsync();
        for (int index = 0; index < cases.Count; index++)
        {
            var row = cases[index];
            int appearance = row.Original;
            ushort color = row.Original == 0 ? (ushort)0 : (ushort)7;
            using (var peer = Open(fixture))
            {
                peer.Select(roles[index].Slot); peer.Barrier();
                peer.Send(19, Move(row.Clone, row.Slot, row.Original));
                peer.Expect(1, 19, Build(w => { w.Write((byte)1); w.Write((byte)1); w.Write((short)0);
                    w.Write(1); w.Write((byte)3); w.Write(row.Slot); }));
                var cleared = peer.Expect(0, 1681).Body;
                if (row.Original != 0)
                {
                    var returned = peer.Expect(0, 1681).Body;
                    That(U32(cleared, 5) == uint.MaxValue && U32(returned, 5) == row.Original,
                        "被覆盖时装退回原包裹格，先清旧克隆图标再完整回填，不能丢失原物品");
                    peer.Expect(0, 1449, Sources(row.Slot, 0));
                }
                // 换装改变1号橱窗内容：投影在Actor前按槽补发0x063F，客户端装扮栏缓存跟随新穿戴。
                That(peer.Expect(0, 0x063F).Body[0] == 0, "换装后补发1号橱窗单条，记录槽位不变");
                var actor = peer.Expect(0, 2).Body;
                var player = peer.Expect(0, 2).Body;
                Verify(actor, player, row.Name, row.Slot, row.Clone, appearance, color);
                peer.Barrier();
            }
            using (var peer = Open(fixture))
            {
                var packets = peer.Select(roles[index].Slot);
                Verify(packets[2].Body, packets[3].Body, row.Name, row.Slot, row.Clone, appearance, color);
                Bytes(peer.CloneAvatarSources, Sources(row.Slot, row.Original == 0 ? -1 : 0),
                    "重登来源表使用原件当前包裹格，无原件的默认外观不伪造来源");
                peer.Barrier();
                if (index == 0)
                {
                    peer.Send(19, Build(w =>
                    {
                        w.Write((byte)1); w.Write((short)0); w.Write(row.Original); w.Write(1);
                        w.Write((byte)1); w.Write((short)2); w.Write(0); w.Write(0); w.Write(-1); w.Write((ushort)0);
                    }));
                    peer.Expect(1, 19, Build(w => { w.Write((byte)1); w.Write((byte)1); w.Write((short)0);
                        w.Write(1); w.Write((byte)1); w.Write((short)2); }));
                    var delta = peer.Expect(0, 14).Body;
                    That(delta[0] == 1 && U16(delta, 1) == 2 && U32(delta, 5) == uint.MaxValue && U32(delta, 170) == row.Original,
                        "原上衣移格仍同步完整原件与清槽，不只更新克隆标记");
                    peer.Expect(0, 1449, Sources(row.Slot, 2));
                    peer.Barrier();
                }
            }
            var detail = JsonSerializer.Deserialize<AvatarDetail>((string)fixture.Query("""
                SELECT a.detail FROM inventory_avatar_details a JOIN character_inventory_slots s ON s.item_uid=a.item_uid
                WHERE s.character_id=$p0 AND s.list_type=3 AND s.slot_index=$p1
                """, roles[index].Id, row.Slot).Single().Single()!)!;
            That(detail.ClearAvatarId == appearance && detail.Color1 == color && detail.Color2 == (color == 0 ? 0 : 8),
                "CMD19提交后的来源、染色和重登wire一致，空槽及觉醒空槽不保存固定默认EQU");
            if (row.Original == 0)
                That(detail.ClearAvatarSourceUid == 0 && fixture.Query(
                    "SELECT COUNT(*) FROM character_inventory_slots WHERE character_id=$p0 AND list_type IN (1,3)",
                    roles[index].Id).Single().Single() is long count && count == (row.Slot == 10 ? 2 : 1),
                    "空槽克隆只保存真实实例，不发放或存储基础外观替代物品");
            if (row.Original != 0)
                That(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE character_id=$p0 AND list_type=1 AND slot_index=$p1",
                    roles[index].Id, index == 0 ? 2 : 0).Single().Single() is long uid && uid == detail.ClearAvatarSourceUid,
                    "来源绑定退回的真实原件UID，来源移格不修改克隆详情或角色revision之外的资产");
        }
        await fixture.StopAsync(); await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(roles[0].Slot);
            Bytes(peer.CloneAvatarSources, Sources(3, 2), "服务实例重建后仍绑定已移格原件");
            peer.Barrier();
            peer.Send(19, Build(w =>
            {
                w.Write((byte)3); w.Write((short)3); w.Write(cases[0].Clone); w.Write(1);
                w.Write((byte)1); w.Write((short)1); w.Write(0); w.Write(0); w.Write(-1); w.Write((ushort)0);
            }));
            peer.Expect(1, 19, Build(w => { w.Write((byte)1); w.Write((byte)3); w.Write((short)3);
                w.Write(1); w.Write((byte)1); w.Write((short)1); }));
            That(U32(peer.Expect(0, 1681).Body, 5) == uint.MaxValue && U32(peer.Expect(0, 1681).Body, 5) == cases[0].Clone,
                "脱下装扮先清ACK图标再完整回填，保留图标effect但不发送获得播报14");
            peer.Expect(0, 1449, Sources(3, -1));
            That(peer.Expect(0, 0x063F).Body[0] == 0, "脱下装扮同样补发1号橱窗单条");
            peer.Expect(0, 2); peer.Expect(0, 2);
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            var packets = peer.Select(roles[1].Slot);
            Verify(packets[2].Body, packets[3].Body, cases[1].Name, 3, 101500878, 0, 0);
            peer.Barrier();
        }

        static InventoryItem Item(long uid, InventorySlot position, int id) =>
            new(uid, position, new(id, ItemKind.Avatar, checked((int)uid))) { Avatar = new() { Color1 = 7, Color2 = 8 } };
        static byte[] Move(int id, short slot, int original) => Build(w =>
        {
            w.Write((byte)1); w.Write((short)0); w.Write(id); w.Write(1); w.Write((byte)3); w.Write(slot);
            w.Write(original); w.Write(0); w.Write(-1); w.Write((ushort)0);
        });
        static byte[] Sources(short linkedSlot, int bagSlot) => Build(w =>
        {
            w.Write((byte)11);
            for (byte slot = 0; slot < 11; slot++)
            {
                w.Write(slot);
                w.Write(slot == linkedSlot && bagSlot >= 0 ? bagSlot + 12 : 0);
            }
        });
        static void Verify(byte[] actor, byte[] player, string name, short slot, int physical, int appearance, ushort color)
        {
            int at = 134 + NameWidth(name);
            That(actor[at - 1] == (slot == 10 ? 2 : 1) && actor[at] == slot && U32(actor, at + 1) == physical &&
                U32(actor, at + 16) == appearance && player[126] == (slot == 10 ? 2 : 1) && player[127] == slot &&
                U32(player, 128) == physical && U32(player, 141) == appearance,
                "Actor及Player保持真实克隆模板，只传真实覆盖来源，空槽由客户端选择基础外观");
            if (slot == 10)
                That(actor[at + 25] == 12 && U32(actor, at + 26) > 0 && U32(actor, at + 41) == 0,
                    "空武器克隆与实际武器独立传输，二者均不伪造克隆来源");
            That(U32(actor, at + 5) == 4 && U16(actor, at + 9) == color && U16(actor, at + 11) == (color == 0 ? 0 : 8),
                "Actor借用染色与关联编号来自同一已提交详情");
            int sockets = slot == 9 ? 187 : 153;
            That(U32(player, sockets) == 30 && U32(player, sockets + 34) == 4 &&
                U16(player, sockets + 38) == color && U16(player, sockets + 40) == (color == 0 ? 0 : 8),
                "Player装扮孔位和染色保持完整；9槽的前置RAW30不挤占实例扩展");
            if (slot == 9)
                That(U32(actor, at + 20) == 30 && U32(player, 145) == 30,
                    "关联光环在附魔前携带额外RAW30，不能沿普通装扮宽度截断");
        }
    }
}
