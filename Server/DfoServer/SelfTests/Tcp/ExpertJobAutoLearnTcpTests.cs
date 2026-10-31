using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Persistence;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;
namespace DfoServer.Testing;
internal static class ExpertJobAutoLearnTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "expert-auto-learn"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var scripts = resources.ItemResources.ExpertJobs;
        var items = resources.ItemResources.Items;
        short scrollSlot = ItemSlotBoundService.Range(items.Get(10015129).Kind, InventorySnapshot.Empty).Start;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            repository.Create(owner, new("Qual898", resources.Towns.Room, resources.Towns.InitialReturn)
            { Level = 50, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([new(0, new(InventoryListType.Main, scrollSlot), InventoryRewardGrantService.Create(items.Get(10015129)))]) });
        int xp = scripts.ExperienceForLevel(1, 11);
        fixture.Sql($"INSERT INTO character_expert_job(character_id,account,job_type,expertness) SELECT id,account,1,{xp} FROM characters");
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            // 期望包独立编码，资格来自当前PVF六个等级门槛，不调用生产Builder。
            byte[] expected = Build(writer =>
            {
                writer.Write((byte)0); writer.Write((byte)1); writer.Write((byte)6);
                foreach (int id in new[] { 10015129, 10015130, 10015131, 10015132, 10015151, 10159564 }) writer.Write(id);
                writer.Write((byte)6); writer.Write(new byte[] { 0, 1, 2, 3, 4, 6 });
                writer.Write(11u); writer.Write(300u);
            });
            Console.WriteLine($"AUTO_LEARN XP={xp} EXPECTED_NOTI205={Convert.ToHexString(expected)}");
            peer.Select(expertJobInfo: expected);
            peer.Barrier();
            string before = JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_inventory_items"));
            peer.Send(44, Build(writer =>
            { writer.Write((ushort)scrollSlot); writer.Write((byte)0); writer.Write(uint.MaxValue); writer.Write(10015129); writer.Write(0u); }));
            peer.Expect(1, 44, Build(writer =>
            { writer.Write((byte)0); writer.Write((ushort)123); writer.Write((byte)0); writer.Write(uint.MaxValue); writer.Write(10015129); }));
            peer.Barrier();
            That(fixture.Query("SELECT learned_recipes FROM character_expert_job") is [["[]"]] &&
                JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_inventory_items")) == before,
                "自动资格卷轴重复学习拒绝，不扣物品、不补写学习存档");
        }
        await fixture.StopAsync();
    }
}
