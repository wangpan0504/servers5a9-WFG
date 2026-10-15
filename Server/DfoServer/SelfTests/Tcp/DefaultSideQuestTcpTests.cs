using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

// 真实外传接取及精确声明的显式NPC会面；不代表全部外传或客户端界面通过。
internal static class DefaultSideQuestTcpTests
{
    internal static Action Verify(TcpTestFixture fixture, TcpTestResources resources, CharacterGrowthCatalog growth,
        Action<TcpPeer, Action<WirePacket>?> drain)
    {
        int checks = Count;
        var diagnostics = new List<(ushort Character, ushort Quest, long Revision, byte Level, byte Job, int Active, string Reason, bool Accepted)>();
        (ushort Id, byte Level, byte Job, uint Scalar, string Suffix, bool Allowed)[] cases =
        [
            (13601,25,0,1,"P",true), (62,60,0,1,"P",true), (649,60,0,1,"P",true),
            (6924,74,15,0,"P",true), (6404,95,0,1,"P",true), (2905,90,0,0,"P",true),
            (8681,90,0,1,"P",true), (8681,89,0,1,"L",false),
            (13601,24,0,1,"L",false), (6924,74,0,0,"J",false), (649,60,0,1,"R",false)
        ];
        foreach (var scenario in cases)
        {
            var quest = resources.Quests.Get(scenario.Id);
            var definition = quest.Definition!;
            That(quest.Grade == "[side]" && quest.MainQuest is null && definition.AcceptanceRewards.IsEmpty &&
                definition.GradeModifiers.IsEmpty && definition.FirstExposedByNpc is null && definition.ExposedByNpc &&
                definition.HideOnCompletedQuests.IsEmpty && definition.RequiredAnswers.IsEmpty &&
                definition.AccountQuest == (scenario.Id == 2905), "真实13QST核实的普通外传声明：" + scenario.Id);
            int[] prerequisite = scenario.Id switch { 649 => [674], 6924 => [3604], 6404 => [6352], _ => [] };
            That(definition.PrerequisiteGroups.Length == (prerequisite.Length == 0 ? 0 : 1) &&
                (prerequisite.Length == 0 || definition.PrerequisiteGroups[0].SequenceEqual(prerequisite)),
                "保持原任务前置，不把relation quest当完成记录：" + scenario.Id);
            // 3145只用于隔离角色已结束开场的城镇/356恢复准备；不写本条活动槽、条件或完成位。
            var done = (scenario.Suffix == "R" ? [] : prerequisite).Append(3145).Distinct().ToArray();
            That(!done.Contains(scenario.Id), "准备不完成目标外传");
            if (scenario.Id == 8681)
                That(quest.Condition == "[meet npc]" && quest.SubType == -1 && quest.IntData.SequenceEqual([2]) &&
                    quest.Attributes.SequenceEqual(["not check auto clear"]) && definition.Values("searching information") is
                        [{ Type: 0, Value: 0, Text: null }], "真实8681显式NPC2及searching0合同");
            string account = ("side127-" + scenario.Id + "-" + scenario.Suffix).ToLowerInvariant();
            string name = "Side" + scenario.Id + scenario.Suffix;
            That(NameWidth(name) <= 12, "测试昵称符合CP936的12字节上限");
            Character created;
            using (var owner = new AccountOwners().Acquire(account, () => { }))
            {
                var candidate = CharacterCreation.Prepare(name, resources.Characters.Get(scenario.Job),
                    scenario.Id == 8681 ? resources.Towns.InitialReturn : resources.Towns.Room,
                    resources.Towns.InitialReturn, resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(scenario.Job, 0, scenario.Level, candidate.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(scenario.Level) }, resources.Learning);
                created = new CharacterRepository(fixture.Database, resources.Quests).Create(owner, candidate with
                {
                    Level = scenario.Level, AcceptedStoryLevel = scenario.Level, CompletedQuests = done,
                    InitialLearning = learning
                });
            }
            That(fixture.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0", created.Id) is [[0L]],
                "隔离角色没有预填活动槽或接取身份：" + scenario.Id);
            string identity = "", persisted = "";
            using (var peer = Open(fixture, account))
            {
                var packets = Select(peer, created);
                That(QuestLifecycleTcpTests.List(peer.AvailableQuests, scenario.Level).Contains(scenario.Id) == scenario.Allowed,
                    "实际登录21按原等级、职业与前置筛选真实外传：" + scenario.Id + scenario.Suffix);
                ActiveProjection(packets, scenario.Id, null);
                string before = State(created.Id);
                peer.Send(31, [31, 0, (byte)scenario.Id, (byte)(scenario.Id >> 8)]);
                if (!scenario.Allowed)
                {
                    peer.Expect(1, 31, [0, 18, 0]); peer.Barrier();
                    That(State(created.Id) == before, "等级/职业/前置31拒绝且没有持久化：" + scenario.Id + scenario.Suffix);
                    Record(false, scenario.Id switch { 13601 or 8681 => "等级不符", 6924 => "职业不符", _ => "前置组未达成" });
                }
                else
                {
                    var ack = peer.Expect(1, 31).Body;
                    That(ack.Length == 8 && ack[0] == 1 && U16(ack, 1) == scenario.Id &&
                        U32(ack, 3) == scenario.Scalar && ack[7] == 0,
                        "真实31无赠品ACK绑定QID与服务端scalar：" + scenario.Id);
                    bool refreshed = false;
                    drain(peer, packet =>
                    {
                        if (packet.Opcode != 21) return;
                        refreshed = true;
                        That(QuestLifecycleTcpTests.List(packet.Body, scenario.Level).Contains(scenario.Id),
                            "接取后21保留活动任务：" + scenario.Id);
                    });
                    That(refreshed, "实际31成功后刷新21");
                    var active = fixture.Query("SELECT quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0", created.Id);
                    That(active.Length == 1 && Convert.ToInt32(active[0][0]) == scenario.Id &&
                        Convert.ToUInt32(active[0][1]) == scenario.Scalar, "31真实提交唯一任务槽及原类型初值：" + scenario.Id);
                    identity = (string)active[0][2];
                    That(Guid.TryParseExact(identity, "N", out var acceptance) && acceptance != Guid.Empty &&
                        fixture.Query("SELECT quest_id,acceptance_id FROM character_quest_lifecycle_receipts WHERE character_id=$p0", created.Id)
                            is [[long receiptQuest, string receiptId]] && receiptQuest == scenario.Id && receiptId == identity,
                        "活动槽和唯一持久接取回执使用同一非空AcceptanceId：" + scenario.Id);
                    NoCompletion(created.Id, scenario.Id);
                    Record(true, "任务事务已提交");
                    if (scenario.Id == 8681)
                    {
                        string accepted = State(created.Id);
                        peer.Send(34,[34,0,233,33,255,255,1,0,255,255]); peer.Expect(1,34,[0,19,0]); peer.Barrier();
                        peer.Send(33,[33,0,233,33,0,0]); peer.Expect(1,33,[0,19,0]); peer.Barrier();
                        That(State(created.Id) == accepted, "未会面先交付及错误真实场景33均拒绝，pending1保持");
                    }
                    if (scenario.Id == 13601)
                    {
                        string accepted = State(created.Id);
                        peer.Send(31, [31, 0, (byte)scenario.Id, (byte)(scenario.Id >> 8)]);
                        peer.Expect(1, 31, [0, 18, 0]); peer.Barrier();
                        That(State(created.Id) == accepted, "重复真实31不新建身份、回执或revision");
                        Record(false, "已完成或已接取");
                    }
                }
                persisted = Progress(created.Id);
                // Dispose实际关闭TCP；重新Open建立新连接，不把同Session重选冒充断线恢复。
            }
            using (var peer = Open(fixture, account))
            {
                var packets = Select(peer, created);
                That(QuestLifecycleTcpTests.List(peer.AvailableQuests, scenario.Level).Contains(scenario.Id) == scenario.Allowed,
                    "实际重连21恢复本角色外传候选：" + scenario.Id + scenario.Suffix);
                ActiveProjection(packets, scenario.Id, scenario.Allowed ? scenario.Scalar : null);
                That(Progress(created.Id) == persisted, "重连不新增接取回执、条件或完成位：" + scenario.Id + scenario.Suffix);
                if (scenario.Allowed)
                    That(fixture.Query("SELECT acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1", created.Id, scenario.Id)
                        is [[string restored]] && restored == identity, "重连恢复原AcceptanceId，不重接：" + scenario.Id);
                NoCompletion(created.Id, scenario.Id);
                if (scenario.Id == 8681 && scenario.Allowed)
                    CompleteExplicitMeet(peer,created,identity,done.ToHashSet());
                Cleanup(peer, created, account);
            }
            void Record(bool accepted, string reason) => diagnostics.Add((created.Id, scenario.Id,
                (long)fixture.Query("SELECT revision FROM characters WHERE id=$p0", created.Id)[0][0], scenario.Level, scenario.Job,
                checked((int)(long)fixture.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0", created.Id)[0][0]),
                reason, accepted));
        }
        Console.WriteLine($"PASS {Count-checks} 真实外传21/31持久接取、断线恢复及8681显式NPC会面交付（非客户端验收）");
        // 共享热服务正常停止后再读一次日志，避免等待刷盘或另启服务验证诊断。
        return () =>
        {
            var events = fixture.Events().Where(row => row.GetProperty("Kind").GetString() == "quest_lifecycle")
                .Select(row => row.GetProperty("Detail"))
                // 通讯装置复用同名事件，但使用Action=image_communication；这里只核真实31的action=Accept。
                .Where(row => row.TryGetProperty("action", out var action) && action.GetInt32() == 0).ToArray();
            foreach (var expected in diagnostics)
            {
                var matches = events.Where(row => row.GetProperty("CharacterId").GetInt32() == expected.Character &&
                    row.GetProperty("questId").GetInt32() == expected.Quest && row.GetProperty("Level").GetInt32() == expected.Level &&
                    row.GetProperty("Job").GetInt32() == expected.Job && row.GetProperty("Reason").GetString() == expected.Reason).ToArray();
                That(matches.Length == 1, "实际31只记录一次对应角色和具体拒绝/提交原因：" + expected.Quest + expected.Reason);
                var detail = matches.Single();
                That(detail.GetProperty("Revision").GetInt64() == expected.Revision && detail.GetProperty("ActiveQuestCount").GetInt32() == expected.Active &&
                    detail.GetProperty("Phase").GetInt32() == (int)SessionPhase.Town && detail.GetProperty("GrowType").GetInt32() == 0 &&
                    detail.GetProperty("Connection").GetInt32() > 0 && detail.GetProperty("action").GetInt32() == 0 &&
                    detail.GetProperty("Accepted").GetBoolean() == expected.Accepted &&
                    detail.GetProperty("Outcome").GetInt32() == (expected.Accepted ? 1 : 0),
                    "实际31诊断绑定业务回执快照，不将拒绝写成提交或发送成功：" + expected.Quest);
            }
        };

        WirePacket[] Select(TcpPeer peer, Character role) => peer.Select(RoleIndex(peer.Roster, role), prepared: true,
            resumeTown: true, room: resources.Towns.IsRoom(role.Position, doneFor(role.Id)));
        HashSet<int> doneFor(ushort id) => fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0", id)
            .Select(row => checked((int)(long)row[0])).ToHashSet();
        void ActiveProjection(WirePacket[] packets, ushort id, uint? scalar)
        {
            byte[] body = packets.Single(packet => packet.Kind == 1 && packet.Opcode == 4).Body;
            That(body.Length == 235 && body[0] == 1 && Enumerable.Range(0,30).All(slot =>
                U16(body,22+6*slot) == (scalar.HasValue && slot == 0 ? id : ushort.MaxValue) &&
                U32(body,24+6*slot) == (scalar.HasValue && slot == 0 ? scalar.Value : 0u)),
                "实际ACK4完整三十槽独立核对QID与scalar、空槽FFFF/0：" + id);
        }
        void NoCompletion(ushort id, ushort quest)
        {
            That(fixture.Query("SELECT count(*) FROM character_completed_quests WHERE character_id=$p0 AND quest_id=$p1", id, quest) is [[0L]] &&
                fixture.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0", id) is [[0L]],
                "接取不预写个人完成位或交付奖励回执：" + quest);
            if (quest == 2905)
                That(fixture.Query("SELECT count(*) FROM account_completed_quests WHERE account=$p0 AND quest_id=$p1", "side127-2905-p", quest) is [[0L]],
                    "账号故事书接取不提前写账号完成位");
        }
        string Progress(ushort id) => fixture.Snapshot("SELECT slot,quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot", id) +
            fixture.Snapshot("SELECT * FROM character_quest_lifecycle_receipts WHERE character_id=$p0 ORDER BY revision", id) +
            fixture.Snapshot("SELECT * FROM character_completed_quests WHERE character_id=$p0 ORDER BY quest_id", id);
        string State(ushort id) => Progress(id) + fixture.Snapshot("SELECT * FROM characters WHERE id=$p0", id);
        void CompleteExplicitMeet(TcpPeer peer, Character role, string acceptance, HashSet<int> done)
        {
            var towns = resources.Towns;
            var target = towns.Room;
            That(towns.Areas[(role.Position.Town,role.Position.Area)].Map(done).Npcs.All(npc => npc.Id != 2) &&
                towns.Areas[(target.Town,target.Area)].Map(done).Npcs.Any(npc => npc.Id == 2),
                "错误街区和目标房间的NPC2来自本轮真实城镇资源");
            var arrival = towns.Entrance(role.Position,target,done)!;
            That(arrival is not null && towns.Admission(role.Position,arrival,role.Level,done,role.Return) == "ready",
                "沿资源中的合法入口前往NPC2，不伪造NPC或场景");
            peer.Travel(arrival!.Town,arrival.Area,arrival.X,arrival.Y,role.Position.Town,role.Position.Area,
                returnTown: role.Position.Town);
            peer.Send(33,[33,0,233,33,0,0]); peer.Expect(1,33,[1,233,33,0,0,0,0]); peer.Barrier();
            That(fixture.Query("SELECT condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0",role.Id)
                is [[0L,string identity]] && identity == acceptance,"真实NPC会面沿恢复的原AcceptanceId归零");
            NoCompletion(role.Id,8681);
            string met = State(role.Id);
            peer.Send(33,[33,0,233,33,0,0]); peer.Expect(1,33,[1,233,33,0,0,0,0]); peer.Barrier();
            That(State(role.Id) == met,"重复显式会面不增加revision或生命周期回执");
            long gold = (long)fixture.Query("SELECT gold FROM characters WHERE id=$p0",role.Id)[0][0];
            peer.Send(34,[34,0,233,33,255,255,1,0,255,255]);
            byte[] ack = peer.Expect(1,34).Body;
            That(ack.Length >= 10 && ack[0] == 1 && U16(ack,1) == 8681 && ack[3] == 4,"8681沿普通34完整奖励投影交付");
            drain(peer,null);
            That(fixture.Query("SELECT q.acceptance_id,q.condition_type,q.event_id IS NULL,q.instance_id IS NULL,q.revision=c.revision,q.after_experience=c.experience,q.after_level=c.level,q.gold_amount=c.gold-$p1,q.rewards,q.consumed FROM character_quest_completion_receipts q JOIN characters c ON c.id=q.character_id WHERE q.character_id=$p0",role.Id,gold)
                is [[string receipt,4L,1L,1L,1L,1L,1L,1L,"[]","[]"]] && receipt == acceptance,
                "实际完成回执、成长与金币原子提交，无伪造清图或额外物品");
            That(fixture.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0",role.Id) is [[0L]] &&
                fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 AND quest_id IN(8681,8682)",role.Id) is [[8681L]],
                "只关闭实际8681活动槽和完成自身，不自动完成relation8682");
            string completed = State(role.Id) + fixture.Snapshot("SELECT * FROM character_quest_completion_receipts WHERE character_id=$p0",role.Id);
            peer.Send(34,[34,0,233,33,255,255,1,0,255,255]); peer.Expect(1,34,[0,19,0]); peer.Barrier();
            That(State(role.Id) + fixture.Snapshot("SELECT * FROM character_quest_completion_receipts WHERE character_id=$p0",role.Id) == completed,
                "重复34拒绝且不重复金币、成长或完成回执");
        }
        void Cleanup(TcpPeer peer, Character role, string account)
        {
            // 仅成功路径经正式流程删自建角色；任何失败均保留现场。
            peer.Send(7); peer.Expect(1, 7, [1]);
            ushort ordinal = RoleIndex(peer.Expect(0, 2).Body, role);
            peer.Send(6, Build(writer => { writer.Write(ordinal); writer.Write(Name(role.Name)); }));
            peer.Expect(1, 6, Build(writer => { writer.Write((byte)1); writer.Write((byte)0); writer.Write(ordinal); }));
            peer.Expect(0, 2); peer.Send(692, Name(role.Name)); peer.Expect(1, 692, [1]);
            That(fixture.Query("SELECT count(*) FROM characters WHERE account=$p0", account) is [[0L]], "成功后只删除本条隔离角色");
            peer.Send(3, [0]); peer.Expect(1, 3, [1]); peer.Closed();
        }
    }

    static ushort RoleIndex(byte[] roster, Character role)
    {
        int at = 15;
        for (ushort ordinal = 0; ordinal < U16(roster, 13); ordinal++)
        {
            int width = checked((int)U32(roster, at + 2));
            if (U16(roster, at) == role.Slot && DecodeName(roster, at + 6, width) == role.Name) return ordinal;
            int equipment = at + 21 + width;
            at = equipment + AssetWireChecks.ActorEntries(roster, equipment).ConsumedBytes + 48;
        }
        throw new InvalidDataException("外传代表不在实际名册中：" + role.Name);
    }
}
