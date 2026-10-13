using System.Collections.Frozen;
using DfoServer.Game.Session;

namespace DfoServer.Network.Protocol;

internal enum CommandResult
{
    Handled,
    Unhandled,
    CloseConnection
}

internal delegate Task<CommandResult> GameCommandHandler(GameConnectionContext context, Packet request);
internal interface IGameCommandModule
{
    void Register(GameCommandRegistry commands);
}

internal sealed class GameCommandRegistry
{
    sealed record Registration(string Source, GameCommandHandler Handler, UserStateRequirement MinimumState);
    readonly Dictionary<ushort, Registration> registrations = [];
    FrozenDictionary<ushort, Registration>? commands;
    public int Count => commands?.Count ?? registrations.Count;
    public void Register(CmdPacketType opcode, GameCommandHandler handler, string source,
        UserStateRequirement minimumState = UserStateRequirement.Owner)
    {
        if (commands is not null)
            throw new InvalidOperationException("Command registry is already sealed");
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (opcode >= CmdPacketType.END)
            throw new ArgumentOutOfRangeException(nameof(opcode));
        if (!Enum.IsDefined(minimumState)) throw new ArgumentOutOfRangeException(nameof(minimumState));
        ushort value = (ushort)opcode;
        if (registrations.TryGetValue(value, out var previous))
            throw new InvalidOperationException($"Duplicate command {opcode} ({value}): {previous.Source} / {source}");
        registrations.Add(value, new(source, handler, minimumState));
    }

    public void Seal()
    {
        if (commands is not null)
            throw new InvalidOperationException("Command registry is already sealed");
        commands = registrations.ToFrozenDictionary();
        registrations.Clear();
    }

    public async Task<CommandResult> DispatchAsync(GameConnectionContext context, Packet request)
    {
        if (commands is null)
            throw new InvalidOperationException("Command registry is not sealed");
        // 保留旧owner拒绝的优先级；等待门禁期间可能接管，取得后仍须复验。
        context.Owner.Execute(() => true);
        await context.CommandGate.WaitAsync(context.Stop);
        try
        {
            context.Owner.Execute(() => true);
            CommandResult result = commands.TryGetValue(request.Opcode, out var command) && context.Session.CheckState(command.MinimumState)
                ? await command.Handler(context, request) : CommandResult.Unhandled;
            // 异步处理结束后仍须属于当前账号；旧连接不能继续下一条业务。
            context.Owner.Execute(() => true);
            return result;
        }
        finally { context.CommandGate.Release(); }
    }
}
