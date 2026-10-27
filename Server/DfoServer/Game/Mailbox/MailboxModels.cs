using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Mailbox;

internal enum MailboxError
{
    None, InvalidRequest, InvalidState, Empty, RecipientMissing, SelfSend, InsufficientGold,
    RecipientGoldLimit, DailyGoldLimit, InvalidAttachment, NotTradable, AccountBound,
    LimitedPeriod, ExpiredItem, LockedItem, MailMissing, InventoryFull, GoldCarryLimit,
    UnclaimedAssets, StorageFull
}

internal sealed record MailboxRecipient(ushort Id, string Account, string Name, byte Job, byte GrowType, byte Level);
internal sealed record MailboxSendItem(byte Type, short Slot, int ItemId, int Count);
internal sealed record MailboxSendRequest(string Recipient, uint Gold, ImmutableArray<MailboxSendItem> Items,
    string Text, uint Special = 0, byte RecipientServerIndex = 0);
internal sealed record MailboxAttachment(uint Id, InventoryItem Item)
{
    // 两条持久序列各受 SQL 上限约束，领取对象与邮件合并 key 永不重叠。
    public const uint ClaimFlag = 0x40000000;
    public uint ClaimId => ClaimFlag | Id;
}
internal sealed record MailboxEntry(uint Id, long Revision, ushort RecipientId, ushort SenderId, string SenderName,
    string Text, uint Gold, long Created, long? Expires, ushort State, bool System,
    ImmutableArray<MailboxAttachment> Attachments)
{
    public bool Available(long now) => State != 0 && (State == 3 || Expires is null || Expires > now);
    public int RemainingSeconds(long now) => Expires is null || State == 3 ? 0 : (int)Math.Clamp(Expires.Value - now, 0, int.MaxValue);
}
internal sealed record MailboxPage(ImmutableArray<MailboxEntry> Entries, int NotLoaded);
internal sealed record MailboxSendPlan(MailboxRecipient Recipient, MailboxSendRequest Request, InventoryPlan Inventory,
    ImmutableArray<InventoryItem> Attachments, uint Fee, int Day, long Created);
internal sealed record MailboxClaim(uint ClaimId, MailboxEntry Mail, MailboxAttachment? Attachment);
internal sealed record MailboxClaimPlan(InventoryPlan Inventory, ImmutableArray<MailboxClaim> Claims,
    ImmutableArray<InventoryItem> Items, ImmutableArray<uint> GoldMails, long Now, ImmutableArray<MailboxClaimChange> Results);
internal readonly record struct MailboxStateChange(uint Id, ushort State);
internal readonly record struct MailboxClaimChange(uint MailId, uint ClaimId, MailboxError Error = MailboxError.None);
internal sealed record MailboxDelivery(string Key, MailboxRecipient Recipient, string SenderName, string Text, uint Gold,
    ImmutableArray<InventoryItem> Items, long Created, long? Expires = null);

internal sealed record MailboxReceipt(Character Character, long Generation, long SelectionEpoch);
internal sealed record MailboxResult(MailboxError Error = MailboxError.None, Character? Character = null, uint MailId = 0)
{
    public bool Accepted => Error == MailboxError.None;
    public MailboxReceipt? Receipt { get; init; }
    public InventoryResult? Inventory { get; init; }
    public MailboxRecipient? Recipient { get; init; }
    public ImmutableArray<MailboxStateChange> Changes { get; init; } = [];
    public ImmutableArray<MailboxClaimChange> Claimed { get; init; } = [];
    public static MailboxResult Reject(MailboxError error) => new(error);
}
