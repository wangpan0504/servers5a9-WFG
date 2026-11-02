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
internal delegate SemaphoreSlim? GameCommandGate(GameConnectionContext context, Packet request);
internal interface IGameCommandModule
{
    void Register(GameCommandRegistry commands);
}

internal sealed class GameCommandRegistry
{
    sealed record Registration(string Source, GameCommandHandler Handler, UserStateRequirement MinimumState, GameCommandGate? SharedGate);
    readonly Dictionary<ushort, Registration> registrations = [];
    FrozenDictionary<ushort, Registration>? commands;
    public int Count => commands?.Count ?? registrations.Count;
    public void Register(CmdPacketType opcode, GameCommandHandler handler, string source,
        UserStateRequirement minimumState = UserStateRequirement.Owner, GameCommandGate? sharedGate = null)
    {
        if (opcode >= CmdPacketType.END)
            throw new ArgumentOutOfRangeException(nameof(opcode));
        RegisterOpcode((ushort)opcode, handler, source, minimumState, sharedGate);
    }

    // 本服扩展只在保留区显式注册，不扩大原生枚举或把未知命令转成成功。
    internal void RegisterPrivate(ushort opcode, GameCommandHandler handler, string source,
        UserStateRequirement minimumState = UserStateRequirement.Owner, GameCommandGate? sharedGate = null)
    {
        if (opcode is < 0x7F00 or > 0x7FFF) throw new ArgumentOutOfRangeException(nameof(opcode));
        RegisterOpcode(opcode, handler, source, minimumState, sharedGate);
    }

    void RegisterOpcode(ushort value, GameCommandHandler handler, string source,
        UserStateRequirement minimumState, GameCommandGate? sharedGate)
    {
        if (commands is not null)
            throw new InvalidOperationException("Command registry is already sealed");
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (!Enum.IsDefined(minimumState)) throw new ArgumentOutOfRangeException(nameof(minimumState));
        if (registrations.TryGetValue(value, out var previous))
            throw new InvalidOperationException($"Duplicate command {(CmdPacketType)value} ({value}): {previous.Source} / {source}");
        registrations.Add(value, new(source, handler, minimumState, sharedGate));
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
        commands.TryGetValue(request.Opcode, out var command);
        CommandResult result = CommandResult.Unhandled;
        Func<Task>? after = null;
        using (var gates = await DispatchGateScope.EnterAsync(context, request, command?.SharedGate))
        {
            context.DispatchGates = gates;
            context.AfterDispatch = null;
            // 共享门禁协调所有成员命令；本人应答也只能入队，不能等慢连接拖住整组。
            using var publication = !gates.HasShared ? null : context.Sender.QueuePublication(() =>
            {
                long epoch = context.Session.SelectionEpoch;
                return () => context.Owner.IsCurrent && context.Session.SelectionEpoch == epoch && !context.Stop.IsCancellationRequested;
            });
            if (command is not null && context.Session.CheckState(command.MinimumState))
            {
                if (context.Creatures is { } creatures) await creatures.BeforeCommandAsync(request);
                result = await command.Handler(context, request);
            }
            // 异步处理结束后仍须属于当前账号；旧连接不能继续下一条业务。
            context.Owner.Execute(() => true);
            after = context.AfterDispatch;
            context.AfterDispatch = null;
        }
        if (after is not null) await after();
        if (result == CommandResult.CloseConnection) await context.Sender.DrainAsync();
        return result;
    }
}
