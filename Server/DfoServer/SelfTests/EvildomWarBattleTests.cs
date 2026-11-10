using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Dungeons;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EvildomWarBattleTests
{
    public static void RunCore()
    {
        int before = Count;
        var now = DateTimeOffset.Parse("2026-10-27T00:00:00Z");
        ImmutableArray<EvildomWarDungeonState> rows = [new(5426, 0, 0, 0), new(5425, 0, 1, 0),
            new(5424, 0, 2, 0), new(5423, 0, 3, 0), new(5422, 1, 0, 3)];
        foreach (int members in new[] { 1, 4 })
        {
            var battle = new EvildomWarBattle(members == 1 ? (byte)2 : (byte)1, now.AddMinutes(50), rows);
            var initial = battle.View;
            That(battle.Enter(5422, Runs(members), now) is null && ReferenceEquals(initial, battle.View), "王座封锁时不能预留Run");
            for (int i = 0; i < 4; i++)
            {
                uint dungeon = checked((uint)rows[i].Id);
                var runs = Runs(members);
                var entered = battle.Enter(dungeon, runs, now)!;
                That(entered.Dungeons[i].State == 1 && entered.Revision == initial.Revision + 1 &&
                    initial.Dungeons[i].State == 0, "进入后才发布战斗中，旧视图保持不可变");
                var facts = runs.Select(run => new DungeonClearFact(run with { Epoch = run.Epoch + 20 }, dungeon, [Guid.NewGuid()])).ToArray();
                That(battle.Complete(facts[..^1], now) is null && ReferenceEquals(entered, battle.View), "缺少任一成员已确认事实不推进");
                That(battle.Complete(facts.Select(fact => fact with { Identity = fact.Identity with { InstanceId = Guid.NewGuid() } }).ToArray(), now) is null,
                    "旧场或其他实例事实不能推进本场");
                var completed = battle.Complete(facts, now)!;
                That(completed.Dungeons[i].State == 2 && completed.Revision == entered.Revision + 1 &&
                    completed.Dungeons[4].State == (i >= 1 ? 0 : 3), "钥匙与魔法阵两层解除即解锁王座，剑盾不是必清门槛");
                That(battle.Complete(facts, now) is null && ReferenceEquals(completed, battle.View) &&
                    battle.Release(runs[0]) is null, "重复清图或原Run退出不重复推进或重置完成图");
                initial = completed;
            }
            var throneRuns = Runs(members);
            That(battle.Enter(5422, throneRuns, now) is not null && battle.Complete(throneRuns.Select(run =>
                new DungeonClearFact(run, 5422, [Guid.NewGuid()])).ToArray(), now) is { Completed: true }, "王座正式清图后战役完成");
        }
        foreach (int first in new[] { 0, 1, 2, 3 })
        {
            var optional = new EvildomWarBattle(0, now.AddMinutes(50), rows);
            foreach (int index in new[] { first, 0, 1 }.Distinct())
            {
                var run = Runs(1);
                uint dungeon = (uint)rows[index].Id;
                optional.Enter(dungeon, run, now);
                optional.Complete([new(run[0], dungeon, [Guid.NewGuid()])], now, (_, checkpoint) => checkpoint.Validate());
                That(optional.View.Dungeons[^1].State == (optional.View.SealsCleared ? 0 : 3),
                    "任意前置顺序只由两张结界属性解锁，单张剑盾不能解除结界");
            }
            var saved = new EvildomWarCheckpoint(optional.View);
            saved.Validate();
            var restored = EvildomWarBattle.Restore(saved);
            var throne = Runs(1);
            That(restored.Enter(5422, throne, now) is not null && restored.Complete([new(throne[0], 5422, [Guid.NewGuid()])],
                now, (_, checkpoint) => checkpoint.Validate()) is { Completed: true },
                "两层解除后恢复可直接打王座，未打剑盾不阻断真实完成检查点");
        }
        var oldView = new EvildomWarView(Guid.NewGuid(), 7, 0, now.AddMinutes(50),
            rows.Select(row => row.Kind == 0 && row.Attribute < 2 ? row with { State = 2 } : row).ToImmutableArray());
        var legacy = EvildomWarBattle.Restore(new(oldView));
        Reject<IOException>(() => legacy.RecoverCheckpoint((_, _) => throw new IOException("checkpoint failure")),
            "旧四图门槛检查点修正失败保持内存原版本");
        That(ReferenceEquals(legacy.View, oldView), "恢复开锁SQL失败仍可按原版本重试");
        legacy.RecoverCheckpoint((beforeView, after) => { That(ReferenceEquals(beforeView, oldView), "旧版本精确提交"); after.Validate(); });
        That(legacy.View.Revision == 8 && legacy.View.Dungeons[^1].State == 0, "旧检查点已清双结界时持久修正开锁，不重新扣费或清图");
        var invalid = legacy.View with { Dungeons = legacy.View.Dungeons.Select(row => row.Attribute == 1 && row.Kind == 0
            ? row with { State = 0 } : row).ToImmutableArray() };
        Reject<InvalidDataException>(() => new EvildomWarCheckpoint(invalid).Validate(), "拒绝结界未解除却开放王座的检查点");
        var retry = new EvildomWarBattle(1, now.AddMinutes(50), rows);
        var beforeReserve = retry.View;
        using (var cancelled = retry.Reserve(5426, Runs(), now))
            That(cancelled is not null && ReferenceEquals(beforeReserve, retry.View) && retry.Reserve(5426, Runs(), now) is null,
                "SQL前只预留原Run，不提前推进，第二个入场不能覆盖预留");
        That(ReferenceEquals(beforeReserve, retry.View), "入场SQL失败释放预留后不改变视图");
        var oldRuns = Runs();
        retry.Enter(5426, oldRuns, now);
        foreach (var run in oldRuns[..^1]) That(retry.Release(run) is null, "仍有原成员在Run内时不重开副本");
        That(retry.Release(oldRuns[^1])?.Dungeons[0].State == 0, "全员确认退场后才恢复未完成图可进入");
        var newRuns = Runs();
        var current = retry.Enter(5426, newRuns, now)!;
        That(retry.Release(oldRuns[0]) is null && retry.Complete(oldRuns.Select(run =>
            new DungeonClearFact(run, 5426, [])).ToArray(), now) is null && ReferenceEquals(current, retry.View), "旧Run不能释放或完成重进的新Run");
        That(retry.Complete(newRuns.Select(run => new DungeonClearFact(run, 5426, [])).ToArray(), now.AddMinutes(50)) is null &&
            new EvildomWarBattle(2, now, rows).Enter(5426, newRuns, now) is null, "到期战役不接受进图或清图事实");
        var surviving = new EvildomWarBattle(1, now.AddMinutes(50), rows);
        var originalMembers = Runs();
        surviving.Enter(5426, originalMembers, now);
        surviving.Release(originalMembers[^1]);
        That(surviving.Complete(originalMembers.Select(run => new DungeonClearFact(run, 5426, [Guid.NewGuid()])).ToArray(), now) is null,
            "已离线成员的迟到事实不混入幸存队员通关");
        That(surviving.Complete(originalMembers[..^1].Select(run => new DungeonClearFact(run, 5426, [Guid.NewGuid()])).ToArray(), now)
            ?.Dungeons[0].State == 2, "离线成员释放原Run后，幸存队员全部真实事实仍能推进战役");
        ResultTime();
        Console.WriteLine($"PASS evildom war battle {Count - before}");

        void ResultTime()
        {
            foreach (byte mode in new byte[] { 0, 1, 2 })
            foreach (uint elapsed in new uint[] { 0, 256780 })
            {
                var battle = new EvildomWarBattle(mode, now.AddMinutes(50), rows, startedAt: now);
                int members = mode == 2 ? 1 : 4;
                foreach (var row in rows.Take(2))
                {
                    var runs = Runs(members);
                    battle.Enter((uint)row.Id, runs, now);
                    battle.Complete(runs.Select(run => new DungeonClearFact(run, (uint)row.Id, [Guid.NewGuid()])).ToArray(), now);
                }
                var throne = Runs(members);
                battle.Enter(5422, throne, now);
                var facts = throne.Select(run => new DungeonClearFact(run, 5422, [Guid.NewGuid()])).ToArray();
                var completedAt = now.AddMilliseconds(elapsed);
                var fighting = battle.View;
                if (mode == 0 && elapsed != 0)
                {
                    Reject<IOException>(() => battle.Complete(facts, completedAt, (_, _) => throw new IOException("time checkpoint failure")),
                        "用时随原完成检查点提交，SQL失败不能提前安装完成时间");
                    That(ReferenceEquals(battle.View, fighting) && battle.Completion is null,
                        "完成持久化失败保留原活动Run、起点及未完成状态");
                }
                EvildomWarCheckpoint? committed = null;
                battle.Complete(facts, completedAt, (_, snapshot) => { snapshot.Validate(); committed = snapshot; });
                That(battle.Completion is { } completion && completion.Created == completedAt &&
                    completion.ElapsedMilliseconds == elapsed && completion.Facts.Length == members,
                    "三模式均从开战到真实全员终点冻结毫秒，零时长及256780毫秒不按整秒取整");
                var restored = EvildomWarBattle.Restore(JsonSerializer.Deserialize<EvildomWarCheckpoint>(JsonSerializer.Serialize(committed))!);
                var restoredCompletion = restored.Completion!;
                That(restored.View.StartedAt == now && restoredCompletion.ElapsedMilliseconds == elapsed &&
                    restored.Complete(facts, completedAt.AddMinutes(1)) is null &&
                    ReferenceEquals(restored.Completion, restoredCompletion) && restoredCompletion.Created == completedAt,
                    "检查点JSON恢复及迟到重复终末保留原起点、完成时刻和用时，不重新计时");
                if (mode != 0 || elapsed == 0) continue;
                var checkpoint = committed!;
                Reject<InvalidDataException>(() => (checkpoint with { Completion = checkpoint.Completion! with { ElapsedMilliseconds = elapsed + 1 } }).Validate(),
                    "完成用时与冻结起止不符时拒绝，不能以客户端限幅掩盖坏数据");
                Reject<InvalidDataException>(() => (checkpoint with { View = checkpoint.View with { StartedAt = completedAt.AddTicks(1) } }).Validate(),
                    "完成早于开战时拒绝，即使差值不足一毫秒也不能夹零");
                Reject<InvalidDataException>(() => (checkpoint with { View = checkpoint.View with { Deadline = completedAt } }).Validate(),
                    "完成必须严格早于原截止");
                Reject<InvalidDataException>(() => (checkpoint with
                {
                    View = checkpoint.View with { Deadline = now.AddDays(100) },
                    Completion = checkpoint.Completion! with { Created = now.AddMilliseconds((long)uint.MaxValue + 1), ElapsedMilliseconds = 0 }
                }).Validate(), "超过u32毫秒时拒绝，不截断或夹到展示上限伪造合法用时");
                var legacyJson = JsonSerializer.SerializeToNode(checkpoint)!;
                legacyJson["View"]!.AsObject().Remove("StartedAt");
                legacyJson["Completion"]!.AsObject().Remove("ElapsedMilliseconds");
                var legacy = JsonSerializer.Deserialize<EvildomWarCheckpoint>(legacyJson.ToJsonString())!;
                legacy.Validate();
                var oldBattle = EvildomWarBattle.Restore(legacy);
                var oldCompletion = oldBattle.Completion!;
                That(oldBattle.View.StartedAt is null && oldCompletion.ElapsedMilliseconds == 0 &&
                    oldBattle.Complete(facts, completedAt.AddMinutes(1)) is null && ReferenceEquals(oldBattle.Completion, oldCompletion),
                    "旧JSON没有起点和用时仍可恢复，保持未知0而不从当前资源倒算");
            }
        }

        DungeonRunIdentity[] Runs(int count = 4)
        {
            var instance = Guid.NewGuid();
            return Enumerable.Range(1, count).Select(id => new DungeonRunIdentity((ushort)id, 0, 1, instance, 1)).ToArray();
        }
    }
}
