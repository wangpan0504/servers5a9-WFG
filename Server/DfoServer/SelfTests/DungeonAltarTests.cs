using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using PvfLib;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DungeonAltarTests
{
    public static void Run()
    {
        int before = Count;
        DungeonEnterCountTests.RunCore();
        That(DungeonAltarRewardPolicy.GoldAmount(new(70, 10000, 20), 7f, 1.02f, 0.1f, 0) == 26985 &&
            DungeonAltarRewardPolicy.GoldAmount(new(70, 10000, 20), 7f, 1.02f, 0.1f, -20) == 21588 &&
            DungeonAltarRewardPolicy.GoldAmount(new(70, 10000, 20), 7f, 1.02f, 0.1f, 20) == 32382,
            "祭坛金币沿两次截断、两次倍率、波动与专用权重计算");
        var ordinary = new ItemTemplate(100, ItemKind.Equipment, 10, 60, 30, 1, 0, 1, 60, "", 1, 0, "fixture") { CreationRate = 5 };
        var pet = ordinary with { Id = 101, Kind = ItemKind.CreatureEquipment, Rarity = 2 };
        var itemPools = DungeonItemDropCatalog.Create([ordinary, pet]);
        That(itemPools.AltarPool(false, 60, 60, 1).TotalWeight == 5 &&
            itemPools.AltarPool(true, 60, 60, 1).TotalWeight == 0 && itemPools.AltarPool(true, 60, 60, 2).Select(0).Id == 101 &&
            itemPools.AltarPool(false, 61, 65, 1).TotalWeight == 0 && itemPools.AltarPool(false, 60, 60, 3).TotalWeight == 0,
            "祭坛类别4只取宠物装备，空等级/稀有度池不改抽其它装备");
        var ranking = DungeonAltarPackets.Ranking(12345, 15, 12000, 15, 15, 98765);
        That(ranking.Length == 24 && BinaryPrimitives.ReadUInt32LittleEndian(ranking.AsSpan(4)) == 15 &&
            BinaryPrimitives.ReadUInt32LittleEndian(ranking.AsSpan(20)) == 98765, "208只发送六项祭坛排名数据");
        var rewards = DungeonAltarPackets.ClearRewards(7, 7, [[(0u, 1200u), (101u, 7u)], [], [], []]);
        That(rewards.Length == 22 && rewards[0] == 7 && rewards[1] == 7 && rewards[2] == 2 &&
            BinaryPrimitives.ReadUInt32LittleEndian(rewards.AsSpan(7)) == 1200 && rewards.AsSpan(19).SequenceEqual(new byte[3]),
            "209保留四个队员计数、金币和物品addInfo，不追加普通结算尾部");
        var resource = new PvfMapAltarWaves(false,
            [new([1001, 0, 300, 200, 800, 7000, 2000, 1, 1, 3]), new([1002, 0, 400, 210, 0, 5000, 4000, 1, 0, 1])],
            [new(0, 0, 1000, 1.5f, 0, 1, 0), new(1, 1, 3000, 2f, 0, 0, 0)]);
        var definition = DungeonAltarDefinition.Read(resource);
        Reject<DungeonObjectKeyOverflowException>(() => DungeonAltarPlan.Create(definition, 12, ushort.MaxValue, 60, true, _ => 0),
            "祭坛尾部容量不足使用专用耗尽异常，使新实例可重试完整编号范围");
        var plan = DungeonAltarPlan.Create(definition, 12, 10, 60, true, _ => 0);
        That(plan.Actors.Length == 7 && plan.NextObjectKey == 18 && plan.Actors.All(actor => actor.ObjectKey != 12),
            "祭坛递增生成数量1+2+3，键分配跳过角色并跨回合保持唯一");
        That(plan.Rounds[0].Batches.Select(row => row.DelayMilliseconds).SequenceEqual(new long[] { 1000, 3000, 5000 }),
            "祭坛首批延迟与后续间隔来自各自字段");
        var state = new DungeonAltarState(plan);
        That(state.TakeDue(100000) is null && !state.WasPublished(10), "加载前不生成，也不接受未发布key");
        That(!state.Begin(100, 1) && state.Begin(100, 0) && !state.Begin(100, 0), "错误难度与重复准备不重置时间线");
        That(state.NextDue == 1100 && state.TakeDue(1099) is null, "提前计时回调不生成");
        var batch = state.TakeDue(1100)!;
        byte[] wire = DungeonAltarPackets.Spawn(batch);
        That(wire.Length == 21 && wire[9] == 0 && wire[10] == 60 &&
            BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(11)) == 1500 &&
            BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(17)) == 800 &&
            BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(19)) == 2000,
            "NOTI210保留rank/level顺序、千分倍率、Z与尾部间隔");
        var dead = batch.Actors.Select(actor => actor.ObjectKey).ToHashSet();
        That(state.WasPublished(10) && !state.WasPublished(11) && !state.Advance(dead, 1100), "杀完当前批次不能跳过尚未生成批次");
        while (state.NextDue is { } due)
            foreach (var actor in state.TakeDue(due)!.Actors) dead.Add(actor.ObjectKey);
        That(state.Advance(dead, 6000) && state.Round == 1 && state.NextDue == 9000 && !state.Completed,
            "无尽清完回合后自动安排下一回合");
        var final = state.TakeDue(9000)!;
        That(final.Interval == ushort.MaxValue && final.Actors.Single().Rank == 3 && !state.CanComplete(dead),
            "终点Boss使用FFFF且未死亡时不能通关");
        That(state.CanComplete(dead, final.Actors[0].ObjectKey), "仅本次最后死亡候选能准备清图");
        dead.Add(final.Actors[0].ObjectKey);
        That(state.Advance(dead, 9100) && state.Completed && state.NextDue is null && !state.Begin(9200, 0), "完成后无重复计时和重开");

        var ultimate = DungeonAltarDefinition.Read(new(true, resource.Monsters,
            [new(0, 1, 5000, 1f, 0, 0, 1), new(1, 1, 5000, 1f, 0, 0, 1), new(1, 1, 5000, 2f, 0, 0, 2)]));
        var choices = new DungeonAltarState(DungeonAltarPlan.Create(ultimate, 1, 100, 70, true, _ => 0));
        That(choices.Begin(0, 1), "极限首回合按A生成");
        var first = choices.TakeDue(5000)!;
        var defeated = first.Actors.Select(actor => actor.ObjectKey).ToHashSet();
        That(choices.Advance(defeated, 5001) && choices.WaitingForDifficulty && choices.TakeDue(999999) is null,
            "极限清回合后等待选择，未选择不生成两个分支");
        That(!choices.Begin(6000, 5) && choices.Begin(6000, 2) && !choices.Begin(6001, 1), "只能选本回合声明的难度且只选一次");
        var selected = choices.TakeDue(11000)!;
        That(selected.Actors.All(actor => actor.Phase.Difficulty == 2 && actor.Kind == 19) &&
            choices.Plan.Actors.Where(actor => actor.Phase.Round == 1 && actor.Phase.Difficulty == 1).All(actor => !choices.WasPublished(actor.ObjectKey)),
            "极限不生成未选难度，也不混入无尽的特殊怪类型");
        That(DungeonAltarRequests.TryParseDeaths([2, 10, 0, 13, 0], out var keys) && keys.SequenceEqual(new ushort[] { 10, 13 }) &&
            !DungeonAltarRequests.TryParseDeaths([2, 10, 0], out _) && !DungeonAltarRequests.TryParseDeaths([1, 10, 0, 0], out _) &&
            !DungeonAltarRequests.TryParseDeaths([2, 10, 0, 10, 0], out _) && !DungeonAltarRequests.TryParseDeaths([1, 0, 0], out _),
            "CMD255严格验证计数、长度、唯一键，不接受伪造的普通39尾");
        var map = new DungeonMap("boss", new(0, 0), 17030, "map/altar-test.map");
        var room = new DungeonRoomDefinition(map, [], [], []) { AltarWaves = resource };
        var actors = DungeonMonsterPlan.FromAltarRoom(room, 12, 10, 60, _ => 0);
        // 部署侧伴侣补丁会把20900行写进祭坛图：祭坛不放行AI，伴侣行必须丢弃，不能因此挡住祭坛准入。
        var companion = new DungeonRoomAiCharacter(DungeonCompanion.WangFuguiId,
            [DungeonCompanion.WangFuguiId, 58, 208, 0, 0, 0], "[character]", "[normal]",
            "aicharacter/priest/heal_soldier/heal_soldier.aic", 95);
        var withCompanion = DungeonMonsterPlan.FromAltarRoom(room with { AiCharacters = [companion] }, 12, 10, 60, _ => 0);
        That(withCompanion.Monsters.Count == actors.Monsters.Count && withCompanion.Monsters.All(row => row.AiCharacter is null),
            "祭坛丢弃部署侧伴侣行，不放行AI也不影响波次演员");
        var alien = companion with { Id = 55430 };
        Reject<InvalidDataException>(() => DungeonMonsterPlan.FromAltarRoom(room with { AiCharacters = [alien] }, 12, 10, 60, _ => 0),
            "祭坛仍拒绝非伴侣AI演员");
        var maze = new DungeonMaze(0, 1, 1, "BB", [map.Position], [map.Position], [map], []);
        var entry = new DungeonEntryPlan(12, 0, 11006, 0, byte.MaxValue, 0, maze, map, map) { BasisLevel = 60 };
        var authority = new NativeDungeonCombatAuthority();
        That(SinglePlayerDungeonRun.TryCreate(entry, 1, 1, new([actors], []), authority, out var run, out _) && authority.BindTo(run!),
            "祭坛计划经过资源复验绑定正式实例");
        run!.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        string fingerprint = new('A', 64);
        That(authority.ValidateAndIssueAltar(run, run.Snapshot.Identity, new(10, fingerprint)).Outcome == DungeonRunOutcome.Rejected &&
            run.Snapshot.LivingMonsters.Count == 0, "正式实例拒绝计时生成前的祭坛死亡报告");
        var running = run.CurrentAltar!;
        That(running.Begin(0, 0), "正式实例房间拥有波次状态");
        for (int round = 0; round < 2; round++)
        {
            while (running.NextDue is { } due)
            {
                var issuedBatch = running.TakeDue(due)!;
                foreach (var actor in issuedBatch.Actors)
                {
                    var fact = authority.ValidateAndIssueAltar(run, run.Snapshot.Identity, new(actor.ObjectKey, fingerprint));
                    That(fact.Death is not null, "已发布的祭坛对象可签发原实例死亡事实");
                    var pending = run.PrepareConfirmedDeath(fact.Death!);
                    That(pending.Pending is not null && run.ConfirmCommittedDeath(pending.Pending).Outcome == DungeonRunOutcome.Accepted,
                        "祭坛死亡通过正式准备与确认链");
                    That(run.PrepareConfirmedDeath(fact.Death!).Outcome == DungeonRunOutcome.Duplicate,
                        "重复祭坛报告复用原死亡，不推进第二次");
                }
            }
            That(run.AdvanceAltar(10000 + round * 10000), "本回合全部死亡后才能推进");
        }
        That(run.Phase == SinglePlayerDungeonPhase.Settlement && run.ClearFact is not null && running.Completed &&
            run.Snapshot.LivingMonsters.Count == 0, "最后实际生成对象死亡才产生正式通关事实");
        Console.WriteLine($"PASS {Count - before} 祭坛时间线、难度分支及协议断言（合成）");
    }
}
