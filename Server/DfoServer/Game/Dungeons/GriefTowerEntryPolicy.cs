using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

// 账号日额度与角色当天挑战身份分开；换角色不能把同账号额度恢复，也不能继承他人的重试资格。
internal sealed record GriefTowerEntryState(int AccountDay, int AccountUsed, int CharacterDay,
    bool CharacterCleared, int Layer);
internal sealed record GriefTowerEntryDecision(bool Accepted, string Reason, int Day, int AccountLimit,
    bool UsesInvitation = false)
{
    public const int InvitationItem = 10161065;
}

// 本服采用账号日限次版本，06:00边界复用既有游戏日。资源的enterable count不解释成付费重试上限。
// 这只是纯准入规划；必须与库存、账号额度及正式实例尝试号原子提交后才能安装实例。
internal static class GriefTowerEntryPolicy
{
    public static GriefTowerEntryDecision Evaluate(GriefTowerCatalog catalog, GriefTowerEntryState state,
        int dungeonId, int level, int adventureLevel, int partyCount, DateTimeOffset now)
    {
        int day = QuestCycle.Day(now);
        if (state.AccountDay < 0 || state.AccountUsed < 0 || state.CharacterDay < 0 || adventureLevel < 1 ||
            state.Layer < 1 || state.Layer > catalog.TopLayer ||
            state.AccountDay == 0 && state.AccountUsed != 0 || state.CharacterDay == 0 && state.CharacterCleared)
            return Reject("grief-tower-state-invalid");
        if (state.AccountDay > day || state.CharacterDay > day) return Reject("grief-tower-clock-before-state");
        long limit = catalog.DeclaredAccountMaximum;
        foreach (var band in catalog.DeclaredAdventureBands)
            if (adventureLevel >= band.MinimumLevel && adventureLevel <= band.MaximumLevel) limit += band.AdditionalCount;
        if (limit > int.MaxValue) return Reject("grief-tower-quota-overflow");
        int maximum = (int)limit;
        if (level < catalog.MinimumLevel) return Reject("grief-tower-level-too-low", maximum);
        if (partyCount != 1) return Reject("grief-tower-single-player-only", maximum);
        if (!catalog.FloorsByDungeon.TryGetValue(dungeonId, out var floor) || floor.Layer != state.Layer)
            return Reject("grief-tower-floor-mismatch", maximum);
        if (state.CharacterDay == day)
            return state.CharacterCleared ? Reject("grief-tower-already-cleared-today", maximum) :
                new(true, "grief-tower-invitation-required", day, maximum, true);
        int used = state.AccountDay == day ? state.AccountUsed : 0;
        return used >= maximum ? Reject("grief-tower-account-quota-exhausted", maximum) :
            new(true, "grief-tower-basic-entry", day, maximum);

        GriefTowerEntryDecision Reject(string reason, int maximum = 0) => new(false, reason, day, maximum);
    }
}
