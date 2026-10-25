using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

// 0x362 公会勋章合成：材料取现场稀有度 0 勋章四枚，阶梯每枚 2500 万分率、封顶必成。
// 拒绝路径没有提交计划，服务端必须在失败 ACK 后按已提交快照整列重发 list38；
// 成功路径先清被复用槽再按提交结果下发差量。
internal static class GuildCompoundTcpTests
{
    const ushort Medal = 0x362;
    const ushort Gem = 0x345;
    const int RowWidth = 123;

    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        var items = resources.ItemResources.Items;
        var selected = Select(items);
        var inventory = new InventorySnapshot([
            .. selected.Materials.Select((id, index) => InventoryRewardGrantService.CreateItem(items.Get(id),
                new(InventoryListType.GuildMedal, checked((short)index)), InventoryRewardGrantService.Create(items.Get(id))))
        ]);

        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "guild-compound"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            repository.Create(owner, new("GuildCompTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 95,
                AcceptedStoryLevel = 1,
                Skills = resources.Characters.Get(0).Skills,
                Inventory = inventory
            });
            var socketedItems = inventory.Items.ToArray();
            socketedItems[0] = socketedItems[0] with
            {
                Core = socketedItems[0].Core with { GuardianGemKeys = 1 }
            };
            repository.Create(owner, new("GuildSocket", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 95,
                AcceptedStoryLevel = 1,
                Skills = resources.Characters.Get(0).Skills,
                Inventory = new([.. socketedItems])
            });
            int legendary = items.GuildCompoundCandidates(ItemKind.GuildMedal, 6).First();
            var legendaryTemplate = items.Get(legendary);
            repository.Create(owner, new("GuildLegend", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 95,
                AcceptedStoryLevel = 1,
                Skills = resources.Characters.Get(0).Skills,
                Inventory = new([.. Enumerable.Range(0, 2).Select(index =>
                    InventoryRewardGrantService.CreateItem(legendaryTemplate,
                        new(InventoryListType.GuildMedal, (short)index),
                        InventoryRewardGrantService.Create(legendaryTemplate) with { Attr = 12 }))])
            });
        }
        await fixture.StartAsync();

        string baseline = "";
        int product = 0;
        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Barrier();
            That(peer.SpecialContainers[3].Body[0] == 38, "选角后公会勋章容器整列恢复");
            // 选角入城会保存位置并推进 revision，基准取登录完成之后。
            baseline = Snapshot("GuildCompTcp");

            // 36B 畸形正文：解析失败也必须回失败码，并把客户端已乐观移除的材料整列重发。
            peer.Send(Medal, Request(4, 0, selected.Slots)[..^1]);
            peer.Expect(1, Medal, [0, 1, 0]);
            ExpectList(peer, selected.Materials, "畸形请求后整列重发材料");
            peer.Barrier();
            That(Snapshot("GuildCompTcp") == baseline, "畸形请求不消费材料或增加revision");

            foreach (ushort opcode in new[] { Medal, Gem })
            {
                var oversized = Request(4, 0, selected.Slots);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(oversized.AsSpan(13), uint.MaxValue);
                peer.Send(opcode, oversized);
                peer.Expect(1, opcode, [0, 1, 0]);
                ExpectList(peer, selected.Materials, "Oversized count restores materials without disconnecting");
                peer.Barrier();
                That(Snapshot("GuildCompTcp") == baseline, "Oversized count leaves assets and revision unchanged");
            }

            // 0x345 材料引用必须在守护珠页 49..97；引用勋章槽按页面拒绝且不扣任何材料。
            peer.Send(Gem, Request(2, 0, [0, 0]));
            peer.Expect(1, Gem, [0, 1, 0]);
            ExpectList(peer, selected.Materials, "守护珠引用勋章槽时整列重发");
            peer.Barrier();

            // 档位等于材料稀有度；稀有度 0 材料按 1 档提交后校验失败。
            peer.Send(Medal, Request(4, 1, selected.Slots));
            peer.Expect(1, Medal, [0, 1, 0]);
            ExpectList(peer, selected.Materials, "材料稀有度与档位不符时整列重发");
            peer.Barrier();
            That(Snapshot("GuildCompTcp") == baseline, "校验拒绝不扣材料或增加revision");

            // 4×2500 万分率封顶：四枚稀有度 0 勋章必成，产物复用清空后的 0 号槽。
            peer.Send(Medal, Request(4, 0, selected.Slots));
            var ack = peer.Expect(1, Medal).Body;
            That(ack.Length == 5 && ack[0] == 1, "合成成功ACK为u8标志与u32产物编号");
            product = checked((int)U32(ack, 1));
            That(selected.ProductPool.Contains(product), "ACK产物来自下一档同族候选池");
            var clear = peer.Expect(0, 14).Body;
            That(clear.Length == 3 + RowWidth && clear[0] == 38 && U16(clear, 1) == 1 &&
                U16(clear, 3) == 0 && U32(clear, 5) == uint.MaxValue,
                "产物复用已删除材料槽前先清除客户端旧对象");
            var delta = peer.Expect(0, 14).Body;
            That(delta.Length == 3 + 4 * RowWidth && delta[0] == 38 && U16(delta, 1) == 4 &&
                U16(delta, 3) == 0 && U32(delta, 5) == (uint)product, "差量在0号槽给出产物");
            for (int index = 1; index < 4; index++)
                That(U16(delta, 3 + index * RowWidth) == index && U32(delta, 5 + index * RowWidth) == uint.MaxValue,
                    "差量清空其余三个材料槽");
            peer.Barrier();
        }

        string committed = Snapshot("GuildCompTcp");
        That(committed != baseline, "成功提交增加revision");
        foreach (int material in selected.Materials)
            That(Count("GuildCompTcp", material) == 0, "成功提交删除全部材料");
        That(fixture.Query("SELECT s.slot_index FROM character_inventory_items i " +
            "JOIN character_inventory_slots s USING(item_uid) " +
            "WHERE s.list_type=38 AND json_extract(i.item_core,'$.ItemId')=$p0", product) is [[long slot]] && slot == 0,
            "产物在list38的0号槽持久化为一件");

        using (var peer = Open(fixture))
        {
            peer.Select();
            var list = peer.SpecialContainers[3].Body;
            That(list.Length == 3 + RowWidth && list[0] == 38 && U16(list, 1) == 1 &&
                U16(list, 3) == 0 && U32(list, 5) == (uint)product, "重连后公会勋章容器只剩产物");
            peer.Barrier();
        }

        using (var peer = Open(fixture))
        {
            peer.Select(slot: 1);
            peer.Barrier();
            string socketedBaseline = Snapshot("GuildSocket");
            foreach (int count in new[] { 2, 3, 4 })
            {
                peer.Send(Medal, Request(count, 0, selected.Slots.Take(count).ToArray()));
                peer.Expect(1, Medal, [0, 1, 0]);
                ExpectList(peer, selected.Materials, "Socketed medal rejection restores every material");
                peer.Barrier();
                That(Snapshot("GuildSocket") == socketedBaseline,
                    "Socketed medal rejection preserves all item metadata and revision");
            }
        }

        int legendaryProduct;
        using (var peer = Open(fixture))
        {
            peer.Select(slot: 2);
            peer.Barrier();
            peer.Send(Medal, Request(2, 6, [0, 1]));
            var ack = peer.Expect(1, Medal).Body;
            That(ack.Length == 5 && ack[0] == 1, "Reinforced legendary medals receive a product ACK");
            legendaryProduct = (int)U32(ack, 1);
            That(items.GuildCompoundCandidates(ItemKind.GuildMedal, 6).Contains(legendaryProduct),
                "Legendary recombination selects only a legendary medal");
            var legendaryDelta = peer.Expect(0, 14).Body;
            if (U16(legendaryDelta, 1) == 1 && U32(legendaryDelta, 5) == uint.MaxValue)
                legendaryDelta = peer.Expect(0, 14).Body;
            That(U16(legendaryDelta, 1) == 2 && U32(legendaryDelta, 5) == (uint)legendaryProduct &&
                U32(legendaryDelta, 5 + RowWidth) == uint.MaxValue,
                "Legendary delta grants one product and removes the second input");
            peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            peer.Select(slot: 2);
            var list = peer.SpecialContainers[3].Body;
            That(list.Length == 3 + RowWidth && U16(list, 1) == 1 &&
                U32(list, 5) == (uint)legendaryProduct,
                "Legendary recombination persists exactly one product across reconnect");
            peer.Barrier();
        }

        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] &&
            fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "公会合成拒绝与提交后数据库及外键完整");

        string Snapshot(string name) => JsonSerializer.Serialize(
            fixture.Query("SELECT c.revision,s.slot_index,i.item_core FROM characters c " +
                "LEFT JOIN character_inventory_slots s ON s.character_id=c.id " +
                "LEFT JOIN character_inventory_items i ON i.item_uid=s.item_uid " +
                "WHERE c.name=$p0 ORDER BY s.list_type,s.slot_index", name));
        long Count(string name, int itemId) => fixture.Query("""
            SELECT coalesce(sum(json_extract(i.item_core,'$.Value')),0)
            FROM character_inventory_items i
            JOIN character_inventory_slots s ON s.item_uid=i.item_uid
            JOIN characters c ON c.id=s.character_id
            WHERE c.name=$p0 AND json_extract(i.item_core,'$.ItemId')=$p1
            """, name, itemId) is [[long count]] ? count : 0;
    }

    static Selection Select(ItemCatalog items)
    {
        var materials = items.GuildCompoundCandidates(ItemKind.GuildMedal, 0).Take(4).ToArray();
        var products = items.GuildCompoundCandidates(ItemKind.GuildMedal, 1);
        if (materials.Length < 4 || products.Count == 0)
            throw new InvalidDataException("现场资源找不到四枚稀有度0勋章或稀有度1候选池");
        // 材料按 0..n-1 槽位铺开；请求里的引用是 list38 槽号，不是物品编号。
        return new(materials, [.. Enumerable.Range(0, materials.Length)], products);
    }

    static void ExpectList(TcpPeer peer, int[] materials, string reason)
    {
        // 整列恢复走 NOTI13（ITEM_LIST 全量），与选角时的特殊容器同形，不是 NOTI14 差量。
        var body = peer.Expect(0, 13).Body;
        That(body.Length == 3 + materials.Length * RowWidth && body[0] == 38 && U16(body, 1) == materials.Length,
            reason);
        for (int index = 0; index < materials.Length; index++)
            That(U16(body, 3 + index * RowWidth) == index &&
                U32(body, 5 + index * RowWidth) == (uint)materials[index], "整列重发行号与材料编号一致");
    }

    static byte[] Request(int count, uint grade, int[] slots) => Build(writer =>
    {
        writer.Write(new byte[13]);
        writer.Write((uint)count);
        writer.Write(grade);
        for (int index = 0; index < 4; index++)
            writer.Write(index < slots.Length ? (uint)slots[index] : uint.MaxValue);
    });

    sealed record Selection(int[] Materials, int[] Slots, IReadOnlyList<int> ProductPool);
}
