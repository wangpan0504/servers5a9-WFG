using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Trade;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public (Character Character, TradeParticipant Participant)? CapturePlayerTrade(bool requireEligible = true)
    {
        try
        {
            return owner.Execute(() => Ready && Phase == SessionPhase.Town && (!requireEligible || CanUseExpertJob()) && Selected is { } character
                ? (character, new TradeParticipant(character.Id, owner.Generation, SelectionEpoch))
                : ((Character, TradeParticipant)?)null);
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return null; }
    }

    public Func<bool> GuardPlayerTrade(TradeParticipant participant) => () => owner.IsCurrent && Ready &&
        Phase == SessionPhase.Town && SelectionEpoch == participant.SelectionEpoch && owner.Generation == participant.Generation &&
        Selected?.Id == participant.CharacterId;

    public Func<bool> GuardPlayerTradeReceipt(InventoryCommitReceipt receipt) => () => owner.IsCurrent && Ready &&
        owner.Generation == receipt.Generation && Phase == receipt.Phase && SelectionEpoch == receipt.SelectionEpoch &&
        ReferenceEquals(Selected, receipt.Character);

    public static (InventoryResult First, InventoryResult Second)? CommitPlayerTrade(GameSession first, GameSession second,
        Character firstExpected, Character secondExpected, TradeTicket ticket, TradeSettlementPlan plan)
    {
        return AccountOwners.Lease.ExecuteTogether<(InventoryResult, InventoryResult)?>(first.ExpertJobOwner, second.ExpertJobOwner, () =>
        {
            if (!ReferenceEquals(first.ExpertJobRepository, second.ExpertJobRepository) || !first.CanUseExpertJob() || !second.CanUseExpertJob() ||
                !ReferenceEquals(first.Selected, firstExpected) || !ReferenceEquals(second.Selected, secondExpected) ||
                ticket.First != new TradeParticipant(firstExpected.Id, first.ExpertJobOwner.Generation, first.SelectionEpoch) ||
                ticket.Second != new TradeParticipant(secondExpected.Id, second.ExpertJobOwner.Generation, second.SelectionEpoch) ||
                !EligibleItems(ticket.FirstOffer) || !EligibleItems(ticket.SecondOffer) ||
                !ticket.TryBeginCommit()) return null;
            var (savedFirst, savedSecond) = first.ExpertJobRepository.CommitPlayerTrade(first.ExpertJobOwner, second.ExpertJobOwner,
                firstExpected, secondExpected, ticket, plan);
            first.Selected = savedFirst;
            second.Selected = savedSecond;
            return (Result(first, savedFirst, firstExpected, plan.First), Result(second, savedSecond, secondExpected, plan.Second));
        });
        static InventoryResult Result(GameSession session, Character saved, Character before, InventoryPlan inventory) =>
            new(true, 0, inventory)
            {
                Receipt = new(saved, session.ExpertJobOwner.Generation, session.SelectionEpoch)
                { Phase = session.Phase, PreviousQuests = before.ActiveMainQuests, PreviousSkillSwitchInventory = before.SkillSwitchInventory }
            };
        static bool EligibleItems(TradeOffer offer)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return offer.Items.All(line => !line.Item.IsExpired(now) && line.Item.ExpireTime == 0 &&
                !line.Item.Core.IsRental && line.Item.Core.TradeRestriction == 0 &&
                line.Item.Core.SortLockFlag == 0 && line.Item.Core.EquipmentLockId == 0);
        }
    }
}
