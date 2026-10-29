using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using PvfLib;

namespace DfoServer;

internal static class QuestCompletionPlanTests
{
    public static void Run()
    {
        int checks = 0;
        var character = CharacterFixture();
        var quest = QuestFixture();
        var parameters = Parameters();
        var reward = QuestExperiencePolicy.Prepare(character, quest, parameters);
        Check(reward.NativeIndex == 1 && reward.BaseExperience == 100 &&
            reward.DifficultyKey == "1" && reward.DifficultyWeight == 1780 && reward.Amount == 1780 && reward.RawAdditionalPercent == -1 &&
            reward.ResourcePath == QuestRewardParameterCatalog.ResourcePath && reward.QuestPath == "n_quest/quest_renewal/epic_quest/elvengard/elvengard_epic_01.qst",
            "明确本服规则保存完整来源与原生index1，值1780由资源参数计算");
        Check(QuestExperiencePolicy.Prepare(character, quest, Parameters(101, 1781)).Amount == 1798,
            "独立合成参数检验整数乘积向下截断，不写死1780");
        Check(QuestExperiencePolicy.Prepare(character with { Job = 15 }, QuestFixture(116000005), parameters).Amount == 1780,
            "全职业任务按枪剑士资源奖励校验经验，不依赖job0");
        Check(QuestExperiencePolicy.Prepare(character with { GrowType = 0x21 }, quest, parameters).Amount == 1780,
            "3145声明grow type=-1，不按转职或觉醒拒绝任务经验");
        Reject(() => QuestExperiencePolicy.Prepare(character with { Job = 16 }, quest, parameters), "无效职业仍拒绝");
        Reject(() => QuestExperiencePolicy.Prepare(character, quest with { Difficulty = "G" }, parameters), "其他difficulty不沿用规则");
        Check(QuestExperiencePolicy.Prepare(character, quest with { Id = 100, Path = "n_quest/fixture.qst", CompleteNpc = 1 }, parameters).Amount == reward.Amount,
            "任务编号、脚本路径、NPC和奖励项数不改变同一经验计算");
        Check(QuestExperiencePolicy.Prepare(character, quest with { Attributes = ["not give exp quest"] }, parameters).Amount == 0,
            "资源禁经验属性返回零，不拒绝任务交付");
        Check(QuestExperiencePolicy.Prepare(character, quest, Parameters(additional: 1)).Amount == 1797,
            "正追加百分比按相同取整顺序计算");
        Check(QuestExperiencePolicy.Prepare(character, quest, Parameters(0,1780)).Amount == 0 &&
            QuestExperiencePolicy.Prepare(character, quest, Parameters(100,0)).Amount == 0,"零基础与零倍率使用同一计算");
        var preparation = quest with { Id = 20737, Grade = "[wedding]", Condition = "[condition under clear]",
            SubType = 22, IntData = [-1, -1, 5], Difficulty = null, Definition = new([]) { Job = "[all]" } };
        var preparationReward = QuestExperiencePolicy.Prepare(character, preparation, parameters);
        Check(preparationReward.DifficultyWeight == 100 && preparationReward.Amount == 100,
            "结婚准备第一步缺省难度沿用参数查询默认百分比，不阻断已达成任务交付");
        foreach (var unsupported in new[] { preparation with { Id = 20738 }, preparation with { Grade = "[side]" },
            preparation with { SubType = 23 }, preparation with { IntData = [-1, 0, 5] } })
            Reject(() => QuestExperiencePolicy.Prepare(character, unsupported, parameters), "缺省难度不扩大至第二步或未知任务形状");
        Reject(() => QuestExperiencePolicy.Prepare(character, preparation with { Difficulty = "unknown" }, parameters),
            "结婚准备显式未知难度仍拒绝");
        var clear = ClearFixture(character);
        Check(QuestCompletionPlan.MatchesClearedActiveQuest(character, clear), "活动槽已达成且稳定接取/clear匹配");
        Check(!QuestCompletionPlan.MatchesClearedActiveQuest(character with { CompletedQuests = [3145] }, clear), "重复已领奖拒绝");
        Check(!QuestCompletionPlan.MatchesClearedActiveQuest(character, clear with { AcceptanceId = Guid.NewGuid() }), "旧接取clear拒绝");
        Check(!QuestCompletionPlan.MatchesClearedActiveQuest(character, clear with { Revision = character.Revision + 1 }), "未来clear revision拒绝");
        Check(!QuestCompletionPlan.MatchesClearedActiveQuest(character, clear with { EventId = Guid.Empty }), "空死亡事件身份拒绝");
        Check(!QuestCompletionPlan.MatchesClearedActiveQuest(character with
            { ActiveMainQuests = [character.ActiveMainQuests[0] with { ConditionState = 1 }] }, clear), "客户端请求不代替可信达成条件");
        Console.WriteLine($"PASS {checks} 3145本服领奖经验与稳定资格断言");

        void Check(bool value, string name)
        {
            checks++;
            if (!value)
                throw new InvalidOperationException("FAIL 3145领奖规则: " + name);
        }
        void Reject(Action action, string name)
        {
            try
            {
                action();
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException)
            {
                Check(true, name);
                return;
            }
            throw new InvalidOperationException("FAIL 3145领奖规则未拒绝: " + name);
        }
    }

