using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class TextureChangeTcpTests
{
    const int ClothId = 100100720;
    const int LightId = 100120622;
    const int PlateId = 100140452;
    const int ClothMaterialId = 3137;
    const int LightMaterialId = 3146;
    const ushort Opcode = 1546;

    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var catalog = resources.ItemResources.TextureChanges;
        That(catalog.TryGet(ClothId, out var group) && group.Material == 0 &&
            group.Variants[2] == LightId && group.Variants[4] == PlateId,
            "真实PVF把超界布甲、轻甲与板甲放在同一转换族");
        // 男女圣骑士的祝福加成本身写在目标物品数据里（板甲有 [buff option]/[skill data up]，
        // 布甲没有），所以换物品编号后由客户端按新物品加载，转出板甲时同样随旧物品移除。
        string plate = resources.Archive.ReadText("equipment/character/common/pants/plate/100140452.equ");
        string cloth = resources.Archive.ReadText("equipment/character/common/pants/cloth/100100720.equ");
        That(plate.Contains("[buff option]") && plate.Contains("[skill data up]") && plate.Contains("`[priest]`") &&
            plate.Contains("`[at priest]`") && !cloth.Contains("[buff option]"),
            "板甲专属祝福加成只存在于板甲物品数据，转换后随目标物品出现、转出后消失");

        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "texture-change"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var items = resources.ItemResources;
        var original = InventoryRewardGrantService.Create(items.Items.Get(ClothId)) with
        {
            Value = 999_999_998, Attr = 14, Durability = 24, EnchantCardId = 5001, EnchantUpgrade = 3,
            AmplifyType = 3, AmplifyValue = 1, EmblemSocketCount = 2, Emblem1 = 300, Emblem2 = 301,
            Random0 = new(70, 12, 13), TradeRestriction = 1
        };
        ItemCore Stack(int id, int count) => InventoryRewardGrantService.Create(items.Items.Get(id)) with { Value = count };
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var created = repository.Create(owner, new("TextureTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Skills = resources.Characters.Get(0).Skills, AcceptedStoryLevel = 1, DungeonState = CharacterDungeonState.NewOrdinary,
                Inventory = new([new(0, Bag(26), original),
                    new(0, Bag(121), Stack(LightMaterialId, 60)), new(0, Bag(122), Stack(LightMaterialId, 40)),
                    new(0, Bag(123), Stack(ClothMaterialId, 80))], Gold: 1_600_000)
            });
            repository.CommitInventory(owner, created, new(created.Inventory,
                created.Inventory with { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.LegendarySoul, 2) }, "texture_fixture"));
        }
        long uid = Convert.ToInt64(fixture.Query("SELECT item_uid FROM character_inventory_slots WHERE list_type=0 AND slot_index=26")[0][0]);
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Send(Opcode, [26, 0, 2]); peer.Expect(1, Opcode, [0, 19, 0]);
            peer.Select();
            string baseline = Snapshot();
            foreach (var (body, error) in new (byte[], byte)[]
            {
                ([26, 0, 0], 19), ([26, 0, 5], 19), ([27, 0, 2], 19), ([26, 0, 2, 0], 19), ([26, 0], 19)
            })
            {
                peer.Send(Opcode, body); peer.Expect(1, Opcode, [0, error, 0]);
            }
            peer.Barrier();
            That(Snapshot() == baseline, "同材质、越界材质、空槽与畸形长度全部无扣除");

            Apply(peer, [26, 0, 2], LightId, 1_100_000);
            var committed = Reload();
            var converted = committed.Inventory.Find(Bag(26))!;
            That(converted.Core.ItemId == LightId && converted.Uid == uid &&
                converted.Core with { ItemId = ClothId } == original,
                "布甲转轻甲只换物品编号，强化、增幅、附魔、徽章、词条、锁、耐久与品级逐字段落库");
            That(committed.Inventory.Find(Bag(121)) is null && committed.Inventory.Find(Bag(122))?.Core.Count == 20 &&
                committed.Inventory.Gold == 1_100_000 && committed.Inventory.AccountWallet.Get(CurrencyKind.LegendarySoul) == 1,
                "目标材质材料两堆共扣80、金币扣50万、传说灵魂扣1");

            baseline = Snapshot();
            peer.Send(Opcode, [26, 0, 2]); peer.Expect(1, Opcode, [0, 19, 0]); peer.Barrier();
            That(Snapshot() == baseline, "已是目标材质后重发拒绝，不再扣费");

            peer.Send(Opcode, [26, 0, 4]);
            peer.Expect(1, Opcode, [0, 17, 0]); peer.Barrier();
            That(Snapshot() == baseline, "缺少板甲材料时整笔拒绝，不产生半成品装备");

            fixture.Sql("CREATE TRIGGER fail_texture AFTER INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'texture rollback'); END");
            peer.Send(Opcode, [26, 0, 0]); peer.Closed();
            That(Snapshot() == baseline, "晚期写库失败整体回滚，不留下物品或费用半成品");
            fixture.Sql("DROP TRIGGER fail_texture");
        }
        using (var peer = Open(fixture))
        {
            peer.Select();
            var committed = Reload();
            That(committed.Inventory.Find(Bag(26))?.Core.ItemId == LightId &&
                committed.Inventory.Find(Bag(26))?.Core == SavedCore(),
                "重连保留已提交材质与完整核心，回滚结果不污染角色快照");
        }
        var plateCore = InventoryRewardGrantService.Create(items.Items.Get(PlateId)) with { Attr = 11, GenuineUpgrade = 3 };
        ushort targetLimit = items.Items.Get(ClothId).Durability;
        That(plateCore.Durability > targetLimit, "真实板甲转布甲模板的耐久上限下降");
        Character clampRole;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            clampRole = repository.Create(owner, new("TextureClamp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 95, Skills = resources.Characters.Get(0).Skills, AcceptedStoryLevel = 1,
                Inventory = new([new(0, Bag(26), plateCore), new(0, Bag(121), Stack(ClothMaterialId, 80))], Gold: 600_000)
            });
        using (var peer = Open(fixture))
        {
            peer.Select(clampRole.Slot); Apply(peer, [26, 0, 0], ClothId, 100_000);
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            var saved = repository.LoadCharacter(owner, clampRole.Slot);
            That(saved.Inventory.Find(Bag(26)) == clampRole.Inventory.Find(Bag(26))! with
                { Core = plateCore with { ItemId = ClothId, Durability = targetLimit } },
                "真实转甲提交后耐久钳制到新上限，UID及其它核心字段逐项保留");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(clampRole.Slot); peer.Barrier();
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            That(repository.LoadCharacter(owner, clampRole.Slot).Inventory.Find(Bag(26))!.Core.Durability == targetLimit,
                "重连仍恢复已提交的新模板耐久，不恢复超限旧值");
        }
        await fixture.StopAsync();

        Character Reload()
        {
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            return repository.List(owner).Single();
        }
        string Snapshot() => fixture.Snapshot() + fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT count(*) FROM inventory_audit_log") + fixture.Snapshot("SELECT count(*) FROM currency_audit_log");
        ItemCore SavedCore() => JsonSerializer.Deserialize<ItemCore>((string)fixture.Query(
            "SELECT item_core FROM character_inventory_items WHERE item_uid=$p0", uid).Single()[0]!)!;
        void Apply(TcpPeer peer, byte[] body, int expectedId, uint gold)
        {
            peer.Send(Opcode, body);
            var clear = peer.Expect(0, 14).Body;
            That(clear[0] == 0 && U16(clear, 1) == 1 && U32(Row(clear, 26), 2) == uint.MaxValue,
                "物品编号变化先清目标槽，客户端不保留旧材质外观");
            var delta = peer.Expect(0, 14).Body;
            That(U32(Row(delta, 26), 2) == expectedId && U32(Row(delta, 0), 6) == gold,
                "随后推送新物品编号与权威金币差量，最后才是成功回执");
            peer.Expect(1, Opcode, [1]);
            peer.Barrier();
        }
    }

    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static byte[] Row(byte[] body, short slot)
    {
        int count = U16(body, 1);
        That(body[0] == 0 && body.Length == 3 + 123 * count, "增量只含Main且每行固定123字节");
        int at = 3 + 123 * Enumerable.Range(0, count).Single(index => U16(body, 3 + index * 123) == slot);
        return body[at..(at + 123)];
    }
}
