using DfoServer.Game.Session;
using DfoServer.Game.Accounts;
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
            Directory.Delete(root, recursive: true);
        }

        Console.WriteLine($"PASS {TestCheck.Count - before} protocol registration, owner, directory assertions");
    }

    static async Task RegistryAsync(TownCatalog towns, string root)
    {
        var repository = new CharacterRepository(Path.Combine(root, "game.db"));
        var owners = new AccountOwners();
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
        var directory = new OnlineSessionDirectory();
        directory.Register(owner, session);
        TestCheck.That(ReferenceEquals(directory.Find("registry"), session), "directory retains the actual session");
        var pending = commands.DispatchAsync(context, new(1, 1723, []));
        await started.Task;
        using var replacement = owners.Acquire("registry", () =>
        {
        });
        var next = new GameSession(repository, towns, replacement);
        directory.Register(replacement, next);
        release.SetResult();
        await RejectAsync<InvalidOperationException>(() => pending, "generation rechecked after asynchronous handler");
        directory.Remove("registry", session);
        TestCheck.That(ReferenceEquals(directory.Find("registry"), next), "late old cleanup cannot remove replacement");
        await RejectAsync<InvalidOperationException>(() =>
        {
            directory.Register(owner, session);
            return Task.CompletedTask;
        }, "stale connection cannot publish session");
        await RejectAsync<InvalidOperationException>(() => commands.DispatchAsync(context, new(1, 35, [])), "generation checked before handler");
        directory.Remove("registry", next);
        directory.Remove("registry", next);
        TestCheck.That(directory.Find("registry") is null && replacement.Execute(() => true), "exact cleanup is repeatable and does not dispose owner");
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
