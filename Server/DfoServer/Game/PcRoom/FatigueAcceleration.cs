using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using System.Collections.Immutable;

namespace DfoServer.Game.PcRoom;

internal interface IFatigueAccelerationStore
{
    bool ReadFatigueAcceleration(AccountOwners.Lease owner, ushort characterId);
    void SaveFatigueAcceleration(AccountOwners.Lease owner, ushort characterId, bool enabled);
}

internal sealed record FatigueAccelerationReceipt(ushort CharacterId, long Generation, long Epoch,
    SessionPhase Phase, bool Selected, bool Active, ushort Error = 0);

internal static class FatigueAccelerationRules
{
    // 倍率绑定本次入场选择，与无限疲劳余额及经验加成比例分开。
    public static ushort Cost(ushort amount, int percent, ushort remaining = ushort.MaxValue) =>
        checked((ushort)Math.Min(remaining, (uint)amount * (percent > 0 ? 2u : 1u)));

    // 正常剧情路线也参与燃烧；它的任务疲劳归属与普通路线不同，不因此丢掉经验资格。
    public static bool Eligible(DungeonEntryPlan entry) => (entry.UsesFatigue || entry.RouteQuestId != 0 &&
        !entry.DisableFatigueConsumption && entry.LostLand is null && !entry.Maze.IsGriefTower && !entry.IsBossRush) && !entry.IsTutorial &&
        !entry.IsPractice && !entry.IsTrainingRoom && !entry.IsClassExperience && !entry.IsTournament && entry.RaidEntry is null;

    public static ExperienceBonuses Apply(ExperienceBonuses reward, uint basis, int percent)
    {
        if (percent < 0) throw new InvalidDataException("疲劳燃烧经验比例不能为负");
        if (percent == 0 || basis == 0) return reward;
        if (reward.Parts.Any(part => part.Source == ExperienceBonusSource.FatigueBurning))
            throw new InvalidDataException("疲劳燃烧经验不能重复提供");
        var extra = ExperienceBonuses.Calculate(0, basis, [new(ExperienceBonusSource.FatigueBurning, percent)]);
        var result = reward with { Parts = reward.Parts.AddRange(extra.Parts).OrderBy(part => part.Source).ToImmutableArray() };
        _ = result.Total;
        return result;
    }
}
