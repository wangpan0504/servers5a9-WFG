using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using DfoServer.Testing;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class CreatureQuestTests
{
    public static void Run(TcpTestResources resources)
    {
        var quest = QuestEligibilityTests.Quest(100) with { Grade = "[normaly repeat]", Condition = "[seeking]", IntData = [1000, 1],
            RewardType = "[creature evolution]", Rewards = [new(0, 14, null)], Attributes = ["not give exp quest"],
            Definition = new([]) { CreatureKind = 13, CreatureLevel = 35, Job = "[all]", JobChange = 10 } };
        var quests = new QuestCatalog(new() { [100] = quest }).WithCreatures(resources.ItemResources.Creatures);
        string root = Path.Combine(Path.GetTempPath(), "S5A9-pet-quest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var repository = new CharacterRepository(path, quests);
            using var owner = new AccountOwners().Acquire("pet-quest", () => { });
            var candidate = CharacterCreation.Prepare("PetQuest", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get);
            var pet = new InventoryItem(0, CreatureRules.EquippedSlot, new(63033, ItemKind.Creature, 0) { Attr = 7, Extension27 = 99 })
                { Creature = new() { Name = "陪伴", Level = 35, Experience = 1300, ExpireTime = int.MaxValue } };
            var character = repository.Create(owner, candidate with { Inventory = candidate.Inventory with { Items = candidate.Inventory.Items
                .Add(pet).Add(new(0, new(InventoryListType.Main, 65), new(1000, ItemKind.Consumable, 1))) } });
            VerifyNativeEvolution(character, resources);
            That(QuestEligibility.Evaluate(character, quest, quests) is { Eligible: true, Visible: true } &&
                !QuestEligibility.Evaluate(character with { Inventory = candidate.Inventory }, quest, quests).Eligible,
                "进化任务只向装备对应种类且达到宠物等级的角色开放");
            That(QuestEligibility.CreatureCandidatesChanged(character with { Inventory = candidate.Inventory }, character, quests) &&
                !QuestEligibility.CreatureCandidatesChanged(character, character, quests),
                "穿戴宠物仅在实际任务资格改变时刷新候选，普通穿脱不额外刷完整任务界面");
            That(!QuestEligibility.Evaluate(character, quest with { Definition = quest.Definition! with { ExposedByNpc = false } }, quests).Visible &&
                !QuestEligibility.Evaluate(character, quest with { Grade = "[unverified]" }, quests).Visible,
                "宠物资格不能绕过显式NPC隐藏或未支持的任务分类");
            var now = DateTimeOffset.UtcNow;
            var items = resources.ItemResources;
            var accepted = QuestLifecyclePlan.Accept(character, quest, quests, items.Items, items.GoldLimits, items.EpicPieces, now);
            character = repository.CommitQuestLifecycle(owner, accepted).Character;
            var prepared = QuestCompletionPlan.Prepare(character, quest, ushort.MaxValue, quests,
                SelfTestCatalogs.QuestRewardParameters(resources.Archive), items.Items, items.GoldLimits, items.EpicPieces,
                SelfTestCatalogs.Growth(resources.Archive, resources.Learning), now);
            That(prepared is { Outcome: QuestCompletionOutcome.Accepted, Plan: not null } && prepared.Plan.Rewards.IsEmpty &&
                prepared.Plan.Inventory.After.Find(CreatureRules.EquippedSlot)!.Core.ItemId == 63034,
                "任务species14不是物品14；材料、完成位与宠物转换冻结在同一领奖计划：" + prepared.Reason);
            var plan = prepared.Plan!;
            Sql("CREATE TRIGGER pet_quest_abort AFTER INSERT ON character_quest_completion_receipts BEGIN SELECT RAISE(ABORT,'pet quest rollback'); END");
            Reject<Microsoft.Data.Sqlite.SqliteException>(() => repository.CommitQuestCompletion(owner, plan), "进化任务领奖末段SQL失败");
            That(QuestCompletionRepositoryTests.Same(character, repository.LoadCharacter(owner, 0)),
                "材料、进化模板、完整详情、任务接取和完成位一起回滚");
            Sql("DROP TRIGGER pet_quest_abort");
            var commit = repository.CommitQuestCompletion(owner, plan);
            var current = commit.Character.Inventory.Find(CreatureRules.EquippedSlot)!;
            That(current.Uid == character.Inventory.Find(CreatureRules.EquippedSlot)!.Uid && current.Core.ItemId == 63034 &&
                current.Core.Extension27 == 99 && current.Creature == pet.Creature && commit.Character.Inventory.Find(new(InventoryListType.Main, 65)) is null &&
                commit.Character.CompletedQuests.Contains(100) && commit.Character.Revision == character.Revision + 1,
                "任务原UID进化保留所有详情，消费材料与领奖只推进一个版本");
            That(repository.CommitQuestCompletion(owner, plan) is { Duplicate: true, ProjectionCurrent: true }, "精确重放进化领奖只读原回执");
            Bytes(QuestPackets.Finish(100, 0, 0, [], 0, [], creatureEvolution: true),
                [1, 100, 0, 0, 0, 0, 0, 0, 0, 10], "进化ACK34以mode10收尾，不追加物品计数或旧版种类字段");

            void Sql(string sql)
            {
                using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
            }
        }
        finally { Directory.Delete(root, true); }
    }

    static void VerifyNativeEvolution(Character character, TcpTestResources resources)
    {
        var quests = resources.Quests.WithCreatures(resources.ItemResources.Creatures);
        var quest = quests.Get(2102);
        That(quest.Difficulty is null && quest.Definition is { CreatureKind: 2, CreatureLevel: 32 } &&
            quest.Rewards is [{ Value: 3 }], "真实2102缺省difficulty，要求种类2的32级宠物并进化为种类3");
        var original = character.Inventory.Find(CreatureRules.EquippedSlot)!;
        var pet = original with { Core = original.Core with { ItemId = 63001 }, Creature = new() { Level = 32, Experience = 918 } };
        character = character with { Inventory = character.Inventory with { Items = character.Inventory.Items.Replace(original, pet) } };
        var items = resources.ItemResources;
        var now = DateTimeOffset.UtcNow;
        var accepted = QuestLifecyclePlan.Accept(character, quest, quests, items.Items, items.GoldLimits, items.EpicPieces, now);
        character = character with { Revision = character.Revision + 1,
            ActiveMainQuests = accepted.ActiveAfter.Select(row => row with { ConditionState = 0 }).ToArray() };
        var active = character.ActiveMainQuests.Single(row => row.QuestId == 2102);
        var clear = new QuestClearReceipt(active.AcceptanceId, 2102, active.Slot, Guid.NewGuid(), Guid.NewGuid(), 1, 2, character.Revision)
            { DungeonId = 153, MapId = 58863 };
        var parameters = SelfTestCatalogs.QuestRewardParameters(resources.Archive);
        var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
        QuestCompletionPreparation Prepare(Character source, StoryQuest definition) => QuestCompletionPlan.Prepare(source,
            definition, ushort.MaxValue, quests, parameters, items.Items, items.GoldLimits, items.EpicPieces, growth, now, clear);
        var prepared = Prepare(character, quest);
        That(prepared is { Outcome: QuestCompletionOutcome.Accepted, Plan: not null }, "真实2102应可交付：" + prepared.Reason);
        var evolved = prepared.Plan!.Inventory.After.Find(CreatureRules.EquippedSlot)!;
        That(evolved.Uid == pet.Uid && evolved.Core.ItemId == 63002 && evolved.Creature == pet.Creature &&
            prepared.Plan.ExperienceReward is { DifficultyKey: "", DifficultyWeight: 100, Amount: > 0 },
            "缺省difficulty使用原生100%权重，真实佛尔卡斯原UID与详情进化为菲尼克斯");
        That(Prepare(character, quest with { Difficulty = "unknown" }).Outcome == QuestCompletionOutcome.Rejected,
            "显式未知difficulty不因缺省支持而放行");
        var younger = pet with { Creature = pet.Creature! with { Level = 31 } };
        That(Prepare(character with { Inventory = character.Inventory with { Items = character.Inventory.Items.Replace(pet, younger) } }, quest)
            .Outcome == QuestCompletionOutcome.Rejected, "缺省difficulty不绕过宠物最低等级");
        Reject<NotSupportedException>(() => QuestExperiencePolicy.Prepare(character,
            quest with { RewardType = "[item]", Rewards = [] }, parameters), "不扩大普通任务的缺省difficulty范围");
    }
}
