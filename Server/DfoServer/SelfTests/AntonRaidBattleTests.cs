using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AntonRaidBattleTests
{
    // 只用于隔离状态机/协议的合成阶段；真实安徒恩资源仍从共享PVF独立验证。
    internal static RaidScript Script(params int[] dungeons) => new("fixture/raid.etc", new(19, 2), null, null,
        ImmutableDictionary<string, int>.Empty.Add("[hp]", 7),
        dungeons.Select((id, phase) => new RaidPhase("fixture" + phase,
            [new(id, RaidDungeonState.Open, "[hp]", 1, 1)],
            [new([new("[on change dungeon state]", 0, id, null, null, null, []),
                    new("[check dungeon state]", 1, id, null, RaidDungeonState.Clear, null, [])],
                [new("[clear phase]", 2, null, null, null, null, [])])], [])).ToImmutableArray(), [], []);

    public static void RunCore()
    {
        int before = Count;
        Bytes(RaidCombatPackets.Dungeons(0, [(210, RaidDungeonState.Open), (218, RaidDungeonState.Hide)], 221),
            [0, 2, 210, 0, 0, 0, 0, 218, 0, 0, 0, 2, 221, 0, 0, 0], "585独立字节预期覆盖6+5N、状态与感染索引");
        Bytes(RaidCombatPackets.DungeonState(210, 0, RaidDungeonState.Clear), [210, 0, 0, 0, 0, 3], "586末字节才是新状态");
        Reject<InvalidDataException>(() => RaidCombatPackets.Dungeons(0, [(210, RaidDungeonState.Open), (210, RaidDungeonState.Hide)], 0),
            "585拒绝重复键，避免客户端顺序覆盖");
        var battle = new AntonRaidBattle(Script(10, 20));
        var identity = new DungeonRunIdentity(1, 0, 1, Guid.NewGuid(), 1);
        var map = new DungeonMap("boss", new(0, 0), 77, "fixture/boss.map");
        var fact = new DungeonClearFact(identity, 10, [Guid.NewGuid()]) { ClearMap = map };
        That(battle.BindDungeon(identity, 10), "阶段接收已安装的正式副本身份");
        int completed = 0;
        Parallel.For(0, 8, _ => { if (battle.CompleteDungeon(fact) is not null) Interlocked.Increment(ref completed); });
        That(completed == 1 && battle.View is { State: AntonRaidBattleState.Reward, Phase: 0, Revision: 1 },
            "同一已提交清图并发只推进一次到阶段奖励");
        Bytes(RaidCombatPackets.State(battle.View), [3, 0], "587按已证u8/i8发出奖励阶段");
        var first = battle.View;
        That(battle.AdvanceAfterRewards(first.Instance, first.Revision)?.After is { Phase: 1, State: AntonRaidBattleState.Fighting },
            "阶段奖励完成后初始化下一阶段");
        var next = identity with { InstanceId = Guid.NewGuid(), TaskAttempt = 2 };
        That(battle.BindDungeon(next, 20), "下一阶段绑定新的正式副本实例");
        That(battle.CompleteDungeon(fact) is null && battle.CompleteDungeon(fact with { Identity = next, DungeonId = 20 }) is not null,
            "上一阶段迟到清图不推进，新阶段真实身份推进");
        var final = battle.View;
        That(battle.AdvanceAfterRewards(final.Instance, final.Revision)?.After.State == AntonRaidBattleState.Completed,
            "末阶段奖励完成后进入已完成状态");
        Bytes(RaidCombatPackets.Result(new(2, 1, 0x12345678, 0x9abc, 4, 7)),
            [2, 1, 0x78, 0x56, 0x34, 0x12, 0xbc, 0x9a, 4, 7], "601独立编码验证读宽与字段顺序");
        Bytes(RaidCombatPackets.Reward(1, [new(0x1234, 5, [new(7, 0x89abcdef, 0x2345, 9)])]),
            [1, 1, 0x34, 0x12, 5, 1, 7, 0xef, 0xcd, 0xab, 0x89, 0x45, 0x23, 9], "600安徒恩专用布局没有普通分支额外u32");
        var additionScript = Script(10);
        additionScript = additionScript with { Symbols = additionScript.Symbols.Add("[signal]", 8).Add("[counter]", 9),
            Phases = additionScript.Phases.SetItem(0, additionScript.Phases[0] with { Rules = [new(
                [new("[on change symbol]", 0, null, "[signal]", null, null, [])],
                [new("[set]", 1, null, "[counter]", null, null, [1]) { Operator = "[+]" }])] }) };
        var arithmetic = new AntonRaidBattle(additionScript);
        arithmetic.SetMechanism(arithmetic.Instance, arithmetic.View.Revision, "[signal]", 1);
        arithmetic.SetMechanism(arithmetic.Instance, arithmetic.View.Revision, "[signal]", 2);
        That(arithmetic.View.Symbols["[counter]"] == 2, "Anton共享算术同样累计加法，不能仍把[+]误当赋值");
        arithmetic.SetMechanism(arithmetic.Instance, arithmetic.View.Revision, "[counter]", int.MaxValue);
        var unchanged = arithmetic.View;
        Reject<OverflowException>(() => arithmetic.SetMechanism(arithmetic.Instance, arithmetic.View.Revision, "[signal]", 3),
            "Anton共享加法也有溢出保护");
        That(ReferenceEquals(unchanged, arithmetic.View), "算术失败不发布部分输入符号");
        Migration(104);
        Migration(107);
        Console.WriteLine($"PASS {Count - before} 攻坚阶段成功、并发清图与结果展示断言");
    }

    static void Migration(int previousVersion)
    {
        string directory = Path.Combine(Path.GetTempPath(), "s5a9-raid-schema-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "game.db");
        try
        {
            _ = new SqliteGameDatabase(path);
            using (var db = Open())
            {
                using var command = db.CreateCommand();
                DatabaseSchemaTests.RewindRaidLifecycle(db);
                command.CommandText = $"DROP TABLE character_raid_reward_receipts; PRAGMA user_version={previousVersion}";
                command.ExecuteNonQuery();
            }
            _ = new SqliteGameDatabase(path);
            using (var db = Open())
            {
                using var command = db.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name IN('character_raid_reward_receipts','character_raid_rewards_week')";
                That(Convert.ToInt64(command.ExecuteScalar()) == 2, $"v{previousVersion}升级原子建立攻坚回执与周索引");
                command.CommandText = "PRAGMA user_version";
                That(Convert.ToInt64(command.ExecuteScalar()) == SqliteMigrations.CurrentVersion, "攻坚迁移完成后发布当前schema版本");
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

        SqliteConnection Open()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); db.Open(); return db;
        }
    }

    internal static void RunResources( RaidScript script)
    {
        var battle = new AntonRaidBattle(script);
        That(battle.DungeonSnapshot(battle.View).Length == 14 && battle.DungeonSnapshot(battle.View)
            .All(row => row.State == (row.Id == 210 ? RaidDungeonState.Open : RaidDungeonState.Hide)),
            "真实初始14图仅210开放，下一阶段7图隐藏，不套下一阶段初始open");
        Clear(210);
        That(battle.View.Dungeons[213] == RaidDungeonState.Open && battle.View.Dungeons[215] == RaidDungeonState.Open &&
            battle.View.Deferred.Values.Any(c => c.Tag == "[reserve dungeon state]"),
            "真实脚本烟雾clear开放双腿，延迟开图保留待调度声明");
        Clear(213);
        That(battle.View.State == AntonRaidBattleState.Fighting, "真实双腿规则等待第二条腿");
        Clear(215);
        That(battle.View.State == AntonRaidBattleState.Reward && battle.View.Symbols["[1phase success]"] == 1,
            "真实51条规则中的双腿clear经成功符号完成第一阶段");
        var first = battle.View; battle.AdvanceAfterRewards(first.Instance, first.Revision);
        That(battle.View is { Phase: 1, State: AntonRaidBattleState.Fighting } && battle.View.Symbols["[black volcano barrier]"] == 10000,
            "真实第二阶段初始化规则安装屏障原始值");
        Clear(219);
        That(battle.View.Dungeons[220] == RaidDungeonState.Open, "真实火山clear规则开放心脏");
        Clear(220);
        That(battle.View.State == AntonRaidBattleState.Reward, "真实心脏clear规则完成第二阶段");

        void Clear(int id)
        {
            var identity = new DungeonRunIdentity(1, 0, 1, Guid.NewGuid(), 1);
            That(battle.BindDungeon(identity, checked((uint)id)), "资源执行夹具绑定当前开放副本");
            var fact = new DungeonClearFact(identity, checked((uint)id), [Guid.NewGuid()])
                { ClearMap = new("boss", new(0, 0), 77, "fixture/resource-clear.map") };
            That(battle.CompleteDungeon(fact) is not null, "资源规则消费已验证清图输入，非自然客户端清图证明");
        }
    }
}
