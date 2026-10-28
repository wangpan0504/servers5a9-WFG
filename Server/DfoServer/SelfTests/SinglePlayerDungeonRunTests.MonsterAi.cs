using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class SinglePlayerDungeonRunTests
{
    static void VerifyMonsterAi()
    {
        int before = Count;
        var settings = new DungeonMonsterAiSettings();
        settings.Replace([
            new(51, null, new() { Value = 90 }, new() { Multiplier = 1.5f }, new() { Multiplier = 1.2f }, new() { Value = 800 }),
            new(51, 76327, Sight: new() { Value = 600 }, AttackSpeed: new())
        ], true);
        // 交接样例：地图规则逐字段覆盖副本默认，空对象明确恢复原生。
        That(settings.Current.Resolve(51, 76327) == new MonsterAiProfile(new(MonsterAiMode.Fixed, 90), new(MonsterAiMode.Fixed, 600),
            new(MonsterAiMode.Multiplier, 1.2f), MonsterAiValue.Native), "地图规则覆盖明确填写的字段，空对象阻止继承");
        That(settings.Current.Resolve(51, 1) == new MonsterAiProfile(new(MonsterAiMode.Fixed, 90), new(MonsterAiMode.Multiplier, 1.5f),
            new(MonsterAiMode.Multiplier, 1.2f), new(MonsterAiMode.Fixed, 800)), "副本默认规则作用于未单独配置的地图");
        That(settings.Current.Resolve(52, 76327) == MonsterAiProfile.Native, "未命中的副本四项全部原生");
        settings.Replace([new(51, null, Warlike: new(), Sight: new() { Value = 0 })], true);
        That(settings.Current.Resolve(51, 7) == new MonsterAiProfile(MonsterAiValue.Native, new(MonsterAiMode.Fixed, 0),
            MonsterAiValue.Native, MonsterAiValue.Native), "配置0是有效固定值，不能当成未配置");
        settings.Replace([new(51, 76327, Warlike: new() { Multiplier = 0 })], true);
        That(settings.Current.Resolve(51, 76327) == new MonsterAiProfile(new(MonsterAiMode.Multiplier, 0), MonsterAiValue.Native,
            MonsterAiValue.Native, MonsterAiValue.Native), "只填地图规则时其余字段不继承其他副本默认");

        foreach (var invalid in new MonsterAiRule[][]
        {
            [new(0, null)], [new(3, 0)], [new(3, null, new() { Value = -1 })], [new(3, null, new() { Multiplier = -0.5f })],
            [new(3, null, new() { Value = 1, Multiplier = 2 })], [new(3, null, new() { Value = 2147483647f })],
            [new(3, null, AttackSpeed: new() { Multiplier = float.PositiveInfinity })],
            [new(3, null), new(3, null)], [new(3, 5), new(3, 5)]
        })
        {
            var previous = settings.Current;
            Reject<InvalidDataException>(() => settings.Replace(invalid, true), "非法AI配置整批拒绝");
            That(ReferenceEquals(previous, settings.Current), "AI重载失败保持完整旧快照");
        }
        settings.Replace([new(3, null, new() { Value = 2147483520f })], true);
        That(settings.Current.Resolve(3, 1).Warlike == new MonsterAiValue(MonsterAiMode.Fixed, 2147483520f),
            "float32可精确表示的最大合法固定值通过");

        // 交接样例的完整56字节与16字节包头的线上字节。
        byte[] golden = Convert.FromHexString("4D4149310100000033000000272A0100" +
            "4E61BC0003000000010000000000B442020000000000C03F020000009A99993F0100000000004844");
        var profile = new MonsterAiProfile(new(MonsterAiMode.Fixed, 90), new(MonsterAiMode.Multiplier, 1.5f),
            new(MonsterAiMode.Multiplier, 1.2f), new(MonsterAiMode.Fixed, 800));
        var body = MonsterAiPackets.Encode(1, 51, 76327, 12345678, new(3, 0), profile);
        That(body.SequenceEqual(golden) && body.Length == MonsterAiPackets.BodyLength, "私有包与交接样例逐字节一致");
        var frame = GameFrame.Build(0, MonsterAiPackets.Opcode, 1, body);
        That(frame.Length == 72 && frame[0] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)) == 0x7FF0 &&
            BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(3)) == 72 && frame.AsSpan(16).SequenceEqual(golden),
            "私有包沿用class0的16字节包头，整帧72字节");
        var native = MonsterAiPackets.Encode(2, 51, 76327, 0, new(0, 0), MonsterAiProfile.Native);
        That(BinaryPrimitives.ReadUInt32LittleEndian(native.AsSpan(16)) == 0 && native[20] == 0 && native[21] == 0 &&
            native[22] == 0 && native[23] == 0 && native.AsSpan(24).ToArray().All(value => value == 0),
            "中性Profile的序号、种子与零坐标合法且四项mode0不带数字");
        foreach (var encode in new Action[]
        {
            () => MonsterAiPackets.Encode(0, 51, 76327, 1, new(0, 0), profile),
            () => MonsterAiPackets.Encode(1, 0, 76327, 1, new(0, 0), profile),
            () => MonsterAiPackets.Encode(1, 51, 0, 1, new(0, 0), profile)
        })
            Reject<ArgumentOutOfRangeException>(encode, "序号/副本/地图必须非零");
        // 坐标沿用原生29的checked((byte))惯例：越界由溢出异常拒绝，不做静默截断。
        foreach (var encode in new Action[]
        {
            () => MonsterAiPackets.Encode(1, 51, 76327, 1, new(256, 0), profile),
            () => MonsterAiPackets.Encode(1, 51, 76327, 1, new(0, -1), profile)
        })
            Reject<OverflowException>(encode, "坐标必须落在u8范围");

        var start = new DungeonMap("map", new(0, 0), 1, "map/ai-start.map");
        var middle = new DungeonMap("map", new(1, 0), 2, "map/ai-middle.map");
        var boss = new DungeonMap("boss", new(2, 0), 3, "map/ai-boss.map");
        var maze = new DungeonMaze(0, 3, 1, "monster-ai", [start.Position], [boss.Position], [start, middle, boss], []);
        var entry = new DungeonEntryPlan(99, 17, 3, 3145, 0, 0, maze, start, boss);
        var route = new DungeonFixedRoute([Plan(start, 10), Plan(middle, 11), BossPlan(boss, 12)],
            [new(start.Position, middle.Position), new(middle.Position, boss.Position)]);
        var authority = new FixtureCombatAuthority();
        settings.Replace([new(3, null, Warlike: new() { Value = 90 })], true);
        var run = Create();
        That(run.CurrentMonsterAi is { Warlike: { Mode: MonsterAiMode.Fixed, Number: 90 } }, "起始房间在首次进入时冻结AI参数");
        settings.Replace([new(3, null, Warlike: new() { Value = 70 })], true);
        That(Create(run.MonsterAiRooms).CurrentMonsterAi == run.CurrentMonsterAi, "组队角色投影复用同一房间访问快照");
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 10, 1));
        settings.Replace([new(3, null, Warlike: new() { Multiplier = 1.5f })], true);
        That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), middle.Position).Outcome == DungeonRunOutcome.Accepted &&
            run.CurrentMonsterAi is { Warlike: { Mode: MonsterAiMode.Multiplier, Number: 1.5f } }, "尚未进入的房间使用最新AI规则");
        run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid());
        run.AcceptConfirmedDeath(authority.Death(run.Snapshot.Identity, 11, 2));
        settings.Replace([new(3, null, Warlike: new() { Value = 50 })], true);
        That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), start.Position).Outcome == DungeonRunOutcome.Accepted &&
            run.CurrentMonsterAi is { Warlike: { Mode: MonsterAiMode.Fixed, Number: 50 } }, "回访房间按当时配置重新解析，不沿用首次冻结");
        settings.Replace([new(3, null, Warlike: new() { Value = 90 })], false);
        That(Create().CurrentMonsterAi is null, "功能关闭时不冻结也不发送AI参数");
        var disabled = Create();
        settings.Replace([new(3, null, Warlike: new() { Value = 90 })], true);
        That(disabled.CurrentMonsterAi is null, "已安装房间的关闭状态不受热改影响，只有新房间重新解析");
        VerifyMonsterAiFile();
        Console.WriteLine($"PASS monster ai: {Count-before} checks");

        SinglePlayerDungeonRun Create(DungeonMonsterAiRooms? shared = null)
        {
            That(SinglePlayerDungeonRun.TryCreate(entry, 1, 100, route, authority, out var created, out var reason,
                monsterAi: settings, sharedMonsterAi: shared), "AI配置不影响资源路线准入:" + reason);
            return created!;
        }
    }

    static void VerifyMonsterAiFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "monster-ai-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var config = new MonsterAiConfiguration(directory);
            config.Reload();
            That(!config.Settings.Current.Enabled && config.Settings.Current.Rules.Count == 0 && config.Answer("other-command") is null,
                "缺省文件不开启AI私有包，不拦截其他GM命令");
            string path = Path.Combine(directory, "monster-ai.json");
            File.WriteAllText(path, """
                {"Enabled":true,"Rules":[{"DungeonId":51,"Warlike":{"Value":90},"Sight":{"Multiplier":1.5},"MoveSpeed":{"Multiplier":1.2},"AttackSpeed":{"Value":800}},
                {"DungeonId":51,"MapId":76327,"Sight":{"Value":600},"AttackSpeed":{}}]}
                """);
            That(config.Answer("monster-ai reload")!.StartsWith("OK ") && config.Settings.Current.Enabled &&
                config.Settings.Current.Resolve(51, 76327) == new MonsterAiProfile(new(MonsterAiMode.Fixed, 90), new(MonsterAiMode.Fixed, 600),
                    new(MonsterAiMode.Multiplier, 1.2f), MonsterAiValue.Native), "GM重载真实文件并发布快照");
            var previous = config.Settings.Current;
            File.WriteAllText(path, "{\"Enabled\":true,\"Rules\":[],\"Typo\":1}");
            That(config.Answer("monster-ai reload")!.StartsWith("ERR ") && ReferenceEquals(config.Settings.Current, previous),
                "未知字段拒绝且不清空旧规则");
            File.WriteAllText(path, "{\"Enabled\":true,\"Rules\":[{\"DungeonId\":51,\"Warlike\":{\"Value\":1,\"Multiplier\":2}}]}");
            That(config.Answer("monster-ai reload")!.StartsWith("ERR ") && ReferenceEquals(config.Settings.Current, previous),
                "同时设置固定值与倍率的规则整批拒绝");
            That(config.Answer("monster-ai status")!.StartsWith("OK ") && config.Answer("monster-ai reload extra")!.StartsWith("ERR "),
                "状态查询和严格命令参数");
            File.Delete(path);
            That(config.Answer("monster-ai reload")!.StartsWith("OK ") && !config.Settings.Current.Enabled,
                "删除配置后关闭私有包并回到原生参数");
        }
        finally { Directory.Delete(directory, true); }
    }
}
