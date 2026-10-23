using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

// 真实2929的城镇协议入口；不进入悲叹之塔，也不生成通关或领奖事实。
internal static class GriefTowerAcceptanceTcpTests
{
    internal static Action Verify(TcpTestFixture fixture, TcpTestResources resources)
    {
        int checks = Count;
        const ushort questId = 2929;
        var quest = resources.Quests.Get(questId);
        That(quest.Grade == "[side]" && quest.Condition == "[clear a dungeon]" && quest.SubType == 6 &&
            quest.MinimumLevel == 90, "2929保持真实外传、等级及列表通关声明");
        var prerequisites = quest.Definition!.PrerequisiteGroups.Take(1).SelectMany(group => group).Append(3145).Distinct().ToArray();
        That(!prerequisites.Contains(questId), "隔离准备不包含目标2929完成位");
        var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
        var repository = new CharacterRepository(fixture.Database, resources.Quests);
        Character created;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("GriefTower", resources.Characters.Get(0),
                resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get);
            created = repository.Create(owner, candidate with
            {
                Level = 90, AcceptedStoryLevel = 90, CompletedQuests = prerequisites,
                InitialLearning = SkillPointLedger.Synchronize(0, 0, 90, candidate.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(90) }, resources.Learning)
            });
        }
        That(QuestEligibility.Evaluate(created, quest, resources.Quests).Eligible &&
            fixture.Query("SELECT count(*) FROM character_active_main_quests WHERE character_id=$p0", created.Id) is [[0L]],
            "实际前置及90级仅准备接取资格，未预填活动槽或通关");
        string acceptance = "", persisted = "", dailyAcceptance = "";
        foreach (bool reconnect in new[] { false, true })
        {
            using var peer = Open(fixture);
            var packets = peer.Select(created.Slot, prepared: true, resumeTown: true,
                room: resources.Towns.IsRoom(created.Position, prerequisites.ToHashSet()));
            That(peer.Id == created.Id && QuestLifecycleTcpTests.List(peer.AvailableQuests, 90).Contains(questId),
                "真实登录21包含2929及正确隔离角色");
            if (!reconnect)
            {
                var daily = fixture.Query("SELECT quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot",created.Id);
                That(daily is [[13056L,1686L,string]],"90级选角只自动分配地轨每日，不预填塔任务");
                dailyAcceptance=(string)daily[0][2];
                peer.Send(31, [31, 0, 0x71, 0x0B]);
                var ack = peer.Expect(1, 31).Body;
                That(ack.Length >= 8 && ack[0] == 1 && U16(ack, 1) == questId && U32(ack, 3) == 1,
                    "真实TCP31接取2929，初值pending1：" + Convert.ToHexString(ack));
                QuestLifecycleTcpTests.DrainNotifications(peer);
                var rows = fixture.Query("SELECT quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot", created.Id);
                That(rows is [[13056L,1686L,string originalDaily],[2929L,1L,string]] && originalDaily==dailyAcceptance,
                    "TCP31只新增2929 pending1，不改变已自动分配的每日任务");
                acceptance = (string)rows[1][2];
                That(Guid.TryParseExact(acceptance, "N", out var identity) && identity != Guid.Empty &&
                    fixture.Query("SELECT quest_id,acceptance_id FROM character_quest_lifecycle_receipts WHERE character_id=$p0", created.Id)
                        is [[2929L, string receipt]] && receipt == acceptance,
                    "活动槽和唯一接取回执使用同一非空AcceptanceId");
            }
            else
            {
                That(Progress() == persisted &&
                    fixture.Query("SELECT acceptance_id,condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=2929", created.Id)
                        is [[string restored, 1L]] && restored == acceptance,
                    "新TCP连接恢复原2929身份和pending1，不重接或完成");
                var active = packets.Single(packet => packet is { Kind: 1, Opcode: 4 }).Body;
                That(active.Length == 235 && active[0] == 1 && Enumerable.Range(0, 30).All(slot =>
                    U16(active, 22 + 6 * slot) == (slot == 0 ? 13056 : slot == 1 ? questId : ushort.MaxValue) &&
                    U32(active, 24 + 6 * slot) == (slot == 0 ? 1686u : slot == 1 ? 1u : 0u)),
                    "重登ACK4完整恢复每日、塔任务pending1和其余空槽");
            }
            string before = State();
            peer.Send(33, [33, 0, 0x71, 0x0B, 0, 0]); peer.Expect(1, 33, [1, 0x71, 0x0B, 1, 0, 0, 0]); peer.Barrier();
            That(State() == before, "真实33保持2929状态、资产、revision及接取回执只读");
            peer.Send(34, [34, 0, 0x71, 0x0B, 255, 255, 1, 0, 255, 255]); peer.Expect(1, 34, [0, 19, 0]); peer.Barrier();
            peer.Send(33, [33, 0, 0x71, 0x0B, 0, 0]); peer.Expect(1, 33, [1, 0x71, 0x0B, 1, 0, 0, 0]); peer.Barrier();
            That(State() == before &&
                fixture.Query("SELECT count(*) FROM character_quest_clear_receipts WHERE character_id=$p0", created.Id) is [[0L]] &&
                fixture.Query("SELECT count(*) FROM character_quest_completion_receipts WHERE character_id=$p0", created.Id) is [[0L]] &&
                fixture.Query("SELECT count(*) FROM character_completed_quests WHERE character_id=$p0 AND quest_id=2929", created.Id) is [[0L]],
                "未通关提前34拒绝，无通关、完成、奖励或资产变化");
            persisted = Progress();
        }
        Console.WriteLine($"PASS {Count-checks} 真实2929 TCP31接取、33只读、提前34拒绝及新连接恢复（非塔准入或通关验收）");
        return () =>
        {
            var events = fixture.Events();
            var accepted = events.Where(row => row.GetProperty("Kind").GetString() == "quest_lifecycle")
                .Select(row => row.GetProperty("Detail"))
                .Where(row => row.TryGetProperty("questId", out var id) && id.GetInt32() == questId &&
                    row.GetProperty("CharacterId").GetInt32() == created.Id).ToArray();
            That(accepted.Length == 1, "2929日志只有本角色的一次真实接取");
            var detail = accepted.Single();
            That(detail.GetProperty("action").GetInt32() == (int)QuestLifecycleAction.Accept &&
                detail.GetProperty("Outcome").GetInt32() == (int)QuestLifecycleOutcome.Committed &&
                detail.GetProperty("Accepted").GetBoolean() && detail.GetProperty("Reason").GetString() == "任务事务已提交",
                "2929实际quest_lifecycle.Reason为已提交，不能把静态资格当TCP结果");
            var condition = events.Where(row => row.GetProperty("Kind").GetString() == "quest_condition")
                .Select(row => row.GetProperty("Detail"))
                .Where(row => row.GetProperty("QuestId").GetInt32() == questId).ToArray();
            That(condition.Length == 4 && condition.GroupBy(row => row.GetProperty("Connection").GetInt32()).Count() == 2 &&
                condition.GroupBy(row => row.GetProperty("Connection").GetInt32()).All(group => group.Count() == 2) &&
                condition.Any(row => row.GetProperty("Connection").GetInt32() == detail.GetProperty("Connection").GetInt32()) &&
                condition.All(row => row.GetProperty("Outcome").GetInt32() == (int)QuestLifecycleOutcome.Unchanged &&
                    row.GetProperty("Reason").GetString() == "当前条件已由实际场景或库存满足，无需再次保存"),
                "两连接在34前后均保持pending1，并记录实际33 Unchanged及Reason");
            var connections = condition.Select(row => row.GetProperty("Connection").GetInt32()).ToHashSet();
            var refusals = events.Where(row => row.GetProperty("Kind").GetString() == "quest_completion")
                .Select(row => row.GetProperty("Detail"))
                .Where(row => row.GetProperty("Quest").GetInt32() == questId && connections.Contains(row.GetProperty("Connection").GetInt32())).ToArray();
            That(refusals.Length == 2 && refusals.Select(row => row.GetProperty("Connection").GetInt32()).Distinct().Count() == 2 &&
                refusals.All(row => row.GetProperty("Outcome").GetInt32() == (int)QuestFinishOutcome.Rejected &&
                    !row.GetProperty("Accepted").GetBoolean() && row.GetProperty("Failure").GetString() == "EligibilityChanged" &&
                    row.GetProperty("RejectReason").GetString() == "缺少本次接取的通关凭据"),
                "两连接提前34确因缺少通关凭据拒绝，不把选择或解析错误当作通过");
            Console.WriteLine("PASS 2929 TCP日志原因：31=" + detail.GetProperty("Reason").GetString() +
                "; 33=" + condition[0].GetProperty("Reason").GetString() + "; 34=" + refusals[0].GetProperty("RejectReason").GetString());
        };

        string Progress() => fixture.Snapshot("SELECT * FROM character_active_main_quests WHERE character_id=$p0", created.Id) +
            fixture.Snapshot("SELECT * FROM character_quest_lifecycle_receipts WHERE character_id=$p0 ORDER BY revision", created.Id) +
            fixture.Snapshot("SELECT * FROM character_completed_quests WHERE character_id=$p0 ORDER BY quest_id", created.Id);
        string State()
        {
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            return Progress() + JsonSerializer.Serialize(repository.LoadCharacter(owner, created.Slot));
        }
    }
}
