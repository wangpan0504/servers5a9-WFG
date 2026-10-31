using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class ExpertJobExtractionTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "expert-job-extraction"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var scripts = resources.ItemResources.ExpertJobs;
        var items = resources.ItemResources.Items;
        var cases = new List<(byte Job, ushort Id, ushort Slot, short Tool, int Product)>();
        foreach (var (job, tool) in new (byte Job, int Tool)[] { (1, 2600482), (2, 2600463), (4, 2600474) })
        {
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            var character = repository.Create(owner, new("ExtractTcp" + job, resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 50, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([new(0, Bag(3), InventoryRewardGrantService.Create(items.Get(10400))),
                    new(0, Bag(4), InventoryRewardGrantService.Create(items.Get(10400)))])
            });
            That(scripts.TryExtractResult(job, tool, items.Get(10400).Rarity, 0, out var output), "三类萃取使用真实基础产物");
            var supply = InventoryRewardGrantService.TryGrantBatch(character, [new(tool, 1), new(output.ItemId, 1)], items,
                resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces);
            character = repository.CommitInventory(owner, character, supply.Plan!);
            fixture.Sql($"INSERT INTO character_expert_job(character_id,account,job_type) VALUES({character.Id},'{fixture.Options.DevelopmentAccount}',{job})");
            cases.Add((job, character.Id, character.Slot, character.Inventory.Items.Single(item => item.Core.ItemId == tool).Position.Index, output.ItemId));
        }
        await fixture.StartAsync();
        foreach (var row in cases)
        {
            using (var peer = Open(fixture))
            {
                peer.Select(checked((byte)row.Slot), expertJobInfo: Info(row)); peer.Barrier();
                Extract(peer, row.Tool, 3);
                ReadSuccess(peer, row, 3);
                var current = Read(row.Slot);
                That(current.Inventory.Find(Bag(3)) is null && current.Inventory.Find(Bag(row.Tool)) is not null &&
                    current.Inventory.Items.Where(item => item.Core.ItemId == row.Product).Sum(item => item.Core.Count) > 1,
                    "416真实消费源装备、合并已有材料且无限工具保留");
                string before = Snapshot();
                Extract(peer, row.Tool, 3); peer.Expect(1, 416, [0, 19, 0]); peer.Barrier();
                That(Snapshot() == before, "空源重放不重复发材料");
                fixture.Sql("CREATE TRIGGER fail_extract_tcp BEFORE UPDATE OF expertness ON character_expert_job BEGIN SELECT RAISE(ABORT,'extract tcp failure'); END");
                Extract(peer, row.Tool, 4); peer.Closed();
                That(Snapshot() == before, "萃取熟练度末段失败回滚源装备、已有材料、工具和revision");
                fixture.Sql("DROP TRIGGER fail_extract_tcp");
            }
            using (var peer = Open(fixture))
            {
                peer.Select(checked((byte)row.Slot), expertJobInfo: Info(row)); peer.Barrier();
                That(Read(row.Slot).Inventory.Find(Bag(4)) is not null, "失败后重连保留原装备");
                Extract(peer, row.Tool, 4); ReadSuccess(peer, row, 4);
                That(Read(row.Slot).Inventory.Find(Bag(4)) is null, "重连后可正常重试原萃取");
            }
        }
        await fixture.StopAsync();

        DfoServer.Game.Characters.Character Read(ushort slot)
        {
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            return repository.LoadCharacter(owner, slot);
        }
        byte[] Info((byte Job, ushort Id, ushort Slot, short Tool, int Product) row) => Build(writer =>
        {
            var current = Read(row.Slot);
            writer.Write((byte)0); writer.Write(row.Job);
            if (row.Job == 1)
            {
                writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((uint)scripts.Level(1, current.ExpertJobExperience)); writer.Write(300u);
            }
            else { writer.Write((byte)1); writer.Write(row.Job == 2 ? 2600149 : 2600083); }
        });
        string Snapshot() => JsonSerializer.Serialize(new[] { fixture.Query("SELECT * FROM character_inventory_items ORDER BY item_uid"),
            fixture.Query("SELECT * FROM character_expert_job ORDER BY character_id"), fixture.Query("SELECT id,revision,gold FROM characters ORDER BY id") });
        void ReadSuccess(TcpPeer peer, (byte Job, ushort Id, ushort Slot, short Tool, int Product) row, ushort slot)
        {
            byte[] ack = peer.Expect(1, 416).Body;
            That(ack[0] == 1 && ack[1] == 0 && U16(ack, 2) == slot && ack[4] > 0 && ack.Length == 5 + 10 * ack[4] &&
                Enumerable.Range(0, ack[4]).Any(index => U32(ack, 7 + index * 10) == row.Product && U32(ack, 11 + index * 10) > 0),
                "416先发实际槽位与增量，已有堆叠不混入原数量");
            peer.Expect(0, 2); peer.Expect(0, 205, Info(row)); peer.Barrier();
        }
    }
    static InventorySlot Bag(int slot) => new(InventoryListType.Main, checked((short)slot));
    static void Extract(TcpPeer peer, short tool, ushort target) => peer.Send(416, Build(writer =>
    { writer.Write((byte)0); writer.Write(tool); writer.Write((byte)0); writer.Write(target); }));
}
