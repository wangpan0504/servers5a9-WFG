using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection.Inventory;

// 可选派生后处理接口；普通装备同步与服务器组打造公告不依赖此接口，Runtime当前未注入。
// 只使用本次已提交快照，不再扣费/改资产；异步发布遵守context.Owner及发送泵的generation检查。
internal interface IRefinementEffects
{
    Task PublishAsync(GameConnectionContext context, Character committed, RefinementReceipt receipt);
}
