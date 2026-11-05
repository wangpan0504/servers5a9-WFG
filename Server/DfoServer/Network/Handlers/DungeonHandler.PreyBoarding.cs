using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class DungeonHandler
{
    async Task UpdatePreyBoardingPartyAsync(GameConnectionContext context)
    {
        if (parties is null) return;
        var notices = await parties.UpdatePreyBoardingAsync(context.Session, context.Stop);
        if (notices.Length != 0) parties.Publish(notices);
    }

    // 调用者已持本小队共享命令门禁及全部连接门禁；不伪造客户端15/16请求。
    internal async Task EnterPreyLandingAsync(GameConnectionContext context, AntonRaidBattleView view, RaidActor actor)
    {
        var party = context.Session.CurrentParty?.Invoke();
        var scene = party ?? preyRaid?.CapturePreySoloScene(context.Session);
        var battle = context.Session.CurrentRaidBattle(actor);
        if (preyRaid is not { Enabled: true } || partyDungeons is null || scene is null ||
            context.DispatchGates?.Holds(context, party?.Commands ?? context.CommandGate) != true || !actor.IsCurrent ||
            context.Session.Phase != SessionPhase.Town || context.Session.Selected?.Position is not { Town: 59, Area: 4 or 5 } position ||
            battle?.CapturePreyLanding(view, position.Area) is not { } landing ||
            scene.Members.Any(member => !member.IsCurrent() || !member.Actor.Session.Ready ||
                member.Actor.Session.Phase != SessionPhase.Town ||
                member.Actor.Session.Selected?.Position is not { Town: 59 } spot || spot.Area != landing.Side)) return;
        PartyDungeonNotice[] selected = [], entered = [];
        string? reason = null;
        try
        {
            selected = preyRaid.Select(context.Session, landing.Dungeon, scene, out reason, landing: landing);
            if (selected.Length == 0) return;
            // 原15/27先建立scene2；正式资源与SQL仍走统一Enter，随后只发布原28/29及加载屏障。
            await PartyDungeonProjection.PublishAsync(selected);
            if (!battle.IsPreyLandingCurrent(landing)) { reason = "prey-landing-arrival-changed"; return; }
            entered = partyDungeons.Enter(context.Session, checked((uint)landing.Dungeon), 2,
                dungeons, rooms, null, null, null, null, preySolo: party is null ? scene : null);
            if (entered.Length == 0) reason = context.Session.LastFormalDungeonRejection ?? "prey-landing-entry-rejected";
            else await PartyDungeonProjection.PublishAsync(entered);
        }
        catch (System.Data.Common.DbException error)
        {
            reason = entered.Length == 0 ? "prey-landing-database-rejected" : "prey-landing-committed-publication-failed";
            if (entered.Length != 0) context.Disconnect();
            log.Write("prey_landing_failed", new { Connection = context.Id, view.Instance, view.Phase,
                landing.Dungeon, Error = error.GetType().Name, Code = error.ErrorCode });
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            reason = "prey-landing-publication-failed";
            context.Disconnect();
        }
        finally
        {
            if (entered.Length == 0)
            {
                context.Session.CancelRaidDungeonSelection(selected);
                var returned = selected.Where(row => row.Selection is { } receipt &&
                        row.Recipient.Actor.IsCurrent() && row.Recipient.Actor.Session.CanPublishDungeonSelection(receipt))
                    .Select(row => (Notice: row, Receipt: row.Recipient.Actor.Session.ReturnFromDungeonSelection(restorePosition: false)))
                    .Where(row => row.Receipt is not null)
                    .Select(row => row.Notice with { Kind = PartyDungeonNoticeKind.SelectionReturn, Selection = row.Receipt }).ToArray();
                await PartyDungeonProjection.PublishAsync(returned);
            }
            log.Write("prey_landing_entry", new { Connection = context.Id, view.Instance, view.Phase,
                landing.Arrival.FromArea, landing.Arrival.Generation, landing.Side, landing.Dungeon,
                Accepted = entered.Length != 0, Members = entered.Length == 0 ? 0 : scene.Members.Length, Reason = reason });
        }
    }
}
