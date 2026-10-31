using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class FiendwarEncounterTests
{
    internal static void RunCore()
    {
        int before = Count;
        var reset = new DateTimeOffset(2026, 11, 3, 6, 0, 0, TimeSpan.FromHours(8));
        That(FiendwarRewards.Cycle.Key(reset.AddTicks(-1)) == 20261027 &&
            FiendwarRewards.Cycle.Key(reset) == 20261103 &&
            FiendwarRewards.Cycle.Key(reset.ToUniversalTime()) == 20261103 &&
            FiendwarRewards.Cycle.Key(reset.AddDays(2)) == 20261103,
            "漩涡独立周二06:00周切，跨月与UTC同一瞬间一致，周四不重置");
        foreach (int count in new[] { 1, 2, 3, 4 })
        {
            var scale = new FiendwarCombatScale(Enumerable.Range(1, count).Select(n => (ushort)n));
            That(scale.ScaleHp(1000) == (ulong)(count * 250) && scale.ScaleAttack(100) == (ulong)(40 + count * 15),
                "统一参与人数曲线：" + count);
            That(scale.ScaleHp(ulong.MaxValue) > 0 && scale.ScaleAttack(ulong.MaxValue) > 0 && scale.ScaleHp(0) == 0,
                "缩放大整数不溢出、零值不凭空补伤害");
        }
        Reject<ArgumentException>(() => new FiendwarCombatScale([]), "零参与者不得创建遭遇");
        Reject<ArgumentException>(() => new FiendwarCombatScale([1, 1]), "重复身份不能虚增倍率");
        Reject<ArgumentException>(() => new FiendwarCombatScale([1, 2, 3, 4, 5]), "普通遭遇最多四人");
        var clock = DateTimeOffset.FromUnixTimeSeconds(1793410200);
        var instance = Guid.NewGuid();
        var identity = new DungeonRunIdentity(1, 0, 0, instance, 2);
        var encounter = FiendwarEncounter.Create(15, 291100349, 0, clock.AddMinutes(8), [identity], new HashSet<int> { 99 });
        var clear = new DungeonClearFact(identity, 291100349, [Guid.NewGuid()]) { ClearMap = new("boss", new(2, 0), 99, "map/boss.map") };
        That(encounter.Complete(clear, clock) is { AllCompleted: true }, "正式终图死亡事实完成遭遇");
        That(encounter.Complete(clear with { Identity = identity with { Epoch = 1 } }, clock) is null &&
            encounter.Complete(clear with { Identity = identity with { InstanceId = Guid.NewGuid() } }, clock) is null &&
            encounter.Complete(clear with { ClearMap = new("map", new(0, 0), 98, "map/other.map") }, clock) is null &&
            encounter.Complete(clear, clock.AddMinutes(8)) is null, "旧代、旧实例、非终图与过期事实均拒绝");
        var completed = encounter.Complete(clear, clock)!;
        That(completed.Complete(clear, clock)!.Completed.Count == 1 && ReferenceEquals(completed.Scale, encounter.Scale),
            "重复clear不叠加贡献，不重算缩放");
        var shared = new FiendwarCombatScale([1], sharedBoss: true);
        var round = FiendwarBossRound.Create(0, shared, 100, 100, [FiendwarElement.Fire, FiendwarElement.Ice, FiendwarElement.Wind, FiendwarElement.Attribute]);
        That(round.CompleteMechanism(FiendwarWorld.Black, FiendwarElement.Ice, clock, TimeSpan.FromSeconds(15), 0) is null,
            "不能冒领对侧分身成果");
        for (int i = 0; i < 3; i++)
            round = round.CompleteMechanism(FiendwarWorld.Black, round.Black.Element!.Value, clock.AddSeconds(i), TimeSpan.FromSeconds(15), 0)!;
        That(round.Black.Stage == FiendwarWorldStage.DamageWindow && round.White.Stage == FiendwarWorldStage.Mechanism &&
            round.RemainingElements.SetEquals([FiendwarElement.Ice]), "本侧连做共享池分身后独立输出，对侧保留真实目标");
        round = round.AddDamage(FiendwarWorld.Black, 80, clock.AddSeconds(5))!;
        That(round.Save(FiendwarWorld.Black, clock.AddSeconds(16)) is null && round.Join() is null, "不提前封窗或凭一侧成果过关");
        round = round.Save(FiendwarWorld.Black, clock.AddSeconds(17))!;
        var saved = round.Black;
        round = round.CompleteMechanism(FiendwarWorld.White, FiendwarElement.Ice, clock.AddSeconds(60), TimeSpan.FromSeconds(15), 0)!;
        That(round.Black == saved && round.White.WindowEnd == clock.AddSeconds(75) && round.RemainingElements.IsEmpty,
            "经战场接力后保留前侧成果，另一侧获得完整原窗口");
        round = round.AddDamage(FiendwarWorld.White, 40, clock.AddSeconds(61))!;
        That(round.AddDamage(FiendwarWorld.Black, 10, clock.AddSeconds(61)) is null, "已封存侧不得追加伤害");
        round = round.Save(FiendwarWorld.White, clock.AddSeconds(75))!.Join()!;
        That(round.RemainingHp == 40 && round.Join()!.RemainingHp == 40 && ReferenceEquals(round.Scale, shared),
            "原剩余血量20与60均摊40，重复合流不重复扣血，倍率保持");
        var overflow = round with { Joined = false, RemainingHp = ulong.MaxValue,
            Black = saved with { Damage = ulong.MaxValue }, White = round.White with { Damage = ulong.MaxValue },
            RemainingElements = ImmutableHashSet<FiendwarElement>.Empty };
        That(overflow.Join()!.RemainingHp == 0, "极大生命池均摊不溢出回血");
        Console.WriteLine($"PASS {Count - before} 漩涡遭遇与双世界接力断言");
    }
}
