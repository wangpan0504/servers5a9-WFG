using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using PvfLib;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class EmblemCompoundTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        var selected = Select(resources);
        var template = resources.ItemResources.Items.Get(selected.InputItemId);
        var source = InventoryRewardGrantService.CreateItem(template,
            new(InventoryListType.Main, 289), InventoryRewardGrantService.Create(template, 2));
        var full = new InventorySnapshot(
        [
            .. Enumerable.Range(289, 63).Select(slot => InventoryRewardGrantService.CreateItem(template,
                new(InventoryListType.Main, checked((short)slot)),
                InventoryRewardGrantService.Create(template, slot == 289 ? 3 : 1)))
        ]);
        byte[] request = Request(selected.InputItemId, 289);

        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "emblem-compound"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            repository.Create(owner, new("EmblemTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Job = selected.Job,
                Level = 95,
                AcceptedStoryLevel = 1,
                Skills = resources.Characters.Get(selected.Job).Skills,
                Inventory = new([source])
            });
            repository.Create(owner, new("EmFullTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Job = selected.Job,
                Level = 95,
                AcceptedStoryLevel = 1,
                Skills = resources.Characters.Get(selected.Job).Skills,
                Inventory = full
            });
        }
        await fixture.StartAsync();

        string original = Snapshot("EmblemTcp");
        using (var peer = Open(fixture))
        {
            peer.Send(256, request);
            peer.Expect(1, 256, [0, 22, 0]);
            That(Snapshot("EmblemTcp") == original, "未选角CMD256失败不消费徽章或增加revision");
            peer.Select();
            peer.Barrier();
            string selectedSnapshot = Snapshot("EmblemTcp");
            peer.Send(256, request[..^1]);
            peer.Expect(1, 256, [0, 22, 0]);
            peer.Barrier();
            That(Snapshot("EmblemTcp") == selectedSnapshot, "畸形徽章合成请求失败后连接继续且资产不变");

            fixture.Sql("CREATE TRIGGER fail_emblem_compound_late BEFORE INSERT ON inventory_audit_log " +
                "WHEN NEW.action_name='emblem_compound' BEGIN SELECT RAISE(ABORT,'emblem rollback'); END");
            peer.Send(256, request);
            peer.Closed();
            That(Snapshot("EmblemTcp") == selectedSnapshot, "徽章合成晚期审计失败回滚材料、奖励、revision与审计且不提前成功");
            fixture.Sql("DROP TRIGGER fail_emblem_compound_late");
        }

        using (var peer = Open(fixture))
        {
            peer.Select(1);
            peer.Barrier();
            string fullSnapshot = Snapshot("EmFullTcp");
            peer.Send(256, request);
            peer.Expect(1, 256, [0, 23, 0]);
            peer.Barrier();
            That(Snapshot("EmFullTcp") == fullSnapshot,
                "徽章页满且输入堆仍有剩余时返回容量错误，不扣材料或增加revision");
        }

        string committedSnapshot;
        int committedRewardItemId;
        long committedInputCount;
        long committedRewardCount;
        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Barrier();
            string beforeCommit = Snapshot("EmblemTcp");
            long inputBefore = Count("EmblemTcp", selected.InputItemId);
            peer.Send(256, request);
            byte[] ack = peer.Expect(1, 256).Body;
            That(ack.Length == 10 && ack[0] == 1 && ack[1] == 1,
                "CMD256成功先返回单行10字节ACK");
            committedRewardItemId = checked((int)U32(ack, 2));
            int rewardCount = checked((int)U32(ack, 6));
            byte[] clear = peer.Expect(0, 14).Body;
            int cleared = DeltaEntries(clear, InventoryListType.Main).Single();
            That(U16(clear, cleared) == 289 && U32(clear, cleared + 2) == uint.MaxValue,
                "奖励复用已删除材料槽时先静默清除客户端旧对象");
            byte[] delta = peer.Expect(0, 14).Body;
            int reward = DeltaEntries(delta, InventoryListType.Main)
                .Single(at => U32(delta, at + 2) == (uint)committedRewardItemId);
            That(rewardCount > 0 && U32(delta, reward + 6) >= (uint)rewardCount,
                "ACK奖励增量对应随后Main权威差量，ACK本身不伪造槽位");
            peer.Barrier();
            long expectedInput = inputBefore - 2;
            committedInputCount = Count("EmblemTcp", selected.InputItemId);
            committedRewardCount = Count("EmblemTcp", committedRewardItemId);
            committedSnapshot = Snapshot("EmblemTcp");
            That(committedInputCount == expectedInput && committedRewardCount >= rewardCount &&
                committedSnapshot != beforeCommit, "同一提交扣两枚输入并持久化一次随机奖励");
        }

        using (var peer = Open(fixture))
        {
            peer.Select();
            peer.Barrier();
            That(Snapshot("EmblemTcp") == committedSnapshot &&
                Count("EmblemTcp", selected.InputItemId) == committedInputCount &&
                Count("EmblemTcp", committedRewardItemId) == committedRewardCount,
                "成功后断线重连恢复同一材料余量、随机奖励与revision，不重复合成");
        }

        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] &&
            fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "徽章合成回滚与重试后数据库及外键完整");

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
        var itemResources = resources.ItemResources;
        var rewardItemIds = itemResources.EmblemCompounds.Entries
            .SelectMany(entry => entry.Value.Rewards)
            .Select(reward => reward.ItemId)
            .ToHashSet();
        var candidateIds = PvfScript.List(resources.Archive.ReadText("stackable/stackable.lst"))
            .Where(entry => entry.Value.Contains("emblem", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Key)
            .Where(itemId => !rewardItemIds.Contains(itemId))
            .Distinct().ToArray();
        foreach (var entry in itemResources.EmblemCompounds.Entries
            .Where(entry => entry.Key.Count == 2)
            .OrderBy(entry => entry.Key.Grade))
        {
            byte[] platinumJobs = entry.Value.Rewards.Where(reward => reward.Rarity == 6)
                .SelectMany(reward => reward.PlatinumJobs).Distinct().ToArray();
            if (entry.Value.Rewards.Any(reward => reward.Rarity == 6) && platinumJobs.Length == 0)
                continue;
            byte job = platinumJobs.FirstOrDefault();
            foreach (int itemId in candidateIds)
            {
                ItemTemplate template;
                try { template = itemResources.Items.Get(itemId); }
                catch (InvalidDataException) { continue; }
                if (template.Kind == ItemKind.Emblem && template.Grade == entry.Key.Grade &&
                    template.StackLimit >= 2)
                    return new(itemId, job);
            }
        }
        throw new InvalidDataException("现场资源找不到可用于两枚合成的徽章与职业");
    }

    static byte[] Request(int itemId, short slot)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)2);
        writer.Write(itemId);
        writer.Write(slot);
        writer.Write(itemId);
        writer.Write(slot);
        return stream.ToArray();
    }

    static IEnumerable<int> DeltaEntries(byte[] body, InventoryListType list)
    {
        int count = U16(body, 1);
        int width = list == InventoryListType.Avatar ? 165 : 123;
        That(body[0] == (byte)list && body.Length == 3 + count * width,
            "NOTI14保留容器编号、u16行数及Main条目宽度");
        return Enumerable.Range(0, count).Select(index => 3 + index * width);
    }

    sealed record Selection(int InputItemId, byte Job);
}