    public static void RunResources(PvfArchive archive, ItemCatalog items, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces)
    {
        var character = CharacterFixture();
        var quests = global::DfoServer.Testing.SelfTestCatalogs.Quests(archive);
        var quest = quests.Get(3145);
        var parameters = global::DfoServer.Testing.SelfTestCatalogs.QuestRewardParameters(archive);
        var growth = global::DfoServer.Testing.SelfTestCatalogs.Growth(archive);
        var clear = ClearFixture(character);
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        QuestCompletionPreparation Prepare(Character expected) => QuestCompletionPlan.Prepare(expected, quest, ushort.MaxValue, quests, parameters,
            items, goldLimits, pieces, growth, now, clear);
        var result = Prepare(character);
        if (result.Outcome != QuestCompletionOutcome.Accepted || result.Plan is not { } plan ||
            !ReferenceEquals(plan.Expected, character) || !ReferenceEquals(plan.Inventory.Before, character.Inventory) ||
            plan.ExperienceReward.Amount != 1780 || plan.Growth.TargetLevel != 2 || plan.Growth.Learning.Experience != 1780 ||
            plan.Growth.LevelSpDelta != 30 || plan.Growth.Learning.Trees[0].Sp != 37 || plan.Growth.Learning.Trees[1].Sp != 59 ||
            plan.Growth.Learning.Trees[0].Tp != 9 || plan.Growth.Learning.Trees[1].Tp != 11 || plan.Rewards.Length != 7 ||
            plan.ActiveAfter.Length != 0 || !plan.CompletedAfter.SequenceEqual([3000, 3145]) || plan.GoldAmount != 0 ||
            plan.Inventory.After.Gold != character.Inventory.Gold || plan.FrozenAt != now ||
            !plan.Rewards.Select(reward => reward.Core.ItemId).SequenceEqual(new[] { 10165055, 101010438, 10403, 12403, 14403, 16403, 18403 }) ||
            plan.Rewards.Any(reward => reward.ExpectedDestinationUid != 0 || reward.GrantedCount != 1) ||
            plan.Inventory.After.Items.Any(item => item.Uid != 0) || character.ActiveMainQuests.Length != 1 || character.Learning.Experience != 0)
            throw new InvalidOperationException("FAIL 3145真实资源七项资产、成长、任务整批只读规划");
        var equipment = items.Get(101010438);
        var range = ItemSlotBoundService.Range(ItemKind.Equipment, character.Inventory.Expansion);
        var full = ImmutableArray.CreateBuilder<InventoryItem>();
        for (short slot = range.Start; slot <= range.End; slot++)
            full.Add(new InventoryItem(500 + slot, new(InventoryListType.Main, slot), InventoryRewardGrantService.Create(equipment, utcNow: now)));
        var fullCharacter = character with { Inventory = character.Inventory with { Items = full.ToImmutable() } };
        var fullResult = Prepare(fullCharacter);
        if (fullResult.Outcome != QuestCompletionOutcome.InsufficientSpace || fullResult.Plan is not null ||
            fullCharacter.Inventory.Items.Length != full.Count || fullCharacter.Learning.Experience != 0 ||
            fullCharacter.ActiveMainQuests.Length != 1 || fullCharacter.CompletedQuests.Contains(3145))
            throw new InvalidOperationException("FAIL 3145满包不发布前半奖励、经验或完成位");
        var oldTree = character with { Learning = CharacterLearningState.Uninitialized };
        if (Prepare(oldTree).Outcome != QuestCompletionOutcome.Rejected || oldTree.Inventory.Items.Length != 0)
            throw new InvalidOperationException("FAIL 3145成长未知不能先发七项物品");
        Console.WriteLine("PASS 3145真实资源完整领奖计划、满包与未知成长回归");
    }

