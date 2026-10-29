using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed record GuildNpcBenefitUseResult(GuildNpcBenefitCommit Commit, InventoryResult? Inventory,
    Func<bool>? IsCurrent);

internal sealed partial class GameSession
{
    public GuildNpcBenefitUseResult BuyGuildNpcBenefit(GuildNpcBenefitService service, uint npcId,
        uint benefitId, string requestId, uint action = 3) => owner.Execute(() =>
    {
        var guard = UseGuard();
        bool Current() => CanPublishConsumable(guard);
        if (!Ready || Phase != SessionPhase.Town || pendingConsumableUse is not null ||
            Selected is not { } expected || CaptureGuildManagement() is not { } identity)
            return new GuildNpcBenefitUseResult(new(14), null, Current);
        var commit = action == 3 ? service.Buy(identity, expected, npcId, benefitId, requestId) :
            npcId == 0 ? service.Craft(identity, expected, benefitId, action, requestId) : new(14);
        if (!commit.Success || commit.Replayed || commit.Character is null || commit.Inventory is null)
            return new(commit, null, Current);
        if (!ReferenceEquals(Selected, expected) || !identity.IsCurrent())
            throw new InvalidOperationException("公会福利提交后的角色身份发生变化");
        Selected = commit.Character;
        var published = UseGuard();
        var inventory = new InventoryResult(true, 0, commit.Inventory)
        {
            Receipt = new(commit.Character, owner.Generation, SelectionEpoch)
            { Phase = Phase, PreviousQuests = expected.ActiveMainQuests, PreviousSkillSwitchInventory = expected.SkillSwitchInventory }
        };
        return new(commit, inventory, () => CanPublishConsumable(published));
    });
}
