using static DfoServer.Testing.TestCheck;
using DfoServer.GameWorld.Quests;
using PvfLib;

namespace DfoServer;

internal static class QuestFixedRewardTests
{
    const string FirstQuestPath = "n_quest/quest_renewal/epic_quest/elvengard/elvengard_epic_01.qst";

    public static void Run()
    {
        int checks = Count;
        QuestRewardToken Int(int value) => new(0, value, null);
        QuestRewardToken Job() => new(6, 1, "[job]");
        QuestFixedRewardDefinition Parse(params QuestRewardToken[] tokens) => QuestFixedRewardDefinition.Parse(Fixture(tokens));

        // 形状取自3145 reward int data；编号/数量为明确合成输入，检验交错、重复及大整数数量。
        var definition = Parse(Int(1000), Int(2), Int(101010438), Job(), Int(0), Int(-1), Int(1),
            Int(1004), Int(70_000), Int(102040220), Job(), Int(1), Int(-1), Int(3), Int(1000), Int(4));
        That(definition.QuestId == 3145 && definition.Path == FirstQuestPath, "定义保留原任务与路径身份");
        That(definition.ForJob(0).SequenceEqual(new QuestFixedItemReward[]
            { new(1000, 2), new(101010438, 1), new(1004, 70_000), new(1000, 4) }),
            "job0固定条件筛选保留交错顺序、重复行与完整整数数量");
        That(definition.ForJob(1).SequenceEqual(new QuestFixedItemReward[]
            { new(1000, 2), new(1004, 70_000), new(102040220, 3), new(1000, 4) }),
            "另一职业仅保留公共及自己的固定奖励");
        That(definition.ForJob(15).SequenceEqual(new QuestFixedItemReward[]
            { new(1000, 2), new(1004, 70_000), new(1000, 4) }), "未列职业仍保留全部公共行");
        That(Parse(Int(1000), Job(), Int(0), Int(-1), Int(1)).ForJob(1).IsEmpty,
            "资源没有该职业匹配行时合法空结果，不补默认物品");
        That(Parse().ForJob(0).IsEmpty, "无固定物品token不伪造奖励");
        var branches = Parse(Int(1000),Job(),Int(0),Int(0),Int(1),Int(1004),Job(),Int(0),Int(1),Int(2),Int(1047),Job(),Int(0),Int(-1),Int(3));
        That(branches.ForJob(0,0).Select(row => row.ItemId).SequenceEqual(new[]{1000,1047}) &&
            branches.ForJob(0,0x21).Select(row => row.ItemId).SequenceEqual(new[]{1004,1047}),
            "奖励按基础转职分支筛选，觉醒位不改变分支，-1保留全部分支");

        QuestRewardToken[] source = [Int(1000), Int(2), Int(1004), Job(), Int(0), Int(-1), Int(3)];
        var frozen = Parse(source);
        var original = source.ToArray();
        source[0] = Int(1047);
        source[6] = Int(99);
        That(frozen.Tokens.SequenceEqual(original) && frozen.ForJob(0).SequenceEqual(new QuestFixedItemReward[]
            { new(1000, 2), new(1004, 3) }), "源token变化不污染完整冻结定义及奖励");
        That(frozen.Tokens.Length == 7 && frozen.Tokens[3] == Job(), "职业条件token未被过滤或归一化");

        Reject(() => definition.ForJob(16), "无效角色职业拒绝");
        Reject(() => Parse(Int(0), Int(1)), "物品零编号拒绝");
        Reject(() => Parse(Int(-1), Int(1)), "负物品编号拒绝");
        Reject(() => Parse(Int(1000), Int(0)), "零数量不当默认奖励");
        Reject(() => Parse(Int(1000), Int(-2)), "负数量拒绝");
        Reject(() => Parse(Int(1000)), "缺少数量拒绝");
        Reject(() => Parse(Job(), Int(0), Int(-1), Int(1)), "缺少物品编号拒绝");
        Reject(() => Parse(Int(1000), new(2, 1, null)), "非整数数量拒绝");
        Reject(() => Parse(new(0, 1000, "伪整数"), Int(1)), "整数token不能夹带字符串语义");
        Reject(() => Parse(Int(1000), new(6, 2, "[grow type]"), Int(0), Int(-1), Int(1)), "未知条件拒绝");
        Reject(() => Parse(Int(1000), Job()), "残缺职业条件拒绝");
        Reject(() => Parse(Int(1000), Job(), Int(-1), Int(-1), Int(1)), "负职业拒绝");
        Reject(() => Parse(Int(1000), Job(), Int(16), Int(-1), Int(1)), "未支持职业拒绝");
        Reject(() => Parse(Int(1000), Job(), Int(0), Int(9), Int(-1), Int(1)), "未核实的多职业条件形状拒绝");
        Reject(() => Parse(Int(1000), Job(), Int(0), Int(1)), "缺少职业结束标记拒绝");
        Reject(() => Parse(Int(1000), Job(), Int(0), Int(-1)), "条件后缺少数量拒绝");
        Reject(() => Parse(Int(1000), Job(), Int(1), Int(-1), Int(1), Int(1004), new(6, 3, "[choice]"), Int(2)),
            "未选职业的后续未知行也拒绝完整定义，不返回前半奖励");
        Reject(() => Parse(Int(1000), Int(1), Int(1004)), "后续残缺公共行拒绝完整定义");
        Console.WriteLine($"PASS {Count - checks} 固定任务奖励纯规则断言");

        void Reject(Action action, string name)
        {
            try
            {
                action();
            }
            catch (InvalidDataException)
            {
                That(true, name);
                return;
            }
            throw new InvalidOperationException("FAIL 固定任务奖励未拒绝: " + name);
        }
    }

    public static void RunResources(PvfArchive pvf)
    {
        var quest = global::DfoServer.Testing.SelfTestCatalogs.Quests(pvf).Get(3145);
        var definition = QuestFixedRewardDefinition.Parse(quest);
        if (definition.Path != FirstQuestPath || !definition.Tokens.SequenceEqual(quest.Rewards))
            throw new InvalidOperationException("FAIL 3145固定奖励资源身份或原token变化");

        for (byte job = 0; job < 16; job++)
        {
            QuestFixedItemReward[] expected = [new(10165055, 1), new(10005531, 1), new(8050536, 1),
                new(12403, 1), new(14403, 1), new(16403, 1), new(18403, 1)];
            if (!definition.ForJob(job).SequenceEqual(expected))
                throw new InvalidOperationException("FAIL 3145固定奖励原顺序或职业条件: job=" + job);
        }
        Console.WriteLine("PASS 17 3145固定奖励资源断言");
    }

    static StoryQuest Fixture(IReadOnlyList<QuestRewardToken> tokens) => new(3145, FirstQuestPath,
        "[clear map]", -1, [76126], 1, 1247, 1, 99, [], null, 3, -1, tokens, 107000903, null, [1000]);
}
