using DfoServer.Game.Session;
using DfoServer.Game.Strongway2019;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;
using System.Runtime.CompilerServices;

namespace DfoServer.Network.Projection;

internal sealed class Strongway2019RewardProjection(ItemCatalog items, IStrongway2019RewardStore store, ServerLog log,
    Strongway2019ProfileProjection? profiles = null, OnlineSessionDirectory? online = null)
{
    sealed class Notification { public bool Sent; }
    readonly ConditionalWeakTable<Strongway2019RewardReceipt, Notification> notifications = new();
    public Strongway2019RewardReceipt? Commit(GameConnectionContext context)
    {
        if (context.Session.PrepareStrongway2019Reward() is not { } pending) return null;
        if (store is IStrongway2019CompletionStore completion)
        {
            if (context.Session.CurrentStrongway2019Match is { } match) { if (!FreezeMatch(match)) return null; }
            else if (context.Session.PrepareStrongway2019Completion(items) is { } plan)
                completion.FreezeStrongway2019Completions([plan]);
            else return null;
        }
        else profiles?.Commit(context, pending);
        return context.Session.CommitStrongway2019Reward(pending, items, store);
    }

    sealed class CompletionFreeze { public bool Frozen; }
    readonly ConditionalWeakTable<Strongway2019Terminal, CompletionFreeze> frozen = new();
    internal bool FreezeMatch(Strongway2019Match match)
    {
        if (store is not IStrongway2019CompletionStore completion || match.Competition.Snapshot.Terminal is not { WinnerSide: not null } terminal) return true;
        var state = frozen.GetValue(terminal, _ => new());
        lock (state)
        {
            if (state.Frozen) return true;
            var members = match.Pair.First.Party.Members.Concat(match.Pair.Second.Party.Members).ToArray();
            // 失败后仍从原在线目录取当前Session的owner，不Acquire临时租约，也不再读取旧Session.Selected。
            var reporter = members.Select(member => member.Actor.Owner).FirstOrDefault(owner => owner.IsCurrent)
                ?? members.Select(member => online?.Find(member.Actor.Owner.Account)?.CaptureStrongway2019CompletionReporter())
                    .FirstOrDefault(owner => owner is not null);
            if (reporter is null) return false;
            var template = items.Get(490012737);
            if (DfoServer.Game.Inventory.Services.InventoryRewardGrantService.GrantDefinitionError(template, new(490012737, 1)) is not null)
                throw new InvalidDataException("首期匹配完整奖励附件资源缺失");
            var core = DfoServer.Game.Inventory.Services.InventoryRewardGrantService.Create(template, 1, DateTimeOffset.FromUnixTimeSeconds(terminal.Created));
            var item = DfoServer.Game.Inventory.Services.InventoryRewardGrantService.CreateItem(template,
                new(DfoServer.Game.Inventory.Models.InventoryListType.Main, 9), core);
            try
            {
                completion.FreezeStrongway2019MatchingCheckpoint(reporter, match.Competition,
                    members.Select(member => (member.Actor.Owner.Account, member.Actor.Character.Id)).ToArray(), item);
            }
            catch (InvalidOperationException) when (!reporter.IsCurrent) { return false; }
            state.Frozen = true; match.CompletionCheckpointCommitted = true;
            return true;
        }
    }

    internal async Task<bool> RecoverReadyRecipientsAsync(Strongway2019Match match)
    {
        bool complete = true;
        if (match.CompletionCheckpointCommitted && match.Competition.Snapshot.Terminal is { WinnerSide: { } winner })
        {
            var party = winner == 0 ? match.Pair.First.Party : match.Pair.Second.Party;
            foreach (var member in party.Members)
            {
                var current = online?.Find(member.Actor.Owner.Account);
                // 原比赛内成员仍走原提交；离线、未Ready或换角色者由其后续Ready消费已保存权益。
                if (current is null || ReferenceEquals(current.CurrentStrongway2019Match, match) ||
                    !current.IsReadyCharacter(member.Actor.Character.Id)) continue;
                if (current.Strongway2019RecoveryArrived is not { } recover || !await recover(member.Actor.Character.Id))
                    complete = false;
            }
        }
        match.ReadyCompletionRecoveryNotified = complete;
        return complete;
    }

    internal async Task RecoverAsync(GameConnectionContext context)
    {
        if (store is not IStrongway2019CompletionStore completion) return;
        var recovered = context.Session.RecoverStrongway2019Completion(completion);
        var mails = recovered.MailIds;
        if (!recovered.Changed && mails.IsEmpty) return;
        if (profiles is not null) await profiles.InformationAsync(context);
        if (mails.IsEmpty) return;
        context.Session.NotifyMailbox(context.Session.Selected!.Id, restoreOnTown: true);
        log.Write("strongway2019_reward_recovered", new { Connection = context.Id, Character = context.Session.Selected.Id, MailIds = mails });
    }

    public async Task CommittedAsync(GameConnectionContext context, Strongway2019RewardReceipt receipt)
    {
        if (!context.Session.CanPublishStrongway2019Reward(receipt)) return;
        if (profiles is not null) await profiles.InformationAsync(context, () => context.Session.CanPublishStrongway2019Reward(receipt));
        if (!receipt.Commit.Granted) return;
        var notification = notifications.GetValue(receipt, _ => new());
        lock (notification)
        {
            if (notification.Sent || !context.Session.CanPublishStrongway2019Reward(receipt)) return;
            // 原MailboxArrived已绑定有界Sender入口，不再另发第二份alarm。
            context.Session.NotifyMailbox(receipt.Preparation.Character.Id, restoreOnTown: true);
            notification.Sent = true;
            log.Write("strongway2019_reward_committed", new { Connection = context.Id, receipt.Preparation.Identity,
                receipt.Commit.MailId, receipt.Commit.Duplicate });
        }
    }
}
