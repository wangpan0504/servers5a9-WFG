using System.Buffers.Binary;
using System.Text.Json;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class SinglePlayerDungeonRunTests
{
    static void VerifyBattleLevels()
    {
        int before = Count;
        var settings = new DungeonBattleLevelSettings();
        settings.Replace([new(3, null, 5, 100), new(3, 2, 0, null)]);
        That(settings.Current.Resolve(3, 1)?.Offset == 5 && settings.Current.Resolve(3, 2)?.Offset == 0 &&
            settings.Current.Resolve(4, 1) is null, "地图零偏移明确覆盖副本规则，其他副本不继承");
        foreach (var invalid in new DungeonBattleLevelRule[][]
        {
            [new(3, null, 1, null)], [new(3, null, 1, 256)], [new(3, 0, 0, null)],
            [new(0, null, 1, 100)], [new(3, null, 1, 100), new(3, null, 2, 100)]
        })
        {
            var previous = settings.Current;
            Reject<InvalidDataException>(() => settings.Replace(invalid), "非法配置整批拒绝");
            That(ReferenceEquals(previous, settings.Current), "重载失败保持完整旧快照");
        }
        That(new DungeonBattleLevelRule(3, null, 0, 100).Apply(255) == 255 &&
            new DungeonBattleLevelRule(3, null, int.MaxValue, 100).Apply(255) == 100 &&
            new DungeonBattleLevelRule(3, null, int.MinValue, 100).Apply(1) == 1,
            "零偏移保留255，极值偏移先提升为有符号长整数再裁剪");

        var start = new DungeonMap("map", new(0, 0), 1, "map/battle-start.map");
        var middle = new DungeonMap("map", new(1, 0), 2, "map/battle-middle.map");
        var boss = new DungeonMap("boss", new(2, 0), 3, "map/battle-boss.map");
        var maze = new DungeonMaze(0, 3, 1, "battle-level", [start.Position], [boss.Position], [start, middle, boss], []);
        var entry = new DungeonEntryPlan(99, 17, 3, 3145, 0, 0, maze, start, boss);
        var original = Plan(start, 10);
        var baseline = DungeonPackets.StartMap(entry, original, 123);
        That(ReferenceEquals(DungeonBattleLevels.Freeze(original, null), original) &&
            ReferenceEquals(DungeonBattleLevels.Freeze(original, new(3, null, 0, null)), original),
            "缺省及零规则返回原计划，不分配演员或改变默认包体");
        var changed = DungeonBattleLevels.Freeze(original, new(3, null, 5, 100));
        var packet = DungeonPackets.StartMap(entry, changed, 123);
        That(packet[31] == 8 && baseline[31] == 3 && changed.Monsters[0].CandidateLevel == 3 &&
            changed.Monsters[0].BattleLevel == 8 && packet.Length == baseline.Length &&
            Enumerable.Range(0, packet.Length).Where(i => packet[i] != baseline[i]).SequenceEqual(new[] { 31 }),
            "普通29仅等级字节变化，奖励原等级和22B创建组等其他字段不变");
        Bytes(DungeonPackets.StartMap(entry, changed, 123, payload: DungeonMapPayload.Cached),
            DungeonPackets.StartMap(entry, original, 123, payload: DungeonMapPayload.Cached), "缓存29不含等级，不随配置变化");
        foreach (byte type in new byte[] { 4, 5, 6, 7, 8 })
        {
            var npc = original with { Monsters = [original.Monsters[0] with { SpawnType = type }] };
            That(DungeonBattleLevels.Freeze(npc, new(3, null, 5, 100)).Monsters[0].BattleLevel == 3,
                "NPC及APC保持原等级");
        }
        That(DungeonBattleLevels.Freeze(original with { Monsters = [original.Monsters[0] with { IsFriendly = true }] },
            new(3, null, 5, 100)).Monsters[0].BattleLevel == 3, "友方演员保持原等级");

        var hell = original.WithHellWaves([new(1, 1, 77, 90, 0, 0, 0), new(1, 1, 88, 90, 5, 0, 0)], 99);
        var hellChanged = DungeonBattleLevels.Freeze(hell, new(3, null, 5, 100));
        var hellBody = DungeonPackets.StartMap(entry, hellChanged, 123);
        var info = DungeonPackets.HellPartyMonsterInfo(hellChanged)!;
        That(hellBody[31+22] == 95 && hellBody[31+44] == 90 &&
            BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(8)) == 95 &&
            BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(16)) == 90 &&
            hellChanged.HellWaves.All(wave => wave.Level == 90) &&
            hellChanged.Monsters.Where(monster => monster.HellWave is not null).All(monster => monster.CandidateLevel == 90),
            "深渊模板和679共用冻结等级，APC与全部原等级保持不变");
        That(hellChanged.Monsters.Select(monster => monster.ObjectKey).SequenceEqual(hell.Monsters.Select(monster => monster.ObjectKey)) &&
            hellChanged.Monsters.Select(monster => monster.HellRewardRolls).SequenceEqual(hell.Monsters.Select(monster => monster.HellRewardRolls)),
            "深渊对象编号与奖励骰次数不变");

        var phases = new DungeonAltarDefinition(false, [new(0, 0, 0, 1, 100, 100, 0, 100, 0, 1000, 100, 1, 0, 1, 1)]);
        var altar = DungeonAltarPlan.Create(phases, 99, 20, 90, false, _ => 0);
        var altarPlan = original with
        {
            Altar = altar,
            Monsters = altar.Actors.Select(actor => new DungeonPlannedMonster(0, actor.ObjectKey, actor.Phase.MonsterId,
                actor.Level, null) { AltarActor = actor, SpawnType = actor.Rank }).ToArray()
        };
        var altarChanged = DungeonBattleLevels.Freeze(altarPlan, new(3, null, 5, 100));
        var batch = altarChanged.Altar!.Rounds[0].Batches[0];
        var altarBody = DungeonAltarPackets.Spawn(batch);
        That(altarBody[10] == 95 && altarChanged.Monsters[0].CandidateLevel == 90 &&
            altarChanged.Monsters[0].AltarActor == batch.Actors[0] && batch.Actors[0].Level == 90 &&
            altarChanged.Altar.Actors[0] == batch.Actors[0], "祭坛210行内8下发战斗等级，计划/批次一致且奖励Level仍90");
        var oldAltarBody = DungeonAltarPackets.Spawn(altar.Rounds[0].Batches[0]);
        That(Enumerable.Range(0, altarBody.Length).Where(i => altarBody[i] != oldAltarBody[i]).SequenceEqual(new[] { 10 }),
            "祭坛210只改等级，不改rank、时序、位置或211");

        var route = new DungeonFixedRoute([Plan(start, 10), Plan(middle, 11), BossPlan(boss, 12)],
            [new(start.Position, middle.Position), new(middle.Position, boss.Position)]);
        var authority = new FixtureCombatAuthority();
        var run = Create();
        That(run.CurrentMonsterPlan.Monsters[0].BattleLevel == 8, "起始房间在首次创建时冻结");
        settings.Replace([new(3, null, 10, 100)]);
        var participant = Create(run.BattleLevels);
        That(participant.CurrentMonsterPlan.Monsters[0].BattleLevel == 8, "组队角色投影在热改后仍使用同一房间快照");
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 10, 1));
        That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), middle.Position).Outcome == DungeonRunOutcome.Accepted &&
            run.CurrentMonsterPlan.Monsters[0].BattleLevel == 13, "尚未生成的房间在首次进入时使用新规则");
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 11, 2));
        settings.Replace([new(3, null, 20, 100)]);
        That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), start.Position).Outcome == DungeonRunOutcome.Accepted &&
            run.CurrentMonsterPlan.Monsters[0].BattleLevel == 8, "回访旧房保留第一次冻结值");
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        run.Move(run.Snapshot.Identity, Guid.NewGuid(), middle.Position);
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), boss.Position).Outcome == DungeonRunOutcome.Accepted &&
            run.CurrentMonsterPlan.Monsters[0].BattleLevel == 23 && run.CurrentMonsterPlan.Monsters[0].CandidateLevel == 3,
            "后续新房用最新规则，奖励原等级保留");
        That(Create().CurrentMonsterPlan.Monsters[0].BattleLevel == 23, "重试新实例重新冻结，不借旧实例房间缓存");
        VerifyBattleLevelFile();
        Console.WriteLine($"PASS monster battle level: {Count-before} checks");

        SinglePlayerDungeonRun Create(DungeonBattleLevelRooms? shared = null)
        {
            That(SinglePlayerDungeonRun.TryCreate(entry, 1, 100, route, authority, out var created, out var reason,
                battleLevels: settings, sharedBattleLevels: shared), "等级规则不影响资源路线准入:" + reason);
            return created!;
        }
    }

    static void VerifyBattleLevelFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "battle-level-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var config = new DungeonBattleLevelConfiguration(directory);
            config.Reload();
            That(config.Settings.Current.Rules.Count == 0 && config.Answer("other-command") is null,
                "缺省文件不开启偏移，不拦截其他GM命令");
            string path = Path.Combine(directory, "monster-battle-level.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { Rules = new[] { new DungeonBattleLevelRule(3, null, 5, 100) } }));
            That(config.Answer("monster-battle-level reload")!.StartsWith("OK ") &&
                config.Settings.Current.Resolve(3, 1)?.Offset == 5, "GM重载真实文件并发布快照");
            var previous = config.Settings.Current;
            File.WriteAllText(path, "{\"Rules\":[],\"Typo\":1}");
            That(config.Answer("monster-battle-level reload")!.StartsWith("ERR ") && ReferenceEquals(config.Settings.Current, previous),
                "未知字段拒绝且不清空旧规则");
            That(config.Answer("monster-battle-level status")!.StartsWith("OK ") &&
                config.Answer("monster-battle-level reload extra")!.StartsWith("ERR "), "状态查询和严格命令参数");
            File.Delete(path);
            Directory.CreateDirectory(path);
            That(config.Answer("monster-battle-level reload")!.StartsWith("ERR ") && ReferenceEquals(config.Settings.Current, previous),
                "配置路径不可读时拒绝重载，不能误判为缺失文件而清空旧规则");
        }
        finally { Directory.Delete(directory, true); }
    }
}
