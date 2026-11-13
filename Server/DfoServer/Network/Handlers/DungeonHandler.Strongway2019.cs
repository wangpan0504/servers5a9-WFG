using DfoServer.Game.Session;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class DungeonHandler
{
    static string Strongway2019EntryRejectionText(string reason)
    {
        var parts = reason.Split(':');
        if (parts is ["strongway2019-anti-evil-required", var required, var actual])
            return $"未能进入：需要抗魔值 {required}，当前服务端计算抗魔值为 {actual}。已返回准备区，请调整装备后重试。";
        return reason switch
        {
            "strongway2019-activity-inactive" => "未能进入：强者之路活动当前未开放。已返回准备区。",
            "strongway2019-level-required" => "未能进入：强者之路需要95级角色。已返回准备区。",
            "strongway2019-member-count-required" => "未能进入：强者之路挑战人数需为1至4人。已返回准备区。",
            "strongway2019-resource-unavailable" => "未能进入：活动副本资源暂不可用，请联系管理员。已返回准备区。",
            _ => "未能进入：入场条件已变化，请确认处于强者之路频道准备区后重试。已返回准备区。"
        };
    }

    async Task ReturnStrongway2019NormalTimeoutAsync(GameConnectionContext context, Strongway2019NormalTimeout timeout)
    {
        if (context.Stop.IsCancellationRequested || !context.Session.IsCurrentStrongway2019NormalTimeout(timeout)) return;
        var party = context.Session.CurrentParty?.Invoke();
        var instance = timeout.PartyDungeon;
        if (instance is not null && (party is null || partyDungeons is null || parties is null)) return;
        var members = instance?.Members ?? [];
        var gates = (party is null ? Enumerable.Empty<SemaphoreSlim>() : [party.Commands])
            .Concat((party?.Members ?? []).Concat(members)
                .OrderBy(member => member.Actor.Owner.Account, StringComparer.Ordinal)
                .Select(member => member.Actor.Session.CommandGate)).Append(context.CommandGate).Distinct();
        var held = new List<SemaphoreSlim>();
        try
        {
            // 后台只零等待取原命令门；一名队友忙时整队保留原Run，下轮再试。
            foreach (var gate in gates)
            {
                if (!gate.Wait(0)) return;
                held.Add(gate);
            }
            if (!context.Session.IsCurrentStrongway2019NormalTimeout(timeout) ||
                !ReferenceEquals(context.Session.CurrentParty?.Invoke(), party)) return;
            if (instance is null)
            {
                var travel = context.Session.ReturnFromFormalDungeon();
                if (travel is null) return;
                if (context.Death is not null) await context.Death.StopAsync();
                using var publication = context.Sender.QueuePublication(() => context.Session.CanPublishTravel(travel));
                await town.ReturnFromFormalDungeonAsync(context, travel, "strongway_normal_timeout_town");
                log.Write("formal_dungeon_return", new { Connection = context.Id, Reason = "strongway_normal_timeout", travel.Character.Id });
                return;
            }
            // 全队到期且仍属于同一原实例才开始退场；每次提交仍走原owner/实例事务及原通知。
            var expired = members.Select(member => (Member: member,
                Timeout: member.Actor.Session.CaptureStrongway2019NormalTimeout())).ToArray();
            if (expired.Length == 0 || expired.Any(row => !row.Member.Actor.IsCurrent() ||
                row.Timeout is null || !ReferenceEquals(row.Timeout.PartyDungeon, instance))) return;
            foreach (var row in expired)
            {
                if (!row.Member.Actor.Session.IsCurrentStrongway2019NormalTimeout(row.Timeout!)) return;
                if (await PartyDungeonProjection.DepartAsync(row.Member.Actor.Session, partyDungeons, parties,
                    () => Task.CompletedTask) == CommandResult.Unhandled) return;
            }
        }
        catch (InvalidOperationException) when (!context.Owner.IsCurrent || members.Any(member => !member.Actor.IsCurrent())) { }
        finally { for (int i = held.Count - 1; i >= 0; i--) held[i].Release(); }
    }
}
