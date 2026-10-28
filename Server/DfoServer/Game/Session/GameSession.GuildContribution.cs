using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed record GuildContributionUseResult(GuildContributionCommit Commit, InventoryResult? Inventory,
    Func<bool>? IsCurrent);

internal sealed partial class GameSession
{
    public GuildContributionUseResult SettleGuildContribution(GuildContributionService service) => owner.Execute(() =>
    {
        var guard = UseGuard();
        bool Current() => CanPublishConsumable(guard);
        if (!Ready || Phase != SessionPhase.Town || pendingConsumableUse is not null ||
            Selected is not { } expected || CaptureGuildManagement() is not { } identity)
            return new GuildContributionUseResult(new(3), null, Current);
        // 完整持有账号锁直到落库并安装权威快照，不能把旧选择角色的到账结果装到新角色。
        var commit = service.Settle(identity, expected);
        if (!commit.Success || commit.Character is null || commit.Inventory is null) return new(commit, null, Current);
        if (!ReferenceEquals(Selected, expected) || !identity.IsCurrent())
            throw new InvalidOperationException("公会周币提交后的角色身份发生变化");
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
