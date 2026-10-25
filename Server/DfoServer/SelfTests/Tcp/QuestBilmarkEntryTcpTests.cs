using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class QuestBilmarkEntryTcpTests
{
    // 真实资源六房静态敌人；39为合成战斗输入，不代表客户端Boss死亡演出已验。
    static readonly int[][] Monsters =
    [
        [62879,62879,62879],
        [62879,62881,62879,62879,62881,62879,62881],
        [62879,62879,62879],
        [62879,62881,62879,62881,62879],
        [62879,62881,62879,62879,62881],
        [107000205]
    ];

    internal static void Verify(TcpTestFixture fixture,TcpTestResources resources,CharacterGrowthCatalog growth,
        Action<TcpPeer,Action<WirePacket>?> drain)
    {
        var parent = resources.Quests.Get(3656);
        var child = resources.Quests.Get(3692);
        var seeking = resources.Quests.Get(3693);
        That(parent.MinimumLevel == 79 && parent.Prerequisites.SequenceEqual(new[] { 3655 }) &&
            parent.IntData.SequenceEqual(new[] { 3692,3693 }) && QuestRelationProgress.SequentialParent(parent,resources.Quests) &&
            QuestClearEligibility.IsSingleMap(child) && child.IntData[0] == 91986 && child.MainQuest == 3656 &&
            child.Definition!.Dungeons.Any(row => row.DungeonId == 240) && QuestRelationProgress.ZeroRewardChild(child) &&
            seeking.Condition == "[seeking]" && seeking.IntData.SequenceEqual(new[] { 10164767,1 }) && seeking.MainQuest == 3656,
            "3656使用真实79级顺序父任务、3692清图及3693小盒子寻物声明");
        var maze = SelfTestCatalogs.Dungeons(resources.Archive).Get(240).Mazes.Single(row => row.ConnectsQuest(3692));
        That(maze.Starts.Single() == new DungeonPoint(0,0) && maze.Bosses.Single() == new DungeonPoint(5,0) &&
            maze.MapSpecification!.OrderBy(row => row.Position.X).Select(row => (row.Id,row.Position)).SequenceEqual(
                Enumerable.Range(0,6).Select(index => (91981+index,new DungeonPoint(index,0)))) &&
            maze.LayeredMaps.Count == 0 && maze.ConditionalMaps.Count == 0 && maze.StoryStages.Count == 0,
            "D240当前子任务唯一六房91981至91986，不借普通37052路线或相邻任务迷宫");
        var entrance = Entrance(resources.Towns);
        foreach (ushort requested in new ushort[] { 3656,3692,0 }) VerifyCase(requested);
        VerifyDelivery();

        void VerifyDelivery()
        {
            const string account = "bilmarkdelivery";
            Character created;
            using (var owner = new AccountOwners().Acquire(account,() => { }))
            {
                var candidate = CharacterCreation.Prepare("BilmarkBox",resources.Characters.Get(0),resources.Towns.Room,resources.Towns.InitialReturn,resources.Learning.Get);
                var template = resources.ItemResources.Items.Get(10164767);
                var range = ItemSlotBoundService.Range(template.Kind,candidate.Inventory.Expansion);
                short slot = Enumerable.Range(range.Start,range.End-range.Start+1).Select(value => checked((short)value))
                    .First(value => candidate.Inventory.Find(new(InventoryListType.Main,value)) is null);
                created = new CharacterRepository(fixture.Database).Create(owner,candidate with
                {
                    Level=79,CompletedQuests=[3655,3692],ActiveMainQuests=[new(0,3656,1)],
                    InitialLearning=SkillPointLedger.Synchronize(0,0,79,candidate.InitialLearning with
                        {Experience=growth.GetLevelStartExperience(79)},resources.Learning),
                    Inventory=candidate.Inventory with {Items=candidate.Inventory.Items.Add(new(0,new(InventoryListType.Main,slot),
                        InventoryRewardGrantService.Create(template,1)))}
                });
            }
            QuestDailyCoexistence? daily = null;
            using (var peer = Open(fixture,account))
            {
                peer.Select(0,prepared:true);
                daily = new(fixture,created.Id,13047,1656);
                peer.Send(31,Request(31,3693,[])); peer.Expect(1,31); drain(peer,null);
                That(daily!.OtherRows("quest_id,condition_state","ORDER BY quest_id") is [[3656L,0L],[3693L,0L]],
                    "真实31接取有小盒子的零奖励末子，父子归零但不提前归档");
                peer.Send(34,Request(34,3656,[255,255,1,0,255,255]));
                var ack = peer.Expect(1,34).Body; drain(peer,null);
                That(ack.Length > 10 && ack[0] == 1 && U16(ack,1) == 3656 && ack[3] == 44 &&
                    fixture.Query("SELECT quest_id FROM character_quest_completion_receipts WHERE character_id=$p0",created.Id) is [[3656L]] &&
                    daily!.OtherRows("count(*)") is [[0L]] &&
                    fixture.Query("SELECT count(*) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=10164767",created.Id) is [[0L]],
                    "单个正式父34成功，原事务扣一个小盒子、清父子槽并只建立父领奖回执");
                string state = fixture.Snapshot("SELECT * FROM characters WHERE id=$p0",created.Id)+fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid",created.Id);
                foreach (ushort id in new ushort[] {3656,3693})
                {
                    peer.Send(34,Request(34,id,[255,255,1,0,255,255])); peer.Expect(1,34,[0,19,0]); drain(peer,null);
                }
                That(state == fixture.Snapshot("SELECT * FROM characters WHERE id=$p0",created.Id)+fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid",created.Id),
                    "真实父子重复34不再次发装备金币或改资产");
            }
            using (var peer = Open(fixture,account))
            {
                peer.Select(0,prepared:true); peer.Barrier();
                That(peer.CompletedQuestHistory[4+3656] == 1 && peer.CompletedQuestHistory[4+3693] == 1 &&
                    fixture.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0",created.Id) is [[1L]],
                    "重连恢复父子完成位，零奖励末子不另建领奖事务");
            }
        }

        void VerifyCase(ushort requested)
        {
            string account = "bilmark"+requested;
            Character created;
            using (var owner = new AccountOwners().Acquire(account,() => { }))
            {
                // 仅准备等级及入口前置；不预写父子槽、条件、清图或10164767库存。
                var candidate = CharacterCreation.Prepare("Bilmark"+requested,resources.Characters.Get(0),
                    entrance.Position,entrance.Position,resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0,0,79,candidate.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(79) },resources.Learning);
                created = new CharacterRepository(fixture.Database).Create(owner,candidate with
                    { Level = 79,AcceptedStoryLevel = 79,CompletedQuests = entrance.Completed,InitialLearning = learning });
            }
            string parentAcceptance = "", childAcceptance = "";
            long childSlot = -1, deathCount = 0;
            byte[] lastDeath = [];
            QuestDailyCoexistence? daily = null;
            using (var peer = Open(fixture,account))
            {
                peer.Select(0,prepared:true,resumeTown:true,room:false);
                daily = new(fixture,created.Id,13047,1656,progress:true);
                That(peer.Id == created.Id && Materials() == 0,"比尔马克独立账号选中唯一角色且未预置小盒子");
                string before = State();
                peer.Send(31,Request(31,3692,[])); peer.Expect(1,31,[0,18,0]); drain(peer,null);
                That(State() == before,"未接父任务不能提前接取3692子任务");
                peer.Send(31,Request(31,3656,[])); peer.Expect(1,31,[1,72,14,2,0,0,0,0]); drain(peer,null);
                parentAcceptance = Acceptance(3656);
                before = State();
                peer.Send(31,Request(31,3693,[])); peer.Expect(1,31,[0,18,0]); drain(peer,null);
                That(State() == before,"父2阶段拒绝跳过3692顺序提前接取3693");
                if (requested == 3692) AcceptClearChild();
                var map = Enter();
                int enabled = 0,bossChecks = 0;
                var dead = new HashSet<ushort>();
                VerifyMap(0);
                FormalDungeonTcpTests.Load(peer); Drain();
                string activeBeforeExit = fixture.Snapshot("SELECT * FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot",peer.Id);
                peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]);
                peer.Expect(0,24); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
                That(fixture.Snapshot("SELECT * FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot",peer.Id) == activeBeforeExit &&
                    fixture.Query("SELECT count(*) FROM character_quest_clear_receipts WHERE character_id=$p0",peer.Id) is [[0L]] &&
                    fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id) is [[0L]] &&
                    fixture.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0",peer.Id) is [[0L]] &&
                    fixture.Query("SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0",peer.Id) is [[0L]] &&
                    Materials() == 0 && enabled == 0 && bossChecks == 0,
                    "未通关42返回保持原父子接取，不造清图、结算或材料");
                map = Enter(); VerifyMap(0); FormalDungeonTcpTests.Load(peer); Drain();
                That(fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0",peer.Id) is [[2L]],
                    "未清返回后的重进入场分配新尝试，仍保留原接取");
                if (requested != 3692)
                {
                    That(daily!.OtherRows("quest_id,condition_state")
                        is [[3656L,2L]] && Acceptance(3656) == parentAcceptance,
                        "父或普通0入场加载只借子布局，不创建子接取或更改父身份");
                    AcceptClearChild();
                }
                for (int index = 0; index < 5; index++)
                {
                    KillRoom();
                    That(enabled == 0 && bossChecks == 0 && Progress() is [[3656L,2L],[3692L,1L]] &&
                        fixture.Query("SELECT count(*) FROM character_quest_clear_receipts WHERE character_id=$p0",peer.Id) is [[0L]],
                        "前五房死亡不能提前清91986、推进父任务或许可结算");
                    var move = new byte[100]; move[0] = checked((byte)(index+1));
                    peer.Send(45,move); peer.Expect(1,45,[1]); map = peer.Expect(0,29).Body;
                    VerifyMap(index+1); FormalDungeonTcpTests.Load(peer); Drain();
                }
                before = State();
                peer.Send(34,Request(34,3692,[255,255,1,0,255,255])); peer.Expect(1,34,[0,19,0]); Drain();
                That(State() == before && enabled == 0 && bossChecks == 0 && Materials() == 0,
                    "末图加载且Boss未死时不能交付3692、生成材料或许可结算");
                int bossOffset = Enumerable.Range(0,map[18]).Select(at => 19+22*at)
                    .Single(offset => U32(map,offset+8) == 107000205 && map[offset+21] == 0);
                lastDeath = FormalDungeonTcpTests.Death(U16(map,bossOffset+6),peer.Id,map[bossOffset+12]);
                Kill(bossOffset);
                That(deathCount == 24 && enabled == 1 && bossChecks == 1 && Progress() is [[3656L,1L]] &&
                    Acceptance(3656) == parentAcceptance && Materials() == 0 && fixture.Query(
                        "SELECT quest_id,experience_amount FROM character_quest_completion_receipts WHERE character_id=$p0",peer.Id)
                        is [[3692L,0L]],
                    "真实末击只归档零奖励3692、推进原父2到1并许可原终点结算，不凭击杀造小盒子");
                var clearRows = fixture.Query("""
                    SELECT r.acceptance_id,r.quest_slot,r.dungeon_id,r.map_id,r.epoch,g.epoch,g.quest_id,
                      r.instance_id,g.instance_id,r.task_attempt,g.task_attempt
                    FROM character_quest_clear_receipts r JOIN character_dungeon_growth_receipts g
                    ON g.character_id=r.character_id AND g.event_id=r.growth_event_id
                    WHERE r.character_id=$p0 AND r.quest_id=3692
                    """,peer.Id);
                That(clearRows is [[string accepted,long slot,240L,91986L,long clearEpoch,long growthEpoch,long instanceQuest,
                        string instance,string growthInstance,long attempt,long growthAttempt]] &&
                    accepted == childAcceptance && slot == childSlot && clearEpoch == growthEpoch+1 && instanceQuest == requested &&
                    instance == growthInstance && attempt == growthAttempt && fixture.Query(
                        "SELECT count(*) FROM character_dungeon_growth_receipts WHERE character_id=$p0",peer.Id) is [[24L]],
                    "清图回执绑定原3692接取、槽位、实例、attempt及growth的下一epoch，保留原请求号");
                before = State(); peer.Send(39,lastDeath); Drain();
                That(State() == before && enabled == 1 && bossChecks == 1,"重复末击不重发任务、成长或115/31");
                var report = DfoServer.DungeonSettlementProtocolTests.Report(peer.Id,99);
                peer.Send(46,report); Drain();
                That(fixture.Query("SELECT quest_id,map_id,instance_id,task_attempt,epoch FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id)
                    is [[long settledQuest,91986L,string settledInstance,long settledAttempt,long settledEpoch]] && settledQuest == requested &&
                    settledInstance == (string)clearRows[0][7] && settledAttempt == (long)clearRows[0][9] && settledEpoch == (long)clearRows[0][4],
                    "46仅提交原请求实例的91986终点，清图与结算保持同一实例及epoch");
                before = State(); peer.Send(46,report); Drain();
                That(State() == before,"重复46不重发通关奖励或新增结算回执");
                peer.Send(34,Request(34,3656,[255,255,1,0,255,255])); peer.Expect(1,34,[0,19,0]); Drain();
                That(State() == before && Materials() == 0,"仅完成3692不能越过缺材料3693交付父任务");
                peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]);
                peer.Expect(0,24); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
                peer.Send(31,Request(31,3693,[])); peer.Expect(1,31,[1,109,14,1,0,0,0,0]); Drain();
                That(Progress() is [[3656L,1L],[3693L,1L]] && Materials() == 0,"无料3693真实接取保留未达成，不冒充3649奖励来源");
                before = State();
                peer.Send(33,Request(33,3693,[0,0])); peer.Expect(1,33,[1,109,14,1,0,0,0]); Drain();
                peer.Send(34,Request(34,3693,[255,255,1,0,255,255])); peer.Expect(1,34,[0,19,0]); Drain();
                peer.Send(34,Request(34,3656,[255,255,1,0,255,255])); peer.Expect(1,34,[0,19,0]); Drain();
                That(State() == before && Acceptance(3656) == parentAcceptance && Materials() == 0,
                    "无材料33不能伪造小盒子或归档3693，子父34保持原接取和全部资产");

                void AcceptClearChild()
                {
                    peer.Send(31,Request(31,3692,[])); peer.Expect(1,31,[1,108,14,1,0,0,0,0]); drain(peer,null);
                    childAcceptance = Acceptance(3692);
                    childSlot = (long)fixture.Query("SELECT slot FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3692",peer.Id)[0][0];
                    That(childAcceptance != parentAcceptance && Progress() is [[3656L,2L],[3692L,1L]],
                        "3692仅经真实31创建独立接取身份，父仍待两子任务");
                }
                byte[] Enter()
                {
                    peer.Send(15,new byte[4]); peer.Expect(1,15,[1]);
                    peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),1]); peer.Expect(0,27,new byte[42]);
                    var selection = Convert.FromHexString("030000000000000000FFFF0000000000490C00000000000000");
                    BinaryPrimitives.WriteUInt32LittleEndian(selection,240);
                    BinaryPrimitives.WriteUInt32LittleEndian(selection.AsSpan(16),requested);
                    peer.Send(16,selection);
                    var information = peer.Expect(0,28).Body;
                    That(information.Length == 36 && U32(information,0) == 240 && information[7] == maze.SourceOrdinal &&
                        information[8] == 5 && information[9] == 0,
                        "父、子及普通0请求均选择3692实际迷宫及原Boss坐标："+requested);
                    return peer.Expect(0,29).Body;
                }
                void VerifyMap(int index)
                {
                    That(map.Length >= 19+22*map[18] && U32(map,14) == 91981+index && map[0] == index && map[1] == 0 &&
                        Enumerable.Range(0,map[18]).Select(at => 19+22*at).Where(at => map[at+21] == 0)
                            .Select(at => checked((int)U32(map,at+8))).SequenceEqual(Monsters[index]),
                        "完整真实MAP及静态敌人名册："+(91981+index));
                }
                void KillRoom()
                {
                    foreach (int offset in Enumerable.Range(0,map[18]).Select(at => 19+22*at).Where(at => map[at+21] == 0)) Kill(offset);
                }
                void Kill(int offset)
                {
                    ushort key = U16(map,offset+6);
                    That(dead.Add(key),"六房死亡只使用本实例唯一key");
                    peer.Send(39,FormalDungeonTcpTests.Death(key,peer.Id,map[offset+12])); Drain(); deathCount++;
                }
                void Drain() => drain(peer,packet =>
                {
                    if (packet.Opcode == 31) { Bytes(packet.Body,[],"正式通关31空正文"); enabled++; }
                    if (packet.Opcode == 115) bossChecks++;
                });
            }
            using (var peer = Open(fixture,account))
            {
                peer.Select(0,prepared:true,resumeTown:true,room:false); peer.Barrier();
                That(peer.Id == created.Id && Progress() is [[3656L,1L],[3693L,1L]] && Acceptance(3656) == parentAcceptance &&
                    peer.CompletedQuestHistory[4+3692] == 1 && peer.CompletedQuestHistory[4+3656] == 0 && peer.CompletedQuestHistory[4+3693] == 0 &&
                    fixture.Query("SELECT quest_id FROM character_quest_completion_receipts WHERE character_id=$p0",peer.Id) is [[3692L]] &&
                    fixture.Query("SELECT map_id FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id) is [[91986L]] && Materials() == 0,
                    "回城重连仅恢复3692及原结算凭据，父与无料子保持进行中");
                string before = State(); peer.Send(39,lastDeath); drain(peer,null);
                That(State() == before,"重连迟到39不能复活旧比尔马克实例或改变剩余寻物任务");
            }

            string Acceptance(ushort quest) => (string)fixture.Query(
                "SELECT acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",created.Id,quest)[0][0];
            object[][] Progress() => daily!.OtherRows("quest_id,condition_state","ORDER BY quest_id");
            long Materials() => (long)fixture.Query("SELECT coalesce(sum(json_extract(item_core,'$.Value')),0) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=10164767",created.Id)[0][0];
            string State() => fixture.Snapshot("SELECT * FROM characters WHERE id=$p0",created.Id)+
                fixture.Snapshot("SELECT * FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot",created.Id)+
                fixture.Snapshot("SELECT * FROM character_quest_clear_receipts WHERE character_id=$p0",created.Id)+
                fixture.Snapshot("SELECT * FROM character_dungeon_growth_receipts WHERE character_id=$p0 ORDER BY combat_sequence",created.Id)+
                fixture.Snapshot("SELECT * FROM character_dungeon_settlement_receipts WHERE character_id=$p0",created.Id)+
                fixture.Snapshot("SELECT * FROM character_quest_completion_receipts WHERE character_id=$p0 ORDER BY quest_id",created.Id)+
                fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid",created.Id)+
                fixture.Snapshot("SELECT * FROM character_inventory_slots WHERE character_id=$p0 ORDER BY list_type,slot_index",created.Id)+
                fixture.Snapshot("SELECT * FROM account_currency_balances WHERE account=$p0 ORDER BY currency_kind",account);
        }
    }

    static (Location Position,int[] Completed) Entrance(TownCatalog towns)
    {
        foreach (var area in towns.Areas.Values.Where(row => row.Level <= 79 && row.DungeonWorldMap is not null))
        foreach (var option in towns.DungeonMaps[area.DungeonWorldMap!.Value].Options.Where(row => row.DungeonId == 240 &&
            (row.QuestId == -1 || row.InProgress && row.QuestId == 3656 || !row.InProgress && row.QuestId is not (3656 or 3692 or 3693))))
        {
            var done = new HashSet<int>(area.Quests) { 3655 };
            if (area.AnyQuests.Length > 0) done.Add(area.AnyQuests[0]);
            if (!option.InProgress && option.QuestId > 0) done.Add(option.QuestId);
            if (done.Any(id => id is 3656 or 3692 or 3693) || area.ExceptQuests.Any(done.Contains)) continue;
            foreach (var gate in area.Map(done).DungeonGates.Where(row => row.Conditions.Length == 0 &&
                row.Quests.All(id => id is not (3656 or 3692 or 3693))))
            {
                var completed = new HashSet<int>(done); completed.UnionWith(gate.Quests);
                var map = area.Map(completed);
                var walk = map.Bounds.FirstOrDefault(row => row.Conditions.Length == 0 && row.Quests.All(completed.Contains));
                if (walk is null || area.ExceptQuests.Any(completed.Contains) ||
                    !map.DungeonGates.Any(row => row.Conditions.Length == 0 && row.Quests.All(completed.Contains))) continue;
                return (new(area.Town,area.Area,checked((ushort)(walk.Bounds.X+walk.Bounds.Width/2)),
                    checked((ushort)(walk.Bounds.Y+walk.Bounds.Height/2))),completed.Order().ToArray());
            }
        }
        throw new InvalidDataException("D240没有不预先完成3656父子任务的合法79级城镇入口");
    }

    static byte[] Request(byte command,ushort quest,ReadOnlySpan<byte> tail)
    {
        var body = new byte[4+tail.Length]; body[0] = command;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2),quest); tail.CopyTo(body.AsSpan(4)); return body;
    }
}
