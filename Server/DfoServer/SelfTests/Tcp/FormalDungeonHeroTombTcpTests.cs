using System.Buffers.Binary;
using DfoServer.GameWorld.Characters;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunHeroTombAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root,"hero-tomb-entry"),resources: resources);
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Create("Hero111",13);
            // 仅在隔离库准备角色等级、地区和已完成前置，任务由真实31接取。
            var growth = CharacterGrowthCatalog.Load(resources.Archive);
            fixture.Sql("UPDATE characters SET level=90,grow_type=36,experience=$p0,town=41,area=1,x=249,y=83 WHERE id=$p1",
                growth.GetLevelStartExperience(90),peer.Id);
            fixture.Sql("INSERT INTO character_completed_quests(character_id,quest_id) VALUES($p0,3223),($p0,3239)",peer.Id);
            peer.Select(prepared: true);
            peer.Travel(41,1,249,83,38,1,bottom: true);
            peer.Travel(41,2,885,223,41,1);
            peer.Send(31,[31,0,168,12]);
            peer.Expect(1,31,[1,168,12,1,0,0,0,0]);
            RefreshedQuests(peer);
            string active = fixture.Snapshot("SELECT * FROM character_active_main_quests");
            string assets = fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid");
            string fatigue = fixture.Snapshot("SELECT * FROM character_fatigue");
            SelectMap(peer);
            byte[] selection = Selection(1);
            BinaryPrimitives.WriteUInt32LittleEndian(selection,151);
            BinaryPrimitives.WriteUInt32LittleEndian(selection.AsSpan(16),3240);
            string beforeRejection = fixture.Snapshot();
            peer.Send(16,selection);
            peer.Expect(1,16,[0,9,0,0]);
            peer.Barrier();
            That(fixture.Snapshot() == beforeRejection,"英雄冢剧情难度1拒绝后保持角色、任务与资产，连接继续可用");
            selection[4] = 0;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                if (attempt > 1) SelectMap(peer);
                peer.Send(16,selection);
                byte[] information = new byte[36];
                information[0] = 151;
                information[7] = 7;
                information[8] = 7;
                information[10] = information[11] = 255;
                information.AsSpan(20,4).Fill(255);
                peer.Expect(0,28,information);
                byte[] map = peer.Expect(0,29).Body;
                That(map[0] == 0 && map[1] == 0 && U32(map,14) == 91391 &&
                    fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0",peer.Id) is [[long actual]] && actual == attempt,
                    "英雄冢真实16按28→29进入任务首图，每次只预留一次实例");
                Load(peer);
                peer.Barrier();
                string loaded = fixture.Snapshot();
                peer.Send(37,new byte[8]);
                peer.Barrier();
                That(fixture.Snapshot() == loaded,"重复加载不重复预留实例、推进任务或改写资产");
                peer.Send(42);
                peer.Expect(1,42,[1]);
                peer.Expect(0,24);
                RefreshedQuests(peer);
                peer.Barrier();
                That(fixture.Snapshot("SELECT * FROM character_active_main_quests") == active &&
                    fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") == assets &&
                    fixture.Snapshot("SELECT * FROM character_fatigue") == fatigue &&
                    fixture.Query("SELECT town,area FROM characters WHERE id=$p0",peer.Id) is [[41L,2L]],
                    "入场、加载和放弃保持任务接取身份、进度与原有资产，返回真实入口地区");
            }
        }
        using (var peer = Open(fixture))
        {
            peer.Select(prepared: true);
            That(fixture.Query("SELECT quest_id,condition_state FROM character_active_main_quests") is [[3240L,1L]],
                "重连保留尚未通关的向暗黑城前进任务");
            peer.Barrier();
        }
        await fixture.StopAsync();
    }
}
