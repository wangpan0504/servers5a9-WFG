using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class QuestQuickClearTcpTests
{
    internal static async Task RunAsync(TcpTestFixture fixture, TcpTestResources resources)
    {
        var quests = SelfTestCatalogs.Quests(resources.Archive);
        var connections = SelfTestCatalogs.QuestConnections(resources.Archive);
        var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
        var roles = new List<(ushort Slot,int Question,int Marker,byte Answer)>();
        foreach (var unit in connections.Units.Where(row => row.QuickClear is not null && row.QuestionQuestId is not null))
        {
            var question = quests.Get(unit.QuestionQuestId!.Value);
            var definition = question.Definition!;
            var required = definition.PrerequisiteGroups.FirstOrDefault(group => group.Length != 0);
            var completed = (required.IsDefault ? [] : required.ToArray()).Concat(definition.RequiredAnswers.Select(row => row.QuestId)).Distinct().ToArray();
            Character created;
            using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,() => { }))
            {
                var candidate = CharacterCreation.Prepare("Skip"+unit.ParentKey,resources.Characters.Get(15),resources.Towns.Room,
                    resources.Towns.InitialReturn,resources.Learning.Get);
                var learning = SkillPointLedger.Synchronize(15,0,95,candidate.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(95) },resources.Learning);
                created = new CharacterRepository(fixture.Database).Create(owner,candidate with
                    { Level = 95, CompletedQuests = completed, InitialLearning = learning });
            }
            foreach (var answer in definition.RequiredAnswers)
                fixture.Sql("UPDATE character_completed_quests SET answer=$p0 WHERE character_id=$p1 AND quest_id=$p2",answer.Answer,created.Id,answer.QuestId);
            using var peer = Open(fixture);
            peer.Select(created.Slot,prepared: true);
            byte[] historyBeforeAccept = peer.CompletedQuestHistory;
            peer.Send(31,[31,0,(byte)question.Id,(byte)(question.Id >> 8)]);
            peer.Expect(1,31,[1,(byte)question.Id,(byte)(question.Id >> 8),1,0,0,0,0]); peer.Expect(0,21);
            That(fixture.Query("SELECT condition_state FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",created.Id,question.Id)
                is [[4294967295L]],"未回答哨兵仅保存在服务端，ACK31仍投影进行中状态1");
            Bytes(peer.Expect(0,356).Body, historyBeforeAccept, "旅程问题接取后保留原完成前置与回答"); peer.Barrier();
            string assets = Assets(created.Id);
            string untouched = State(created.Id);
            peer.Send(1714,Build(writer => writer.Write(uint.MaxValue))); peer.Expect(1,1714,[0,0,0]); peer.Barrier();
            That(State(created.Id) == untouched,"未知完整u32 parent key不截断，完整普通/任务资产及进度都不变");
            peer.Send(1714,Build(writer => writer.Write((uint)unit.ParentKey))); peer.Expect(1,1714,[1]);
            var next = peer.Read();
            if (next is { Kind: 0, Opcode: 14 })
            {
                var special = fixture.Query("""
                    SELECT s.slot_index,json_extract(i.item_core,'$.ItemId'),json_extract(i.item_core,'$.Value')
                    FROM character_inventory_slots s JOIN character_inventory_items i ON i.item_uid=s.item_uid
                    WHERE s.character_id=$p0 AND s.list_type=0 AND json_extract(i.item_core,'$.Kind')=$p1
                    ORDER BY s.slot_index
                    """,created.Id,(int)ItemKind.Quest);
                var delta = next.Body;
                That(delta.Length == 3 + 123 * special.Length && delta[0] == 0 && U16(delta,1) == special.Length,
                    "跳过ACK后先投影完整任务物品差量，数量与同事务持久槽一致");
                for (int at = 0; at < special.Length; at++)
                {
                    int offset = 3 + 123 * at;
                    That(U16(delta,offset) == Convert.ToInt32(special[at][0]) && U32(delta,offset+2) == Convert.ToInt32(special[at][1]) &&
                        U32(delta,offset+6) == Convert.ToInt32(special[at][2]) && resources.ItemResources.Items.Get(checked((int)U32(delta,offset+2))).Kind == ItemKind.Quest,
                        "任务物品差量按实际持久槽、模板和数量逐行匹配，不发布普通装备奖励");
                }
                next = peer.Read();
            }
            else
                That(fixture.Query("SELECT count(*) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.Kind')=$p1",
                    created.Id,(int)ItemKind.Quest) is [[0L]],"没有任务物品变化时不省略应发布的资产差量");
            That(next is { Kind: 0, Opcode: 574 },"特殊任务物品差量之后发布完整活动任务列表");
            var active = next.Body;
            That(active.Length == 4+6*U32(active,0),"skip完整活动投影逐行保留原任务槽");
            peer.Expect(0,21);
            var table = peer.Expect(0,356).Body;
            var quick = unit.QuickClear!.Value;
            That(table.Length == 30004 && U32(table,0) == 30000 && table[4+question.Id] == quick.Answer+1 && table[4+quick.MarkerQuestId] == 1,
                "skip完成表最后发布真实回答与角色cascade marker");
            peer.Barrier();
            That(Assets(created.Id) == assets && fixture.Query("SELECT count(*) FROM character_quest_skip_receipts WHERE character_id=$p0",created.Id) is [[1L]],
                "真实旅程skip仅兑现任务物品，普通物品UID、金币、经验、技能和普通领奖回执保持");
            string saved = State(created.Id);
            peer.Send(1714,Build(writer => writer.Write((uint)unit.ParentKey))); peer.Expect(1,1714,[0,0,0]); peer.Barrier();
            That(State(created.Id) == saved,"跳过已提交后的重复请求不重发任务奖励或写第二条回执");
            roles.Add((created.Slot,question.Id,quick.MarkerQuestId,checked((byte)quick.Answer)));
        }
        // fixture使用共享PVF在宿主进程内服务；这是服务实例恢复，不宣称独立Windows进程恢复。
        await fixture.StopAsync(); await fixture.StartAsync();
        foreach (var role in roles)
        {
            using var peer = Open(fixture);
            var packets = peer.Select(role.Slot,prepared: true);
            var table = packets.Single(packet => packet.Kind == 0 && packet.Opcode == 356).Body;
            That(table[4+role.Question] == role.Answer+1 && table[4+role.Marker] == 1,"服务实例重建后真实skip回答和角色标记正常回放");
            peer.Send(1714,Build(writer => writer.Write((uint)connections.Units.Single(unit => unit.QuestionQuestId == role.Question).ParentKey)));
            peer.Expect(1,1714,[0,0,0]); peer.Barrier();
        }

        string Assets(ushort id) => fixture.Snapshot("SELECT job,grow_type,level,experience,equipment_slot_flags FROM characters WHERE id=$p0",id)+
            fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.Kind')!=$p1 ORDER BY item_uid",id,(int)ItemKind.Quest)+
            fixture.Snapshot("SELECT s.* FROM character_inventory_slots s JOIN character_inventory_items i ON i.item_uid=s.item_uid WHERE s.character_id=$p0 AND json_extract(i.item_core,'$.Kind')!=$p1 ORDER BY s.list_type,s.slot_index",id,(int)ItemKind.Quest)+
            fixture.Snapshot("SELECT * FROM character_skill_points WHERE character_id=$p0 ORDER BY context",id)+
            fixture.Snapshot("SELECT * FROM character_skills WHERE character_id=$p0 ORDER BY skill_id",id)+
            fixture.Snapshot("SELECT * FROM character_skill_bindings WHERE character_id=$p0 ORDER BY context,slot",id)+
            fixture.Snapshot("SELECT * FROM account_wallets")+fixture.Snapshot("SELECT * FROM account_currency_balances")+
            fixture.Snapshot("SELECT * FROM inventory_audit_log WHERE COALESCE(item_kind_before,-1) NOT IN(-1,$p0) OR COALESCE(item_kind_after,-1) NOT IN(-1,$p0)",(int)ItemKind.Quest)+
            fixture.Snapshot("SELECT * FROM character_quest_completion_receipts");

        string State(ushort id) => fixture.Snapshot()+Assets(id)+
            fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid",id)+
            fixture.Snapshot("SELECT * FROM character_inventory_slots WHERE character_id=$p0 ORDER BY list_type,slot_index",id)+
            fixture.Snapshot("SELECT * FROM inventory_audit_log")+
            fixture.Snapshot("SELECT * FROM character_active_main_quests WHERE character_id=$p0 ORDER BY slot",id)+
            fixture.Snapshot("SELECT * FROM character_completed_quests WHERE character_id=$p0 ORDER BY quest_id",id)+
            fixture.Snapshot("SELECT * FROM character_quest_skip_receipts WHERE character_id=$p0",id)+
            fixture.Snapshot("SELECT * FROM character_quest_effect_receipts WHERE character_id=$p0",id)+
            fixture.Snapshot("SELECT * FROM character_quest_special_reward_claims WHERE character_id=$p0",id);
    }
}
