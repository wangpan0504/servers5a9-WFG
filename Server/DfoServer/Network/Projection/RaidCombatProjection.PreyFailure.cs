using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static partial class RaidCombatProjection
{
    internal static void PostPreyFailure(GameConnectionContext context, AntonRaidBattleView view, RaidActor actor, Func<bool> current)
    {
        if (!view.PhaseExpired || context.Session.CurrentRaidBattle(actor) is not
            { Kind: RaidKind.Prey, PreyGuide: false, LukeWaitingRoom: { } waiting }) return;
        var result = (actor.Epoch, actor.Character, view.Instance, view.Phase, Stage: (byte)0);
        var movie = result with { Stage = 1 };
        var ended = result with { Stage = 2 };
        bool Returned() => current() && context.Session.Phase == SessionPhase.Town &&
            context.LukePhaseExit?.ReturnPending(view.Instance) != true &&
            context.Session.Selected?.Position is { } position && position.Town == waiting.Town && position.Area == waiting.Area;
        context.Owner.Execute(() =>
        {
            if (!current()) return false;
            // 601先安装result3，587/state3才会选择原生失败影片；必须发生在原场景退场之前。
            context.Sender.Post(0, (ushort)NotiPacketType.RAID_RESULT,
                RaidCombatPackets.Result(new(3, checked((byte)view.Phase), 0, 0, 0, 0)), "prey_failed_result",
                () => current() && context.PreyFailureDisplay != result && context.PreyFailureDisplay != movie && context.PreyFailureDisplay != ended,
                () => context.PreyFailureDisplay = result);
            context.Sender.Post(0, (ushort)NotiPacketType.RAID_STATE, [3, checked((byte)view.Phase)], "prey_failed_movie",
                () => current() && context.PreyFailureDisplay == result,
                () => context.PreyFailureDisplay = movie);
            // 原退场执行器等待影片期限并完成24后，state6只负责清理manager，不打开奖励窗口。
            context.Sender.Post(0, (ushort)NotiPacketType.RAID_STATE, [6, checked((byte)view.Phase)], "prey_failed_state",
                () => Returned() && context.PreyFailureDisplay == movie,
                () => context.PreyFailureDisplay = ended);
            return true;
        });
    }
}
