using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class CharacterLevelProjection(CharacterStatComputer stats)
{
    public async Task RefreshAsync(GameConnectionContext context, Character committed, byte previousLevel,
        string action, Func<bool> canSend)
    {
        if (previousLevel is < 1 or > 95 || committed.Level < previousLevel)
            throw new InvalidDataException("等级刷新必须使用真实提交前后等级");
        if (committed.Level == previousLevel || !canSend())
            return;
        // 沿S4A21：副本/结算只追加Player，避免升级时重走Actor路径。
        // 城镇升级仍同步Actor等级；89B仅携带CHR基础，不重复累计装备和技能。
        if (context.Owner.Execute(() => context.Session.Phase is SessionPhase.Town or SessionPhase.MapSelect))
            await context.Notify(NotiPacketType.USERINFO, TownPackets.Actor(committed), action + "_actor", canSend);
        await context.Notify(NotiPacketType.USERINFO, TownPackets.Player(committed, stats.Compute(committed)), action + "_player", canSend);
    }
}
