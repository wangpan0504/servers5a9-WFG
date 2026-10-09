using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestCharacterRewardTests
{
    public static void RunCore()
    {
        int checks = Count;
        var quest = QuestEligibilityTests.Quest(100) with
        {
            Attributes = ["slot expansion"],
            Definition = new([
                new("exception filter",[new(6,0,"character")],[]),
                new("exception quest",[new(6,0,"[urgent]")],[]),
                new("attribute",[new(6,0,"slot expansion")],[])])
            { Job = "[all]", Exceptions = [new(6,0,"[urgent]")] }
        };
        That(QuestCoverage.HasOrdinaryDeclarations(quest),"已确认角色分类、urgent例外与槽位属性沿普通任务合同");
        var catalog = new QuestCatalog(new() { [100] = quest });
        var character = new Character(1,0,"Slots",1,new Location(38,2,450,240),null);
        for (byte job = 0; job < 16; job++)
            That(QuestEligibility.Evaluate(character with { Job = job, EquipmentSlotFlags = 0x13 },quest,catalog).Visible,
                "分类及属性不猜测装备槽未解锁资格，保留所有职业实际资格 " + job);
        foreach (var invalid in new[]
        {
            Field("exception filter",[new(6,0,"future")]), Field("exception filter",[new(0,3,null)]),
            Field("exception filter",[]), Field("exception filter",[new(6,0,"character"),new(6,0,"character")]),
            Field("exception filter",[new(6,0,"character")],[new("unknown",[],[])]),
            Field("exception quest",[new(6,0,"[future]")]), Field("exception quest",[new(0,0,null)]),
            Field("exception quest",[]), Field("exception quest",[new(6,0,"[urgent]"),new(6,0,"[urgent]")]),
            Field("exception quest",[new(6,0,"[urgent]")],[new("unknown",[],[])]),
            Field("attribute",[new(6,0,"future")]), Field("attribute",[new(0,29,null)]),
            Field("attribute",[new(6,0,"slot expansion")],[new("unknown",[],[])])
        })
        {
            That(!QuestCoverage.HasOrdinaryDeclarations(invalid),"未知分类、例外、属性或形状不借用已确认合同");
            Reject<NotSupportedException>(() => QuestCoverage.RequireOrdinaryDeclarations(invalid),"声明失败有明确限制");
        }
        That(QuestEligibility.InspectResource(Field("exception filter",[new(6,0,"future")])) is
            { Entry: QuestResourceEntry.Dedicated, Reason: "exception filter需要已确认的单字符串character声明" },
            "未确认filter保留专用入口与字段限制");
        var empty = ReadPrerequisites(false); var mixed = ReadPrerequisites(true);
        var prerequisites = new QuestCatalog(new() { [100] = empty, [103] = QuestEligibilityTests.Quest(103) });
        That(empty.Definition is { Diagnostics.Length: 0, PrerequisiteGroups.Length: 0 } &&
            empty.Definition.Has("pre required quest") && QuestEligibility.Evaluate(character,empty,prerequisites).Eligible,
            "空前置声明保留原文并按external忽略，不阻断正常资格");
        That(mixed.Definition is { Diagnostics.Length: 0, PrerequisiteGroups.Length: 1 } &&
            mixed.Definition.PrerequisiteGroups[0].SequenceEqual([103]) &&
            !QuestEligibility.Evaluate(character,mixed,prerequisites).Eligible &&
            QuestEligibility.Evaluate(character with { CompletedQuests = [103] },mixed,prerequisites).Eligible,
            "空OR组不能绕过保留下来的真实非空前置");
        Console.WriteLine($"PASS {Count-checks} 槽位任务声明、分类及空前置合同断言");

        StoryQuest Field(string name,ImmutableArray<QuestRewardToken> values,ImmutableArray<QuestField> children = default)
        {
            var definition = quest.Definition!;
            return quest with { Definition = definition with
            {
                Fields = definition.Fields.Select(field => field.Name == name ? new(name,values,children.IsDefault ? [] : children) : field).ToImmutableArray(),
                Exceptions = name == "exception quest" ? values : definition.Exceptions
            } };
        }
        static StoryQuest ReadPrerequisites(bool mixed)
        {
            var strings = new Dictionary<int,string> { [1] = "[level]", [2] = "[type]", [3] = "[meet npc]", [4] = "[pre required quest]" };
            var tokens = new List<PvfToken> { new(3,1),new(0,1),new(0,99),new(3,2),new(6,3),new(3,4) };
            if (mixed) { tokens.Add(new(3,4)); tokens.Add(new(0,103)); }
            return QuestDefinitionReader.Read(100,"fixture/slots.qst",tokens,id => strings[id]);
        }
    }

    public static void Run(TownCatalog towns,PvfArchive pvf,CharacterCatalog characters,ItemResources items)
    {
        int checks = Count;
        var skills = SkillLearningCatalog.Load(pvf); var growth = CharacterGrowthCatalog.Load(pvf,skills);
        var parameters = QuestRewardParameterCatalog.Load(pvf);
        var empty = QuestEligibilityTests.Quest(100) with { Npc = 2, CompleteNpc = 2, IntData = [2], Attributes = ["not give exp quest"] };
        var catalog = QuestCatalog.Load(pvf);
        That(catalog.All.Where(quest => quest.RewardType == "[slot expansion]" && QuestCharacterReward.TryScalar(quest,out _))
            .Select(quest => quest.Rewards[0].Value).Distinct().Order().SequenceEqual([0,1,2]),"三个装备槽奖励使用实际标量而非固定任务编号");
        That(catalog.All.Where(quest => quest.RewardType == "[tp]").All(quest => quest.Rewards.Count == 0 &&
            !QuestCharacterReward.TryScalar(quest,out _)),"本版空参数TP奖励不套用不存在的正整数声明，不伪造额外来源");
        var slotQuests = catalog.All.Where(quest => quest.Attributes.Any(value => value is "slot expansion" or "magic stone")).ToArray();
        That(slotQuests.Select(quest => quest.Id).Order().SequenceEqual([649,650,674,675,676,677,2635,2636,2711]) &&
            slotQuests.All(QuestCoverage.HasOrdinaryDeclarations),"九条真实槽位任务完整保留已确认声明，尾部任务不按固定六项遗漏");
        That(catalog.Get(675).Definition is { PrerequisiteGroups.Length: 0 } &&
            catalog.Get(675).Definition!.Values("pre required quest").Length == 0 && catalog.Get(675).Definition!.Has("pre required quest"),
            "真实魔法石前驱的空前置不被变成永久资格门禁");
        foreach (var quest in slotQuests)
        {
            var ready = new Character(1,0,"Slots",1,towns.Room,null) { Level = (byte)quest.MinimumLevel,
                CompletedQuests = quest.Prerequisites.ToArray() };
            for (byte job = 0; job < 16; job++)
            {
                var expected = ready with { Job = job };
                That(QuestEligibility.Evaluate(expected,quest,catalog).Visible &&
                    QuestEligibility.Evaluate(expected with { EquipmentSlotFlags = 0x13 },quest,catalog).Visible &&
                    !QuestEligibility.Evaluate(expected with { Level = (byte)(quest.MinimumLevel-1) },quest,catalog).Eligible,
                    "真实槽位链执行资源等级与16职业资格，不额外强加slot flag条件 " + quest.Id + "/" + job);
            }
            if (quest.Prerequisites.Count != 0)
                That(!QuestEligibility.Evaluate(ready with { CompletedQuests = [] },quest,catalog).Eligible,
                    "真实槽位后继仍要求完成相应前驱 " + quest.Id);
            if (quest.Condition != "[hunt monster]") continue;
            That(QuestHuntProgress.TryTargets(quest,out var targets),"真实前驱四元组进入共用权威猎杀模型 " + quest.Id);
            var active = new ActiveMainQuest(0,(ushort)quest.Id,QuestHuntProgress.Initial(quest)) { AcceptanceId = Guid.NewGuid() };
            var map = new DungeonMap("boss",new(0,0),76126,"map/fixture.map");
            var maze = new DungeonMaze(0,1,1,"fixture",[map.Position],[map.Position],[map],[]);
            var entry = new DungeonEntryPlan(1,1,0,(uint)quest.Id,0,2,maze,map,map) { QuestAcceptances = [active] };
            var after = active;
            foreach (var target in targets)
                after = QuestHuntProgress.MonsterDeath([after],entry with { DungeonId = (uint)target.DungeonId },catalog,target.MonsterId)[0];
            That(after.ConditionState == 0 && after.AcceptanceId == active.AcceptanceId &&
                QuestHuntProgress.MonsterDeath([active with { AcceptanceId = Guid.NewGuid() }],
                    entry with { DungeonId = (uint)targets[0].DungeonId },catalog,targets[0].MonsterId)[0].ConditionState == active.ConditionState,
                "真实前驱包括多地下城通道只由绑定接取的对应死亡推进，旧接取不能推进 " + quest.Id);
        }
        foreach (var quest in slotQuests.Where(quest => quest.RewardType == "[slot expansion]"))
        {
            byte level = (byte)quest.MinimumLevel;
            using var f = new QuestOrdinaryCompletionTests.Fixture(towns,characters,skills,growth,parameters,items,quest,
                materialCount: 0,definitions: slotQuests,active: [],useCycles: true,configure: candidate =>
                {
                    var learning = SkillPointLedger.Synchronize(candidate.Job,candidate.GrowType,level,
                        candidate.InitialLearning with { Experience = growth.GetLevelStartExperience(level) },skills);
                    return candidate with { Level = level, CompletedQuests = quest.Prerequisites.ToArray(),
                        EquipmentSlotFlags = 0x20, InitialLearning = learning,
                        Skills = learning.Trees[0].Skills.Select(skill => new CharacterSkill(skill.Id,skill.Level)).OrderBy(skill => skill.Id).ToArray() };
                });
            var accepted = f.Change((ushort)quest.Id,QuestLifecycleAction.Accept);
            That(accepted?.Commit is { } accept && accept.Character.ActiveMainQuests.Single().AcceptanceId != Guid.Empty &&
                accept.Character.ActiveMainQuests.Single().ConditionState == 1,"真实寻物解锁从普通接取事务生成独立身份 " + quest.Id);
            var materials = Enumerable.Range(0,quest.IntData.Count/2).Select(at => new InventoryReward(quest.IntData[at*2],quest.IntData[at*2+1])).ToArray();
            That(f.Session.MutateInventory(character => InventoryRewardGrantService.TryGrantBatch(character,materials,
                items.Items,items.GoldLimits,items.EpicPieces)).Accepted && f.Read().ActiveMainQuests.Single().ConditionState == 0,
                "真实金币、晶体钱包与实体材料通过库存事务重算完成状态 " + quest.Id);
            var before = f.Read();
            var finished = f.Finish(ushort.MaxValue,(ushort)quest.Id);
            That(finished.Receipt?.Commit is { } commit && commit.Character.EquipmentSlotFlags == QuestCharacterReward.EquipmentSlots(before,quest) &&
                commit.Character.CompletedQuests.Contains(quest.Id) && commit.Character.ActiveMainQuests.Length == 0 &&
                commit.Consumed.Sum(row => (long)row.Count) == materials.Sum(row => (long)row.Count) &&
                materials.All(row => CurrencyWallet.RewardKind(row.ItemId) is { } kind ?
                    CurrencyWallet.Get(commit.Character.Inventory,kind) == (kind == CurrencyKind.Gold ? commit.GoldAmount : 0) :
                    commit.Character.Inventory.Items.All(item => item.Core.ItemId != row.ItemId)) && f.Receipts == 1,
                "真实三类解锁一次提交完成位、物品消费与槽flag，沿通用奖励经验逻辑 " + quest.Id);
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(f.Read().EquipmentSlotFlags == QuestCharacterReward.EquipmentSlots(before,quest) &&
                f.Finish(ushort.MaxValue,(ushort)quest.Id).Outcome == QuestFinishOutcome.AlreadyCompleted && f.Receipts == 1,
                "真实解锁重登恢复，重复提交不再消费或发奖 " + quest.Id);
        }
        foreach (var reward in new[] { (Index: 0,Bit: 1), (Index: 1,Bit: 2), (Index: 2,Bit: 16) })
        {
            var slot = empty with { Condition = "[seeking]", IntData = [0,100], RewardType = "[slot expansion]", Rewards = [new(0,reward.Index,null)] };
            using var f = Make(slot,configure: value => value with { EquipmentSlotFlags = 0x20,
                Inventory = value.Inventory with { Gold = 1000 } });
            var before = f.Read();var plan = f.Prepare(ushort.MaxValue).Plan!;
            That(QuestLifecyclePlan.HasMaterials(before,slot,DateTimeOffset.UtcNow.ToUnixTimeSeconds()),"金币编号0按真实余额参与寻物目标");
            f.Sql("CREATE TRIGGER slot_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'slot rollback'); END");
            Reject<Microsoft.Data.Sqlite.SqliteException>(() => f.Repository.CommitQuestCompletion(f.Owner,plan),"装备槽奖励末段失败");
            That(QuestCompletionRepositoryTests.Same(before,f.Read()),"槽解锁、扣金币、清槽、完成位与回执全部回滚");
            f.Sql("DROP TRIGGER slot_abort");
            var commit = f.Finish(ushort.MaxValue).Receipt!.Commit;
            That(commit.Character.EquipmentSlotFlags == (0x20 | reward.Bit) && commit.Character.Inventory.Gold == 900 &&
                commit.Consumed is [{ Uid: 0, ItemId: 0, Count: 100 }] && commit.ChangedSlots.Contains(new(InventoryListType.Main,0)),
                "按本版位值解锁，保留其它标志并原子消费Gold虚拟槽");
            int itemId = reward.Index switch { 0 => 100341597, 1 => 2747519, _ => 100390252 };
            var template = items.Items.Get(itemId);
            var core = InventoryRewardGrantService.Create(template);
            var worn = new InventorySlot(InventoryListType.Equipment,template.EquipmentSlot);
            for (byte job = 0; job < 16; job++)
            {
                var character = commit.Character with { Job = job, Level = 95 };
                That(!InventoryMoveService.Allows(character with { EquipmentSlotFlags = 0 },worn,core,items.Items) &&
                    InventoryMoveService.Allows(character,worn,core,items.Items) == ((template.Jobs & (1 << job)) != 0) &&
                    !InventoryMoveService.Allows(character with { Level = 1 },worn,core,items.Items),
                    "解锁穿戴面向16职业，仍执行真实物品职业和等级限制");
            }
            var ack = QuestPackets.Finish(100,0,0,[new(0,100)],0,[]);
            That(ack.Length == 18 && ack[9] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(ack.AsSpan(10)) == 0 &&
                BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(12)) == 100 && ack[16] == 0 && ack[17] == 0,
                "槽奖励沿普通空物品ACK，Gold材料编码本次扣除量");
            var player = TownPackets.Player(commit.Character,new CharacterStatComputer(characters).Compute(commit.Character));
            That(player[125] == (0x20 | reward.Bit),"Player属性块后发送实际持久装备槽标志");
            f.Session.ReturnToRoster();f.Session.Select(0);f.Session.MarkReady();
            That(f.Read().EquipmentSlotFlags == commit.Character.EquipmentSlotFlags && f.Receipts == 1 &&
                f.Finish(ushort.MaxValue).Outcome == QuestFinishOutcome.AlreadyCompleted,"重登恢复解锁且不能再次扣费发奖");
        }
        Console.WriteLine($"PASS {Count-checks} 角色任务奖励、金币消费、回滚和重登断言");

        QuestOrdinaryCompletionTests.Fixture Make(StoryQuest quest,Func<NewCharacter,NewCharacter>? configure = null) =>
            new(towns,characters,skills,growth,parameters,items,quest,materialCount: 0,active: [new(0,100,0)],configure: configure);
    }
}
