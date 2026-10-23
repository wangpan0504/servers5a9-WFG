using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class ExpertJobLearningTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        ExpertJobLearningTests.Run(resources.Towns, resources.ItemResources, resources.ItemResources.ExpertJobs);
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "expert-job-learning"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var items = resources.ItemResources.Items;
        short start = ItemSlotBoundService.Range(items.Get(10015129).Kind, InventorySnapshot.Empty).Start;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            repository.Create(owner, new("LearnTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 50, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([new(0, Slot(0), InventoryRewardGrantService.Create(items.Get(10015129))),
                    new(0, Slot(1), InventoryRewardGrantService.Create(items.Get(10015129))),
                    new(0, Slot(2), InventoryRewardGrantService.Create(items.Get(2600141))),
                    new(0, Slot(3), InventoryRewardGrantService.Create(items.Get(10015130)))], Gold: 999)
            });
        }
        fixture.Sql("INSERT INTO character_expert_job(character_id,account,job_type,expertness) SELECT id,account,1,0 FROM characters");
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            string before = Snapshot();
            Use(peer, 0, 10015129); Ack(peer, 0, 10015129, false);
            That(Snapshot() == before, "未选角完整拒绝学习且不扣图");
            peer.Select(expertJobInfo: Info([], []));
            before = Snapshot();
            Use(peer, 3, 10015130); Ack(peer, 3, 10015130, false); peer.Barrier();
            That(Snapshot() == before, "真实TCP拒绝未达到副职业等级的设计图");
            fixture.Sql("CREATE TRIGGER fail_learning BEFORE UPDATE OF learned_recipes ON character_expert_job BEGIN SELECT RAISE(ABORT,'learning failure'); END");
            Use(peer, 0, 10015129); peer.Closed();
            That(Snapshot() == before, "TCP末段资格写失败回滚源图与revision，不先发成功ACK");
            fixture.Sql("DROP TRIGGER fail_learning");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(expertJobInfo: Info([], []));
            Use(peer, 0, 10015129); Ack(peer, 0, 10015129, true);
            peer.Expect(0, 205, Info([10015129], [0])); peer.Barrier();
            string before = Snapshot();
            Use(peer, 1, 10015129); Ack(peer, 1, 10015129, false); peer.Barrier();
            That(Snapshot() == before, "重复学习不消费第二张图、不追加资格或再次增版本");
            Use(peer, 2, 2600141); Ack(peer, 2, 2600141, true);
            peer.Expect(0, 205, Info([2600141, 10015129], [])); peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            peer.Select(expertJobInfo: Info([2600141, 10015129], [0])); peer.Barrier();
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            var restored = repository.LoadCharacter(owner, 0);
            That(restored.ExpertJobRecipes.SetEquals([2600141, 10015129]) && restored.Inventory.Find(Slot(0)) is null &&
                restored.Inventory.Find(Slot(2)) is null && restored.Inventory.Find(Slot(1)) is not null &&
                restored.Inventory.Gold == 999 && restored.ExpertJobExperience == 0,
                "重连投影完整资格，普通后续205不重复追加稀有度，图和资格同事务持久化");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "学习TCP数据库结构和外键完整");

        InventorySlot Slot(int offset) => new(InventoryListType.Main, (short)(start + offset));
        void Use(TcpPeer peer, int offset, int id) => peer.Send(44, Build(writer =>
        { writer.Write((ushort)(start + offset)); writer.Write((byte)0); writer.Write(uint.MaxValue); writer.Write(id); writer.Write(0u); }));
        void Ack(TcpPeer peer, int offset, int id, bool accepted) => peer.Expect(1, 44, Build(writer =>
        { writer.Write((byte)(accepted ? 1 : 0)); writer.Write((ushort)(accepted ? start + offset : 123)); writer.Write((byte)0); writer.Write(uint.MaxValue); writer.Write(id); }));
        string Snapshot() => JsonSerializer.Serialize(new[] { fixture.Query("SELECT * FROM character_inventory_items ORDER BY item_uid"),
            fixture.Query("SELECT * FROM character_expert_job"), fixture.Query("SELECT id,revision FROM characters") });
    }

    // 独立按已核实的客户端读序编码，不调用被测生产builder。
    static byte[] Info(int[] recipes, byte[] rarities) => Build(writer =>
    {
        writer.Write((byte)0); writer.Write((byte)1); writer.Write((byte)recipes.Length);
        foreach (int id in recipes) writer.Write(id);
        writer.Write((byte)rarities.Length); writer.Write(rarities); writer.Write(1u); writer.Write(300u);
    });
}
