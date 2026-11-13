namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    // 系统退场只消费原Run的已提交结果；桥是城镇落点，不在此创建另一侧遭遇。
    internal FiendwarMovement? ReserveFiendwarReturn(RaidActor actor, Guid? run)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Fiendwar || !actor.IsCurrent || !members.ContainsKey(actor) ||
                current.Fiendwar is not { } field || fiendwarMovements.ContainsKey(actor.Character)) return null;
            if (run is { } id && (!dungeonRuns.TryGetValue(id, out var encounter) || !encounter.Active.Contains(actor)))
                return null;
            int destination;
            if (field.Cleared || field.Failed) destination = FiendwarBaseArea;
            else if (run is { } original && current.FiendwarExpiredRuns.Contains(original) &&
                current.FiendwarBridgeReturns.TryGetValue(original, out int bridge)) destination = bridge;
            else return null;
            // 强制退出允许两支队伍在桥上交接；正式遭遇仍保留最多四人及占用门禁。
            var movement = new FiendwarMovement(this, actor, destination);
            fiendwarMovements.Add(actor.Character, movement);
            return movement;
        }
    }
}
