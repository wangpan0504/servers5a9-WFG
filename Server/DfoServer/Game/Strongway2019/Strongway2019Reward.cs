using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Strongway2019;

internal enum Strongway2019RewardBucket { Challenge, Matching }
internal readonly record struct Strongway2019RewardPeriod(int Day, int Week)
{
    public static Strongway2019RewardPeriod At(DateTimeOffset now)
    {
        var day = now.ToOffset(TimeSpan.FromHours(8)).AddHours(-6).Date;
        var week = day.AddDays(-((int)day.DayOfWeek - (int)DayOfWeek.Wednesday + 7) % 7);
        return new(day.Year * 10000 + day.Month * 100 + day.Day,
            week.Year * 10000 + week.Month * 100 + week.Day);
    }
}

// 匹配资格只接受原竞赛保存的Terminal引用和固定胜队成员，不能用客户端或调用方bool声明胜利。
internal sealed record Strongway2019RewardPlan(Character Expected, SinglePlayerDungeonRun Run,
    DungeonClearFact? Fact, InventoryItem Item, long Created)
{
    // 无个人清图的退出胜利也冻结原终局时的epoch，不能从随后返城的可变Run重算回执。
    public DungeonRunIdentity Identity { get; init; } = Fact?.Identity ?? Run.Scene.Identity;
    public Strongway2019Competition? Competition { get; init; }
    public Strongway2019Terminal? Terminal { get; init; }
}
internal sealed record Strongway2019RewardState(int DailyUsed, int WeeklyUsed);
internal sealed record Strongway2019RewardCommit(uint MailId, bool Duplicate, bool Granted);

internal interface IStrongway2019RewardStore
{
    Strongway2019RewardState ReadStrongway2019RewardState(AccountOwners.Lease owner,
        Strongway2019RewardPeriod period, Strongway2019RewardBucket bucket);
    Strongway2019RewardCommit CommitStrongway2019Reward(AccountOwners.Lease owner, Strongway2019RewardPlan plan);
}
