using System.Collections.Immutable;

namespace DfoServer.GameWorld.Quests;

internal readonly record struct QuestFixedItemReward(int ItemId, int Count);

// 只解释已核对的固定物品奖励；资格、选择奖励、经验和金币由各自业务合同处理。
internal sealed class QuestFixedRewardDefinition
{
    readonly ImmutableArray<RewardRow> rows;
    public int QuestId { get; }
    public string Path { get; }
    public ImmutableArray<QuestRewardToken> Tokens { get; }

    QuestFixedRewardDefinition(StoryQuest quest, ImmutableArray<QuestRewardToken> tokens, ImmutableArray<RewardRow> rows)
        => (QuestId, Path, Tokens, this.rows) = (quest.Id, quest.Path, tokens, rows);

    public static QuestFixedRewardDefinition Parse(StoryQuest quest)
    {
        ArgumentNullException.ThrowIfNull(quest);
        if (quest.Id <= 0 || string.IsNullOrWhiteSpace(quest.Path) || quest.Rewards is null)
            throw new InvalidDataException("固定任务奖励缺少资源身份或token");

        // 完整验证并冻结定义后才能按职业筛选；不跳过未选职业的未知或畸形行。
        var tokens = quest.Rewards.ToImmutableArray();
        var rows = ImmutableArray.CreateBuilder<RewardRow>();
        for (int at = 0; at < tokens.Length;)
        {
            int itemId = Integer(at++);
            if (itemId <= 0)
                throw Invalid("物品编号必须为正整数");

            byte? job = null;
            if (at < tokens.Length && tokens[at].Type == 6)
            {
                if (tokens[at].Text != "[job]")
                    throw Invalid("未支持的固定奖励条件");
                at++;
                int resourceJob = Integer(at++);
                if (resourceJob is < 0 or > 15 || Integer(at++) != -1)
                    throw Invalid("职业条件必须为job0..15及结束标记-1");
                job = (byte)resourceJob;
            }

            int count = Integer(at++);
            if (count <= 0)
                throw Invalid("固定奖励数量必须为正整数");
            rows.Add(new(new(itemId, count), job));
        }
        return new(quest, tokens, rows.ToImmutable());

        int Integer(int at)
        {
            if (at >= tokens.Length || tokens[at].Type != 0 || tokens[at].Text is not null)
                throw Invalid("固定奖励需要完整整数行");
            return tokens[at].Value;
        }
        InvalidDataException Invalid(string reason) => new($"{reason}: quest={quest.Id}, path={quest.Path}");
    }

    public ImmutableArray<QuestFixedItemReward> ForJob(byte job)
    {
        if (job > 15)
            throw new InvalidDataException("固定任务奖励的职业超出job0..15");
        var rewards = ImmutableArray.CreateBuilder<QuestFixedItemReward>();
        foreach (var row in rows)
            if (row.Job is null || row.Job == job)
                rewards.Add(row.Reward);
        // 保留匹配行的资源顺序、整数数量和重复行；没有匹配行时不补默认奖励。
        return rewards.ToImmutable();
    }

    readonly record struct RewardRow(QuestFixedItemReward Reward, byte? Job);
}
