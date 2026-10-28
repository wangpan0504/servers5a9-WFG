using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal enum GuildHongbaoAction { Issue, Take, Refund }
internal sealed record GuildHongbaoUseResult(GuildHongbaoCommit Commit, InventoryResult? Inventory, Func<bool> IsCurrent);

internal sealed partial class GameSession
{
    public GuildHongbaoUseResult UseGuildHongbao(GuildHongbaoService service, GuildHongbaoAction action,
        uint value = 0, byte count = 0) => owner.Execute(() =>
    {
        var guard = UseGuard();
        bool Current() => CanPublishConsumable(guard);
        if (!Ready || Phase != SessionPhase.Town || pendingConsumableUse is not null ||
            Selected is not { } expected || CaptureGuildManagement() is not { } actor)
            return new GuildHongbaoUseResult(new(3), null, Current);
        var commit = action switch
        {
            GuildHongbaoAction.Issue => service.Issue(actor, expected, value, count),
            GuildHongbaoAction.Take => service.Take(actor, expected, value),
            GuildHongbaoAction.Refund => service.Refund(actor, expected),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        if (!commit.Success || commit.Character is null || commit.Inventory is null) return new(commit, null, Current);
        if (!ReferenceEquals(Selected, expected) || !actor.IsCurrent())
            throw new InvalidOperationException("公会红包提交后的角色身份发生变化");
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
