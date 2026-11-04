using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;

namespace DfoServer.Game.Mailbox;

// 邮箱与资产共用一次提交；不从业务内另开角色库存事务。
internal interface IMailboxStore
{
    MailboxRecipient? FindMailRecipient(string name);
    MailboxPage LoadMailbox(AccountOwners.Lease owner, ushort characterId, long now);
    ImmutableArray<MailboxClaim> LoadMailClaims(AccountOwners.Lease owner, ushort characterId, ImmutableArray<uint> ids, long now);
    void ExpireMailbox(AccountOwners.Lease owner, ushort characterId, long now);
    MailboxResult SendMail(AccountOwners.Lease owner, Character expected, MailboxSendPlan plan);
    MailboxResult ClaimMail(AccountOwners.Lease owner, Character expected, MailboxClaimPlan plan);
    MailboxResult ChangeMailState(AccountOwners.Lease owner, ushort characterId, ImmutableArray<uint> ids, ushort state, long now, bool force = false);
    /// <summary>GM 强制删除：无视未领取资产/过期，直接 state=0 并按到期路径销毁附件。</summary>
    MailboxResult ForceDeleteMails(AccountOwners.Lease owner, ushort characterId, ImmutableArray<uint> ids, long now);
    MailboxResult DeliverMail(MailboxDelivery delivery);
}
