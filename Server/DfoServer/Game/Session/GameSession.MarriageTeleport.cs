using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Channels;
using System.Collections.Immutable;

namespace DfoServer.Game.Session;

internal sealed record MarriageTeleportQuote(Guid Relation, long Revision, MarriageCapture Actor,
    GameSession Partner, MarriageCapture Peer, ChannelIdentity Channel, uint Slot, long ExpiresAt,
    TeleportPlan Plan);

internal sealed partial class GameSession
{
    MarriageTeleportQuote? marriageTeleport;

    public bool QuoteMarriageTeleport(MarriageTeleportQuote? quote) => owner.Execute(() =>
    {
        marriageTeleport = null;
        if (quote is null || !CanTeleport() || !MatchesMarriage(quote.Actor, true) || ChannelEntry?.Identity != quote.Channel) return false;
        marriageTeleport = quote;
        return true;
    });

    public MarriageTeleportQuote? TakeMarriageTeleport(MarriageCapture capture, long now) => owner.Execute(() =>
    {
        var quote = marriageTeleport;
        marriageTeleport = null;
        return quote is not null && quote.ExpiresAt >= now && CanTeleport() && MatchesMarriage(capture, true) &&
            ReferenceEquals(quote.Actor.Character, capture.Character) && quote.Actor.Generation == capture.Generation &&
            quote.Actor.Epoch == capture.Epoch ? quote : null;
    });

    public TeleportResult CommitMarriageTeleport(MarriageTeleportQuote quote, Func<bool> relationCurrent, TimeProvider clock) =>
        AccountOwners.Lease.ExecuteTogether(owner, quote.Partner.MarriageOwner, () =>
        {
            if (!CanTeleport() || !quote.Partner.CanTeleport() || !MatchesMarriage(quote.Actor, true) ||
                !quote.Partner.MatchesMarriage(quote.Peer, true) || ChannelEntry?.Identity != quote.Channel ||
                quote.Partner.ChannelEntry?.Identity != quote.Channel || !relationCurrent())
                return new TeleportResult("partner-context-changed");
            var plan = quote.Plan;
            long elapsed = Math.Max(0, clock.GetUtcNow().ToUnixTimeMilliseconds() - plan.Now);
            // 报价确认等待不占用成功后的冷却；日限额和道具到期仍由原提交前置条件复核。
            if (elapsed != 0)
            {
                var states = plan.Inventory.After.ConsumableStates.Select(row =>
                    row.Kind == ConsumableStateKind.Cooldown && !plan.Inventory.Before.ConsumableStates.Contains(row)
                        ? row with { UntilMilliseconds = checked(row.UntilMilliseconds + elapsed) } : row).ToImmutableArray();
                plan = plan with { Inventory = plan.Inventory.WithAfter(plan.Inventory.After with { ConsumableStates = states }) };
            }
            Selected = repository.CommitTeleport(owner, plan);
            InvalidateImageCommunication();
            InvalidateExpertJobStore();
            var result = new InventoryResult(true, 0, plan.Inventory)
            {
                Receipt = new(Selected, owner.Generation, SelectionEpoch)
                {
                    PreviousQuests = quote.Actor.Character.ActiveMainQuests,
                    PreviousSkillSwitchInventory = quote.Actor.Character.SkillSwitchInventory
                }
            };
            return new TeleportResult("committed", new(new(Selected, owner.Generation, SelectionEpoch), result));
        });
}
