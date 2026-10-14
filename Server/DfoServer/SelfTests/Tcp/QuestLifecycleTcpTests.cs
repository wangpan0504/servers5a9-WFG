using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class QuestLifecycleTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        // 合成定义验证寻物/选择/零XP和已有父任务的wire组合，不冒充实际PVF或Windows界面验收。
        var synthetic = QuestEligibilityTests.Quest(29990) with { Condition = "[seeking]", IntData = [10165055,3],
            Attributes = ["not give exp quest"], Rewards = [new(0,2058,null),new(0,3,null)],
            Definition = new([]) { Job = "[all]", AcceptanceRewards = [new(0,10165055,null),new(0,3,null)],
                SelectRewards = [new(0,10403,null),new(0,1,null),new(0,2058,null),new(0,2,null)] } };
        var parent = QuestEligibilityTests.Quest(29991) with { Condition = "[quest clear]", SubType = 2, IntData = [29992,29993],
            Attributes = ["not give exp quest"], Rewards = [new(0,2058,null),new(0,1,null)] };
        var meeting = QuestEligibilityTests.Quest(29992) with { MainQuest = 29991,
            Rewards = [new(0,0,null),new(0,0,null)], Attributes = ["not give exp quest"] };
        var seeking = synthetic with { Id = 29993, MainQuest = 29991, Condition = "[seek n meet npc]", IntData = [10165055,3,3033,2,1],
            Rewards = [new(0,0,null),new(0,0,null)],
            Definition = synthetic.Definition! with { SelectRewards = [] } };
        var hunting = QuestEligibilityTests.Quest(29994) with { Condition = "[hunt enemy]", IntData = [3,-1,1,1,2], Attributes = ["not give exp quest"],
            Definition = new([]) { Job = "[all]", Drops = [new(1,1,3,-1,10165055,1,100,2)] } };
        var huntMonster = hunting with { Id = 29987, Condition = "[hunt monster]", IntData = [3,-1,1,2],
            Definition = new([]) { Job = "[all]" } };
        var seekMeet = synthetic with { Id = 29995, Condition = "[seek n meet npc]", IntData = [10165055,3,1], Rewards = [],
            Definition = synthetic.Definition! with { SelectRewards = [] } };
        var range = QuestEligibilityTests.Quest(29996) with { Condition = "[reach the range]", SubType = 1,
            IntData = [38,0,400,150,300,300], Attributes = ["not give exp quest"] };
        var cinematic = resources.Quests.Get(3158) with { Id = 29997, MinimumLevel = 1, Prerequisites = [], Attributes = ["not give exp quest"], Rewards = [], DeclaredGoldMultiple = null,
            Definition = new([
                new("auto accept",[new(6,0,"enter town"),new(0,38,null)],[]),
                new("go area guide",[new(0,38,null),new(0,0,null),new(0,600,null),new(0,250,null),new(6,0,"[bottom]")],[])
            ]) { Job = "[all]" } };
        var catalog = resources.Quests.All.ToDictionary(quest => quest.Id);
        var repeat = QuestEligibilityTests.Quest(29998) with { Grade = "[normaly repeat]", Attributes = ["not give exp quest"] };
        var daily = repeat with { Id = 29999, Grade = "[daily]" };
        var account = repeat with { Id = 29989, Grade = "[common unique]",
            Definition = new([]) { Job = "[all]", AccountQuest = true } };
        var batch = synthetic with { Id = 29986, Grade = "[normaly repeat]", Rewards = [new(0,2058,null),new(0,3,null),new(0,3033,null),new(0,3,null)],
            Definition = new([]) { Job = "[all]", AcceptanceRewards = [new(0,10165055,null),new(0,6,null)] } };
        var wallet = batch with { Id = 29985, Grade = "[epic]", IntData = [3033,2], Rewards = [new(0,3035,null),new(0,4,null)],
            Definition = new([]) { Job = "[all]" } };
        var question = repeat with { Id = 29984, Grade = "[epic]", Condition = "[question]", CompleteNpc = -1,
            Definition = new([new("question info",[new(6,0,"title"),new(6,0,"message"),new(6,0,"first"),new(6,0,"second")],[])]) { Job = "[all]" } };
        var firstAnswer = repeat with { Id = 29982, Grade = "[epic]", Definition = new([]) { Job = "[all]", RequiredAnswers = [new(29984,0)] } };
        var secondAnswer = firstAnswer with { Id = 29983, Definition = firstAnswer.Definition! with { RequiredAnswers = [new(29984,1)] } };
        var equipmentSlot = repeat with { Id = 29981, Grade = "[side]", Condition = "[seeking]", IntData = [0,100], CompleteNpc = -1,
            RewardType = "[slot expansion]", Rewards = [new(0,2,null)] };
        foreach (var quest in new[] { synthetic,parent,meeting,seeking,hunting,huntMonster,seekMeet,range,cinematic,repeat,daily,account,batch,wallet,question,firstAnswer,secondAnswer,equipmentSlot }) catalog.Add(quest.Id, quest);
        await using var f = new TcpTestFixture(options, Path.Combine(root, "quests"), resources: resources, questCatalog: new(catalog));
        await f.StartAsync();
        using (var peer = Open(f))
        {
            peer.Create("QuestWire");
            // 合成已完成前置的隔离fixture；生产不回填现场角色或伪造清图。
            f.Sql("DELETE FROM character_active_main_quests; INSERT INTO character_completed_quests(character_id,quest_id) VALUES(1,3145)");
            peer.Select(prepared: true);
            byte[] initialHistory = peer.CompletedQuestHistory;
            That(List(peer.AvailableQuests, 1).Contains(4873), "完成前置后登录列表下发真实4873定义");
            peer.Send(31, Convert.FromHexString("1F000913"));
            peer.Expect(1, 31, Convert.FromHexString("0109130100000000"));
            That(List(peer.Expect(0, 21).Body, 1).Contains(4873), "已接取任务保留在列表，不能重复接取");
            Bytes(peer.Expect(0,356).Body, initialHistory, "接取下一任务后完成历史不丢失，仍含真实3145完成位");
            string first = (string)f.Query("SELECT acceptance_id FROM character_active_main_quests WHERE quest_id=4873")[0][0];
            string state = State();
            peer.Send(31, Convert.FromHexString("1F000913")); peer.Expect(1, 31, [0, 18, 0]); peer.Barrier();
            That(State() == state, "真实TCP重复接取正常拒绝且不保存");
            peer.Send(33, Convert.FromHexString("210009130000")); peer.Expect(1, 33, [0, 19, 0]); peer.Barrier();
            That(State() == state, "目标NPC不在当前房间时客户端报告不能完成会面");
            peer.Travel(38, 0, 622, 196, 38, 1, bottom: true);
            peer.Send(33, Convert.FromHexString("210009130000")); peer.Expect(1, 33, Convert.FromHexString("01091300000000"));
            peer.Barrier();
            That(f.Query("SELECT condition_state FROM character_active_main_quests WHERE quest_id=4873") is [[0L]],
                "实际NPC场景中的33完成会面，仅发ACK保留对话顺序");
            state = State();
            peer.Send(33, Convert.FromHexString("210009130000")); peer.Expect(1, 33, Convert.FromHexString("01091300000000")); peer.Barrier();
            That(State() == state, "已达成条件重复请求没有新的writer或revision");
            peer.Send(33, Convert.FromHexString("21000913100000")); peer.Expect(1, 33, [0, 19, 0]); peer.Barrier();
            That(State() == state, "多通道不能借单会面类型推进任务");
            peer.Send(32, Convert.FromHexString("20000913")); peer.Expect(1, 32, Convert.FromHexString("010913"));
            That(List(peer.Expect(0, 21).Body, 1).Contains(4873), "放弃后重新计算可接列表");
            Bytes(peer.Expect(0,356).Body, initialHistory, "放弃任务不清除之前已完成的历史");
            peer.Send(31, Convert.FromHexString("1F000913")); peer.Expect(1, 31, Convert.FromHexString("0109130100000000"));
            peer.Expect(0, 21); peer.Expect(0,356);
            That(f.Query("SELECT acceptance_id FROM character_active_main_quests WHERE quest_id=4873") is [[string second]] &&
                second != first && f.Query("SELECT count(*) FROM character_quest_lifecycle_receipts") is [[4L]],
                "接受、条件、放弃、重接各持久化一次，新接取身份独立");
        }
        using (var peer = Open(f))
        {
            peer.Select(prepared: true);
            That(List(peer.AvailableQuests, 1).Contains(4873) && f.Query("SELECT condition_state FROM character_active_main_quests WHERE quest_id=4873") is [[1L]],
                "重连完整列表和活动状态使用已提交新接取");
            peer.Travel(38, 0, 622, 196, 38, 1, bottom: true);
            peer.Send(33, Convert.FromHexString("210009130000")); peer.Expect(1, 33, Convert.FromHexString("01091300000000"));
            long revision = (long)f.Query("SELECT revision FROM characters")[0][0];
            peer.Send(34, Convert.FromHexString("22000913FFFF0100FFFF"));
            byte[] acknowledgement = Finished(peer).Body;
            That(acknowledgement.Length == 29 && acknowledgement[0] == 1 &&
                BinaryPrimitives.ReadUInt16LittleEndian(acknowledgement.AsSpan(1)) == 4873 && acknowledgement[3] == 4 &&
                BinaryPrimitives.ReadUInt32LittleEndian(acknowledgement.AsSpan(4)) == 30 && acknowledgement[8] == 0 && acknowledgement[9] == 1 &&
                BinaryPrimitives.ReadUInt16LittleEndian(acknowledgement.AsSpan(10)) == 0 &&
                BinaryPrimitives.ReadUInt32LittleEndian(acknowledgement.AsSpan(12)) == 0 &&
                BinaryPrimitives.ReadUInt32LittleEndian(acknowledgement.AsSpan(16)) == 101,
                "真实4873按本服G百分比发30XP、item0声明发101Gold，按会面分类完整投影");
            peer.Expect(0, 14); peer.Expect(0, 37);
            int[] available = List(peer.Expect(0, 21).Body, 1);
            var completed4873 = peer.Expect(0,356).Body;
            That(completed4873.Length == 30004 && completed4873[4+4873] == 1,"整次投影最后同步真实4873完成位");
            That(!available.Contains(4873) && !available.Contains(3146), "交付后列表不再显示4873，不因前置完成越过Lv3资格");
            That(f.Query("SELECT revision,experience,gold FROM characters") is [[long next,30L,101L]] && next == revision + 1,
                "普通交付一次revision保存XP和Gold");
            That(f.Query("SELECT quest_id,condition_type,event_id IS NULL,instance_id IS NULL FROM character_quest_completion_receipts") is [[4873L,4L,1L,1L]],
                "普通交付保存完整回执，不伪造清图");
            string after = f.Snapshot();
            peer.Send(34, Convert.FromHexString("22000913FFFF0100FFFF")); Finished(peer, [0,19,0]); peer.Barrier();
            That(f.Snapshot() == after, "已完成普通交付正常拒绝，不重复发XP、金币或成功增量");
        }
        using (var peer = Open(f))
        {
            peer.Select(prepared: true); peer.Travel(38,0,622,196,38,1,bottom: true);
            foreach (var (id,type) in new (ushort,byte)[] { (29995,25),(29996,64),(29997,65) })
            {
                peer.Send(31,[(byte)31,0,(byte)id,(byte)(id>>8)]);
                byte[] accepted = peer.Expect(1,31).Body;
                That(accepted[0] == 1 && U16(accepted,1) == id && U32(accepted,3) == (type == 25 ? 512 : 1),
                    "复合及场景目标按独立类型接取 " + type);
                if (type == 25) peer.Expect(0,14);
                peer.Expect(0,21); peer.Expect(0,356);
                peer.Send(33,type == 25 ? [(byte)33,0,(byte)id,(byte)(id>>8),0x20,0,0] : [(byte)33,0,(byte)id,(byte)(id>>8),0,0]);
                peer.Expect(1,33,[1,(byte)id,(byte)(id>>8),0,0,0,0]);
                peer.Send(34,[(byte)34,0,(byte)id,(byte)(id>>8),255,255,1,0,255,255]);
                byte[] finished = Finished(peer).Body;
                That(finished[0] == 1 && U16(finished,1) == id && finished[3] == type && U32(finished,4) == 0,
                    "业务提交后ACK34保持本版真实分类、编号和零经验 " + type);
                if (type == 25)
                {
                    That(finished.Length == 18 && finished[8] == 1 && finished[9] == 0 && U32(finished,12) == 3 &&
                        finished[16] == 0 && finished[17] == 0,"寻物会面材料数组消费后没有额外奖励尾部");
                    peer.Expect(0,14);
                }
                else That(finished.Length == 10 && finished[8] == 0 && finished[9] == 0,"范围及演出跳过材料数组，空奖励回包完整");
                peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
                That(f.Query($"SELECT condition_type FROM character_quest_completion_receipts WHERE quest_id={id}") is [[long condition]] && condition == type,
                    "TCP回包与持久领奖分类一致 " + type);
            }
        }
        object[] dungeonState = f.Query("SELECT * FROM character_dungeon_state WHERE character_id=1").Single();
        f.Sql("DELETE FROM character_dungeon_state WHERE character_id=1");
        using (var peer = Open(f))
        {
            peer.Select(prepared: true);
            peer.Travel(38, 0, 622, 196, 38, 1, bottom: true);
            peer.Send(31, Convert.FromHexString("1F002675"));
            byte[] accepted = peer.Expect(1,31).Body;
            That(accepted.Length == 18 && accepted[0] == 1 && BinaryPrimitives.ReadUInt32LittleEndian(accepted.AsSpan(3)) == 0 && accepted[7] == 1 &&
                BinaryPrimitives.ReadUInt32LittleEndian(accepted.AsSpan(10)) == 10165055 && BinaryPrimitives.ReadUInt32LittleEndian(accepted.AsSpan(14)) == 3,
                "合成寻物接取按10B赠品行下发实际三件，初始库存即达成");
            peer.Expect(0,14); peer.Expect(0,21); peer.Expect(0,356);
            string before = f.Snapshot();
            peer.Send(34,Convert.FromHexString("2200267502000100FFFF")); Finished(peer,[0,19,0]); peer.Barrier();
            That(f.Snapshot() == before, "错误选择不能先消耗任务材料");
            peer.Send(34,Convert.FromHexString("2200267501000100FFFF"));
            byte[] complete = Finished(peer).Body;
            That(complete.Length == 37 && complete[0] == 1 && complete[3] == 0 && BinaryPrimitives.ReadUInt32LittleEndian(complete.AsSpan(4)) == 0 &&
                complete[8] == 1 && complete[9] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(complete.AsSpan(10)) == 177 &&
                BinaryPrimitives.ReadUInt32LittleEndian(complete.AsSpan(12)) == 3 && complete[16] == 0 && complete[17] == 1 &&
                BinaryPrimitives.ReadUInt32LittleEndian(complete.AsSpan(20)) == 2058 && BinaryPrimitives.ReadUInt32LittleEndian(complete.AsSpan(24)) == 5,
                "合成寻物ACK34写消耗增量3与固定/选择合并增量5，零XP无需不存在的成长附属owner");
            peer.Expect(0,14); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
            That(f.Query("SELECT experience,gold FROM characters") is [[30L,101L]] &&
                f.Query("SELECT count(*) FROM character_active_main_quests") is [[0L]] &&
                f.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE quest_id=29990 AND experience_amount=0 AND condition_type=0") is [[1L]],
                "寻物扣除/奖励/清槽/零经验回执完整落库，其他余额保留");
        }
        f.Sql("INSERT INTO account_wallets(account) SELECT account FROM characters WHERE id=1 ON CONFLICT(account) DO NOTHING; " +
            "INSERT INTO account_currency_balances(account,currency_kind,balance) SELECT account,$p0,2 FROM characters WHERE id=1 ON CONFLICT(account,currency_kind) DO UPDATE SET balance=2",
            (int)DfoServer.Game.Currency.CurrencyKind.BlackCube);
        using (var peer = Open(f))
        {
            peer.Select(prepared: true); peer.Travel(38,0,622,196,38,1,bottom: true);
            peer.Send(31,Convert.FromHexString("1F002775")); peer.Expect(1,31,Convert.FromHexString("0127750200000000")); peer.Expect(0,21); peer.Expect(0,356);
            That(!List(peer.AvailableQuests,1).Contains(29992), "隐藏子任务不由全量普通列表提前暴露");
            string before = f.Snapshot();
            peer.Send(34,Convert.FromHexString("22002775FFFF0100FFFF")); Finished(peer,[0,19,0]); peer.Barrier();
            That(f.Snapshot() == before, "新父任务不能提前领取自身奖励");
            peer.Send(31,Convert.FromHexString("1F002975")); peer.Expect(1,31,[0,18,0]); peer.Barrier();
            That(f.Snapshot() == before, "真实TCP不能跳接尚未到达的第二个子任务");
            peer.Send(31,Convert.FromHexString("1F002875")); peer.Expect(1,31,Convert.FromHexString("0128750100000000")); peer.Expect(0,21); peer.Expect(0,356);
            peer.Send(33,Convert.FromHexString("210028750000")); peer.Expect(1,33,Convert.FromHexString("01287500000000"));
            peer.Barrier();
            peer.Send(34,Convert.FromHexString("22002875FFFF0100FFFF")); Finished(peer,[1,40,117,4,0,0,0,0,0,0]);
            peer.Expect(0,574); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
            That(f.Query("SELECT quest_id,condition_state FROM character_active_main_quests") is [[29991L,1L]] &&
                f.Query("SELECT count(*) FROM character_completed_quests WHERE quest_id=29992") is [[1L]],
                "零物品占位会面33保留对话，真实子34归档并推进父剩余为1，不发经验或金币");
            peer.Send(31,Convert.FromHexString("1F002975"));
            var accept = peer.Expect(1,31).Body;
            That(accept.Length == 18 && BinaryPrimitives.ReadUInt32LittleEndian(accept.AsSpan(3)) == 512 && accept[7] == 1,
                "第二个寻物会面子任务赠品已足额，仍等待NPC通道，接取身份独立");
            ushort materialSlot = BinaryPrimitives.ReadUInt16LittleEndian(accept.AsSpan(8));
            peer.Expect(0,14); peer.Expect(0,21); peer.Expect(0,356);
            long revision = (long)f.Query("SELECT revision FROM characters")[0][0];
            peer.Send(33,Convert.FromHexString("21002975100000")); peer.Expect(1,33,Convert.FromHexString("01297500020000")); peer.Barrier();
            That((long)f.Query("SELECT revision FROM characters")[0][0] == revision,
                "材料通道重算不代替NPC完成，也不生成无变化写入");
            peer.Send(33,Convert.FromHexString("21002975200000")); peer.Expect(1,33,Convert.FromHexString("01297500000000"));
            peer.Barrier();
            That(f.Query("SELECT quest_id,condition_state FROM character_active_main_quests") is [[29991L,1L],[29993L,0L]],
                "NPC终末33只持久化子归零，不提前重建任务引导或归档");
            peer.Send(33,Convert.FromHexString("210027750000")); peer.Expect(1,33,Convert.FromHexString("01277500000000"));
            var inventory = peer.Expect(0,14).Body;
            That(U16(inventory,1) == 2 && U16(inventory,3) == materialSlot && U32(inventory,5) == uint.MaxValue &&
                U16(inventory,126) == 354 && U32(inventory,132) == 0,
                "父33同步实体材料删除及实际钱包扣除，保留原生对话边界");
            peer.Barrier();
            That(f.Query("SELECT quest_id,condition_state FROM character_active_main_quests") is [[29991L,0L]] &&
                f.Query("SELECT revision,experience,gold FROM characters") is [[long next,30L,101L]] && next == revision + 2 &&
                f.Query("SELECT count(*) FROM character_completed_quests WHERE quest_id=29991") is [[0L]] &&
                f.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE quest_id IN(29992,29993)") is [[2L]],
                "NPC条件先持久化，末子交付与父归零同一次revision，父未自动完成或领奖");
            peer.Send(33,Convert.FromHexString("210027750000")); peer.Expect(1,33,Convert.FromHexString("01277500000000")); peer.Barrier();
        }
        using (var peer = Open(f))
        {
            peer.Select(prepared: true); peer.Barrier();
            That(f.Query("SELECT quest_id,condition_state FROM character_active_main_quests") is [[29991L,0L]] &&
                f.Query("SELECT count(*) FROM character_completed_quests WHERE quest_id IN(29992,29993)") is [[2L]],
                "父子任务在断线重登后保留真实提交状态");
            peer.Travel(38,0,622,196,38,1,bottom: true);
            long revision = (long)f.Query("SELECT revision FROM characters")[0][0];
            peer.Send(34,Convert.FromHexString("22002775FFFF0100FFFF"));
            byte[] finish = Finished(peer).Body;
            That(finish.Length == 29 && finish[3] == 44 && finish[9] == 1 && U32(finish,12) == 2058 && U32(finish,16) == 1,
                "父任务ACK34分类44只发实际父奖励增量，不再发子奖励或消耗段");
            peer.Expect(0,14); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
            That(f.Query("SELECT revision,experience,gold FROM characters") is [[long next,30L,101L]] && next == revision + 1 &&
                f.Query("SELECT condition_type,event_id IS NULL FROM character_quest_completion_receipts WHERE quest_id=29991") is [[44L,1L]],
                "父交付同事务保存自身奖励与分类44回执，不伪造清图或经验");
        }
        // 模拟旧跳过留下的已完成父任务与终末子接取，复验选角后的574→21→356恢复。
        f.Sql("DELETE FROM character_completed_quests WHERE quest_id=29993; INSERT INTO character_active_main_quests VALUES(1,5,29993,0,'8d9a7f7bb6d94979b1ab3f519b90a652')");
        var orphanRevision = f.Query("SELECT revision,experience,gold FROM characters")[0];
        using (var peer = Open(f))
        {
            peer.Select(prepared: true);
            peer.Expect(0,574,[0,0,0,0]); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
            That(f.Query("SELECT count(*) FROM character_active_main_quests") is [[0L]] &&
                f.Query("SELECT count(*) FROM character_completed_quests WHERE quest_id IN(29991,29993)") is [[2L]] &&
                f.Query("SELECT revision,experience,gold FROM characters") is [[long restored,long xp,long gold]] &&
                restored == (long)orphanRevision[0]+2 && xp == (long)orphanRevision[1] && gold == (long)orphanRevision[2],
                "TCP重选进入房间一次revision、孤立终末子归档一次revision，完成356位于清空574之后，资产保持");
            That(f.Query("SELECT experience_amount,gold_amount,consumed FROM character_quest_completion_receipts WHERE acceptance_id='8d9a7f7bb6d94979b1ab3f519b90a652'") is [[0L,0L,"[]"]],
                "历史归档复用原子回执，材料已缺失也不重新消费或发奖");
            peer.Send(34,[34,0,41,117,255,255,1,0,255,255]); Finished(peer,[0,19,0]); peer.Barrier();
            That(f.Query("SELECT revision FROM characters") is [[long same]] && same == (long)orphanRevision[0]+2,
                "归档后重复34不再次保存或制造奖励");
        }
        // 为猎杀隔离用例恢复本fixture的合法3145接取；不写现场库或据此证明完整剧情推进。
        f.Sql("INSERT INTO character_dungeon_state VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6)", dungeonState);
        f.Sql("DELETE FROM character_active_main_quests; DELETE FROM character_completed_quests WHERE quest_id=3145; INSERT INTO character_active_main_quests VALUES(1,0,3145,1,'1234567890abcdef1234567890abcdef')");
        using (var peer = Open(f))
        {
            peer.Select(prepared: true);
            peer.Send(31,Convert.FromHexString("1F002A75")); peer.Expect(1,31,Convert.FromHexString("012A750200000000")); peer.Expect(0,21); peer.Expect(0,356);
            peer.Send(31,[31,0,35,117]); peer.Expect(1,31,[1,35,117,2,0,0,0,0]); peer.Expect(0,21); peer.Expect(0,356);
            string before = State();
            peer.Send(33,Convert.FromHexString("21002A75100000")); peer.Expect(1,33,[0,19,0]); peer.Barrier();
            That(State() == before, "城镇客户端猎杀报告不能生成死亡或减少次数");
            FormalDungeonTcpTests.EntryTown(peer);
            var map = FormalDungeonTcpTests.Enter(peer); FormalDungeonTcpTests.Load(peer);
            before = State();
            foreach (int length in new[] { 52,74 })
            {
                byte[] passive = new byte[length];
                BinaryPrimitives.WriteUInt32LittleEndian(passive,13099);
                BinaryPrimitives.WriteUInt16LittleEndian(passive.AsSpan(4),ushort.MaxValue);
                passive[37] = 1; BinaryPrimitives.WriteUInt32LittleEndian(passive.AsSpan(42),1);
                FormalDungeonTcpTests.SealDeath(passive);
                peer.Send(39,passive); peer.Barrier();
                That(State() == before, "被动物件格式不关闭连接，也不向未确认地图制造物件死亡或任务进度 " + length);
                passive[^1] ^= 1;
                peer.Send(39,passive); peer.Barrier();
                That(State() == before, "被动物件摘要不匹配没有死亡、经验或revision " + length);
            }
            peer.Send(33,Convert.FromHexString("21002A75100000")); peer.Expect(1,33,Convert.FromHexString("012A7502000000")); peer.Barrier();
            That(State() == before, "正式实例内的33仅读取真实剩余2，不按客户端0减少");
            for (int at = 0; at < 2; at++)
            {
                int offset = 19 + at * 22;
                var death = FormalDungeonTcpTests.Death(U16(map,offset + 6),peer.Id,map[offset + 12]);
                peer.Send(39,death); peer.Expect(0,38); peer.Expect(0,37);
                byte[] granted = peer.Expect(0,14).Body;
                That(granted.Length == 126 && granted[0] == 0 && U16(granted,1) == 1 && U16(granted,3) == 177 &&
                    U32(granted,5) == 10165055 && U32(granted,9) == at + 1,
                    "任务掉落通知独立消费Main、槽位和完整123B核心，数量等于实际已入库堆叠");
                That(f.Query("SELECT json_extract(item_core,'$.Value') FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=10165055") is [[long held]] && held == at + 1 &&
                    f.Query("SELECT count(*) FROM character_quest_drop_receipts") is [[long facts]] && facts == at + 1,
                    "TCP任务物品核心与每条真实死亡的持久掉落回执同步");
                byte[] states = peer.Expect(0,574).Body;
                That(states.Length == 22 && U32(states,0) == 3 && U16(states,10) == 29994 && U32(states,12) == 1 - at &&
                    U16(states,16) == 29987 && U32(states,18) == 1-at,
                    "同一权威死亡同事务推进分类6/2，并完整574投影两项真实剩余次数");
                before = State();
                peer.Send(39,death); peer.Barrier();
                That(State() == before, "重复死亡没有额外任务推进、XP或revision");
            }
            peer.Send(33,Convert.FromHexString("21002A75100000")); peer.Expect(1,33,Convert.FromHexString("012A7500000000")); peer.Barrier();
            peer.Send(33,[33,0,35,117,16,0,0]); peer.Expect(1,33,[1,35,117,0,0,0,0]); peer.Barrier();
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,24); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
        }
        using (var peer = Open(f))
        {
            peer.Select(prepared: true); peer.Travel(38,0,622,196,38,1,bottom: true);
            long revision = (long)f.Query("SELECT revision FROM characters")[0][0];
            peer.Send(34,Convert.FromHexString("22002A75FFFF0100FFFF")); Finished(peer,Convert.FromHexString("012A7506000000000000")); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
            That(f.Query("SELECT count(*) FROM character_completed_quests WHERE quest_id=29994") is [[1L]] &&
                f.Query("SELECT condition_type,event_id IS NULL FROM character_quest_completion_receipts WHERE quest_id=29994") is [[6L,1L]] &&
                f.Query("SELECT revision FROM characters") is [[long next]] && next == revision + 1,
                "重登后猎杀在实际NPC处零资产交付，分类6回执一次保存，不伪造清图");
            peer.Send(34,[34,0,35,117,255,255,1,0,255,255]); Finished(peer,[1,35,117,2,0,0,0,0,0,0]); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
            That(f.Query("SELECT condition_type,event_id IS NULL FROM character_quest_completion_receipts WHERE quest_id=29987") is [[2L,1L]],
                "分类2按独立ACK34枚举保存，重登后用真实达成进度交付");
        }
        using (var peer = Open(f))
        {
            peer.Select(prepared: true); peer.Travel(38,0,622,196,38,1,bottom: true);
            foreach (ushort id in new ushort[] { 29998,29998,29999 })
            {
                bool repeated = f.Query($"SELECT quest_id FROM character_completed_quests WHERE quest_id={id}").Length != 0;
                peer.Send(31,[31,0,(byte)id,(byte)(id>>8)]); peer.Expect(1,31,[1,(byte)id,(byte)(id>>8),1,0,0,0,0]);
                peer.Expect(0,21);
                byte[] flags = peer.Expect(0,356).Body;
                if (repeated)
                {
                    That(flags.Length == 30004 && U32(flags,0) == 30000 && flags[4 + id] == 0,
                        "重复接取下发已提交完成表，当前完成位清零");
                }
                peer.Send(33,[33,0,(byte)id,(byte)(id>>8),0,0]); peer.Expect(1,33,[1,(byte)id,(byte)(id>>8),0,0,0,0]);
                peer.Send(34,[34,0,(byte)id,(byte)(id>>8),255,255,1,0,255,255]); Finished(peer,[1,(byte)id,(byte)(id>>8),4,0,0,0,0,0,0]);
                byte[] available = peer.Expect(0,21).Body; peer.Expect(0,356); peer.Barrier();
                byte level = checked((byte)(long)f.Query("SELECT level FROM characters")[0][0]);
                That(List(available,level).Contains(id) == (id == 29998),
                    "TCP完成后按重复/每日规则刷新可接资格 " + id);
            }
            string before = f.Snapshot();
            peer.Send(31,[31,0,47,117]); peer.Expect(1,31,[0,18,0]); peer.Barrier();
            That(f.Snapshot() == before && f.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE quest_id=29998") is [[2L]] &&
                f.Query("SELECT daily_period FROM character_completed_quests WHERE quest_id=29999") is [[long day]] && day == DfoServer.Game.Quests.QuestCycle.Day(DateTimeOffset.UtcNow),
                "普通重复两次独立领奖，每日周期持久限领；当日重接无SQL变化");
        }
        int previousDay = DfoServer.Game.Quests.QuestCycle.Day(DateTimeOffset.UtcNow.AddDays(-1));
        f.Sql("UPDATE characters SET quest_cycle_day=$p0; UPDATE character_completed_quests SET daily_period=$p0 WHERE quest_id=29999",previousDay);
        using (var peer = Open(f))
        {
            peer.Select(prepared: true); peer.Barrier();
            byte level = checked((byte)(long)f.Query("SELECT level FROM characters")[0][0]);
            That(List(peer.AvailableQuests,level).Contains(29999) &&
                f.Query("SELECT quest_id FROM character_completed_quests WHERE quest_id=29999").Length == 0 &&
                f.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE quest_id=29999") is [[1L]],
                "隔离库模拟离线跨日，重新选角撤销过期每日位并保留历史领奖回执");
        }
        using (var peer = Open(f))
        {
            peer.Select(prepared: true);
            peer.Send(31,[31,0,37,117]); peer.Expect(1,31,[1,37,117,1,0,0,0,0]); peer.Expect(0,21); peer.Expect(0,356);
        }
        using (var peer = Open(f))
        {
            peer.Create("QuestAlt"); peer.Select(slot: 1);
            That(List(peer.AvailableQuests,1).Contains(29989),"同账号另一角色可接尚未交付的账号任务");
            peer.Send(31,[31,0,37,117]); peer.Expect(1,31,[1,37,117,1,0,0,0,0]); peer.Expect(0,21);
            That(f.Query("SELECT count(*) FROM character_active_main_quests WHERE quest_id=29989") is [[2L]],
                "账号任务接取分别保存两个角色的独立接取身份");
        }
        using (var peer = Open(f))
        {
            peer.Select(prepared: true); peer.Travel(38,0,622,196,38,1,bottom: true);
            peer.Send(33,[33,0,37,117,0,0]); peer.Expect(1,33,[1,37,117,0,0,0,0]);
            peer.Send(34,[34,0,37,117,255,255,1,0,255,255]); Finished(peer,[1,37,117,4,0,0,0,0,0,0]);
            peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
            That(f.Query("SELECT quest_id,character_id FROM account_completed_quests WHERE quest_id=29989") is [[29989L,1L]] &&
                f.Query("SELECT count(*) FROM character_active_main_quests WHERE quest_id=29989") is [[0L]],
                "账号交付一次事务保存共享完成位并撤销其他角色同一任务");
        }
        using (var peer = Open(f))
        {
            var packets = peer.Select(slot: 1, prepared: true);
            byte[] flags = packets.Single(packet => packet.Opcode == 356).Body;
            That(flags.Length == 30004 && flags[4 + 29989] == 1 && !List(peer.AvailableQuests,1).Contains(29989),
                "另一角色重登收到账号完成位，列表不再次开放已共享完成的任务");
            string before = f.Snapshot();
            peer.Send(31,[31,0,37,117]); peer.Expect(1,31,[0,18,0]);
            peer.Send(34,[34,0,37,117,255,255,1,0,255,255]); Finished(peer,[0,19,0]); peer.Barrier();
            That(f.Snapshot() == before && f.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE quest_id=29989") is [[1L]],
                "跨角色重复接取及领奖拒绝，历史领奖回执只保存一次");
        }
        // 使用同一只读资源及服务实例，只准备隔离fixture的金币和既有无关标志。
        f.Sql("UPDATE characters SET gold=1000,equipment_slot_flags=32 WHERE id=1");
        using (var peer = Open(f))
        {
            peer.Select(prepared: true);
            peer.Send(31,[31,0,29,117]);peer.Expect(1,31,[1,29,117,0,0,0,0,0]);peer.Expect(0,21); peer.Expect(0,356);
            peer.Send(34,[34,0,29,117,255,255,1,0,255,255]);
            Finished(peer,[1,29,117,0,0,0,0,0,1,0,0,0,100,0,0,0,0,0]);
            var gold = peer.Expect(0,14).Body;
            That(gold.Length == 126 && U16(gold,1) == 1 && U16(gold,3) == 0 && U32(gold,5) == 0 && U32(gold,9) == 900,
                "Gold材料消费后同步Main槽0的实际900余额，不回报扣除增量或伪造物品UID");
            var player = peer.Expect(0,2).Body;
            That(player[0] == 1 && player[125] == 48 && f.Query("SELECT gold,equipment_slot_flags FROM characters WHERE id=1") is [[900L,48L]],
                "槽解锁成功ACK后Player发送实际提交位，保留原无关位");
            peer.Expect(0,21);peer.Expect(0,356); peer.Barrier();
            var before = f.Snapshot();
            peer.Send(34,[34,0,29,117,255,255,1,0,255,255]);Finished(peer,[0,19,0]);peer.Barrier();
            That(f.Snapshot() == before,"装备槽重复交付不再扣费、解锁或新增回执");
        }

        await f.StopAsync();
        await using var extra = new TcpTestFixture(options,Path.Combine(root,"quest-extras"),resources: resources,questCatalog: new(catalog));
        await extra.StartAsync();
        using (var peer = Open(extra))
        {
            peer.Create("QuestExtras"); peer.Select(); peer.Travel(38,0,622,196,38,1,bottom: true);
            peer.Send(31,[31,0,34,117]);
            var accepted = peer.Expect(1,31).Body;
            That(accepted.Length == 18 && U32(accepted,3) == 0 && U32(accepted,14) == 6,$"批量寻物赠品6与接取初值一次保存，ACK31使用10B赠品行：{Convert.ToHexString(accepted)}");
            peer.Expect(0,14); peer.Expect(0,21);
            peer.Send(34,[34,0,34,117,255,255,2,0,255,255]);
            var finished = Finished(peer).Body;
            That(finished.Length == 56 && finished[3] == 0 && finished[8] == 1 && finished[9] == 0 && U32(finished,12) == 6 &&
                finished[16] == 0 && finished[17] == 2 && U32(finished,20) == 2058 && U32(finished,24) == 6 &&
                U16(finished,37) == 354 && U32(finished,39) == 3033 && U32(finished,43) == 6,
                "34倍率2核对Main消耗及实体/钱包两种19B奖励行，数量由实际提交决定");
            var inventory = peer.Expect(0,14).Body; MainBalance(inventory,354,3033,6); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
            That(extra.Query("SELECT multiplier FROM character_quest_completion_receipts WHERE quest_id=29986") is [[2L]] &&
                extra.Query("SELECT count(*) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=3033") is [[0L]],
                "批量倍数持久化，晶体不创建普通物品或伪UID");
            var before = extra.Snapshot();
            peer.Send(34,[34,0,34,117,255,255,2,0,255,255]); Finished(peer,[0,19,0]); peer.Barrier();
            That(extra.Snapshot() == before,"批量重复34不再次扣除材料或增加钱包");
            peer.Send(31,[31,0,33,117]); peer.Expect(1,31,[1,33,117,0,0,0,0,0]); peer.Expect(0,21); peer.Expect(0,356);
            peer.Send(34,[34,0,33,117,255,255,1,0,255,255]);
            finished = Finished(peer).Body;
            That(finished.Length == 37 && finished[8] == 1 && finished[9] == 0 && U16(finished,10) == 354 && U32(finished,12) == 2 &&
                finished[17] == 1 && U16(finished,18) == 356 && U32(finished,20) == 3035 && U32(finished,24) == 4,
                "寻物按虚拟Main槽扣晶体，奖励行回实际红色晶体增量");
            inventory = peer.Expect(0,14).Body; MainBalance(inventory,354,3033,4); MainBalance(inventory,356,3035,4);
            peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
            peer.Send(31,[31,0,32,117]); peer.Expect(1,31,[1,32,117,1,0,0,0,0]); peer.Expect(0,21); peer.Expect(0,356);
            peer.Send(33,[33,0,32,117,1,0]); peer.Expect(1,33,[1,32,117,1,0,0,0]);
            peer.Send(34,[34,0,32,117,255,255,1,0,255,255]); peer.Expect(1,34,[1,32,117,42,0,0,0,0,0,0]);
            int[] available = List(peer.Expect(0,21).Body,1);
            var completed = peer.Expect(0,356).Body;
            That(completed[4+29984] == 2 && available.Contains(29983) && !available.Contains(29982) &&
                extra.Query("SELECT answer FROM character_completed_quests WHERE quest_id=29984") is [[1L]],
                "33零基第二选项和356值2一致，按持久回答开放唯一后继"); peer.Barrier();
        }
        using (var peer = Open(extra))
        {
            var packets = peer.Select(prepared: true);
            That(packets.Single(packet => packet.Opcode == 356).Body[4+29984] == 2 && List(peer.AvailableQuests,1).Contains(29983) &&
                extra.Query("SELECT currency_kind,balance FROM account_currency_balances ORDER BY currency_kind") is [[3L,4L],[5L,4L]] &&
                extra.Query("SELECT count(*) FROM character_quest_completion_receipts") is [[3L]],
                "断线重登恢复回答、钱包余额和三笔完成回执，不重复发增量奖励");
        }
        using (var peer = Open(extra))
        {
            peer.Create("QuestIntro"); peer.Select(slot: 1);
            peer.Travel(38,0,622,196,38,1,bottom: true); peer.Travel(38,2,1085,249,38,0,kind: 5);
            var map = FormalDungeonTcpTests.Enter(peer); FormalDungeonTcpTests.Load(peer);
            ClearRooms([(1,1),(2,1),(2,0),(3,0),(4,0)]);
            peer.Send(34,[34,0,73,12,255,255,1,0,255,255]); That(Finished(peer).Body[0] == 1,"新角色实际清图3145后正常领奖"); Drain();
            peer.Send(31,[31,0,9,19]); peer.Expect(1,31,[1,9,19,1,0,0,0,0]); Drain();
            That(extra.Query("SELECT quest_id,condition_state FROM character_active_main_quests WHERE character_id=2") is [[4873L,1L]],
                "真实3145结算副本内接取4873，未回城就持久保存接取身份");
            peer.Send(42,[]); peer.Expect(1,42,[1]); Drain();
            peer.Travel(38,0,450,250,38,2);
            peer.Send(33,[33,0,9,19,0,0]); peer.Expect(1,33,[1,9,19,0,0,0,0]);
            peer.Send(34,[34,0,9,19,255,255,1,0,255,255]); That(Finished(peer).Body[0] == 1,"4873回城真实会面林纳斯交付"); Drain();
            peer.Send(31,[31,0,74,12]); peer.Expect(1,31,[1,74,12,2,0,0,0,0]); Drain();
            peer.Send(31,[31,0,85,12]); peer.Expect(1,31,[1,85,12,1,0,0,0,0]); Drain();
            peer.Travel(38,2,1085,249,38,0);
            map = FormalDungeonTcpTests.Enter(peer,3146); FormalDungeonTcpTests.Load(peer);
            That(U32(map,14) == 76131,"真实3146起始地图包含右侧对话物件13099");
            long experience = (long)extra.Query("SELECT experience FROM characters WHERE id=2")[0][0];
            byte[] passiveDeath = new byte[74];
            BinaryPrimitives.WriteUInt32LittleEndian(passiveDeath,13099);
            BinaryPrimitives.WriteUInt16LittleEndian(passiveDeath.AsSpan(4),ushort.MaxValue);
            passiveDeath[37] = 1;
            // 原生产者+41C返回的默认值为1；这个u32不是对象flags或物件实例key。
            BinaryPrimitives.WriteUInt32LittleEndian(passiveDeath.AsSpan(42),1);
            FormalDungeonTcpTests.SealDeath(passiveDeath);
            peer.Send(39,passiveDeath); peer.Expect(0,574); peer.Barrier();
            That(extra.Query("SELECT quest_id,condition_state FROM character_active_main_quests WHERE character_id=2 ORDER BY quest_id") is
                [[3146L,2L],[3157L,0L]], "原生39附加值1按真实唯一物件推进子任务，父任务等待实际交付");
            var passiveState = extra.Query("SELECT revision,experience FROM characters WHERE id=2");
            peer.Send(39,passiveDeath); peer.Barrier();
            That(extra.Query("SELECT revision,experience FROM characters WHERE id=2").SelectMany(row => row).SequenceEqual(passiveState.SelectMany(row => row)),
                "原生39重复报告不再次写revision或死亡经验，后续33仍沿原场景交付");
            peer.Send(33,[33,0,85,12,16,0,0]); peer.Expect(1,33,[1,85,12,0,0,0,0]);
            peer.Expect(0,574,Convert.FromHexString("010000004A0C01000000"));
            var flags = peer.Expect(0,356).Body; That(flags[4+3157] == 1,"对话33无需怪物死亡或后续34即可闭合3157"); peer.Barrier();
            That(extra.Query("SELECT quest_id,condition_state FROM character_active_main_quests WHERE character_id=2") is [[3146L,1L]] &&
                extra.Query("SELECT experience FROM characters WHERE id=2") is [[long after]] && after == experience,
                "对话子任务只有完成位和父剩余1，没有击杀经验");
            peer.Send(33,[33,0,74,12,1,0]); peer.Expect(1,33,[1,74,12,1,0,0,0]); peer.Barrier();
            peer.Send(31,[31,0,238,11]); peer.Expect(1,31,[1,238,11,1,0,0,0,0]); Drain();
            ClearRooms([(0,1),(1,1),(2,1),(3,1),(4,1)]);
            That(extra.Query("SELECT quest_id,condition_state FROM character_active_main_quests WHERE character_id=2") is [[3146L,0L]] &&
                extra.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=2 AND quest_id=3054") is [[3054L]],
                "真实清图直接闭合3054并更新父任务，不依赖客户端发送33或34");
            peer.Send(46,DfoServer.DungeonSettlementProtocolTests.Report(peer.Id,99)); Drain();
            peer.Send(34,[34,0,74,12,255,255,1,0,255,255]); That(Finished(peer).Body[0] == 1,"3146实际两个子任务完成后领取父奖励"); Drain();
            That(extra.Query("SELECT quest_id,experience_amount FROM character_quest_completion_receipts WHERE character_id=2 AND quest_id IN(3157,3054) ORDER BY quest_id") is [[3054L,0L],[3157L,0L]],
                "NPC对话与清图子任务均保存唯一零经验完成回执");
            peer.Send(31,[31,0,75,12]); peer.Expect(1,31,[1,75,12,1,0,0,0,0]); Drain();
            That(extra.Query("SELECT quest_id,condition_state FROM character_active_main_quests WHERE character_id=2") is [[3147L,1L]],
                "fixture 已接取真实剧情后继3147，后续15沿其合法资源路线进入");
            map = ContinueToNextQuest(extra,peer,map);

            void ClearRooms((byte X,byte Y)[] path)
            {
                for (int room = 0; room <= path.Length; room++)
                {
                    for (int at = 0; at < map[18]; at++)
                    {
                        int offset = 19+22*at;
                        peer.Send(39,FormalDungeonTcpTests.Death(U16(map,offset+6),peer.Id,map[offset+12])); Drain();
                    }
                    if (room == path.Length) break;
                    var move = new byte[100]; move[0] = path[room].X; move[1] = path[room].Y;
                    peer.Send(45,move); peer.Expect(1,45,[1]); map = peer.Expect(0,29).Body;
                    FormalDungeonTcpTests.Load(peer);
                }
            }
            // 等级刷新数量取决于真实经验；只收通知，不吞掉失败ACK，持久结果逐步核对。
            void Drain() => DrainNotifications(peer);
        }
        // 复现zz所选的真实转职分支；fixture前置独立准备，不计入全新角色连续推进验收。
        var growth = CharacterGrowthCatalog.Load(resources.Archive,resources.Learning);
        using (var owner = new AccountOwners().Acquire(extra.Options.DevelopmentAccount,() => { }))
        {
            var candidate = CharacterCreation.Prepare("QuestCareer",resources.Characters.Get(14),resources.Towns.Room,
                resources.Towns.InitialReturn,resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(14,0,15,candidate.InitialLearning with {
                Experience = growth.GetLevelStartExperience(15) },resources.Learning);
            new CharacterRepository(extra.Database).Create(owner,candidate with { Level = 15,
                CompletedQuests = [3145,4873,3146,3147,3148,4875,3149,3150,3151,3152,3153,6929],InitialLearning = learning });
        }
        using (var peer = Open(extra))
        {
            peer.Select(slot: 2,prepared: true); FormalDungeonTcpTests.EntryTown(peer);
            byte[] info = [];
            var map = FormalDungeonTcpTests.Enter(peer,6948,1000,row => info = row); FormalDungeonTcpTests.Load(peer);
            var dungeons = DungeonCatalog.Load(resources.Archive); var rooms = DungeonRoomCatalog.Load(resources.Archive);
            var maze = dungeons.Get(1000).Mazes.Single(row => row.SourceOrdinal == info[7]);
            var boss = new DungeonPoint(info[8],info[9]);
            var path = QuestStoryRouteTests.Path(maze.StaticTopology,new(map[0],map[1]),boss);
            foreach (var point in path)
            {
                if (point == boss && maze.LayeredMaps.SingleOrDefault(row => row.Id == 76196) is { } target)
                    Move(point,1);
                var current = maze.MapSpecification!.Concat(maze.LayeredMaps).Single(row => row.Id == U32(map,14) && row.Position == point);
                var room = rooms.Get(current);
                for (int at = 0; at < map[18]; at++)
                {
                    int offset = 19+22*at;
                    var source = room.Monsters[checked((int)U32(map,offset+2))];
                    if (current.Kind == "layered" || source.Modifiers.Contains("[dummy]"))
                    {
                        var death = new byte[74]; BinaryPrimitives.WriteUInt32LittleEndian(death,U16(map,offset+6));
                        BinaryPrimitives.WriteUInt16LittleEndian(death.AsSpan(4),peer.Id);
                        death[31] = 0x22; FormalDungeonTcpTests.SealDeath(death);
                        peer.Send(39,death);
                    }
                    else peer.Send(39,FormalDungeonTcpTests.Death(U16(map,offset+6),peer.Id,map[offset+12]));
                    DrainNotifications(peer);
                }
                if (point != boss) Move(path[Array.IndexOf(path,point)+1],0);
            }
            peer.Send(34,[34,0,36,27,255,255,1,0,255,255]);
            That(peer.Expect(0,19).Body.Length > 0,"转职ACK前先装载同事务提交的完整技能快照");
            var result = Finished(peer).Body;
            That(result is [1,36,27,3,_,_,_,_,1,3,0,0],"真实6948任务返回mode1、巫女分支3和两页技能count");
            peer.Expect(0,37);
            var actor = peer.Expect(0,2).Body;
            That(actor[0] == 0 && actor[127+NameWidth("QuestCareer")] == 14 && actor[128+NameWidth("QuestCareer")] == 3,
                "结算副本内同步实际job/grow的Actor包，不能仅发不含职业字段的Player");
            That(peer.Expect(0,2).Body[0] == 1,"职业Actor之后同步实际Player属性和两棵技能树");
            peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
            That(extra.Query("SELECT grow_type FROM characters WHERE name='QuestCareer'") is [[3L]] &&
                extra.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE quest_id=6948") is [[1L]],
                "真实清图、分支变化与转职任务回执原子持久化");
            string before = extra.Snapshot();
            peer.Send(34,[34,0,36,27,255,255,1,0,255,255]); Finished(peer,[0,19,0]); peer.Barrier();
            That(extra.Snapshot() == before,"重复转职交付不重建角色、不重发奖励、不改技能或revision");
            void Move(DungeonPoint point,byte layer)
            {
                var move = new byte[100]; move[0] = checked((byte)point.X); move[1] = checked((byte)point.Y); move[10] = layer;
                peer.Send(45,move); peer.Expect(1,45,[1]); map = peer.Expect(0,29).Body; FormalDungeonTcpTests.Load(peer);
            }
        }
        // 隔离准备真实前置和职业，验证无任务连接的转职地图经过生产准入；不修改玩家进度。
        using (var owner = new AccountOwners().Acquire(extra.Options.DevelopmentAccount,() => { }))
        {
            var candidate = CharacterCreation.Prepare("QuestVaga",resources.Characters.Get(11),resources.Towns.Room,
                resources.Towns.InitialReturn,resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(11,0,15,candidate.InitialLearning with {
                Experience = growth.GetLevelStartExperience(15) },resources.Learning);
            new CharacterRepository(extra.Database).Create(owner,candidate with { Level = 15,
                CompletedQuests = [3145,4873,3146,3147,3148,4875,3149,3150,3151,3152,3153,3155], InitialLearning = learning });
        }
        string acceptance;
        using (var peer = Open(extra))
        {
            peer.Select(slot: 3,prepared: true); FormalDungeonTcpTests.EntryTown(peer);
            byte[] information = [];
            var map = FormalDungeonTcpTests.Enter(peer,13102,7143,row => information = row);
            That(U32(information,0) == 7143 && U32(map,14) == 76115,
                "真实流浪武士接取及选图下发7143和起点76115，不改为普通森林");
            FormalDungeonTcpTests.Load(peer);
            acceptance = extra.Snapshot("SELECT quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0",peer.Id);
            That(extra.Query("SELECT quest_id,condition_state FROM character_active_main_quests WHERE character_id=$p0",peer.Id)
                is [[13102L,1L]], "成功加载不伪造清图或转职完成");
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,24); peer.Expect(0,21);
            Bytes(peer.Expect(0,356).Body,peer.CompletedQuestHistory,"转职任务回城保留已知前置完成历史"); peer.Barrier();
            That(extra.Snapshot("SELECT quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0",peer.Id) == acceptance,
                "未通关回城保留本次持久接取及清图进度");
        }
        using (var peer = Open(extra))
        {
            peer.Select(slot: 3,prepared: true); peer.Barrier();
            That(extra.Snapshot("SELECT quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0",peer.Id) == acceptance &&
                extra.Query("SELECT grow_type FROM characters WHERE id=$p0",peer.Id) is [[0L]],
                "重连仍回放原接取，未清图不改变职业分支");
        }
        await QuestQuickClearTcpTests.RunAsync(extra,resources);
        VerifySequentialParent(extra,resources,growth);
        VerifyNonBossSettlement(extra,resources,growth);
        await extra.StopAsync();
        string State() => f.Snapshot("SELECT revision FROM characters") + f.Snapshot("SELECT * FROM character_active_main_quests") +
            f.Snapshot("SELECT * FROM character_quest_lifecycle_receipts ORDER BY revision");

        void MainBalance(byte[] body,ushort slot,uint item,uint balance)
        {
            That(body[0] == 0 && body.Length == 3+123*U16(body,1),"独立读器确认NOTI14 Main行长度");
            int offset = Enumerable.Range(0,U16(body,1)).Select(at => 3+123*at).Single(at => U16(body,at) == slot);
            That(U32(body,offset+2) == item && U32(body,offset+6) == balance,"NOTI14钱包slot与实际绝对余额一致");
        }
    }

    static void VerifyNonBossSettlement(TcpTestFixture fixture,TcpTestResources resources,CharacterGrowthCatalog growth)
    {
        var done = new HashSet<int> { 3259 };
        var area = resources.Towns.Areas.Values.First(row => row.Level <= 37 && row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 153));
        done.UnionWith(area.Quests);
        if (area.AnyQuests.Length != 0) done.Add(area.AnyQuests[0]);
        var walk = area.Map(done).Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new DfoServer.GameWorld.Towns.Location(area.Town,area.Area,
            checked((ushort)(walk.X+walk.Width/2)),checked((ushort)(walk.Y+walk.Height/2)));
        Character created;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,() => { }))
        {
            // 仅准备隔离Lv37与资源入口前置；本组完整TCP不作为新角色连续游玩证据。
            var candidate = CharacterCreation.Prepare("QuestGold",resources.Characters.Get(0),position,position,resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0,0,37,candidate.InitialLearning with { Experience=growth.GetLevelStartExperience(37) },resources.Learning);
            created = new CharacterRepository(fixture.Database).Create(owner,candidate with { Level=37,AcceptedStoryLevel=37,
                CompletedQuests=done.ToArray(),InitialLearning=learning });
        }
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot,prepared:true,resumeTown:true,room:false);
            var map = FormalDungeonTcpTests.Enter(peer,3260,153);
            int deaths = 0;
            foreach (var point in new (byte X,byte Y)[] { (1,2),(1,1),(1,0),(2,0),(3,0),(3,1) })
            {
                FormalDungeonTcpTests.Load(peer);
                for (int at = 0; at < map[18]; at++)
                {
                    int offset = 19+22*at;
                    peer.Send(39,FormalDungeonTcpTests.Death(U16(map,offset+6),peer.Id,map[offset+12]));
                    DrainNotifications(peer); deaths++;
                }
                var move = new byte[100]; move[0]=point.X; move[1]=point.Y;
                peer.Send(45,move); peer.Expect(1,45,[1]); map=peer.Expect(0,29).Body;
            }
            That(U32(map,14) == 70107 && map[18] == 1 && U32(map,27) == 66417 && map[32] == 0,
                "黄金矿洞终点保留真实普通友方66417，没有伪造Boss或删除演员");
            FormalDungeonTcpTests.Load(peer); DrainNotifications(peer);
            That(deaths == 40 && fixture.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=3260",peer.Id) is [[0L]],
                "完整六房真实猎杀完成两通道目标，team110按相对本人仍属敌对");
            long revision = Convert.ToInt64(fixture.Query("SELECT revision FROM characters WHERE id=$p0",peer.Id)[0][0]);
            peer.Send(39,FormalDungeonTcpTests.Death(U16(map,25),peer.Id,map[31]));
            var retired = peer.Expect(0,38).Body;
            That(retired.Length == 9 && U16(retired,0) == U16(map,25) && U16(retired,2) == 0,
                "友方非阻塞演员消失只投影空掉落通知，不作为敌对死亡"); peer.Barrier();
            That(fixture.Query("SELECT revision FROM characters WHERE id=$p0",peer.Id) is [[long same]] && same == revision,
                "友方终点报告不授予额外任务、成长事实或revision变化");
            peer.Send(46,DfoServer.DungeonSettlementProtocolTests.Report(peer.Id,99)); DrainNotifications(peer);
            That(fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id) is [[1L]],
                "无Boss的已清终点沿真实46完成一次结算");
            peer.Send(34,[34,0,188,12,255,255,1,0,255,255]); That(Finished(peer).Body[0] == 1,"3260实际猎杀完成后正常交付"); DrainNotifications(peer);
        }
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot,prepared:true); peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0 AND quest_id=3260",peer.Id) is [[1L]] &&
                fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id) is [[1L]],
                "重连恢复原任务交付和结算，不因友方终点补发奖励");
        }
    }

    static void VerifySequentialParent(TcpTestFixture fixture, TcpTestResources resources, CharacterGrowthCatalog growth)
    {
        var done = new HashSet<int> { 3272,5149 };
        var area = resources.Towns.Areas.Values.First(row => row.Level <= 39 && row.DungeonWorldMap is { } world &&
            resources.Towns.DungeonMaps[world].Options.Any(option => option.DungeonId == 239));
        // 夹具只准备本入口资源声明的城镇资格，不作为从新角色连续游玩的证据。
        done.UnionWith(area.Quests);
        if (area.AnyQuests.Length != 0) done.Add(area.AnyQuests[0]);
        var map = area.Map(done);
        var walk = map.Bounds.First(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains)).Bounds;
        var position = new DfoServer.GameWorld.Towns.Location(area.Town,area.Area,
            checked((ushort)(walk.X + walk.Width / 2)),checked((ushort)(walk.Y + walk.Height / 2)));
        Character created;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,() => { }))
        {
            var candidate = CharacterCreation.Prepare("QuestNoir",resources.Characters.Get(0),position,position,resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0,0,39,candidate.InitialLearning with { Experience = growth.GetLevelStartExperience(39) },resources.Learning);
            created = new CharacterRepository(fixture.Database).Create(owner,candidate with { Level = 39, AcceptedStoryLevel = 39,
                CompletedQuests = done.ToArray(),InitialLearning = learning,ActiveMainQuests = [new(5,3273,1) { AcceptanceId = Guid.NewGuid() }] });
        }
        string parentAcceptance = fixture.Snapshot("SELECT quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0",created.Id);
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot,prepared: true,resumeTown: true,room: false);
            foreach (ushort requestQuest in new ushort[] { 3273,0,5150 })
            {
                peer.Send(15,new byte[4]); peer.Expect(1,15,[1]); peer.Expect(0,3); peer.Expect(0,27,new byte[42]);
                byte[] request = new byte[25]; BinaryPrimitives.WriteUInt32LittleEndian(request,239);
                BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(9),ushort.MaxValue);
                BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16),requestQuest);
                peer.Send(16,request);
                That(U32(peer.Expect(0,28).Body,0) == 239 && U32(peer.Expect(0,29).Body,14) == 70148,
                    "诺伊佩拉父/0/子请求沿真实5150迷宫加载起点，不改为普通图 " + requestQuest);
                FormalDungeonTcpTests.Load(peer);
                if (requestQuest == 3273)
                {
                    That(fixture.Snapshot("SELECT quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0",peer.Id) == parentAcceptance,
                        "父请求入场及加载不自动接取5150、不改父scalar或接取身份");
                    peer.Send(31,[31,0,30,20]); peer.Expect(1,31,[1,30,20,1,0,0,0,0]);
                    peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
                }
                peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,24); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
                That(fixture.Query("SELECT quest_id,condition_state FROM character_active_main_quests WHERE character_id=$p0 ORDER BY quest_id",peer.Id)
                    is [[3273L,1L],[5150L,1L]], "未通关返回保留父/子进行中，不伪造任务完成 " + requestQuest);
            }
        }
        string accepted = fixture.Snapshot("SELECT quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0 ORDER BY quest_id",created.Id);
        using (var peer = Open(fixture))
        {
            peer.Select(created.Slot,prepared: true,resumeTown: true,room: false); peer.Barrier();
            That(fixture.Snapshot("SELECT quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0 ORDER BY quest_id",peer.Id) == accepted,
                "诺伊佩拉父请求及真实副本内子31重连回放原持久接取，未清图不生成凭据");
        }
    }

    public static async Task RunContinuationAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        await VerifyNoMainQuestReturnAsync(options,root,resources);
        await using var fixture = new TcpTestFixture(options,Path.Combine(root,"quest-continuation"),resources: resources);
        await fixture.StartAsync();
        var growth = CharacterGrowthCatalog.Load(resources.Archive,resources.Learning);
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,() => { }))
        {
            // 只准备早期前置与已覆盖的对话子任务；本组真实验证一条清图、交付和后继转场。
            var candidate = CharacterCreation.Prepare("QuestNext",resources.Characters.Get(0),resources.Towns.Room,
                resources.Towns.InitialReturn,resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0,0,3,candidate.InitialLearning with {
                Experience = growth.GetLevelStartExperience(3) },resources.Learning);
            new CharacterRepository(fixture.Database).Create(owner,candidate with {
                Level = 3, AcceptedStoryLevel = 1, CompletedQuests = [3145,4873,3157], InitialLearning = learning });
        }
        using (var peer = Open(fixture))
        {
            peer.Select(prepared: true); FormalDungeonTcpTests.EntryTown(peer);
            var map = FormalDungeonTcpTests.Enter(peer,3146); FormalDungeonTcpTests.Load(peer);
            That(U32(map,14) == 76131,"独立连图fixture按真实3146路线进入，未重跑Lv1前缀");
            peer.Send(31,[31,0,238,11]); peer.Expect(1,31,[1,238,11,1,0,0,0,0]); DrainNotifications(peer);
            var path = new (byte X,byte Y)[] { (0,1),(1,1),(2,1),(3,1),(4,1) };
            for (int room = 0; room <= path.Length; room++)
            {
                for (int at = 0; at < map[18]; at++)
                {
                    int offset = 19+22*at;
                    peer.Send(39,FormalDungeonTcpTests.Death(U16(map,offset+6),peer.Id,map[offset+12]));
                    DrainNotifications(peer);
                }
                if (room == path.Length) break;
                var move = new byte[100]; move[0] = path[room].X; move[1] = path[room].Y;
                peer.Send(45,move); peer.Expect(1,45,[1]); map = peer.Expect(0,29).Body;
                FormalDungeonTcpTests.Load(peer);
            }
            That(fixture.Query("SELECT quest_id,condition_state FROM character_active_main_quests WHERE character_id=$p0",peer.Id)
                    is [[3146L,0L]] &&
                fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 AND quest_id=3054",peer.Id)
                    is [[3054L]],"独立连图fixture真实清图完成子任务并更新父条件");
            peer.Send(46,DfoServer.DungeonSettlementProtocolTests.Report(peer.Id,99)); DrainNotifications(peer);
            peer.Send(34,[34,0,74,12,255,255,1,0,255,255]);
            That(Finished(peer).Body[0] == 1,"独立连图fixture真实交付3146父任务"); DrainNotifications(peer);
            peer.Send(31,[31,0,75,12]); peer.Expect(1,31,[1,75,12,1,0,0,0,0]); DrainNotifications(peer);
            ContinueToNextQuest(fixture,peer,map,acceptStoryDigest: true);
        }
        await fixture.StopAsync();
    }

    static async Task VerifyNoMainQuestReturnAsync(ServerOptions options,string root,TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(options,Path.Combine(root,"quest-no-main-return"),resources: resources);
        await fixture.StartAsync();
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,() => { }))
        {
            var candidate = CharacterCreation.Prepare("NoMainNext",resources.Characters.Get(0),resources.Towns.Room,
                resources.Towns.InitialReturn,resources.Learning.Get);
            new CharacterRepository(fixture.Database).Create(owner,candidate with { CompletedQuests = [3145,4873,3157] });
        }
        using (var peer = Open(fixture))
        {
            peer.Select(prepared: true); FormalDungeonTcpTests.EntryTown(peer);
            That(fixture.Query("SELECT quest_id FROM character_active_main_quests WHERE character_id=$p0",peer.Id).Length == 0 &&
                !List(peer.AvailableQuests,1).Contains(3146),"已完成救出赛丽亚及对话，等级不足时没有活动主线或可接3146");
            peer.Send(15,new byte[4]); peer.Expect(1,15,[1]);
            peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),1]); peer.Expect(0,27,new byte[42]);
            peer.Send(16,Convert.FromHexString("030000000000000000FFFF0000000000000000000000000000"));
            That(peer.Expect(0,28).Body[7] == 0,"没有任务身份的普通maze不能依靠活动剧情连接决定回城");
            var map = peer.Expect(0,29).Body;
            var path = new (byte X,byte Y)[] { (1,1),(2,1),(2,0),(3,0) };
            for (int room = 0; room <= path.Length; room++)
            {
                FormalDungeonTcpTests.Load(peer); DrainNotifications(peer);
                var enemies = Enumerable.Range(0,map[18]).Select(at => 19+22*at)
                    .Where(at => map[at+13] != 9 && map[at+14] == 0).OrderBy(at => map[at+13] == 3);
                foreach (int at in enemies)
                {
                    var death = FormalDungeonTcpTests.Death(U16(map,at+6),peer.Id,map[at+12]);
                    if (map[at+13] == 3)
                    {
                        death[55] = 1;
                        BinaryPrimitives.WriteUInt64LittleEndian(death.AsSpan(18),9000);
                        BinaryPrimitives.WriteUInt32LittleEndian(death.AsSpan(26),30);
                        FormalDungeonTcpTests.SealDeath(death);
                    }
                    peer.Send(39,death); DrainNotifications(peer);
                }
                if (room == path.Length) break;
                var move = new byte[100]; move[0] = path[room].X; move[1] = path[room].Y;
                peer.Send(45,move); peer.Expect(1,45,[1]); map = peer.Expect(0,29).Body;
            }
            peer.Send(46,DfoServer.DungeonSettlementProtocolTests.Report(peer.Id,99)); DrainNotifications(peer);
            long attempt = Convert.ToInt64(fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0",peer.Id)[0][0]);
            peer.Send(72,[1,1]); peer.Expect(1,72,[1,1,2]); peer.Expect(0,24); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
            string returned = fixture.Snapshot();
            peer.Send(72,[1,3]); peer.Barrier();
            That(fixture.Snapshot() == returned && fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0",peer.Id)
                is [[long actual]] && actual == attempt,"无主线明确确认回城并忽略旧尾包，不再发选图或建立连图尝试");
            peer.Send(35,Move(400,249)); peer.Barrier();
            That(fixture.Query("SELECT x FROM characters WHERE id=$p0",peer.Id) is [[400L]],"回城完成后城镇移动恢复，连接保持可用");
        }
        await fixture.StopAsync();
    }

    static byte[] ContinueToNextQuest(TcpTestFixture fixture,TcpPeer peer,byte[] oldMap,bool acceptStoryDigest = false)
    {
        That(fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 AND quest_id=3146",peer.Id)
                is [[3146L]] &&
            fixture.Query("SELECT quest_id,condition_state FROM character_active_main_quests WHERE character_id=$p0",peer.Id)
                is [[3147L,1L]],"连图从已提交前驱和真实已接取3147开始，不用可接列表代替活动任务");
        long attempt = Attempt();
        peer.Send(72,[1,1]); peer.Expect(1,72,[1,1,1]); peer.Expect(0,24); peer.Expect(0,21); peer.Expect(0,356); peer.Barrier();
        That(Attempt() == attempt,"72开始后继立即ACK72和24/21退场，真实15前不安装副本或预留尝试");
        long? storyRevision = null;
        if (acceptStoryDigest)
        {
            var before = fixture.Query("SELECT level,accepted_story_level,revision FROM characters WHERE id=$p0",peer.Id)[0];
            That(Convert.ToInt64(before[1]) == 1 && Convert.ToInt64(before[0]) > 1,
                "独立连图fixture保留低于角色等级的剧情接受值，1446确实提交而非无操作");
            peer.Send(1446,[]); peer.Barrier();
            var after = fixture.Query("SELECT level,accepted_story_level,revision FROM characters WHERE id=$p0",peer.Id)[0];
            storyRevision = Convert.ToInt64(after[2]);
            That(Convert.ToInt64(after[0]) == Convert.ToInt64(before[0]) &&
                Convert.ToInt64(after[1]) == Convert.ToInt64(before[0]) && storyRevision == Convert.ToInt64(before[2])+1 &&
                Attempt() == attempt,"连图24后空1446保存服务端等级并只增加一次revision，没有应答或预留副本尝试");
        }
        string retired = fixture.Snapshot();
        for (int replay = 0; replay < 2; replay++)
        {
            peer.Send(72,[1,3]); peer.Barrier();
            That(fixture.Snapshot() == retired && Attempt() == attempt,
                "旧结算页首次及重复1,3尾包均无响应，不重放ACK72/24或改变后继准备");
        }
        peer.Send(15,new byte[4]); peer.Expect(1,15,[1]);
        peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),1]);
        peer.Expect(0,27,new byte[42]);
        var nextInfo = peer.Expect(0,28).Body; var map = peer.Expect(0,29).Body;
        That(U32(nextInfo,0) == 6 && U32(map,14) == 76141 && Attempt() == attempt+1,
            "3146到3147的真实15建立一次新尝试，进入地下城6的76141而非重开旧图");
        string loading = fixture.Snapshot();
        if (storyRevision is { } savedRevision)
        {
            That(fixture.Query("SELECT revision FROM characters WHERE id=$p0",peer.Id) is [[long entryRevision]] &&
                entryRevision == savedRevision+1,"1446后保留预备路线，真实15仅以更新后的revision安装一次新副本");
            peer.Send(1446,[]); peer.Barrier();
            That(fixture.Snapshot() == loading,"新图加载前重复1446无写入或通知，保留已安装Run及pending重放快照");
        }
        peer.Send(72,[1,3]); peer.Barrier();
        That(fixture.Snapshot() == loading,"新图加载前的旧1,3尾包仍无响应，不覆盖新图状态或建立尝试");
        peer.Send(15,new byte[4]); peer.Expect(1,15,[1]); peer.Barrier();
        That(fixture.Snapshot() == loading && Attempt() == attempt+1,
            "新图加载前重复15只重放ACK，不创建副本或重发地图");
        FormalDungeonTcpTests.Load(peer); peer.Barrier();
        string durable = fixture.Snapshot();
        peer.Send(39,FormalDungeonTcpTests.Death(U16(oldMap,25),peer.Id,oldMap[31])); peer.Barrier();
        That(fixture.Snapshot() == durable &&
            fixture.Query("SELECT quest_id,condition_state FROM character_active_main_quests WHERE character_id=$p0",peer.Id)
                is [[3147L,1L]],"旧实例迟到死亡不影响新任务、经验或完成记录");
        return map;

        long Attempt() => Convert.ToInt64(fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0",peer.Id)[0][0]);
    }

    static WirePacket Finished(TcpPeer peer, byte[]? expected = null) => peer.Expect(1,34,expected);

    static void DrainNotifications(TcpPeer peer)
    {
        peer.SendBarrier();
        for (int at = 0; at < 64; at++)
        {
            var packet = peer.Read();
            if (packet.Kind == 1 && packet.Opcode == 1347) { Bytes(packet.Body,[1],"任务剧情阶段处理完成"); return; }
            if (packet.Kind == 0 && packet.Opcode == 1760)
            {
                peer.ReadCardMembershipState(packet);
                continue;
            }
            That(packet.Kind == 0,"剧情事件后只有通知，没有未检查的失败ACK");
        }
        throw new InvalidDataException("任务剧情通知超过有界数量");
    }

    // 测试独立消费protobuf；不调用生产Builder/Parser解释自己的输出。
    internal static int[] List(byte[] body, byte level)
    {
        That(body.Length >= 10 && BinaryPrimitives.ReadUInt32LittleEndian(body) == body.Length - 4, "任务列表protobuf长度精确");
        int at = 4;
        That(Read() == 8 && Read() == 21 && Read() == 16 && Read() == 0 && Read() == 24 && Read() == level,
            "独立读器消费enum、required默认值和当前guide等级");
        var ids = new List<int>();
        if (at != body.Length)
        {
            That(Read() == 34, "任务编号为field4 packed");
            int length = Read(), end = checked(at + length);
            That(end == body.Length, "任务packed字段无额外尾部");
            while (at < end) ids.Add(Read());
        }
        That(at == body.Length && ids.All(id => id is > 0 and < 30000) && ids.Distinct().Count() == ids.Count, "任务列表完整、合法且无重复编号");
        return ids.ToArray();
        int Read()
        {
            int value = 0;
            for (int shift = 0; shift < 35 && at < body.Length; shift += 7)
            {
                byte b = body[at++]; value |= (b & 127) << shift;
                if ((b & 128) == 0) return value;
            }
            throw new InvalidDataException("任务列表varint截断或越界");
        }
    }
}
