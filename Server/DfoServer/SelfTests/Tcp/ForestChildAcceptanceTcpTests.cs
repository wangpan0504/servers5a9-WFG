using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class ForestChildAcceptanceTcpTests
{
    // 复用热服务；只准备合法等级、城镇与前置完成夹具，不代表连续游玩前置或Windows界面验收。
    internal static void Verify(TcpTestFixture fixture, TcpTestResources resources, CharacterGrowthCatalog growth,
        Action<TcpPeer, Action<WirePacket>?> drain)
    {
        var quest = resources.Quests.Get(3292);
        That(quest.MinimumLevel == 41 && quest.MaximumLevel == 99 && quest.Npc == 42 &&
            quest.Condition == "[clear map]" && quest.SubType == -1 && quest.IntData is [92046, -1] &&
            quest.Definition is { AcceptanceRewards.Length: 0 } definition &&
            definition.PrerequisiteGroups is [[3291]], "真实3292保留不限难度尾项、雷诺和唯一前置3291，无接取赠品");
        foreach (bool eligible in new[] { true, false }) VerifyCase(eligible);

        void VerifyCase(bool eligible)
        {
            const byte level = 41;
            string account = eligible ? "forest-child" : "forest-no-prior";
            string name = eligible ? "ForestChild" : "ForestNoPre";
            var prerequisites = quest.Definition!.PrerequisiteGroups.Single();
            var area = resources.Towns.Areas.Values.First(row => row.Level <= level &&
                row.Map(Completed(row)).Npcs.Any(npc => npc.Id == quest.Npc) &&
                resources.Towns.DestinationAdmission(new(row.Town, row.Area, 0, 0), level, Completed(row)) == "ready");
            var done = Completed(area);
            var map = area.Map(done);
            var npc = map.Npcs.Single(row => row.Id == quest.Npc);
            var walk = map.Bounds.Where(row => row.Conditions.Length == 0 && row.Quests.All(done.Contains))
                .OrderBy(row => Math.Abs(Math.Clamp(npc.X, row.Bounds.X, row.Bounds.X + row.Bounds.Width) - npc.X) +
                    Math.Abs(Math.Clamp(npc.Y, row.Bounds.Y, row.Bounds.Y + row.Bounds.Height) - npc.Y)).First().Bounds;
            var position = new Location(area.Town, area.Area,
                checked((ushort)Math.Clamp(npc.X, walk.X, walk.X + walk.Width)),
                checked((ushort)Math.Clamp(npc.Y, walk.Y, walk.Y + walk.Height)));
            That(resources.Towns.IsPosition(position, done) && done.Contains(3291) == eligible,
                "隔离角色处于真实雷诺城镇合法位置，仅前置3291区分可接与拒绝");
            Character created;
            using (var owner = new AccountOwners().Acquire(account, () => { }))
            {
                var candidate = CharacterCreation.Prepare(name, resources.Characters.Get(0), position, position, resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(0, 0, level, candidate.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(level) }, resources.Learning);
                created = new CharacterRepository(fixture.Database).Create(owner, candidate with
                    { Level = level, AcceptedStoryLevel = level, CompletedQuests = done.ToArray(), InitialLearning = learning });
            }
            That(fixture.Query("SELECT slot FROM character_active_main_quests WHERE character_id=$p0", created.Id).Length == 0,
                "首次登录前没有预填活动槽或接取身份");
            string acceptance = "";
            using (var peer = Open(fixture, account))
            {
                Roster(peer.Roster, true);
                Selected(peer.Select(0, prepared: true, resumeTown: true, room: false)[0].Body, false);
                That(QuestLifecycleTcpTests.List(peer.AvailableQuests, level).Contains(3292) == eligible,
                    "初始21按实际前置决定3292候选，不用视频金币替代资格");
                string before = State();
                peer.Send(31, [31, 0, 220, 12]);
                if (eligible)
                {
                    peer.Expect(1, 31, [1, 220, 12, 1, 0, 0, 0, 0]);
                    That(QuestLifecycleTcpTests.List(peer.Expect(0, 21).Body, level).Contains(3292),
                        "成功ACK31后21保留原任务定义，活动身份由真实接取建立");
                    Bytes(peer.Expect(0, 356).Body, peer.CompletedQuestHistory, "接取后前置历史完整且不完成3292");
                    drain(peer, null);
                    var rows = fixture.Query("SELECT slot,quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0", created.Id);
                    That(rows is [[0L, 3292L, 1L, string accepted]] && Guid.TryParseExact(accepted, "N", out var identity) &&
                        identity != Guid.Empty, "真实31落库唯一活动槽0、剩余1和非空接取UUID");
                    acceptance = (string)rows.Single()[3];
                    That(fixture.Query("SELECT action,quest_id,acceptance_id FROM character_quest_lifecycle_receipts WHERE character_id=$p0", created.Id)
                        is [["Accept", 3292L, string saved]] && saved == acceptance &&
                        fixture.Query("SELECT quest_id FROM character_completed_quests WHERE character_id=$p0 AND quest_id=3292", created.Id).Length == 0,
                        "活动槽与原接取生命周期共事务，不补造完成或清图事实");
                    string acceptedState = State();
                    peer.Send(31, [31, 0, 220, 12]); peer.Expect(1, 31, [0, 18, 0]); peer.Barrier();
                    That(State() == acceptedState, "重复31拒绝且原UUID、条件、资产和生命周期均不变");
                }
                else
                {
                    peer.Expect(1, 31, [0, 18, 0]); peer.Barrier();
                    That(State() == before, "缺真实前置3291时拒绝31，不创建槽、UUID或资产副作用");
                }
                peer.Send(3, [0]); peer.Expect(1, 3, [1]); peer.Closed();
            }
            using (var peer = Open(fixture, account))
            {
                Roster(peer.Roster, true);
                Selected(peer.Select(0, prepared: true, resumeTown: true, room: false)[0].Body, eligible);
                That(QuestLifecycleTcpTests.List(peer.AvailableQuests, level).Contains(3292) == eligible &&
                    peer.CompletedQuestHistory[4 + 3292] == 0, "重连21与完成历史保持实际接取资格，不把重连视为通关");
                That(eligible ? fixture.Query("SELECT slot,quest_id,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0", created.Id)
                    is [[0L, 3292L, 1L, string restored]] && restored == acceptance :
                    fixture.Query("SELECT slot FROM character_active_main_quests WHERE character_id=$p0", created.Id).Length == 0,
                    "重连回放原活动槽、UUID与状态；拒绝场景仍为空");
                // 本条全部断言成功后才清理自己的角色；失败自然保留独立账号现场。
                peer.Send(7); peer.Expect(1, 7, [1]); Roster(peer.Expect(0, 2).Body, true);
                peer.Send(6, Build(writer => { writer.Write((ushort)0); writer.Write(Name(name)); }));
                peer.Expect(1, 6, [1, 0, 0, 0]); Roster(peer.Expect(0, 2).Body, false);
                peer.Send(692, Name(name)); peer.Expect(1, 692, [1]);
                That(fixture.Query("SELECT count(*) FROM characters WHERE account=$p0", account) is [[0L]], "成功后仅删本条自建角色");
                peer.Send(3, [0]); peer.Expect(1, 3, [1]); peer.Closed();
            }

            HashSet<int> Completed(TownArea target)
            {
                var result = new HashSet<int>(target.Quests);
                if (target.AnyQuests.Length != 0) result.Add(target.AnyQuests[0]);
                result.ExceptWith(prerequisites); result.Remove(3292);
                if (eligible) result.UnionWith(prerequisites);
                return result;
            }
            void Selected(byte[] body, bool active)
            {
                That(body.Length == 235 && body[0] == 1 && U16(body, 9) == created.Id, "完整ACK4绑定隔离账号唯一角色");
                for (int slot = 0; slot < 30; slot++)
                    That(U16(body, 22 + 6 * slot) == (active && slot == 0 ? 3292 : ushort.MaxValue) &&
                        U32(body, 24 + 6 * slot) == (active && slot == 0 ? 1u : 0u), "ACK4完整三十槽只回放真实接取");
            }
            void Roster(byte[] body, bool present)
            {
                That(body[0] == 2 && body[1] == (present ? 1 : 0) && U16(body, 7) == body[1] && U16(body, 13) == body[1],
                    "独立账号名册只包含本条角色或清理后的空名册");
                if (present) That(U16(body, 15) == created.Slot && DecodeName(body, 21, checked((int)U32(body, 17))) == name,
                    "首次选角名册行0精确绑定真实slot及确认名");
            }
            string State() => fixture.Snapshot("SELECT * FROM characters WHERE id=$p0", created.Id) +
                fixture.Snapshot("SELECT * FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot", created.Id) +
                fixture.Snapshot("SELECT * FROM character_quest_lifecycle_receipts WHERE character_id=$p0 ORDER BY revision", created.Id) +
                fixture.Snapshot("SELECT * FROM character_completed_quests WHERE character_id=$p0 ORDER BY quest_id", created.Id) +
                fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", created.Id) +
                fixture.Snapshot("SELECT * FROM character_inventory_slots WHERE character_id=$p0 ORDER BY list_type,slot_index", created.Id);
        }
    }
}
