using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Dungeons;

internal sealed record DungeonTournamentReward(ImmutableArray<DungeonCardReward> Normal,
    ImmutableArray<DungeonCardReward> Champion)
{
    public ImmutableArray<DungeonCardReward> Row(int side) => side == 0 ? Normal : Champion;
    public ImmutableArray<DungeonCardReward> Selected(ImmutableArray<byte> choices) =>
        choices.Length == 2 && choices[0] < Normal.Length && choices[1] < Champion.Length
            ? [Normal[choices[0]], Champion[choices[1]]]
            : throw new InvalidDataException("大会领奖必须选择两组冻结牌面");
}

internal sealed record TournamentCardClaim(Character Expected, DungeonTournamentReward Reward,
    ImmutableArray<byte> Choices, DungeonCardDelivery Delivery);

internal sealed record TournamentCardCommit(Character Character, InventoryPlan Inventory, uint? MailId, bool Duplicate);

internal sealed record FormalTournamentCardResult(FormalDungeonReceipt Receipt, DungeonTournamentReward Reward,
    ImmutableArray<byte> Choices, bool Complete, InventoryResult? Inventory = null, uint? MailId = null, bool Duplicate = false);
