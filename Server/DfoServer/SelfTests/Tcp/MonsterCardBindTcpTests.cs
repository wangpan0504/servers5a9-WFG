using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class MonsterCardBindTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        Inventory.MonsterCardBindTests.RunResources(resources.ItemResources);
        var selected = Select(resources);
        var items = resources.ItemResources.Items;
        var binderTemplate = items.Get(selected.BinderItemId);
        var cardTemplate = items.Get(selected.CardItemId);
        short binderSlot = ItemSlotBoundService.Range(binderTemplate.Kind, InventorySnapshot.Empty).Start;
        var cardRange = ItemSlotBoundService.Range(cardTemplate.Kind, InventorySnapshot.Empty);
        short card0 = cardRange.Start;
        short card1 = checked((short)(cardRange.Start + 1));
        var binder = InventoryRewardGrantService.CreateItem(binderTemplate,
            new(InventoryListType.Main, binderSlot), InventoryRewardGrantService.Create(binderTemplate, 1));
        var first = InventoryRewardGrantService.CreateItem(cardTemplate,
            new(InventoryListType.Main, card0), InventoryRewardGrantService.Create(cardTemplate, 1));
        var second = InventoryRewardGrantService.CreateItem(cardTemplate,
            new(InventoryListType.Main, card1), InventoryRewardGrantService.Create(cardTemplate, 1));
        byte[] request = Request(binderSlot, card0, card1);

        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "monster-card-bind"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            repository.Create(owner, new("CardBindTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Job = 0,
                Level = 95,
                AcceptedStoryLevel = 1,
                Skills = resources.Characters.Get(0).Skills,
                Inventory = new([binder, first, second])
            });
        }
        await fixture.StartAsync();

        string original = Snapshot("CardBindTcp");
        using (var peer = Open(fixture))
        {
            peer.Send(414, request);
            peer.Expect(1, 414, [0, 22, 0]);
            That(Snapshot("CardBindTcp") == original, "未选角CMD414失败不消费合成器或卡片，也不增加revision");
            peer.Select();
            peer.Barrier();
            string selectedSnapshot = Snapshot("CardBindTcp");
            peer.Send(414, request[..^1]);
            peer.Expect(1, 414, [0, 22, 0]);
            peer.Barrier();
            That(Snapshot("CardBindTcp") == selectedSnapshot, "畸形卡片合成请求失败后连接继续且资产不变");

            fixture.Sql("CREATE TRIGGER fail_monster_card_bind_late BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='monster_card_bind' BEGIN SELECT RAISE(ABORT,'card bind rollback'); END");
            peer.Send(414, request);
            peer.Closed();
            That(Snapshot("CardBindTcp") == selectedSnapshot, "卡片合成晚期审计失败回滚材料、奖励、revision与审计且不提前成功");
            fixture.Sql("DROP TRIGGER fail_monster_card_bind_late");
        }

        int committedRewardItemId;
        long committedBinderCount;
        long committedCardCount;
        long committedRewardCount;
        string committedSnapshot;
        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Barrier();
            string beforeCommit = Snapshot("CardBindTcp");
            long binderBefore = Count("CardBindTcp", selected.BinderItemId);
            long cardBefore = Count("CardBindTcp", selected.CardItemId);
            peer.Send(414, request);
            byte[] ack = peer.Expect(1, 414).Body;
            That(ack.Length >= 15 && ack[0] == 1 && U16(ack, 1) == (ushort)binderSlot && ack[3] == 2,
                "CMD414成功先返回带合成器槽和两张删除槽的ACK");
            That(U16(ack, 4) == (ushort)card0 && U16(ack, 6) == (ushort)card1,
                "ACK删除槽与请求卡片槽一致，不含合成器槽");
            byte rewardCount = ack[8];
            That(rewardCount >= 1 && ack.Length == 9 + rewardCount * 11, "奖励行按slot/itemId/value/extra展开");
            committedRewardItemId = checked((int)U32(ack, 11));
            int rewardValue = checked((int)U32(ack, 15));
            That(committedRewardItemId > 0 && rewardValue > 0 && ack[19] == 0,
                "普通合成奖励extra为0，不伪造升级卡核心");
            peer.Barrier();
            committedBinderCount = Count("CardBindTcp", selected.BinderItemId);
            committedCardCount = Count("CardBindTcp", selected.CardItemId);
            committedRewardCount = Count("CardBindTcp", committedRewardItemId);
            committedSnapshot = Snapshot("CardBindTcp");
            long expectedCards = committedRewardItemId == selected.CardItemId
                ? cardBefore - 2 + rewardValue : cardBefore - 2;
            That(committedBinderCount == binderBefore - 1 && committedCardCount == expectedCards &&
                committedRewardCount >= rewardValue && committedSnapshot != beforeCommit,
                "同一提交扣1个合成器和两张卡片并持久化一次随机奖励");
        }

        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Barrier();
            That(Snapshot("CardBindTcp") == committedSnapshot &&
                Count("CardBindTcp", selected.BinderItemId) == committedBinderCount &&
                Count("CardBindTcp", selected.CardItemId) == committedCardCount &&
                Count("CardBindTcp", committedRewardItemId) == committedRewardCount,
                "成功后断线重连恢复同一材料余量、随机奖励与revision，不重复合成");
        }

        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] &&
            fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "卡片合成回滚与重试后数据库及外键完整");

        string Snapshot(string name) => System.Text.Json.JsonSerializer.Serialize(
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

    static Selection Select(TcpTestResources resources)
    {
        var items = resources.ItemResources.Items;
        ItemTemplate binder;
        try { binder = items.Get(2600479); }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("现场缺少粗糙卡片合成器2600479");
        }
        if (binder.MonsterCardBindType != 0)
            throw new InvalidDataException("现场2600479不是bind type 0");
        var pool = resources.ItemResources.MonsterCardBinds.Pool(0, 0);
        int? cardId = null;
        foreach (var reward in pool.Rewards)
        {
            ItemTemplate template;
            try { template = items.Get(reward.ItemId); }
            catch (InvalidDataException) { continue; }
            if (template.IsMonsterCard && template.Rarity == 0 && template.StackLimit >= 1 &&
                ItemSlotBoundService.List(template.Kind) == InventoryListType.Main)
            {
                cardId = reward.ItemId;
                break;
            }
        }
        if (cardId is not { } selectedCard)
            throw new InvalidDataException("现场旧稀有度0奖池找不到可放入Main的怪物卡片");
        return new(binder.Id, selectedCard);
    }

    static byte[] Request(short synthesizer, short card0, short card1)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(synthesizer);
        writer.Write((byte)2);
        writer.Write(card0);
        writer.Write(card1);
        return stream.ToArray();
    }

    sealed record Selection(int BinderItemId, int CardItemId);
}
