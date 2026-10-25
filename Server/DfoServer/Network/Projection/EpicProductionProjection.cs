using DfoServer.Game.Dungeons;
using DfoServer.Game.EpicProduction;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class EpicProductionProjection(TimeProvider? clock=null)
{
    readonly TimeProvider timer=clock??TimeProvider.System;
    public Task InitializeAsync(GameConnectionContext context)
    {
        var character=context.Session.Selected??throw new InvalidOperationException("史诗制作初始化缺少选角");
        var state=character.EpicProduction.InWeek(WeeklyDungeonPolicy.WeekKey(timer.GetUtcNow()));
        return context.Notify(NotiPacketType.EPIC_PRODUCTION_INFO,EpicProductionPackets.State(character.Id,state),"epic_production_initialize");
    }
    public Task RefreshAsync(GameConnectionContext context,EpicProductionCommit receipt)=>
        context.Notify(NotiPacketType.EPIC_PRODUCTION_INFO,EpicProductionPackets.State(receipt.Character.Id,receipt.Character.EpicProduction),
            "epic_production_committed",()=>context.Session.CanPublishEpicProduction(receipt));
}
