using DfoServer.Development;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Network.Management;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class GmFatigueBatteryTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(options, root, gm: true, resources: resources);
        var repository = new CharacterRepository(fixture.Database, growthCatalog: resources.Growth);
        ushort first, second, other;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var plan = CharacterCreation.Prepare("BattFirst", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get) with { CompletedQuests = [3145] };
            first = repository.Create(owner, plan).Id;
            second = repository.Create(owner, plan with { Name = "BattSecond" }).Id;
        }
        using (var owner = new AccountOwners().Acquire("battery-other", () => { }))
            other = repository.Create(owner, new("BattOther", resources.Towns.Room, resources.Towns.InitialReturn)
                { DungeonState = CharacterDungeonState.NewOrdinary }).Id;
        fixture.Sql("UPDATE characters SET level=60 WHERE id=$p0", first);
        fixture.Sql("UPDATE characters SET level=95 WHERE id=$p0", second);
        fixture.Sql("UPDATE character_fatigue SET used=49,remaining=107,used_maximum=23,context_auxiliary=11,selection_auxiliary=9");
        fixture.Sql("INSERT INTO character_fatigue_battery(character_id,charging,buff) VALUES($p0,17,2)", first);
        var original = fixture.Query("SELECT id,revision FROM characters ORDER BY id");
        var originalFatigue = fixture.Query("SELECT * FROM character_fatigue ORDER BY character_id");
        var originalDays = fixture.Query("SELECT * FROM character_fatigue_reset_days ORDER BY character_id");
        fixture.Sql($"CREATE TRIGGER fail_fill AFTER INSERT ON character_fatigue_battery WHEN NEW.character_id={second} BEGIN SELECT RAISE(ABORT,'fill rollback'); END");
        await fixture.StartAsync();
        string command = $"account {fixture.Options.DevelopmentAccount} fatigue-battery-fill";
        That(DevelopmentRequest.Parse(command, "wrong").Writes, "一键电池命令归属显式账号并标记写操作");
        That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, command, out var failed) && failed.StartsWith("ERR "),
            "第二角色写入失败向GM返回拒绝");
        That(fixture.Query("SELECT buff FROM character_fatigue_battery WHERE character_id=$p0", first) is [[2L]] &&
            fixture.Query("SELECT count(*) FROM character_fatigue_battery WHERE character_id=$p0", second) is [[0L]],
            "账号整笔回滚，不留下前一个角色已满或后一个新行");
        That(fixture.Query("SELECT id,revision FROM characters ORDER BY id").SelectMany(row => row).SequenceEqual(original.SelectMany(row => row)),
            "批量失败回滚全部角色revision");
        fixture.Sql("DROP TRIGGER fail_fill");
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            PartyDungeonTcpTests.Drain(peer);
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, command, out var reply) && reply.StartsWith("OK "),
                "在线账号一键填满成功");
            var packet = peer.Expect(0, 36).Body;
            That(U16(packet, 6) == resources.Growth.FatigueBattery.BuffLimit(60), "真实N36立即刷新在线电量");
            foreach (var (id, level) in new[] { (first, 60), (second, 95) })
                That(fixture.Query("SELECT buff FROM character_fatigue_battery WHERE character_id=$p0", id) is [[long buff]] &&
                    buff == resources.Growth.FatigueBattery.BuffLimit(level), "账号内在线及离线满级角色均按等级表填满");
            That(fixture.Query("SELECT count(*) FROM character_fatigue_battery WHERE character_id=$p0", other) is [[0L]] &&
                fixture.Query("SELECT charging FROM character_fatigue_battery WHERE character_id=$p0", first) is [[17L]],
                "保留充能池并隔离其他账号");
            var revisions = fixture.Query("SELECT id,revision FROM characters ORDER BY id").SelectMany(row => row).ToArray();
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, command, out reply) && reply.Contains("\"changed\":0") &&
                fixture.Query("SELECT id,revision FROM characters ORDER BY id").SelectMany(row => row).SequenceEqual(revisions),
                "已满重复点击合法零变化，不增加revision");
            peer.Expect(0, 36);
            FormalDungeonTcpTests.EntryTown(peer);
            FormalDungeonTcpTests.Enter(peer, requestQuestId: 0);
            FormalDungeonTcpTests.Load(peer); PartyDungeonTcpTests.Drain(peer);
            That(fixture.Query("SELECT buff FROM character_fatigue_battery WHERE character_id=$p0", first) is [[long consumed]] &&
                consumed == resources.Growth.FatigueBattery.BuffLimit(60) - 1,
                "填满后在线角色可继续原生入图扣点，不被旧快照覆盖或保持常满");
        }
        await fixture.StopAsync();
        fixture.Sql("UPDATE character_fatigue_battery SET buff=0 WHERE character_id=$p0", second);
        That(DevelopmentCommands.RunGm(["account", fixture.Options.DevelopmentAccount, "fatigue-battery-fill"],
            fixture.Options with { GmPort = 0 }, resources.Archive) == 0 &&
            fixture.Query("SELECT buff FROM character_fatigue_battery WHERE character_id=$p0", second) is [[long offlineBuff]] &&
            offlineBuff == resources.Growth.FatigueBattery.BuffLimit(95), "真实停服CLI装配等级资源并填满离线账号");
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var reloaded = new CharacterRepository(fixture.Database, growthCatalog: resources.Growth).LoadCharacter(owner, 1);
            That(reloaded.Fatigue!.BatteryBuff == resources.Growth.FatigueBattery.BuffLimit(95), "停服重开仓储保留离线角色电量");
        }
        That(fixture.Query("SELECT * FROM character_fatigue WHERE character_id=$p0", second).Single()
                .SequenceEqual(originalFatigue.Single(row => Convert.ToInt64(row[0]) == second)) &&
            fixture.Query("SELECT * FROM character_fatigue_reset_days ORDER BY character_id").SelectMany(row => row)
                .SequenceEqual(originalDays.SelectMany(row => row)), "填满不改变普通疲劳、附属字段和重置日戳");
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "填满与后续消耗后数据库完整");
    }
}
