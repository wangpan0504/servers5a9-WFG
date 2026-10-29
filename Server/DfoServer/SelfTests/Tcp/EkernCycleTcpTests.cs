using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Towns;
using System.Buffers.Binary;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class EkernCycleTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        DungeonSettlementTests.RunEkernResources(resources.Archive);
        await using var fixture = new TcpTestFixture(source,Path.Combine(root,"ekern-cycle"),resources:resources);
        var repository = new CharacterRepository(fixture.Database);
        var area = resources.Towns.Areas.Values.First(area => area.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps.TryGetValue(world,out var map) && map.Path == "worldmap/legend_universe.wdm");
        var key = resources.Towns.Areas.Single(pair => ReferenceEquals(pair.Value,area)).Key;
        var done = area.Quests.Concat(area.AnyQuests).Concat(area.Default.DungeonGates.SelectMany(gate => gate.Quests)).Distinct().ToArray();
        var bounds = area.Map(done.ToHashSet()).DungeonGates.First(gate => gate.Conditions.Length == 0 && gate.Quests.All(done.Contains)).Bounds;
        var position = new Location(key.Item1,key.Item2,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        Character actor;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,() => { }))
        {
            var seed = CharacterCreation.Prepare("EkernWire",resources.Characters.Get(0),position,position,resources.Learning.Get);
            actor = repository.Create(owner,seed with { Level = 95, AcceptedStoryLevel = 95, CompletedQuests = done,
                InitialLearning = seed.InitialLearning with { Experience = resources.Growth.GetLevelStartExperience(95) } });
            var grants = new InventoryService(resources.ItemResources.Items,resources.ItemResources.GoldLimits,resources.ItemResources.EpicPieces);
            actor = InventoryService.Commit(repository,owner,actor,grants.Grant(actor,[new(10161610,10)]));
        }
        await fixture.StartAsync();
        foreach (int clears in new[] {0,3})
        {
            if (clears != 0)
                fixture.Sql("INSERT INTO character_ekern_cycle(character_id,account,service_day,normal_clears,trial_clears) VALUES($p0,$p1,$p2,3,0)",
                    actor.Id,fixture.Options.DevelopmentAccount,ShopPurchaseCount.Today(DateTimeOffset.UtcNow));
            byte[] expected = Build(w => { w.Write((byte)2); w.Write(5097); w.Write(clears); w.Write(0); w.Write(0); w.Write(5098); w.Write(0); w.Write(0); w.Write(0); });
            using var peer = Open(fixture);
            peer.Select(actor.Slot,prepared:true,resumeTown:true,room:false,ekernCycle:expected);
            peer.Barrier();
            peer.Send(15,new byte[4]);
            peer.Expect(0,656,expected);
            peer.Expect(1,15,[1]);
            peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),1]);
            int notices = 0;
            QuestLifecycleTcpTests.DrainNotifications(peer,packet =>
            {
                if (packet.Opcode == 27) { Bytes(packet.Body,peer.SelectionNoticeBody(),"艾肯保持原完整选图通知"); notices++; }
            });
            That(notices == 1,"真实TCP登录和CMD15同步持久通关计数");
            byte[] entry = new byte[25]; BinaryPrimitives.WriteUInt32LittleEndian(entry,5098);
            entry[4] = 2; entry[9] = entry[10] = 255;
            peer.Send(16,entry);
            if (clears == 0)
            {
                peer.Expect(1,16,[0,9,0,0]); peer.Barrier();
                That(fixture.Query("SELECT count(*) FROM character_dungeon_incount WHERE character_id=$p0 AND dungeon=5098",actor.Id) is [[0L]],
                    "真实16拒绝未完成前置，不消耗试炼次数");
            }
            else
            {
                var dungeon = peer.Expect(0,28).Body;
                That(U32(dungeon,0) == 5098,"三次前置后正式16进入原试炼路线");
                peer.Expect(0,29);
                QuestLifecycleTcpTests.DrainNotifications(peer);
                That(fixture.Query("SELECT daily_used FROM character_dungeon_incount WHERE character_id=$p0 AND dungeon=5098",actor.Id) is [[1L]],
                    "试炼合法入场仍按原事务扣除一次额度");
            }
        }
        await fixture.StopAsync();
    }
}
