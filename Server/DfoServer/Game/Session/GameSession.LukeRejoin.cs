using DfoServer.Game.Raids;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    // 仅取已发布绑定中的不可变身份提示，不读Selected或授权；调用者必须零等待取得全团owner后复验。
    internal RaidActor? ReadLukeDisconnectActorHint() =>
        Volatile.Read(ref raidCombat) is { Battle.Kind: RaidKind.Luke } combat ? combat.Actor : null;

    // 调用方已持本owner及全团门禁；只装原Battle身份，不改位置/资产/加载屏障。
    internal bool InstallLukeRejoin(RaidActor actor, AntonRaidBattle battle)
    {
        if (!MatchesRaidCombatActor(actor) || !CanChangeTownParty || raidCombat is not null ||
            raidRegistry is null || battle.Kind != RaidKind.Luke) return false;
        raidCombat = new(actor, battle);
        return true;
    }
}
