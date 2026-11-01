using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Marriage;

internal sealed record MarriageCharacterCard(MailboxRecipient Recipient, IReadOnlyList<int> Equipment)
{
    public IReadOnlyList<int> ClearAvatarIds { get; init; } = [];
}

internal interface IMarriageStore
{
    // GM离线专用；同账号复用同一owner，双方关系及唯一成员索引原子建立，不改双方库存。
    MarriageRecord CommitDirectMarriage(AccountOwners.Lease firstOwner, AccountOwners.Lease secondOwner,
        Character first, Character second, MarriageRecord after);
    MarriageRecord? FindMarriage(ushort characterId);
    MarriageRecord? FindMarriage(Guid id);
    MarriageRecord? FindMarriageByContext(uint contextId);
    MarriageRecord? FindLatestMarriage(ushort characterId);
    MailboxRecipient? FindMarriageRecipient(ushort characterId);
    MarriageCharacterCard? FindMarriageCard(ushort characterId);
    Character CommitMarriageHallTravel(AccountOwners.Lease owner, Character expected, MarriageRecord wedding,
        Location destination, Location? returnPoint, long now, InventoryPlan? inventory = null,
        MarriageRecord? afterGift = null);
    IReadOnlyList<MarriageRecord> FindPendingWeddings();
    MarriageRecord CommitMarriageSettlement(MarriageRecord before, MarriageRecord after, IReadOnlyList<MailboxDelivery>? mail = null);
    // 新建仅允许双方联合提交。before=null 时 after.Revision 必须为1；更新必须恰好递增1。
    // 旧聚合版本（包括提交成功后的原计划重放）整体拒绝，不重复扣费或投递。
    MarriageCommit CommitMarriageSingle(AccountOwners.Lease owner, Character expected, InventoryPlan inventory,
        MarriageRecord? before, MarriageRecord after, IReadOnlyList<MailboxDelivery>? mail = null);
    MarriageCommit CommitMarriageTogether(AccountOwners.Lease firstOwner, AccountOwners.Lease secondOwner,
        Character first, Character second, InventoryPlan firstInventory, InventoryPlan secondInventory,
        MarriageRecord? before, MarriageRecord after, IReadOnlyList<MailboxDelivery>? mail = null);
}
