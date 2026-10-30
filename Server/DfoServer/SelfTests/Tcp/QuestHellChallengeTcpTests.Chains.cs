using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class QuestHellChallengeTcpTests
{
    static void VerifyOnlineSidequestChains(TcpTestFixture fixture, TcpTestResources resources)
    {
        var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
        var repository = new CharacterRepository(fixture.Database, resources.Quests);
        var chains = InfernoQuestUnlock.Pairs.Select(pair =>
            (Previous: pair.HellQuest, First: pair.IntroductionQuest, Last: pair.InfernoQuest))
            .Concat([(6080,6081,10112), (6081,10112,0)]);
        foreach (var (previous, first, last) in chains)
        {
            var quest = resources.Quests.Get(first);
            var done = quest.Prerequisites.Append(previous).ToHashSet();
            var area = resources.Towns.Areas.Values.First(row => row.Level <= 95 &&
                row.Map(done).Npcs.Any(npc => npc.Id == quest.IntData[0]));
            done.UnionWith(area.Quests);
            if (area.AnyQuests.Length > 0 && !area.AnyQuests.Any(done.Contains)) done.Add(area.AnyQuests[0]);
            That(!done.Contains(first) && (last == 0 || !done.Contains(last)),
                "在线外传链只准备前史和真实NPC场景，不预填首尾任务");
            var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
            var location = new Location(area.Town, area.Area, checked((ushort)(bounds.X + bounds.Width / 2)),
                checked((ushort)(bounds.Y + bounds.Height / 2)));
            string account = "abyss905-" + first;
            Character created;
            using (var owner = new AccountOwners().Acquire(account, () => { }))
            {
                var candidate = CharacterCreation.Prepare("Abyss" + first, resources.Characters.Get(0),
                    location, resources.Towns.InitialReturn, resources.Learning.Get);
                created = repository.Create(owner, candidate with { Level = 95, AcceptedStoryLevel = 95,
                    CompletedQuests = done.Order().ToArray(),
                    InitialLearning = SkillPointLedger.Synchronize(0,0,95,candidate.InitialLearning with
                        { Experience = growth.GetLevelStartExperience(95) },resources.Learning) });
            }
            // 本版356仅把非零下标加入完成集合，零值不会删除旧记录；跨整条连接累计才能发现伪完成污染。
            var imported = new HashSet<int>();
            using (var peer = Open(fixture, account))
            {
                peer.Select(prepared: true, resumeTown: true, room: false);
                byte[] available = peer.AvailableQuests;
                Observe(peer.CompletedQuestHistory);
                That(!imported.Contains(first) && (last == 0 || !imported.Contains(last)),
                    $"外传{first}/{last}接取前未被356提前写成已完成");
                if (last != 0)
                {
                    peer.Send(31,Build(w => { w.Write((ushort)31); w.Write(checked((ushort)last)); }));
                    peer.Expect(1,31,[0,18,0]); peer.Barrier();
                    That(fixture.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",
                        created.Id,last) is [[0L]], $"尾步{last}缺少首步真实完成记录时仍拒绝");
                }
                Accept(first);
                Meet(first);
                Finish(first);
                if (last != 0)
                {
                    That(!imported.Contains(last), $"首步{first}完成后同连接尾步{last}没有残留完成标记");
                    Accept(last);
                    if (resources.Quests.Get(last).SubType == 1)
                    {
                        Meet(last);
                        Finish(last);
                    }
                    else
                        That(fixture.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",
                            created.Id,last) is [[1L]], "Ep6超星空外传仍须真实休亚会面，不在南瓜球场景代办");
                }

                void Accept(int id)
                {
                    That(!imported.Contains(id) && QuestLifecycleTcpTests.List(available,95).Contains(id),
                        $"同连接候选{id}真实可接且不在客户端累计完成集合中");
                    peer.Send(31,Build(w => { w.Write((ushort)31); w.Write(checked((ushort)id)); }));
                    var ack = peer.Expect(1,31).Body;
                    That(ack.Length == 8 && ack[0] == 1 && U16(ack,1) == id &&
                        U32(ack,3) == (resources.Quests.Get(id).SubType == 1 ? 0u : 1u),
                        $"外传{id}真实31保持原会面子类型");
                    Refresh(Drain(peer));
                }
                void Meet(int id)
                {
                    peer.Send(33,Build(w => { w.Write((ushort)33); w.Write(checked((ushort)id)); w.Write((ushort)0); }));
                    peer.Expect(1,33,Build(w => { w.Write((byte)1); w.Write(checked((ushort)id)); w.Write(0u); }));
                    peer.Barrier();
                }
                void Finish(int id)
                {
                    peer.Send(34,Build(w => { w.Write((ushort)34); w.Write(checked((ushort)id));
                        w.Write(ushort.MaxValue); w.Write((ushort)1); w.Write(ushort.MaxValue); }));
                    var ack = peer.Expect(1,34).Body;
                    That(ack[0] == 1 && U16(ack,1) == id && ack[8] == 0, $"外传{id}真实交付沿原mode0");
                    Refresh(Drain(peer));
                    That(imported.Contains(id) && fixture.Query(
                        "SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=$p1",created.Id,id) is [[1L]],
                        $"外传{id}只在实际交付后发布完成位和唯一奖励回执");
                }
                void Refresh(List<WirePacket> packets)
                {
                    foreach (var packet in packets.Where(packet => packet.Opcode == 356)) Observe(packet.Body);
                    available = packets.Single(packet => packet.Opcode == 21).Body;
                }
            }
            void Observe(byte[] body)
            {
                That(body.Length == 30004 && U32(body) == 30000, "在线链读取真实356完整完成表");
                for (int id = 1; id < 30000; id++)
                    if (body[4+id] != 0) imported.Add(id);
            }
        }
    }
}
