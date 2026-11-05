using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static partial class RaidCombatProjection
{
    static void PostPreyFailure(GameConnectionContext context, AntonRaidBattleView view, RaidActor actor, Func<bool> current)
    {
        if (!view.PhaseExpired || context.Session.CurrentRaidBattle(actor) is not
            { Kind: RaidKind.Prey, PreyGuide: false, LukeWaitingRoom: { } waiting }) return;
        var result = (actor.Epoch, actor.Character, view.Instance, view.Phase, Ended: false);
        var ended = result with { Ended = true };
        bool Ready() => current() && context.Session.Phase == SessionPhase.Town &&
            context.LukePhaseExit?.ReturnPending(view.Instance) != true &&
            context.Session.Selected?.Position is { } position && position.Town == waiting.Town && position.Area == waiting.Area;
        context.Owner.Execute(() =>
        {
            if (!Ready()) return false;
            // 最后24完成后再装失败ANI/影片。无成功结算统计，保留完整601各列并显式给零。
            context.Sender.Post(0, (ushort)NotiPacketType.RAID_RESULT,
                RaidCombatPackets.Result(new(3, checked((byte)view.Phase), 0, 0, 0, 0)), "prey_failed_result",
                () => Ready() && context.PreyFailureDisplay != result && context.PreyFailureDisplay != ended,
                () => context.PreyFailureDisplay = result);
            // 015B824D按先前601的result3显示失败并重置manager，不能套奖励state3/4。
            context.Sender.Post(0, (ushort)NotiPacketType.RAID_STATE, [6, checked((byte)view.Phase)], "prey_failed_state",
                () => Ready() && context.PreyFailureDisplay == result,
                () => context.PreyFailureDisplay = ended);
            return true;
        });
    }
}
