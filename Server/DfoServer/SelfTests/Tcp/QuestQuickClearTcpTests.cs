using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
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
        var quests = QuestCatalog.Load(resources.Archive);
        var connections = QuestConnectionCatalog.Load(resources.Archive);
        var growth = CharacterGrowthCatalog.Load(resources.Archive,resources.Learning);
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
            peer.Send(31,[31,0,(byte)question.Id,(byte)(question.Id >> 8)]);
            peer.Expect(1,31,[1,(byte)question.Id,(byte)(question.Id >> 8),255,255,255,255,0]); peer.Expect(0,21); peer.Barrier();
            string assets = Assets(created.Id);
            peer.Send(1714,Build(writer => writer.Write(uint.MaxValue))); peer.Expect(1,1714,[0,0,0]); peer.Barrier();
            That(Assets(created.Id) == assets,"未知完整u32 parent key不截断、不修改资产");
            peer.Send(1714,Build(writer => writer.Write((uint)unit.ParentKey))); peer.Expect(1,1714,[1]);
            var active = peer.Expect(0,574).Body;
            That(active.Length == 4+6*U32(active,0),"skip完整活动投影逐行保留原任务槽");
            peer.Expect(0,21);
            var table = peer.Expect(0,356).Body;
            var quick = unit.QuickClear!.Value;
            That(table.Length == 30004 && U32(table,0) == 30000 && table[4+question.Id] == quick.Answer+1 && table[4+quick.MarkerQuestId] == 1,
                "skip完成表最后发布真实回答与角色cascade marker");
            peer.Barrier();
            That(Assets(created.Id) == assets && fixture.Query("SELECT count(*) FROM character_quest_skip_receipts WHERE character_id=$p0",created.Id) is [[1L]],
                "真实旅程skip无物品、金币、经验、技能、UID或普通领奖回执变化");
            string saved = fixture.Snapshot();
            peer.Send(1714,Build(writer => writer.Write((uint)unit.ParentKey))); peer.Expect(1,1714,[0,0,0]); peer.Barrier();
            That(fixture.Snapshot() == saved,"跳过已提交后的重复请求不重发奖励或写第二条回执");
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
            fixture.Snapshot("SELECT * FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid",id)+
            fixture.Snapshot("SELECT * FROM character_skill_points WHERE character_id=$p0 ORDER BY context",id)+
            fixture.Snapshot("SELECT * FROM character_skills WHERE character_id=$p0 ORDER BY skill_id",id)+
            fixture.Snapshot("SELECT * FROM character_skill_bindings WHERE character_id=$p0 ORDER BY context,slot",id)+
            fixture.Snapshot("SELECT * FROM account_wallets")+fixture.Snapshot("SELECT * FROM account_currency_balances")+
            fixture.Snapshot("SELECT * FROM inventory_audit_log")+fixture.Snapshot("SELECT * FROM character_quest_completion_receipts");
    }
}
