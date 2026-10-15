using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

// 只验证真实外传接取，不代表目标剧情、交付或客户端界面通过。
internal static class DefaultSideQuestTcpTests
{
    internal static void Verify(TcpTestFixture fixture, TcpTestResources resources, CharacterGrowthCatalog growth,
        Action<TcpPeer, Action<WirePacket>?> drain)
    {
        int checks = Count;
        (ushort Id, byte Level, byte Job, uint Scalar, string Suffix, bool Allowed)[] cases =
        [
            (13601,25,0,1,"P",true), (62,60,0,1,"P",true), (649,60,0,1,"P",true),
            (6924,74,15,0,"P",true), (6404,95,0,1,"P",true), (2905,90,0,0,"P",true),
            (13601,24,0,1,"L",false), (6924,74,0,0,"J",false)
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
            var done = prerequisite.Append(3145).Distinct().ToArray();
            That(!done.Contains(scenario.Id), "准备不完成目标外传");
            string account = ("side127-" + scenario.Id + "-" + scenario.Suffix).ToLowerInvariant();
            string name = "Side" + scenario.Id + scenario.Suffix;
            That(NameWidth(name) <= 12, "测试昵称符合CP936的12字节上限");
            Character created;
            using (var owner = new AccountOwners().Acquire(account, () => { }))
            {
                var candidate = CharacterCreation.Prepare(name, resources.Characters.Get(scenario.Job), resources.Towns.Room,
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
                    That(State(created.Id) == before, "低一级/错误职业31拒绝且没有持久化：" + scenario.Id + scenario.Suffix);
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
                    if (scenario.Id == 13601)
                    {
                        string accepted = State(created.Id);
                        peer.Send(31, [31, 0, (byte)scenario.Id, (byte)(scenario.Id >> 8)]);
                        peer.Expect(1, 31, [0, 18, 0]); peer.Barrier();
                        That(State(created.Id) == accepted, "重复真实31不新建身份、回执或revision");
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
                Cleanup(peer, created, account);
            }
        }
        Console.WriteLine($"PASS {Count-checks} 六条真实外传21/31持久接取与断线恢复（非目标通关或客户端验收）");

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
