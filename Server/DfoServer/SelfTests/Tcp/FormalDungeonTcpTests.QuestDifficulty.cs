using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    static void VerifyQuestDifficulty(TcpTestFixture fixture, TcpTestResources resources, CharacterGrowthCatalog growth)
    {
        foreach (var sample in new[] { (Dungeon: 101u, Quest: (ushort)3708, Start: 76450u),
            (Dungeon: 104u, Quest: (ushort)3749, Start: 76610u), (Dungeon: 86u, Quest: (ushort)3571, Start: 57807u) })
        {
            var entrance = MiddleQuestEntryTests.Entrance(resources.Towns, resources.Quests.Get(sample.Quest), sample.Dungeon);
            Character created;
            using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            {
                var candidate = CharacterCreation.Prepare("Diff" + sample.Quest, resources.Characters.Get(0),
                    entrance.Position, entrance.Position, resources.Learning.Get);
                created = new CharacterRepository(fixture.Database).Create(owner, candidate with
                {
                    Level = 95, AcceptedStoryLevel = 95, CompletedQuests = entrance.Completed,
                    InitialLearning = SkillPointLedger.Synchronize(0, 0, 95, candidate.InitialLearning with
                        { Experience = growth.GetLevelStartExperience(95) }, resources.Learning)
                });
            }
            using var peer = Open(fixture);
            peer.Select(created.Slot, prepared: true, resumeTown: true, room: false);
            peer.Send(31, [31, 0, (byte)sample.Quest, (byte)(sample.Quest >> 8)]);
            That(peer.Expect(1, 31).Body[0] == 1, "难度回归由生产31接取真实主线");
            RefreshedQuests(peer); peer.Barrier();
            string active = Active();
            var maze = SelfTestCatalogs.Dungeons(resources.Archive).Get((int)sample.Dungeon).Mazes
                .Single(row => row.ConnectsQuest(sample.Quest));
            long attempt = 0;
            foreach (byte difficulty in new byte[] { 0, 1, 3 })
            foreach (ushort request in new ushort[] { sample.Quest, 0 })
            {
                SelectMap(peer, incompleteHellQuest: HasIncompleteHellQuest(resources, created));
                var selection = Selection(difficulty);
                BinaryPrimitives.WriteUInt32LittleEndian(selection, sample.Dungeon);
                BinaryPrimitives.WriteUInt32LittleEndian(selection.AsSpan(16), request);
                if (sample.Dungeon == 86 && difficulty == 3 && request == sample.Quest)
                {
                    string before = fixture.Snapshot();
                    selection[4] = 4;
                    peer.Send(16, selection); peer.Expect(1, 16, [0, 9, 0, 0]); peer.Barrier();
                    That(fixture.Snapshot() == before, "地下城未开放的噩梦难度拒绝且不保存尝试或任务");
                    selection[4] = difficulty;
                }
                peer.Send(16, selection);
                var information = peer.Expect(0, 28).Body;
                var map = peer.Expect(0, 29).Body;
                That(information.Length == 36 && U32(information, 0) == sample.Dungeon && information[4] == difficulty &&
                    information[7] == maze.SourceOrdinal && information[8] == maze.Bosses.Single().X &&
                    information[9] == maze.Bosses.Single().Y && U32(map, 14) == sample.Start,
                    "普通/冒险/王者及显式/零请求保留所选难度、原任务maze、首房与Boss坐标：" + sample.Quest);
                Load(peer); peer.Barrier();
                That(Active() == active && fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0", peer.Id)
                    is [[long actual]] && actual == ++attempt, "任务难度只创建一次尝试，保留原接取和进度");
                peer.Send(42); peer.Expect(1, 42, [1]); peer.Expect(0, 3, [1, (byte)peer.Id, (byte)(peer.Id >> 8), 0]);
                peer.Expect(0, 24); RefreshedQuests(peer); peer.Barrier();
            }
            That(fixture.Query("SELECT count(*) FROM character_quest_clear_receipts WHERE character_id=$p0", peer.Id) is [[0L]],
                "只入场加载不伪造任务通关或奖励资格");
            string Active() => fixture.Snapshot("SELECT quest_id,slot,acceptance_id,condition_state FROM character_active_main_quests WHERE character_id=" + peer.Id);
        }
    }
}
