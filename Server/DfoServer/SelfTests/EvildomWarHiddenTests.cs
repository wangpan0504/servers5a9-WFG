using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EvildomWarHiddenTests
{
    public static void RunCore()
    {
        int before = Count;
        var now = DateTimeOffset.Parse("2026-10-27T00:00:00Z");
        ImmutableArray<EvildomWarDungeonState> stages = [new(11, 0, 0, 0), new(12, 0, 1, 0),
            new(13, 0, 2, 0), new(14, 0, 3, 0), new(15, 1, 0, 3)];
        ImmutableArray<EvildomWarHiddenDungeon> hidden = [new(0, 21, 1000), new(1, 22, 1000), new(2, 23, 1000)];
        foreach (byte mode in new byte[] { 0, 1, 2 })
        {
            foreach (int target in Enumerable.Range(0, 4))
            {
                var layout = stages.Select((row, index) => row.Kind == 0 ? row with
                    { Attribute = checked((byte)((index - target + 4) % 4)) } : row).ToImmutableArray();
                int draws = 0;
                var selected = EvildomWarHiddenPolicy.Select(mode, layout, hidden, maximum =>
                {
                    draws++;
                    return maximum == 10000 ? 999 : target == 3 ? 1 : 0;
                })!;
                That(draws == 2 && selected.StageDungeon == stages[target].Id && selected.Dungeon == 21 + mode,
                    "隐藏只替代钥匙魔法阵，随机属性落在任意前置位置都可触发，模式互不混用");
                var battle = new EvildomWarBattle(mode, now.AddMinutes(50), layout, selected);
                uint stage = selected.StageDungeon;
                var identity = new DungeonRunIdentity(1, 0, 1, Guid.NewGuid(), 1);
                using (var cancelled = battle.Reserve(selected.Dungeon, [identity], now))
                    That(cancelled is not null && battle.View.Dungeons[target].State == 0, "隐藏入场预留失败不提前改变战况");
                That(battle.Enter(selected.Dungeon, [identity], now)?.Dungeons[target].State == 1 &&
                    battle.Complete([new(identity, stage, [Guid.NewGuid()])], now) is null,
                    "隐藏真实Run绑定原面板图，普通图事实不能替代隐藏事实");
                var restoredCheckpoint = JsonSerializer.Deserialize<EvildomWarCheckpoint>(JsonSerializer.Serialize(new EvildomWarCheckpoint(battle.View)))!;
                restoredCheckpoint.Validate();
                var restored = EvildomWarBattle.Restore(restoredCheckpoint);
                restored.RecoverCheckpoint((_, checkpoint) => checkpoint.Validate());
                That(restored.View.Hidden == selected && restored.View.Dungeons[target].State == 0 &&
                    restored.View.ActualDungeon(stage) == selected.Dungeon && restored.View.StageDungeon(selected.Dungeon) == stage,
                    "检查点序列化及进程重建保留同一隐藏路线，未完成图恢复可选");
                battle.Release(identity);
                That(battle.View.Hidden == selected && battle.Enter(selected.Dungeon, [identity], now) is not null,
                    "退出重进不重新抽样");
                var facts = new[] { new DungeonClearFact(identity, selected.Dungeon, [Guid.NewGuid()]) };
                EvildomWarCheckpoint? savedHidden = null;
                That(battle.Complete(facts, now, (_, checkpoint) => { checkpoint.Validate(); savedHidden = checkpoint; })?.Dungeons[target].State == 2 && battle.Complete(facts, now) is null,
                    "隐藏真实事实只完成原前置图一次");
                var completedHidden = savedHidden!.HiddenCompletion!;
                var resumed = EvildomWarBattle.Restore(JsonSerializer.Deserialize<EvildomWarCheckpoint>(JsonSerializer.Serialize(savedHidden))!);
                That(resumed.HiddenCompletion is not null && resumed.HiddenCompletion.Facts[0].DungeonId == selected.Dungeon,
                    "隐藏完成事实随检查点跨进程恢复，不借王座事实代替");
                Reject<InvalidDataException>(() => (savedHidden with { HiddenCompletion = completedHidden with
                    { Facts = [facts[0] with { DungeonId = stage }] } }).Validate(), "隐藏事实不能使用普通前置图ID");
                Reject<InvalidDataException>(() => (savedHidden with { HiddenCompletion = completedHidden with
                    { Facts = [facts[0], facts[0]] } }).Validate(), "隐藏事实拒绝重复角色");
                foreach (var remaining in resumed.View.Dungeons.Where(row => row.Kind == 0 && row.Attribute < 2 && row.State == 0).ToArray())
                {
                    var next = identity with { InstanceId = Guid.NewGuid(), TaskAttempt = 2 };
                    resumed.Enter((uint)remaining.Id, [next], now);
                    resumed.Complete([new(next, (uint)remaining.Id, [Guid.NewGuid()])], now,
                        (_, checkpoint) => { checkpoint.Validate(); That(checkpoint.HiddenCompletion == resumed.HiddenCompletion,
                            "后续结界检查点保留同一隐藏事实"); });
                }
                var throne = identity with { InstanceId = Guid.NewGuid(), TaskAttempt = 3 };
                resumed.Enter(15, [throne], now);
                resumed.Complete([new(throne, 15, [Guid.NewGuid()])], now, (_, checkpoint) =>
                {
                    checkpoint.Validate();
                    That(checkpoint.HiddenCompletion == resumed.HiddenCompletion && checkpoint.Completion!.Facts[0].DungeonId == 15,
                        "王座终点与隐藏终点分别持久保存，不互相覆盖");
                });
            }
            int misses = 0;
            That(EvildomWarHiddenPolicy.Select(mode, stages, hidden, maximum => { misses++; return 1000; }) is null && misses == 1,
                "万分未命中边界不抽前置图");
        }
        foreach (int invalid in new[] { -1, 10000 })
            Reject<InvalidDataException>(() => EvildomWarHiddenPolicy.Select(0, stages, hidden, _ => invalid), "拒绝越界随机源");
        That(EvildomWarHiddenPolicy.Select(0, stages, hidden, maximum => maximum == 10000 ? 0 : 1)?.StageDungeon == 12,
            "第二种结界属性也能触发隐藏，不替代剑盾");
        var oldRoute = new EvildomWarCheckpoint(new(Guid.NewGuid(), 1, 0, now.AddMinutes(50), stages) { Hidden = new(13, 21) });
        oldRoute.Validate();
        That(EvildomWarBattle.Restore(oldRoute).View.Hidden == oldRoute.View.Hidden,
            "旧版已经冻结的剑盾隐藏路线继续保留，不因新规则重新抽样");
        var bad = new EvildomWarView(Guid.NewGuid(), 0, 0, now, stages) { Hidden = new(15, 21) };
        Reject<InvalidDataException>(() => new EvildomWarCheckpoint(bad).Validate(), "隐藏路线不能替代王座");
        Reject<InvalidDataException>(() => new EvildomWarCheckpoint(bad with { Hidden = new(11, 12) }).Validate(), "隐藏副本不能混用面板图");
        Console.WriteLine($"PASS evildom war hidden {Count - before}");
    }
}
