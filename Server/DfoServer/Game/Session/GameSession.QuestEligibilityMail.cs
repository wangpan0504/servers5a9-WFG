using DfoServer.Game.Mailbox;
using DfoServer.Game.Quests;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    long questMailCheckedRevision=-1;
    long questMailCheckedSelection=-1;

    public MailboxReceipt? EnsureQuestEligibilityMail(bool initializing=false)
    {
        var expected=owner.Execute(()=>
            questCatalog is not null && Selected is not null &&
            (initializing || Ready && Phase==SessionPhase.Town && pendingRental is null && !HasPendingTownQuestCommit) &&
            (questMailCheckedRevision!=Selected.Revision || questMailCheckedSelection!=SelectionEpoch) ? Selected : null);
        if (expected is null) return null;
        var epoch=SelectionEpoch;
        // 资源解析和候选计算在owner外，提交时再核验快照与全部接取条件。
        var plan=QuestEligibilityMailPlan.Prepare(expected,questCatalog!,DateTimeOffset.UtcNow);
        return owner.Execute<MailboxReceipt?>(()=>
        {
            if (!ReferenceEquals(Selected,expected) || SelectionEpoch!=epoch ||
                !initializing && (!Ready || Phase!=SessionPhase.Town || pendingRental is not null || HasPendingTownQuestCommit)) return null;
            var ids=repository.CommitQuestEligibilityMail(owner,plan);
            questMailCheckedRevision=expected.Revision;questMailCheckedSelection=epoch;
            return ids.IsEmpty ? null : new MailboxReceipt(expected,owner.Generation,epoch);
        });
    }
}
