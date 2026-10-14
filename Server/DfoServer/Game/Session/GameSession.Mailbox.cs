using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public (MailboxPage Page, MailboxReceipt Receipt) OpenMailbox(MailboxService service, bool initializing = false) => owner.Execute(() =>
    {
        if (Selected is null || !initializing && (!Ready || Phase != SessionPhase.Town))
            throw new InvalidDataException("邮件查询不在当前选角阶段");
        return (service.Open(owner, Selected.Id, DateTimeOffset.UtcNow), new MailboxReceipt(Selected, owner.Generation, SelectionEpoch));
    });

    public MailboxResult SendMail(MailboxService service, MailboxSendRequest request)
    {
        var (character, epoch) = MailCharacter();
        if (character is null)
            return MailboxResult.Reject(MailboxError.InvalidState);
        var error = service.PlanSend(character, owner.Account, request, DateTimeOffset.UtcNow, out var plan);
        return owner.Execute(() =>
        {
            if (!MailPlanIsCurrent(character, epoch))
                return MailboxResult.Reject(MailboxError.InvalidState) with { Receipt = new(character, owner.Generation, epoch) };
            return InstallMailResult(error == MailboxError.None ? service.Send(owner, character, plan!) : MailboxResult.Reject(error), plan?.Inventory);
        });
    }

    public MailboxResult ClaimMail(MailboxService service, ImmutableArray<uint> ids)
    {
        var (character, epoch) = MailCharacter();
        if (character is null)
            return MailboxResult.Reject(MailboxError.InvalidState);
        var error = service.PlanClaim(owner, character, ids, DateTimeOffset.UtcNow, out var plan);
        return owner.Execute(() =>
        {
            if (!MailPlanIsCurrent(character, epoch))
                return MailboxResult.Reject(MailboxError.InvalidState) with { Receipt = new(character, owner.Generation, epoch) };
            return InstallMailResult(error == MailboxError.None ? service.Claim(owner, character, plan!) : MailboxResult.Reject(error), plan?.Inventory);
        });
    }

    public MailboxResult ChangeMailState(MailboxService service, ImmutableArray<uint> ids, ushort state) => owner.Execute(() =>
    {
        if (!Ready || Phase != SessionPhase.Town || Selected is null)
            return MailboxResult.Reject(MailboxError.InvalidState);
        return InstallMailResult(service.ChangeState(owner, Selected.Id, ids, state, DateTimeOffset.UtcNow), null);
    });

    public MailboxResult QueryMailRecipient(MailboxService service, string name) => owner.Execute(() =>
    {
        if (!Ready || Phase != SessionPhase.Town || Selected is null)
            return MailboxResult.Reject(MailboxError.InvalidState);
        var recipient = service.FindRecipient(name);
        var result = recipient is null ? MailboxResult.Reject(MailboxError.RecipientMissing) : recipient.Id == Selected.Id
            ? MailboxResult.Reject(MailboxError.SelfSend) : new MailboxResult() { Recipient = recipient };
        return InstallMailResult(result, null);
    });

    (Character? Character, long Epoch) MailCharacter() => owner.Execute(() =>
        (pendingRental is null && Ready && Phase == SessionPhase.Town ? Selected : null, SelectionEpoch));
    bool MailPlanIsCurrent(Character character, long epoch) => pendingRental is null && Ready && Phase == SessionPhase.Town &&
        MatchesSelection(epoch, character.Id) && ReferenceEquals(Selected, character);

    MailboxResult InstallMailResult(MailboxResult result, InventoryPlan? plan)
    {
        var previous = Selected!.ActiveMainQuests;
        if (result.Accepted && result.Character is not null)
            Selected = result.Character;
        var receipt = new MailboxReceipt(Selected!, owner.Generation, SelectionEpoch);
        return result with { Receipt = receipt, Inventory = result.Accepted && plan is not null ?
            new InventoryResult(true, 0, plan) { Receipt = new(Selected!, owner.Generation, SelectionEpoch) { PreviousQuests = previous } } : null };
    }

    public bool CanPublishMailbox(MailboxReceipt receipt, bool initializing = false) => owner.Execute(() =>
        (initializing ? !Ready && Phase == SessionPhase.Unselected : Ready && Phase == SessionPhase.Town) &&
        receipt.Generation == owner.Generation && SelectionEpoch == receipt.SelectionEpoch && Selected?.Id == receipt.Character.Id);

    public (MailboxPage Page, MailboxReceipt Receipt)? RefreshMailbox(MailboxService service, MailboxReceipt receipt) =>
        owner.Execute<(MailboxPage Page, MailboxReceipt Receipt)?>(() =>
        {
            if (!CanPublishMailbox(receipt))
                return null;
            return OpenMailbox(service);
        });

    // Runtime 绑定原连接的有界通知入口；目录仍只保存这一份 Session。
    public Action<MailboxReceipt>? MailboxArrived { private get; set; }
    public void NotifyMailbox(ushort recipientId)
    {
        try
        {
            var notice = owner.Execute(() => Ready && Selected?.Id == recipientId ?
                (MailboxArrived, new MailboxReceipt(Selected, owner.Generation, SelectionEpoch)) : default);
            notice.Item1?.Invoke(notice.Item2!);
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { /* 刚被目录替换的原连接不接收新邮件提示。 */ }
    }
}
