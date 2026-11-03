using DfoServer.Game.Raids;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AntonRaidEventLedgerTests
{
    internal static void Run()
    {
        var instance = Guid.NewGuid();
        var run = Guid.NewGuid();
        var key = new AntonRaidEventKey(instance, run, 4, 1, 218, 17);
        var ledger = new AntonRaidEventLedger(2);

        That(!ledger.TryCommit(key with { InstanceId = Guid.Empty }, AntonRaidEventKind.ObjectDestroyed),
            "安徒恩对象事件拒绝空实例");
        That(!ledger.TryCommit(key with { Dungeon = 219 }, AntonRaidEventKind.ObjectDestroyed),
            "安徒恩对象事件拒绝未列入合同的地下城");
        That(ledger.Activate(key), "当前 Run generation 进入事件账本");
        That(ledger.TryCommit(key, AntonRaidEventKind.ObjectDestroyed), "首个对象事件进入有界账本");
        That(ledger.Contains(key, AntonRaidEventKind.ObjectDestroyed), "已提交对象事件可回读");
        That(!ledger.TryCommit(key, AntonRaidEventKind.ObjectDestroyed), "同一事件重复提交幂等拒绝");
        That(!ledger.TryCommit(key with { RunGeneration = 3 }, AntonRaidEventKind.ObjectDestroyed),
            "旧 generation 事件不能冒充当前 Run");
        That(ledger.TryCommit(key with { EventId = 18 }, AntonRaidEventKind.HatcheryOpened),
            "不同事件号和类型独立记账");
        That(!ledger.TryCommit(key with { EventId = 19 }, AntonRaidEventKind.EnergySettled),
            "达到容量后拒绝无界增长");
        That(ledger.RemoveRun(run) == 2 && ledger.Count == 0, "Run 回收时一并清理事件账本");
    }
}
