using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class QuestHellChallengeTcpTests
{
    public static Task RunAsync(ServerOptions options, string root, TcpTestResources resources) =>
        RunAsync(options, root, resources, skipMissingBeastMeeting: false);

    public static Task RunWithoutBeastMeetingAsync(ServerOptions options, string root, TcpTestResources resources) =>
        RunAsync(options, root, resources, skipMissingBeastMeeting: true);

    static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources, bool skipMissingBeastMeeting)
    {
        var baseMeetingHistory = resources.Quests.Get(5927).Prerequisites.Append(3145).ToHashSet();
        var towns = resources.Towns;
        var selectedMeeting = FindMeetingArea(towns,baseMeetingHistory);
        if (selectedMeeting is null && !skipMissingBeastMeeting)
        {
            await VerifyMissingBeastNpcAsync(options,root,resources);
            towns = MeetingContractTowns(resources.Towns);
            var area = towns.Areas[(31,254)];
            selectedMeeting = (area,baseMeetingHistory.Concat(area.Quests).ToHashSet());
        }
        string fixtureName = skipMissingBeastMeeting ? "quest-hell-challenge-without-beast-meeting" : "quest-hell-challenge";
        await using var fixture = new TcpTestFixture(options, Path.Combine(root,fixtureName),resources: resources,
            townCatalog:ReferenceEquals(towns,resources.Towns) ? null : towns);
        var growth = SelfTestCatalogs.Growth(resources.Archive,resources.Learning);
        var repository = new CharacterRepository(fixture.Database,resources.Quests);
        await fixture.StartAsync();
        // 复用资格任务网络矩阵：深渊有经验金币，魔兽5927沿参数表结算经验且无金币，均从真实接取交付。
        foreach (var scenario in new[] { (Quest: (ushort)6080, Prerequisite: false), (Quest: (ushort)6880, Prerequisite: true),
            (Quest: (ushort)6080, Prerequisite: true), (Quest: (ushort)5927, Prerequisite: false),
            (Quest: (ushort)5927, Prerequisite: true) })
        {
            var quest = resources.Quests.Get(scenario.Quest);
            string account = "hell323-" + scenario.Quest + (scenario.Prerequisite ? "p" : "r");
            Character created;
            using (var owner = new AccountOwners().Acquire(account,() => { }))
            {
                var done = new HashSet<int>((scenario.Prerequisite ? quest.Prerequisites : []).Append(3145));
                var location = resources.Towns.Room;
                if (scenario.Quest == 5927 && scenario.Prerequisite)
                {
                    if (selectedMeeting is not { } meeting)
                    {
                        if (!skipMissingBeastMeeting)
                            throw new InvalidDataException("魔兽5927正向TCP夹具缺少town31合法场景NPC598；默认完整组不省略该用例");
                        TestCheck.Skip("beast.meeting-npc-missing", $"{fixtureName} quest=5927 prerequisite=true：匹配PVF缺少town31合法场景NPC598，仅魔兽会面正向TCP未执行；深渊及缺前置拒绝仍执行");
                        continue;
                    }
                    var area = meeting.Area;
                    done.UnionWith(meeting.Completed);
                    var bounds = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
                    location = new(area.Town,area.Area,checked((ushort)(bounds.X + bounds.Width/2)),checked((ushort)(bounds.Y + bounds.Height/2)));
                }
                var candidate = CharacterCreation.Prepare("Hell" + scenario.Quest + (scenario.Prerequisite ? "P" : "R"),resources.Characters.Get(0),
                    location,resources.Towns.InitialReturn,resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0,0,90,candidate.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(90) },resources.Learning);
                created = repository.Create(owner,candidate with { Level = 90, AcceptedStoryLevel = 90,
                    CompletedQuests = done.Order().ToArray(),
                    InitialLearning = learning });
            }
            That(fixture.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0",created.Id) is [[0L]],
                "深渊任务fixture只准备真实前置，没有预填目标接取和完成");
            byte[] completed = [];
            string saved = "";
            using (var peer = Open(fixture,account))
            {
                bool beastMeeting = scenario.Quest == 5927 && scenario.Prerequisite;
                peer.Select(prepared: true, resumeTown: beastMeeting, room: !beastMeeting);
                That(fixture.Query("SELECT quest_id,condition_state FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot",created.Id) is [[6115L,1686L],[13056L,1686L]],"90级自动分配两项重叠等级每日，资格任务仍等待31");
                string daily=fixture.Snapshot("SELECT slot,quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id IN(6115,13056) ORDER BY slot",created.Id);
                That(QuestLifecycleTcpTests.List(peer.AvailableQuests,90).Contains(scenario.Quest) == scenario.Prerequisite,
                    "实际21保留Ep6前置资格，不因奖励支持直接放行");
                string before = State();
                peer.Send(31,Build(writer => { writer.Write((ushort)31); writer.Write(scenario.Quest); }));
                if (!scenario.Prerequisite)
                {
                    peer.Expect(1,31,[0,18,0]); peer.Barrier();
                    That(State() == before,"缺少6045时31拒绝且无目标槽、奖励或完成回执");
                    continue;
                }
                uint initial = scenario.Quest == 5927 ? 1u : 0u;
                peer.Expect(1,31,Build(writer => { writer.Write((byte)1); writer.Write(scenario.Quest); writer.Write(initial); writer.Write((byte)0); }));
                peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
                That(peer.CompletedQuestHistory.Length == 30004 && U32(peer.CompletedQuestHistory) == 30000 &&
                    peer.CompletedQuestHistory[4 + scenario.Quest] == 0,"接取后的356尚未把目标投影为已完成");
                var active = fixture.Query("SELECT condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",created.Id,scenario.Quest);
                That(active is [[long scalar,string]] && scalar == initial && fixture.Query("SELECT count(*) FROM character_completed_quests WHERE character_id=$p0 AND quest_id=$p1",created.Id,scenario.Quest) is [[0L]],
                    "资格任务按真实子类型初始化，尚未领奖或写目标完成位");
                string acceptance = (string)active[0][1];
                That(Guid.TryParseExact(acceptance,"N",out var identity) && identity != Guid.Empty,"真实31生成非空接取身份");
                before = State();
                peer.Send(33,Build(writer => { writer.Write((ushort)33); writer.Write(scenario.Quest); writer.Write((ushort)0); }));
                peer.Expect(1,33,Build(writer => { writer.Write((byte)1); writer.Write(scenario.Quest); writer.Write(0u); })); peer.Barrier();
                That(scenario.Quest == 5927 ? State() != before : State() == before,
                    "魔兽会面在选定NPC场景经真实33归零，已归零深渊33不重复保存或发奖");
                That(fixture.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",created.Id,scenario.Quest) is [[0L]],
                    "只有真实33完成会面后才进入资格交付");
                long gold = Convert.ToInt64(fixture.Query("SELECT gold FROM characters WHERE id=$p0",created.Id)[0][0]);
                var inventory = fixture.Query("SELECT item_uid,item_core FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid",created.Id);
                SendFinish(peer);
                byte[] ack = peer.Expect(1,34).Body;
                That(ack.Length == 9 && ack[0] == 1 && U16(ack,1) == scenario.Quest && ack[3] == 4 &&
                    U32(ack,4) > 0 && ack[8] == (scenario.Quest == 5927 ? 31 : 3),
                    "真实34保持会面type4，深渊mode3/魔兽mode31不追加物品count或将资格参数当物品");
                var notices = Drain(peer);
                That(scenario.Quest != 5927 || notices.All(packet => packet.Opcode != 1709),
                    "魔兽资格由真实ACK34打开原生窗口，不再追加普通公告");
                long goldAfter = Convert.ToInt64(fixture.Query("SELECT gold FROM characters WHERE id=$p0",created.Id)[0][0]);
                if (scenario.Quest == 5927)
                    That(goldAfter == gold && notices.All(packet => packet.Opcode != 14),
                        "魔兽无金币奖励，不为开启窗口伪造金币或余额通知");
                else
                {
                    byte[] goldUpdate = notices.Single(packet => packet.Opcode == 14).Body;
                    That(goldAfter > gold && goldUpdate[0] == 0 && U16(goldUpdate,1) == 1 && U16(goldUpdate,3) == 0 &&
                        U32(goldUpdate,5) == 0 && U32(goldUpdate,9) == goldAfter,
                        "mode3无金币增量行，14仍独立发布已提交Main0绝对金币");
                }
                int available = notices.FindIndex(packet => packet.Opcode == 21), history = notices.FindIndex(packet => packet.Opcode == 356);
                That(available >= 0 && history > available && notices.Take(available).Any(packet => packet.Opcode == 37),
                    "交付按实际奖励同步成长，再按21和356发布候选与完成历史");
                completed = notices[history].Body;
                That(completed.Length == 30004 && U32(completed) == 30000 && completed[4 + scenario.Quest] == 1,
                    "34后的356独立核对实际目标完成位，不能只比较两份相同的错误投影");
                That(!QuestLifecycleTcpTests.List(notices[available].Body,90).Contains(scenario.Quest) &&
                    fixture.Query("SELECT condition_type,acceptance_id,experience_amount,gold_amount FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=$p1",created.Id,scenario.Quest)
                        is [[4L,string receipt,long experience,long granted]] && receipt == acceptance && experience == U32(ack,4) && granted == goldAfter - gold &&
                    fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 AND quest_id=$p1",created.Id,scenario.Quest) is [[long done]] && done == scenario.Quest &&
                    fixture.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0 AND quest_id NOT IN(6115,13056)",created.Id) is [[0L]] &&
                    fixture.Snapshot("SELECT slot,quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id IN(6115,13056) ORDER BY slot",created.Id)==daily,
                    "34完整提交原AcceptanceId、真实成长金币、唯一完成位及清槽");
                That(System.Text.Json.JsonSerializer.Serialize(inventory) == System.Text.Json.JsonSerializer.Serialize(
                    fixture.Query("SELECT item_uid,item_core FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid",created.Id)),
                    "资格任务没有生成物品1或改写现有物品UID");
                saved = State();
                SendFinish(peer); peer.Expect(1,34,[0,19,0]); peer.Barrier();
                That(State() == saved,"重复34不多发经验金币、不重写完成或回执");
            }
            using (var peer = Open(fixture,account))
            {
                bool beastMeeting = scenario.Quest == 5927 && scenario.Prerequisite;
                peer.Select(prepared: true, resumeTown: beastMeeting, room: !beastMeeting);
                Bytes(peer.CompletedQuestHistory,completed,"断线重连356恢复同一完成表");
                That(!QuestLifecycleTcpTests.List(peer.AvailableQuests,90).Contains(scenario.Quest) && State() == saved,
                    "重连不重新开放一次性深渊任务或重领奖");
                peer.Send(31,Build(writer => { writer.Write((ushort)31); writer.Write(scenario.Quest); }));
                peer.Expect(1,31,[0,18,0]); peer.Barrier();
                That(State() == saved,"重连后重复接取仍拒绝，已完成和奖励保持一次");
            }
            string State() => System.Text.Json.JsonSerializer.Serialize(new[] {
                fixture.Query("SELECT gold,experience,revision FROM characters WHERE id=$p0",created.Id),
                fixture.Query("SELECT quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot",created.Id),
                fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 ORDER BY quest_id",created.Id),
                fixture.Query("SELECT quest_id,acceptance_id FROM character_quest_completion_receipts WHERE character_id=$p0 ORDER BY quest_id",created.Id) });
            void SendFinish(TcpPeer peer) => peer.Send(34,Build(writer => { writer.Write((ushort)34); writer.Write(scenario.Quest);
                writer.Write(ushort.MaxValue); writer.Write((ushort)1); writer.Write(ushort.MaxValue); }));
        }
        VerifyInfernoSidequests(fixture,resources);
        VerifyHarlemContinuation(fixture,resources);
        VerifyAlbertOrphan(fixture,resources);
    }

    // 用同一份真实前置和区域准入历史选择当前地图；资源含598时保留实际场景正向覆盖。
    static (TownArea Area,HashSet<int> Completed)? FindMeetingArea(TownCatalog towns,IReadOnlySet<int> prerequisite)
    {
        foreach (var area in towns.Areas.Values.Where(area => area.Town == 31 && area.Level <= 90).OrderBy(area => area.Area))
        {
            var done = prerequisite.Concat(area.Quests).ToHashSet();
            if (area.AnyQuests.Length > 0 && !area.AnyQuests.Any(done.Contains)) done.Add(area.AnyQuests[0]);
            if (area.ExceptQuests.Any(done.Contains)) continue;
            if (area.Map(done).Npcs.Any(npc => npc.Id == 598)) return (area,done);
        }
        return null;
    }

    // 仅在上述真实场景选择无匹配时验证资源拒绝，不将接取NPC469替代目标。
    static async Task VerifyMissingBeastNpcAsync(ServerOptions options,string root,TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(options,Path.Combine(root,"quest-hell-missing-npc"),resources:resources);
        await fixture.StartAsync();
        var quest = resources.Quests.Get(5927);
        var area = resources.Towns.Areas[(31,1)];
        var done = quest.Prerequisites.Concat(area.Quests).Append(3145).ToHashSet();
        var map = area.Map(done);
        That(!map.Npcs.Any(npc => npc.Id == 598),"实际CentralPark缺少598的资源合同；资源变化时须重新核对本负向输入");
        var bounds = map.Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var location = new Location(area.Town,area.Area,checked((ushort)(bounds.X+bounds.Width/2)),checked((ushort)(bounds.Y+bounds.Height/2)));
        var repository = new CharacterRepository(fixture.Database,resources.Quests);
        Character created;
        const string account = "hell323-missing-npc";
        using (var owner = new AccountOwners().Acquire(account,() => { }))
        {
            var candidate = CharacterCreation.Prepare("HellNoNpc",resources.Characters.Get(0),location,resources.Towns.InitialReturn,resources.Learning.Get);
            var growth = SelfTestCatalogs.Growth(resources.Archive,resources.Learning);
            created = repository.Create(owner,candidate with { Level=90,AcceptedStoryLevel=90,CompletedQuests=done.Order().ToArray(),
                InitialLearning=SkillPointLedger.Synchronize(0,0,90,candidate.InitialLearning with
                    { Experience=growth.GetLevelStartExperience(90) },resources.Learning) });
        }
        using var peer = Open(fixture,account);
        peer.Select(prepared:true,resumeTown:true,room:false);
        peer.Send(31,[31,0,0x27,0x17]);
        peer.Expect(1,31,[1,0x27,0x17,1,0,0,0,0]);
        peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
        That(fixture.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=5927",created.Id) is [[1L]] &&
            fixture.Query("SELECT count(*) FROM character_completed_quests WHERE character_id=$p0 AND quest_id=5927",created.Id) is [[0L]],
            "真实接取仍保留会面scalar1和未完成资格，不能预填目标");
        string before = State();
        peer.Send(33,[33,0,0x27,0x17,0,0]);
        peer.Expect(1,33,[0,19,0]); peer.Barrier();
        That(State()==before,"实际资源缺598时33精确拒绝，角色、任务、资产、完成与回执零提交");
        string State() => System.Text.Json.JsonSerializer.Serialize(new[] {
            fixture.Query("SELECT gold,experience,revision FROM characters WHERE id=$p0",created.Id),
            fixture.Query("SELECT slot,quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot",created.Id),
            fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 ORDER BY quest_id",created.Id),
            fixture.Query("SELECT quest_id,acceptance_id,experience_amount,gold_amount FROM character_quest_completion_receipts WHERE character_id=$p0 ORDER BY quest_id",created.Id),
            fixture.Query("SELECT item_uid,item_core FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid",created.Id) });
    }

    // 仅验证既有会面consumer的有NPC合同，不冒称PVF或客户端已经提供598；共享目录和原地图均不修改。
    static TownCatalog MeetingContractTowns(TownCatalog source)
    {
        var original = source.Areas[(31,1)];
        var bounds = original.Default.Bounds.First(row => row.Quests.Length == 0 && row.Conditions.Length == 0).Bounds;
        var map = original.Default with { Path="map/selftest/quest-hell-meeting-598.map",
            Npcs=[new(598,bounds.X+bounds.Width/2,bounds.Y+bounds.Height/2)] };
        var areas = source.Areas.ToDictionary(row => row.Key,row => row.Value);
        That(!areas.ContainsKey((31,254)),"局部会面合同区域不能覆盖实际资源区域");
        areas.Add((31,254),original with { Area=254,Gate=null,Default=map,Phases=[],Shifts=[] });
        return new(areas,source.Room,source.InitialReturn,source.Dock,source.RoomPortals,source.NpcMoves,source.DungeonMaps)
        {
            EpisodeTowns=source.EpisodeTowns,NpcExits=source.NpcExits,EquipmentTransportNpcs=source.EquipmentTransportNpcs,
            EquipmentResealNpcs=source.EquipmentResealNpcs,LostLandEntryNpcs=source.LostLandEntryNpcs,SpecialChannelQuests=source.SpecialChannelQuests
        };
    }

    static List<WirePacket> Drain(TcpPeer peer)
    {
        var packets = new List<WirePacket>();
        peer.SendBarrier();
        for (int at = 0; at < 64; at++)
        {
            var packet = peer.Read();
            if (packet is { Kind: 1, Opcode: 1347 }) { Bytes(packet.Body,[1],"深渊任务通知处理完成"); return packets; }
            That(packet.Kind == 0,"34后只有通知，不能跳过其它ACK或错误");
            if (packet.Opcode == 1760) peer.ReadCardMembershipState(packet);
            else packets.Add(packet);
        }
        throw new InvalidDataException("深渊任务通知超过有界数量");
    }
}
