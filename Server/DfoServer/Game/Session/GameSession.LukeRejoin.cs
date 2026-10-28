using DfoServer.Game.Raids;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    // 调用方已持本owner及全团门禁；只装原Battle身份，不改位置/资产/加载屏障。
    internal bool InstallLukeRejoin(RaidActor actor, AntonRaidBattle battle)
    {
        if (!MatchesRaidCombatActor(actor) || !CanChangeTownParty || raidCombat is not null ||
            raidRegistry is null || battle.Kind != RaidKind.Luke) return false;
        raidCombat = new(actor, battle);
        return true;
    }
}
