using DfoServer.Game.Session;
using DfoServer.Game.Accounts;
using DfoServer.Game.Caching;
using DfoServer.GameWorld.Towns;
using System.Net;
using DfoServer.Game.Auth;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using DfoServer.Testing;

namespace DfoServer;

internal static class ProtocolArchitectureTests
{
    public static async Task RunAsync(TownCatalog towns)
    {
        int before = TestCheck.Count;
        string root = Path.Combine(Path.GetTempPath(), "S5A9-protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RegistryAsync(towns, root);
        }
        finally
        {
            TestFiles.DeleteDirectory(root, recursive: true);
        }

        Console.WriteLine($"PASS {TestCheck.Count - before} protocol registration, owner, directory assertions");
    }

    static async Task RegistryAsync(TownCatalog towns, string root)
    {
        var repository = new CharacterRepository(Path.Combine(root, "game.db"));
        using var memory = new GameCache();
        var owners = new AccountOwners(memory);
        using var owner = owners.Acquire("registry", () =>
        {
        });
        using var lifetime = new CancellationTokenSource();
        using var bytes = new MemoryStream();
        await using var sender = new GameConnectionSender(owner, bytes, lifetime, 0, (_, _, _, _, _) =>
        {
        });
        var session = new GameSession(repository, towns, owner);
        var context = new GameConnectionContext(new(1, IPAddress.Loopback, bytes, lifetime), owner, session, sender);
        var commands = new GameCommandRegistry();
        await RejectAsync<InvalidOperationException>(() => commands.DispatchAsync(context, new(1, 3, [0])), "cannot dispatch before seal");
        commands.Register(CmdPacketType.EXIT, (_, _) => Task.FromResult(CommandResult.CloseConnection), "Session");
        commands.Register(CmdPacketType.GET_USERINFO, (_, _) => Task.FromResult(CommandResult.Unhandled), "Character");
        commands.Register(CmdPacketType.SET_USER_POSITION, (_, _) => Task.FromResult(CommandResult.Handled), "Town");
        try
        {
            commands.Register(CmdPacketType.EXIT, (_, _) => Task.FromResult(CommandResult.Handled), "Other");
            throw new Exception("Duplicate registration accepted");
        }
        catch (InvalidOperationException e)
        {
            TestCheck.That(e.Message.Contains("Session") && e.Message.Contains("Other") && e.Message.Contains("EXIT"), "duplicate diagnostic identifies both sources");
        }

        await RejectAsync<ArgumentOutOfRangeException>(() =>
        {
            commands.Register(CmdPacketType.END, (_, _) => Task.FromResult(CommandResult.Handled), "Sentinel");
            return Task.CompletedTask;
        }, "END cannot be a handler");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        commands.Register(CmdPacketType.PING, async (_, _) =>
        {
            started.SetResult();
            await release.Task;
            return CommandResult.Handled;
        }, "Delayed");
        commands.Seal();
        await RejectAsync<InvalidOperationException>(() =>
        {
            commands.Register(CmdPacketType.CREATE_CHARACTER, (_, _) => Task.FromResult(CommandResult.Handled), "Late");
            return Task.CompletedTask;
        }, "sealed registrations cannot change");
        TestCheck.That(await commands.DispatchAsync(context, new(1, 35, [])) == CommandResult.Handled, "handled result retained");
        TestCheck.That(await commands.DispatchAsync(context, new(1, 3, [0])) == CommandResult.CloseConnection, "close result retained");
        TestCheck.That(await commands.DispatchAsync(context, new(1, 8, [0])) == CommandResult.Unhandled, "unknown subtype retained");
        foreach (ushort opcode in new ushort[]
        {
            1748,
            65535
        })
            TestCheck.That(await commands.DispatchAsync(context, new(1, opcode, [])) == CommandResult.Unhandled, "unknown command remains unhandled");
        TestCheck.That(bytes.Length == 0, "registry does not fabricate protocol responses");
        var directory = new OnlineSessionDirectory(memory);
        directory.Register(owner, session);
        int fatigueRefreshes = 0, replacementFatigueRefreshes = 0;
        directory.RegisterConnection(1, session, (_, _, _) => { }, refreshFatigue: () => fatigueRefreshes++);
        var fatigueSettings = new DfoServer.Game.Settings.GameDebugSettings();
        string FatigueCommand(string line) => DfoServer.Development.DevelopmentGm.Answer(line, "registry", repository, owners,
            directory.Find, debugSettings: fatigueSettings, refreshFatigue: directory.RefreshFatigueDisplays);
        TestCheck.That(FatigueCommand("infinite-fatigue").StartsWith("OK ") && fatigueRefreshes == 0 &&
            FatigueCommand("infinite-fatigue invalid").StartsWith("ERR ") && fatigueRefreshes == 0,
            "查询和非法疲劳开关不触发在线显示写通知");
        TestCheck.That(FatigueCommand("infinite-fatigue off").StartsWith("OK ") &&
            !fatigueSettings.InfiniteFatigueFor("registry") && fatigueRefreshes == 1,
            "成功关闭疲劳开关通过原在线连接索引请求即时刷新");
        TestCheck.That(ReferenceEquals(directory.Find("registry"), session), "directory retains the actual session");
        TestCheck.That(ReferenceEquals(directory.FindConnection(1)?.Session, session) && memory.Count == 3,
            "共享缓存只保存账号、会话及连接索引的原对象，不复制在线角色");
        var pending = commands.DispatchAsync(context, new(1, 1723, []));
        await started.Task;
        using var replacement = owners.Acquire("registry", () =>
        {
        });
        var next = new GameSession(repository, towns, replacement);
        directory.Register(replacement, next);
        directory.RegisterConnection(1, next, (_, _, _) => { }, refreshFatigue: () => replacementFatigueRefreshes++);
        directory.RefreshFatigueDisplays();
        TestCheck.That(fatigueRefreshes == 1 && replacementFatigueRefreshes == 1,
            "同连接号替换后只刷新新会话，不调用已替换的疲劳回调");
        release.SetResult();
        await RejectAsync<InvalidOperationException>(() => pending, "generation rechecked after asynchronous handler");
        directory.Remove("registry", session);
        directory.RemoveConnection(1, session);
        TestCheck.That(ReferenceEquals(directory.Find("registry"), next), "late old cleanup cannot remove replacement");
        TestCheck.That(ReferenceEquals(directory.FindConnection(1)?.Session, next) && memory.Count == 3,
            "旧连接收尾不删除共享底座中的新连接或账号owner");
        await RejectAsync<InvalidOperationException>(() =>
        {
            directory.Register(owner, session);
            return Task.CompletedTask;
        }, "stale connection cannot publish session");
        await RejectAsync<InvalidOperationException>(() => commands.DispatchAsync(context, new(1, 35, [])), "generation checked before handler");
        directory.Remove("registry", next);
        directory.Remove("registry", next);
        directory.RemoveConnection(1, next);
        directory.RemoveConnection(1, next);
        TestCheck.That(directory.Find("registry") is null && replacement.Execute(() => true), "exact cleanup is repeatable and does not dispose owner");
        TestCheck.That(directory.FindConnection(1) is null && memory.Count == 1,
            "在线目录精确清理释放会话及连接槽，不释放仍有效的账号租约");
    }

    static async Task RejectAsync<T>(Func<Task> action, string name)
        where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            TestCheck.That(true, name);
            return;
        }

        throw new InvalidOperationException("FAIL expected " + typeof(T).Name + ": " + name);
    }
}
