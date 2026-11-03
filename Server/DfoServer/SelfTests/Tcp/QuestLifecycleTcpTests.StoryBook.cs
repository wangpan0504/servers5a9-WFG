using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class QuestLifecycleTcpTests
{
    static async Task RunStoryBookAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        var items = resources.ItemResources.Items;
        That(resources.Quests.Get(3108) is { Condition: "[play only]", Definition.AccountQuest: true } &&
            resources.Quests.StoryBookQuestIds.Contains(3108) && resources.Quests.StoryBookQuestIds.Contains(3109) &&
            items.ActionConsumable(10149048) is { Label: "[story book]", Parameters: [3108] },
            "TCP使用当前PVF故事簿与真实账号任务，不用合成成功替代资源资格");
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "story-book"), resources: resources);
        var repository = new CharacterRepository(fixture.Database, resources.Quests);
        Character created, sibling;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("StoryBook", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get);
            created = repository.Create(owner, candidate with { Inventory = new([new(0, new(InventoryListType.Main, 99),
                InventoryRewardGrantService.Create(items.Get(10149048), utcNow: DateTimeOffset.UtcNow))]) });
            sibling = repository.Create(owner, CharacterCreation.Prepare("StorySibling", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get));
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot);
            That(peer.CompletedStoryBooks.Length == 0, "未完成任务不因拥有道具或登录而解锁故事");
            string before = State();
            peer.Send(34, [34, 0, 36, 12, 255, 255, 1, 0, 255, 255]); peer.Expect(1, 34, [0, 19, 0]); peer.Barrier();
            That(before == State() && peer.CompletedStoryBooks.Length == 0, "未接取任务的失败交付不能创建故事完成或解锁通知");
            Use(peer, 3109);
            peer.Expect(1, 515, [0, 0, 0, 99, 0, 0, 139]); peer.Barrier();
            That(before == State(), "任务号与书不符时拒绝且源数量、revision及活动任务不变");
            Use(peer, 3108);
            peer.Expect(1, 515, [1, 99, 0, 0, 139, 0, 0, 0]);
            peer.Expect(0, 21); peer.Expect(0, 356); peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0", peer.Id)
                    is [[0L]] &&
                fixture.Query("SELECT count(*) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=10149048", peer.Id)
                    is [[0L]], "515原生开启已扣一张；确认前不发31成功、不占活动槽、不重复用14扣源");
            string accepted = State();
            Use(peer, 3108); peer.Expect(1, 515, [0, 0, 0, 99, 0, 0, 139]); peer.Barrier();
            That(accepted == State(), "重复515不重扣，原生取消或暂不确认不产生接取");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot);
            That(List(peer.AvailableQuests, 1).Contains(3108), "取消后重新登录仍从既有任务候选恢复，不丢失故事入口或补发源道具");
            peer.Send(31, [31, 0, 36, 12]); peer.Expect(1, 31, [1, 36, 12, 0, 0, 0, 0, 0]);
            peer.Expect(0, 21); peer.Expect(0, 356); peer.Barrier();
            That(fixture.Query("SELECT quest_id,condition_state,length(acceptance_id) FROM character_active_main_quests WHERE character_id=$p0", peer.Id)
                is [[3108L, 0L, 32L]], "原生确认31创建唯一接取身份并按49保存初值");
            peer.Send(33, [33, 0, 36, 12, 0, 0]); peer.Expect(1, 33, [1, 36, 12, 0, 0, 0, 0]); peer.Barrier();
            peer.Send(34, [34, 0, 36, 12, 255, 255, 1, 0, 255, 255]);
            That(peer.Expect(1, 34).Body[0] == 1, "故事簿接取后沿原类型49交付，不伪造任务完成");
            bool bookUpdated = false;
            DrainNotifications(peer, packet =>
            {
                if (packet.Opcode == 703) bookUpdated = true;
            });
            That(fixture.Query("SELECT count(*) FROM account_completed_quests WHERE account=$p0 AND quest_id=3108", fixture.Options.DevelopmentAccount)
                is [[1L]] && bookUpdated && peer.CompletedStoryBooks.SequenceEqual([3108]),
                "故事完成同次发布703解锁当前章节，不能只写完成表或一并解锁3109后续章节");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot);
            That(peer.CompletedStoryBooks.SequenceEqual([3108]), "重登从既有完成事实恢复703，不需要重扣书或重做任务");
            Use(peer, 3108);
            peer.Expect(1, 515, [0, 0, 0, 99, 0, 0, 139]); peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_quest_lifecycle_receipts WHERE character_id=$p0 AND quest_id=3108 AND action='Accept'", peer.Id)
                is [[1L]], "重新登录不重复接取已完成故事或恢复源道具");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(sibling.Slot);
            That(peer.CompletedStoryBooks.SequenceEqual([3108]) && fixture.Query(
                "SELECT count(*) FROM character_completed_quests WHERE character_id=$p0 AND quest_id=3108", peer.Id) is [[0L]],
                "同账号其它角色从账号完成记录恢复章节，不伪造该角色的完成、来源或重复奖励");
        }
        string State() => fixture.Snapshot("SELECT id,revision FROM characters") +
            fixture.Snapshot("SELECT * FROM character_inventory_items") + fixture.Snapshot("SELECT * FROM character_active_main_quests") +
            fixture.Snapshot("SELECT * FROM character_quest_lifecycle_receipts");
        static void Use(TcpPeer peer, uint questId) => peer.Send(515, Build(writer =>
        {
            writer.Write((ushort)99); writer.Write((byte)0); writer.Write(0u);
            writer.Write(139u); writer.Write(questId); writer.Write(0u);
        }));
    }
}