    static Character CharacterFixture() => new(99, 0, "QuestPlan", 17, new Location(38, 2, 100, 100), null)
    {
        Job = 0,
        DungeonState = CharacterDungeonState.NewOrdinary,
        Level = 1,
        ActiveMainQuests = [new(0, 3145, 0) { AcceptanceId = Guid.NewGuid() }],
        CompletedQuests = [3000],
        Learning = new(0, [new(0, 7, 9, [new(12, 5, 2)]), new(1, 29, 11, [new(7, 8, 3)])]),
        Inventory = new([], Gold: 456)
    };

    static QuestClearReceipt ClearFixture(Character character) => new(character.ActiveMainQuests[0].AcceptanceId, 3145, 0,
        Guid.NewGuid(), Guid.NewGuid(), 1, 3, 16) { DungeonId = 3, MapId = 76126 };

    static StoryQuest QuestFixture(int weapon = 101010438) => new(3145, "n_quest/quest_renewal/epic_quest/elvengard/elvengard_epic_01.qst", "[clear map]", -1, [76126], 1, 1247,
        1, 99, [], null, 3, -1, new[] { 10165055, weapon, 10403, 12403, 14403, 16403, 18403 }
            .SelectMany(id => new[] { new QuestRewardToken(0, id, null), new QuestRewardToken(0, 1, null) }).ToArray(),
        107000903, null, [1000]) { Grade = "[epic]", Difficulty = "1", RewardType = "[item]" };

    static QuestRewardParameterCatalog Parameters(int basis = 100, int difficulty = 1780, int additional = -1)
    {
        var strings = new Dictionary<int, string>();
        var tokens = new List<PvfToken>();
        PvfToken Text(string value, byte type)
        {
            int id = strings.Count;
            strings.Add(id, value);
            return new(type, id);
        }
        void Integers(string tag, params int[] values)
        {
            tokens.Add(Text(tag, 3));
            tokens.AddRange(values.Select(value => new PvfToken(0, value)));
        }
        tokens.Add(Text("[difficulty]", 3));
        tokens.Add(Text("1", 6));
        tokens.Add(new(0, difficulty));
        tokens.Add(Text("[/difficulty]", 3));
        Integers("[exp reward table]", 12345, -1, basis, additional);
        Integers("[basic level penalty]",100);
        foreach(string tag in new[] { "[green level penalty]","[epic green level penalty]" }) Integers(tag,6,10,50);
        foreach(string tag in new[] { "[grey level penalty]","[epic grey level penalty]" }) Integers(tag,11,255,0);
        return QuestRewardParameterCatalog.Parse(tokens, id => strings[id]);
    }
}
