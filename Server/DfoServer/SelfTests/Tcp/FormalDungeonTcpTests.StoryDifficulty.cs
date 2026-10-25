using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    static void VerifyStoryDifficultyEntry(TcpTestFixture fixture,TcpTestResources resources,CharacterGrowthCatalog growth)
    {
        var done = new HashSet<int> { 3777 };
        var area = resources.Towns.Areas.Values.First(row => row.Level <= 84 && row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 233));
        done.UnionWith(area.Quests);
        if (area.AnyQuests.Length != 0) done.Add(area.AnyQuests[0]);
        var walk = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town,area.Area,checked((ushort)(walk.X+walk.Width/2)),checked((ushort)(walk.Y+walk.Height/2)));
        Character created;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,() => { }))
        {
            // 独立测试库只准备入口和前置；3778继续由生产31接取，不造活动身份或完成凭据。
            var candidate = CharacterCreation.Prepare("StoryHeart",resources.Characters.Get(0),position,position,resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0,0,84,candidate.InitialLearning with
                { Experience=growth.GetLevelStartExperience(84) },resources.Learning);
            created = new CharacterRepository(fixture.Database).Create(owner,candidate with { Level=84,AcceptedStoryLevel=84,
                CompletedQuests=done.ToArray(),InitialLearning=learning });
        }
        using var peer = Open(fixture);
        peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
        EnterAndReturn(3778,1);
        var original = fixture.Query("SELECT acceptance_id,condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3778",peer.Id).Single();
        SelectMap(peer);
        string before = fixture.Snapshot();
        var wrong = Selection(2);
        BinaryPrimitives.WriteUInt32LittleEndian(wrong,233);
        BinaryPrimitives.WriteUInt32LittleEndian(wrong.AsSpan(16),3778);
        peer.Send(16,wrong); peer.Expect(1,16,[0,9,0,0]); peer.Barrier();
        That(fixture.Snapshot() == before,"真实故事请求不能借普通指定二难度入场，拒绝不保存新尝试或任务");
        peer.Send(132); peer.Expect(1,132,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),0]);
        peer.Expect(0,23); peer.Expect(0,24); peer.Barrier();
        EnterAndReturn(0,2);
        That(fixture.Query("SELECT acceptance_id,condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3778",peer.Id)
            is [[string acceptance,1L]] && acceptance == (string)original[0] &&
            fixture.Query("SELECT count(*) FROM character_quest_clear_receipts WHERE character_id=$p0",peer.Id) is [[0L]] &&
            fixture.Query("SELECT count(*) FROM character_completed_quests WHERE character_id=$p0 AND quest_id=3778",peer.Id) is [[0L]],
            "显式/普通0加载及拒绝恢复保留同一3778接取，未通关不产生奖励资格");

        void EnterAndReturn(ushort request,long attempt)
        {
            byte[] information=[];
            var map=Enter(peer,3778,233,row=>information=row,requestQuestId:request);
            That(U32(map,14) == 310064 && information[4] == 0 &&
                fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0",peer.Id) is [[long actual]] && actual==attempt,
                "真实3778沿零难度原地图完成正式入场，失败请求不增加尝试");
            Load(peer); peer.Barrier();
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]); peer.Expect(0,24); RefreshedQuests(peer); peer.Barrier();
        }
    }

    static async Task VerifyMultipleStoryBlocksAsync(ServerOptions source,string root,TcpTestResources resources,CharacterGrowthCatalog growth)
    {
        // 全职业用例已占满原测试账号；两条故事分支共用独立测试库和已有只读资源。
        await using var fixture = new TcpTestFixture(source,Path.Combine(root,"multiple-story-blocks"),resources:resources);
        await fixture.StartAsync();
        foreach (var sample in new[] { (Quest:(ushort)6206,Previous:6205),(Quest:(ushort)6214,Previous:6213) })
        {
            var done = new HashSet<int> { sample.Previous };
            var area = resources.Towns.Areas.Values.First(row => row.Level <= 95 && row.DungeonWorldMap is { } world &&
                resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 291100373));
            done.UnionWith(area.Quests);
            if (area.AnyQuests.Length != 0) done.Add(area.AnyQuests[0]);
            var walk = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
            var position = new Location(area.Town,area.Area,checked((ushort)(walk.X+walk.Width/2)),checked((ushort)(walk.Y+walk.Height/2)));
            Character created;
            using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,() => { }))
            {
                var candidate = CharacterCreation.Prepare("Story"+sample.Quest,resources.Characters.Get(0),position,position,resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0,0,95,candidate.InitialLearning with
                    { Experience=growth.GetLevelStartExperience(95) },resources.Learning);
                created = new CharacterRepository(fixture.Database).Create(owner,candidate with { Level=95,AcceptedStoryLevel=95,
                    CompletedQuests=done.ToArray(),InitialLearning=learning });
            }
            using var peer = Open(fixture);
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
            foreach (ushort requested in new ushort[] { sample.Quest,0 })
            {
                byte[] information=[];
                var map = Enter(peer,sample.Quest,291100373,row => information=row,requestQuestId:requested);
                That(U32(map,14) == 292106186 && information[4] == 0,
                    "真实互斥故事任务由生产接取和Session预热进入原首房，显式及零请求一致");
                Load(peer); peer.Barrier();
                peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),0]);
                peer.Expect(0,24); RefreshedQuests(peer); peer.Barrier();
            }
            That(fixture.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",peer.Id,sample.Quest)
                is [[1L]] && fixture.Query("SELECT count(*) FROM character_quest_clear_receipts WHERE character_id=$p0",peer.Id) is [[0L]],
                "真实两个主线分支只入场加载，不伪造清图或奖励资格");
        }
        VerifyHamelinStoryBosses(fixture,resources,growth);
    }
    static void VerifyHamelinStoryBosses(TcpTestFixture fixture,TcpTestResources resources,CharacterGrowthCatalog growth)
    {
        var area = resources.Towns.Areas.Values.First(row => row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 51 && option.QuestId == -1));
        var done = area.Quests.Concat(area.AnyQuests.Take(1)).Append(3343).ToHashSet();
        done.UnionWith(area.Map(done).DungeonGates.Where(gate => gate.Conditions.Length == 0).SelectMany(gate => gate.Quests));
        var walk = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new Location(area.Town,area.Area,checked((ushort)(walk.X+walk.Width/2)),checked((ushort)(walk.Y+walk.Height/2)));
        Character created;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,() => { }))
        {
            var candidate = CharacterCreation.Prepare("StoryHamelin",resources.Characters.Get(0),position,position,resources.Learning.Get);
            created = new CharacterRepository(fixture.Database).Create(owner,candidate with
            {
                Level = 53, AcceptedStoryLevel = 53, CompletedQuests = done.ToArray(),
                InitialLearning = SkillPointLedger.Synchronize(0,0,53,candidate.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(53) },resources.Learning)
            });
        }
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
            byte[] information = [];
            var map = Enter(peer,3344,51,row => information = row);
            That(information[8] == 3 && information[9] == 0 && U32(map,14) == 76320,"真实3344入场发布首个剧情Boss目标");
            (byte X,byte Y,uint Id)[] path = [(0,0,76320),(1,0,76321),(1,1,76322),(2,1,76323),(3,1,76324),
                (4,1,76325),(4,0,76326),(3,0,76327),(4,0,76328),(3,0,76329)];
            int updates = 0;
            var seen = new HashSet<ushort>();
            foreach (var room in path)
            {
                if (room.Id != 76320)
                {
                    byte[] move = new byte[100]; move[0] = room.X; move[1] = room.Y;
                    peer.Send(45,move); peer.Expect(1,45,[1]); map = peer.Expect(0,29).Body;
                }
                That(U32(map,14) == room.Id && (room.Id is not (76328 or 76329) || map[13] == 2),
                    "真实任务重复坐标按PVF阶段换图，76328/76329使用Refresh");
                Load(peer);
                if (room.Id == 76329) { peer.Barrier(); break; }
                foreach (int at in Enumerable.Range(0,map[18]).Select(index => 19+22*index)
                    .Where(at => map[at+13] <= 3 && map[at+21] == 0).OrderBy(at => map[at+13] == 3))
                {
                    ushort key = U16(map,at+6);
                    That(seen.Add(key),"真实剧情阶段独立key不复用旧死亡身份");
                    peer.Send(39,Death(key,peer.Id,map[at+12]));
                    byte[] death = peer.Expect(0,38).Body;
                    int tail = 4+U16(death,2)*131;
                    if (room.Id is 76327 or 76328 && map[at+13] == 3)
                    {
                        byte expectedX = room.Id == 76327 ? (byte)4 : (byte)3;
                        Bytes(death.AsSpan(tail).ToArray(),[0,0,255,1,expectedX,0,0],"真实剧情假Boss提交后NOTI38发布下一目标");
                        updates++;
                    }
                    else Bytes(death.AsSpan(tail).ToArray(),[0,0,255,0,0],"普通怪不改变剧情Boss标记");
                    peer.Expect(0,37);
                }
                peer.Barrier();
            }
            That(updates == 2 && fixture.Query("SELECT count(*) FROM character_quest_clear_receipts WHERE character_id=$p0",peer.Id) is [[0L]],
                "两次剧情假Boss死亡不提前产生最终清图资格");
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),0]);
            peer.Expect(0,24); RefreshedQuests(peer); peer.Barrier();
        }
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
            byte[] information = [];
            var map = Enter(peer,3344,51,row => information = row);
            That(information[8] == 3 && information[9] == 0 && U32(map,14) == 76320 && map[13] == 1,
                "回城重连重进从新Run首房与初始目标开始，不继承旧阶段或门缓存");
            Load(peer); peer.Barrier();
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id>>8),0]);
            peer.Expect(0,24); RefreshedQuests(peer); peer.Barrier();
        }
    }

}
