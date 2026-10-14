using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    static void VerifyQuestBossEntry(TcpTestFixture fixture,TcpTestResources resources,CharacterGrowthCatalog growth)
    {
        var dungeons = DungeonCatalog.Load(resources.Archive);
        foreach (var scenario in new[] { (Quest:3772,Dungeon:235,Previous:3771,Level:(byte)84), (Quest:3773,Dungeon:227,Previous:3772,Level:(byte)84),
            (Quest:3774,Dungeon:1005,Previous:3773,Level:(byte)84), (Quest:3776,Dungeon:1007,Previous:3775,Level:(byte)84),
            (Quest:3777,Dungeon:238,Previous:3776,Level:(byte)84), (Quest:3946,Dungeon:310,Previous:3945,Level:(byte)88) })
        {
            var done = new HashSet<int> { scenario.Previous };
            var area = resources.Towns.Areas.Values.First(row => row.Level <= scenario.Level && row.DungeonWorldMap is { } world &&
                resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == scenario.Dungeon));
            done.UnionWith(area.Quests);
            if (area.AnyQuests.Length != 0) done.Add(area.AnyQuests[0]);
            var walk = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
            var position = new Location(area.Town,area.Area,checked((ushort)(walk.X+walk.Width/2)),checked((ushort)(walk.Y+walk.Height/2)));
            Character created;
            using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,() => { }))
            {
                // 只在隔离测试库准备真实入口和前置；任务仍由生产31接取，不预造完成条件或清图凭据。
                var candidate = CharacterCreation.Prepare("BossMap"+scenario.Quest,resources.Characters.Get(0),position,position,resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0,0,scenario.Level,candidate.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(scenario.Level) },resources.Learning);
                created = new CharacterRepository(fixture.Database).Create(owner,candidate with { Level=scenario.Level,AcceptedStoryLevel=scenario.Level,
                    CompletedQuests=done.ToArray(),InitialLearning=learning });
            }
            using var peer = Open(fixture);
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
            string? acceptance = null;
            long attempt = 0;
            foreach (ushort request in new[] { checked((ushort)scenario.Quest),(ushort)0 })
            {
                byte[] information = [];
                var map = Enter(peer,checked((ushort)scenario.Quest),checked((uint)scenario.Dungeon),row => information=row,requestQuestId:request);
                var maze = dungeons.Get(scenario.Dungeon).Mazes.Single(row => row.SourceOrdinal == information[7]);
                var start = maze.MapSpecification!.Single(row => row.Position == maze.Starts.Single());
                That(U32(map,14) == start.Id && information[8] == maze.Bosses.Single().X && information[9] == maze.Bosses.Single().Y,
                    "真实TCP的任务及普通0请求进入原起点与Boss坐标："+scenario.Quest);
                Load(peer); peer.Barrier();
                var active = fixture.Query("SELECT acceptance_id,condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",peer.Id,scenario.Quest).Single();
                acceptance ??= (string)active[0];
                That((string)active[0] == acceptance && active[1] is 1L &&
                    fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0",peer.Id) is [[long actualAttempt]] && actualAttempt == ++attempt,
                    "两种请求保留同一真实接取，分别只建立一次正式尝试："+scenario.Quest);
                peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]); peer.Expect(0,24); RefreshedQuests(peer); peer.Barrier();
                That(fixture.Query("SELECT count(*) FROM character_quest_clear_receipts WHERE character_id=$p0",peer.Id) is [[0L]] &&
                    fixture.Query("SELECT count(*) FROM character_completed_quests WHERE character_id=$p0 AND quest_id=$p1",peer.Id,scenario.Quest) is [[0L]],
                    "真实未通关退出不制造条件、清图凭据或完成位："+scenario.Quest);
            }
        }
    }
}
