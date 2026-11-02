using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;

namespace DfoServer.Game.BattlePass;

internal sealed record BattlePassRewardCommit(Character Character, BattlePassSeasonState State);

internal interface IBattlePassStore
{
    BattlePassSeasonState ReadBattlePass(AccountOwners.Lease owner, string seasonKey);
    BattlePassRewardCommit CommitBattlePassReward(AccountOwners.Lease owner, Character expected, BattlePassRewardPlan plan);
}

internal sealed class BattlePassRewardChangedException : InvalidOperationException
{
    public BattlePassRewardChangedException() : base("Battle-pass reward state changed.") { }
}
