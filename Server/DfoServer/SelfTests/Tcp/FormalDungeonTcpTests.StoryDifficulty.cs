using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
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
}
