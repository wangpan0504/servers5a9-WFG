using DfoServer.Application;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class HostedDungeonEligibilityTests
{
    public static void RunCore()
    {
        int checks = Count;
        RunRules();
        RunProcessAsync().GetAwaiter().GetResult();
        Console.WriteLine($"PASS {Count - checks} hosted dungeon eligibility rule and process assertions");
    }

    static DungeonEntryPlan Plan(uint dungeonId = 3, uint questId = 0)
    {
        var start = new DungeonMap("map", new(0, 0), 1, "map/hosted-start.map");
        var boss = new DungeonMap("boss", new(1, 0), 2, "map/hosted-boss.map");
        var maze = new DungeonMaze(0, 2, 1, "BB", [start.Position], [boss.Position], [start, boss], []);
        return new(1, 1, dungeonId, questId, questId == 0 ? byte.MaxValue : (byte)0, 0, maze, start, boss);
    }

    static void RunRules()
    {
        That(HostedDungeonEligibilityRules.RejectReason(Plan(), false, false, false) is null, "普通零任务路线具备探测资格");
        That(HostedDungeonEligibilityRules.RejectReason(Plan(questId: 3145), false, false, false) ==
            HostedDungeonEligibilityRules.ReasonNotOrdinary, "任务路线不是普通资格图");
        That(HostedDungeonEligibilityRules.RejectReason(Plan(), true, false, false) ==
            HostedDungeonEligibilityRules.ReasonPartyDungeon, "组队实例拒绝探测");
        That(HostedDungeonEligibilityRules.RejectReason(Plan(), false, true, false) ==
            HostedDungeonEligibilityRules.ReasonScriptedReturn, "脚本返回拒绝探测");
        That(HostedDungeonEligibilityRules.RejectReason(Plan(), false, false, true) ==
            HostedDungeonEligibilityRules.ReasonNavigationReplay, "导航重播拒绝探测");
        That(HostedDungeonEligibilityRules.RejectReason(Plan() with { HellParty = new DungeonHellPartyRoom(1, new(0, 0)) }, false, false, false) ==
            HostedDungeonEligibilityRules.ReasonHell, "深渊派对拒绝探测");
        That(HostedDungeonEligibilityRules.RejectReason(Plan() with { IsOrdinaryAnton = true }, false, false, false) ==
            HostedDungeonEligibilityRules.ReasonSpecialNormal, "安徒恩普通拒绝探测");
        That(HostedDungeonEligibilityRules.RejectReason(Plan() with { IsPractice = true }, false, false, false) ==
            HostedDungeonEligibilityRules.ReasonTraining, "练习模式拒绝探测");
        var spawned = HostedDungeonEligibilityRules.Spawns(
        [
            new DungeonPlannedMonster(0, 7, 100, 10, null) { BattleLevelOverride = 14 },
            new DungeonPlannedMonster(1, 8, 101, 12, null)
        ]);
        That(spawned[0] is { ObjectKey: 7, SpawnLevel: 10, BattleLevel: 14 } &&
            spawned[1] is { ObjectKey: 8, SpawnLevel: 12, BattleLevel: 12 },
            "有显式 BattleLevel 时生成声明不得改填 CandidateLevel");
        var probe = Guid.NewGuid();
        That(HostedDungeonEligibilityRules.FromReply(null, probe).Reason == HostedDungeonEligibilityRules.ReasonInvalidReply,
            "空回复按无效拒绝");
        That(HostedDungeonEligibilityRules.FromReply(new(Guid.NewGuid(), false, "missing-combat-profile", null), probe).Reason ==
            HostedDungeonEligibilityRules.ReasonStaleReply, "旧 probeId 不得应用到新会话");
        var fought = HostedDungeonEligibilityRules.FromReply(new(probe, true, null, null), probe);
        That(!fought.CanFight && fought.Reason == HostedDungeonEligibilityRules.ReasonCanFightWithoutProfile,
            "缺少 CombatProfile 时 CanFight 保持 false");
        var profile = HostedDungeonEligibilityRules.FromReply(new(probe, false, "ok", "private-v1"), probe);
        That(!profile.CanFight && profile.Reason == HostedDungeonEligibilityRules.ReasonUnsupportedProfile,
            "未知 profile schema 明确拒绝");
        var missing = HostedDungeonEligibilityRules.FromReply(new(probe, false, null, null), probe);
        That(!missing.CanFight && missing.Reason == HostedDungeonEligibilityRules.ReasonMissingCombatProfile,
            "无 schema 的成功退出仍按缺失 CombatProfile 拒绝");
    }

    static async Task RunProcessAsync()
    {
        var probe = Guid.NewGuid();
        var request = new HostedDungeonEligibilityWireRequest(probe, 1, 2, 3, 4,
            new HostedDungeonEligibilityWireIdentity(4, 0, 1, Guid.NewGuid(), 5),
            3, 0, 0, 0, 1, [new HostedDungeonEligibilityWireSpawn(1, 100, 10, 14)]);
        var missing = await new HostedDungeonEligibilityCoordinator(new HostedDungeonEligibilityOptions { Enabled = true })
            .RunAsync(new HostedDungeonEligibilityCapture(probe, 1, 2, 4, 3, new DungeonRunIdentity(4, 0, 1, Guid.NewGuid(), 5),
                3, 0, 1, new DungeonPoint(0, 0), [1], request.Spawns, null), CancellationToken.None);
        That(!missing.CanFight && missing.Reason == HostedDungeonEligibilityRules.ReasonMissingCombatProfile,
            "On 且无宿主时进程内明确拒绝缺失 CombatProfile");
        var disabled = await new HostedDungeonEligibilityCoordinator(new HostedDungeonEligibilityOptions())
            .RunAsync(new HostedDungeonEligibilityCapture(probe, 1, 2, 4, 3, new DungeonRunIdentity(4, 0, 1, Guid.NewGuid(), 5),
                3, 0, 1, new(0, 0), [1], request.Spawns, null), CancellationToken.None);
        That(disabled.Reason == HostedDungeonEligibilityRules.ReasonDisabled, "默认关闭的 coordinator 不发起宿主");
        var special = await new HostedDungeonEligibilityCoordinator(new HostedDungeonEligibilityOptions { Enabled = true })
            .RunAsync(new HostedDungeonEligibilityCapture(probe, 1, 2, 4, 3, new DungeonRunIdentity(4, 0, 1, Guid.NewGuid(), 5),
                3, 3145, 1, new(0, 0), [1], request.Spawns, HostedDungeonEligibilityRules.ReasonNotOrdinary), CancellationToken.None);
        That(!special.CanFight && special.Reason == HostedDungeonEligibilityRules.ReasonNotOrdinary, "特殊请求不启动宿主");

        var child = await HostedDungeonEligibilityProcess.RunAsync(Fake("missing-profile"), request, CancellationToken.None);
        That(!child.CanFight && child.Reason == HostedDungeonEligibilityRules.ReasonMissingCombatProfile && child.ProbeId == probe,
            "受控假宿主缺失 profile 仍明确拒绝");
        var stale = await HostedDungeonEligibilityProcess.RunAsync(Fake("stale"), request, CancellationToken.None);
        That(!stale.CanFight && stale.Reason == HostedDungeonEligibilityRules.ReasonStaleReply, "旧回复 probeId 不影响当前探测");
        var fought = await HostedDungeonEligibilityProcess.RunAsync(Fake("canfight"), request, CancellationToken.None);
        That(!fought.CanFight && fought.Reason == HostedDungeonEligibilityRules.ReasonCanFightWithoutProfile,
            "假宿主声称可战斗也不能在无 CombatProfile 时放行");
        var invalid = await HostedDungeonEligibilityProcess.RunAsync(Fake("invalid"), request, CancellationToken.None);
        That(invalid.Reason == HostedDungeonEligibilityRules.ReasonInvalidReply, "非 JSON 输出按无效拒绝");
        var eof = await HostedDungeonEligibilityProcess.RunAsync(Fake("eof"), request, CancellationToken.None);
        That(eof.Reason == HostedDungeonEligibilityRules.ReasonEof, "空输出按 EOF 拒绝");
        var overflow = await HostedDungeonEligibilityProcess.RunAsync(Fake("overflow"), request, CancellationToken.None);
        That(overflow.Reason == HostedDungeonEligibilityRules.ReasonOverBudget, "stdout 超预算拒绝并回收进程");
        var stderr = await HostedDungeonEligibilityProcess.RunAsync(Fake("stderr-overflow"), request, CancellationToken.None);
        That(stderr.Reason == HostedDungeonEligibilityRules.ReasonOverBudget, "stderr 超预算同样有界拒绝");
        var timed = await HostedDungeonEligibilityProcess.RunAsync(Fake("hang", TimeSpan.FromMilliseconds(400)), request, CancellationToken.None);
        That(timed.Reason == HostedDungeonEligibilityRules.ReasonTimeout, "超时杀本进程并拒绝");
        using var cancel = new CancellationTokenSource();
        var hanging = HostedDungeonEligibilityProcess.RunAsync(Fake("hang", TimeSpan.FromSeconds(5)), request, cancel.Token);
        cancel.CancelAfter(100);
        var cancelled = await hanging;
        That(cancelled.Reason == HostedDungeonEligibilityRules.ReasonCancelled, "取消只回收本次子进程");
    }

    static HostedDungeonEligibilityOptions Fake(string scenario, TimeSpan? timeout = null)
    {
        var (file, prefix) = CurrentHost();
        return new()
        {
            Enabled = true,
            HostFileName = file,
            HostArguments = [.. prefix, "--hosted-eligibility-fake-host", scenario],
            Timeout = timeout ?? TimeSpan.FromSeconds(2)
        };
    }

    static (string FileName, string[] Prefix) CurrentHost()
    {
        string processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("缺少当前宿主路径");
        if (Path.GetFileNameWithoutExtension(processPath)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string dll = Path.Combine(AppContext.BaseDirectory, "DfoServer.dll");
            if (File.Exists(dll))
                return (processPath, [dll]);
        }
        return (processPath, []);
    }
}
